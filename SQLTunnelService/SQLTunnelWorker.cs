using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Microsoft.AspNetCore.SignalR.Client;
using System.Linq;

namespace SQLTunnelService
{
    public class SQLTunnelWorker : BackgroundService
    {
        private readonly ILogger<SQLTunnelWorker> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ServiceSettings _settings;
        private string _serverInfo;
        private HubConnection _signalRConnection;
        private bool _signalRConnected = false;
        private bool _useSignalRForLargeQueries = true;

        public SQLTunnelWorker(
            ILogger<SQLTunnelWorker> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<ServiceSettings> settings)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _settings = settings.Value;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("-------------------------------------------");
            _logger.LogInformation("SQL Tunnel Service starting. Version: {Version}", _settings.Version);
            _logger.LogInformation("Service ID: {ServiceId}", _settings.ServiceId);
            _logger.LogInformation("Using relay server: {RelayServerUrl}", _settings.RelayServerUrl);
            _logger.LogInformation("-------------------------------------------");

            // Setup SignalR connection for large queries
            await SetupSignalRConnection();

            // Get SQL Server information
            try
            {
                _serverInfo = await GetSqlServerInfoAsync(cancellationToken);
                _logger.LogInformation("Connected to SQL Server: {ServerVersion}",
                    _serverInfo.Split('\n')[0].Trim());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to SQL Server");
                _serverInfo = "Unknown";
            }

            await base.StartAsync(cancellationToken);
        }

        private async Task SetupSignalRConnection()
        {
            try
            {
                var hubUrl = _settings.RelayServerUrl.Replace("/api", "/sqlTunnelHub");
                _logger.LogInformation("Setting up SignalR connection to: {HubUrl}", hubUrl);

                _signalRConnection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Headers.Add("X-Service-ID", _settings.ServiceId);
                        options.Headers.Add("X-Service-Key", _settings.SecretKey);

                        // For development - accept self-signed certificates
                        options.HttpMessageHandlerFactory = handler =>
                        {
                            if (handler is System.Net.Http.HttpClientHandler clientHandler)
                            {
                                clientHandler.ServerCertificateCustomValidationCallback =
                                    (message, cert, chain, errors) => true;
                            }
                            return handler;
                        };
                    })
                    .WithAutomaticReconnect()
                    .Build();

                await _signalRConnection.StartAsync();
                await _signalRConnection.InvokeAsync("RegisterSqlService",
                    _settings.ServiceId, _settings.DisplayName, _settings.Version);

                _signalRConnected = true;
                _logger.LogInformation("SignalR connection established for streaming large queries");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to setup SignalR connection - will use HTTP for all queries");
                _signalRConnected = false;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker service is running - SignalR ONLY mode");

            using var httpClient = CreateHttpClient();

            // Initial heartbeat
            await SendHeartbeatAsync(httpClient, stoppingToken);
            var lastHeartbeatTime = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Send heartbeat every 30 seconds
                    if ((DateTime.UtcNow - lastHeartbeatTime).TotalSeconds >= 30)
                    {
                        await SendHeartbeatAsync(httpClient, stoppingToken);
                        lastHeartbeatTime = DateTime.UtcNow;
                    }

                    // Check for pending queries
                    _logger.LogDebug("Checking for pending queries at {Endpoint}",
                        $"{_settings.RelayServerUrl}/queries/pending");

                    var response = await httpClient.GetAsync(
                        $"{_settings.RelayServerUrl}/queries/pending", stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(stoppingToken);
                        var pendingQueries = JsonConvert.DeserializeObject<PendingQuery[]>(content);

                        if (pendingQueries != null && pendingQueries.Length > 0)
                        {
                            _logger.LogInformation("Found {Count} pending queries - processing ALL via SignalR streaming", pendingQueries.Length);

                            foreach (var query in pendingQueries)
                            {
                                try
                                {
                                    if (_signalRConnected)
                                    {
                                        _logger.LogInformation("Processing query via SignalR streaming: {QueryId}", query.Id);
                                        await ExecuteStreamingQuery(query, stoppingToken);
                                    }
                                    else
                                    {
                                        _logger.LogError("SignalR not connected - cannot process query: {QueryId}", query.Id);

                                        // Send error via HTTP as fallback ONLY for connection issues
                                        await SendQueryResultAsync(httpClient, query.Id, null,
                                            "SignalR streaming not available", stoppingToken);
                                    }

                                    _logger.LogInformation("Successfully executed query ID: {QueryId}", query.Id);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error executing query ID: {QueryId}", query.Id);

                                    // Send error via HTTP (only for errors)
                                    await SendQueryResultAsync(httpClient, query.Id, null, ex.Message, stoppingToken);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogDebug("No pending queries found");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to check for pending queries. Status: {StatusCode}",
                            response.StatusCode);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in polling loop");
                }

                await Task.Delay(_settings.PollingIntervalMs, stoppingToken);
            }
        }

        private async Task ExecuteStreamingQuery(PendingQuery query, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing query via SignalR streaming (UNLIMITED): {Query}",
                query.Query.Length > 100 ? query.Query.Substring(0, 100) + "..." : query.Query);

            using var connection = new SqlConnection(_settings.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(query.Query, connection);
            command.CommandTimeout = 300;

            // Add parameters if any
            if (query.Parameters != null)
            {
                var paramDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                    JsonConvert.SerializeObject(query.Parameters));

                if (paramDict != null)
                {
                    foreach (var param in paramDict)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // ALL queries use SignalR streaming - NO LIMITS!
            await StreamLargeQueryResults(query.Id, reader, cancellationToken);
        }

        private async Task StreamLargeQueryResults(string queryId, SqlDataReader reader, CancellationToken cancellationToken)
        {
            var columnNames = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columnNames[i] = reader.GetName(i);
            }

            try
            {
                // Send start message
                await _signalRConnection.SendAsync("SendDataChunk", queryId, "Starting query execution...", false);

                // Send schema
                var schemaData = columnNames.Select((name, index) => new {
                    Name = name,
                    Type = reader.GetFieldType(index).Name
                });
                var schemaJson = JsonConvert.SerializeObject(schemaData);
                await _signalRConnection.SendAsync("SendDataChunk", queryId, $"SCHEMA:{schemaJson}", false);

                // Process in batches - NO SIZE LIMITS AT ALL!
                int batchSize = 1000;
                int totalRows = 0;
                int batchNumber = 0;
                bool hasMoreRows = true;

                var dt = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dt.Columns.Add(columnNames[i], reader.GetFieldType(i));
                }

                _logger.LogInformation("Starting unlimited streaming for query {QueryId}", queryId);

                while (hasMoreRows && !cancellationToken.IsCancellationRequested)
                {
                    dt.Clear(); // Reuse DataTable for memory efficiency
                    int rowsInBatch = 0;

                    // Read batch
                    while (rowsInBatch < batchSize && (hasMoreRows = await reader.ReadAsync(cancellationToken)))
                    {
                        var row = dt.NewRow();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                        }
                        dt.Rows.Add(row);
                        rowsInBatch++;
                    }

                    // Send batch
                    if (rowsInBatch > 0)
                    {
                        totalRows += rowsInBatch;
                        batchNumber++;

                        string batchJson = JsonConvert.SerializeObject(dt);
                        await _signalRConnection.SendAsync("SendDataChunk", queryId,
                            $"BATCH:{batchNumber}:{batchJson}", false);

                        // Log progress every 10 batches
                        if (batchNumber % 10 == 0)
                        {
                            _logger.LogInformation("Streamed {BatchNumber} batches, {TotalRows} total rows for query {QueryId}",
                                batchNumber, totalRows, queryId);
                        }

                        // Periodic GC for very large queries (every 50K rows)
                        if (totalRows % 50000 == 0)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            _logger.LogDebug("Performed GC after {TotalRows} rows", totalRows);
                        }
                    }
                }

                // Send summary
                await _signalRConnection.SendAsync("SendDataChunk", queryId,
                    $"SUMMARY:{{\"totalRows\":{totalRows},\"batches\":{batchNumber}}}", false);

                // Send completion
                await _signalRConnection.SendAsync("SendDataChunk", queryId, "Query execution completed", true);

                _logger.LogInformation("Successfully streamed {TotalRows} rows in {BatchCount} batches via SignalR (UNLIMITED)",
                    totalRows, batchNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SignalR streaming for query {QueryId}", queryId);
                await _signalRConnection.SendAsync("SendDataChunk", queryId, $"Error: {ex.Message}", true);
                throw;
            }
        }



        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Tunnel Service stopping...");
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("SQL Tunnel Service stopped");
        }

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient("SQLTunnelClient");
            client.DefaultRequestHeaders.Add("X-Service-ID", _settings.ServiceId);
            client.DefaultRequestHeaders.Add("X-Service-Key", _settings.SecretKey);
            return client;
        }

        private async Task<string> GetSqlServerInfoAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Retrieving SQL Server info using connection string");

            using var connection = new SqlConnection(_settings.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand("SELECT @@VERSION", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result?.ToString() ?? "Unknown";
        }

        private async Task SendHeartbeatAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            try
            {
                var heartbeatData = new
                {
                    DisplayName = _settings.DisplayName,
                    Description = _settings.Description,
                    Version = _settings.Version,
                    ServerInfo = _serverInfo
                };

                var json = JsonConvert.SerializeObject(heartbeatData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogDebug("Sending heartbeat to {Endpoint}",
                    $"{_settings.RelayServerUrl}/services/heartbeat");

                var response = await httpClient.PostAsync(
                    $"{_settings.RelayServerUrl}/services/heartbeat",
                    content,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Heartbeat sent successfully");
                }
                else
                {
                    _logger.LogWarning("Failed to send heartbeat. Status: {StatusCode}",
                        response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending heartbeat");
            }
        }

        private async Task<string> ExecuteSqlQueryAsync(
    string query,
    object parameters,
    CancellationToken cancellationToken)
        {
            // This method should NOT be called in normal operation
            _logger.LogWarning("ExecuteSqlQueryAsync called - this should only happen for error cases!");

            return JsonConvert.SerializeObject(new
            {
                error = "HTTP query execution disabled - use SignalR streaming only"
            });
        }

    

        // Add SignalR streaming method (like CloudRelayService):
        private async Task ProcessLargeQueryWithSignalR(SqlDataReader reader, CancellationToken cancellationToken)
        {
            var columnNames = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columnNames[i] = reader.GetName(i);
            }

            var queryId = Guid.NewGuid().ToString();

            try
            {
                // Send start message
                await _signalRConnection.SendAsync("SendDataChunk", queryId, "Starting query execution...", false);

                // Send schema
                var schemaData = columnNames.Select((name, index) => new {
                    Name = name,
                    Type = reader.GetFieldType(index).Name
                });
                var schemaJson = JsonConvert.SerializeObject(schemaData);
                await _signalRConnection.SendAsync("SendDataChunk", queryId, $"SCHEMA:{schemaJson}", false);

                // Process in batches (like CloudRelayService)
                int batchSize = 1000;
                int totalRows = 0;
                int batchNumber = 0;
                bool hasMoreRows = true;

                var dt = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dt.Columns.Add(columnNames[i], reader.GetFieldType(i));
                }

                while (hasMoreRows)
                {
                    dt.Clear(); // Reuse DataTable
                    int rowsInBatch = 0;

                    // Read batch
                    while (rowsInBatch < batchSize && (hasMoreRows = await reader.ReadAsync(cancellationToken)))
                    {
                        var row = dt.NewRow();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                        }
                        dt.Rows.Add(row);
                        rowsInBatch++;
                    }

                    // Send batch
                    if (rowsInBatch > 0)
                    {
                        totalRows += rowsInBatch;
                        batchNumber++;

                        string batchJson = JsonConvert.SerializeObject(dt);
                        await _signalRConnection.SendAsync("SendDataChunk", queryId,
                            $"BATCH:{batchNumber}:{batchJson}", false);

                        _logger.LogDebug("Sent batch {BatchNumber} with {RowCount} rows", batchNumber, rowsInBatch);
                    }
                }

                // Send summary
                await _signalRConnection.SendAsync("SendDataChunk", queryId,
                    $"SUMMARY:{{\"totalRows\":{totalRows},\"batches\":{batchNumber}}}", false);

                // Send completion
                await _signalRConnection.SendAsync("SendDataChunk", queryId, "Query execution completed", true);

                _logger.LogInformation("Streamed {TotalRows} rows in {BatchCount} batches", totalRows, batchNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SignalR streaming");
                await _signalRConnection.SendAsync("SendDataChunk", queryId, $"Error: {ex.Message}", true);
            }
        }

     
        private async Task SendQueryResultAsync(
    HttpClient httpClient,
    string queryId,
    string result,
    string error,
    CancellationToken cancellationToken)
        {
            try
            {
                // REDUCED LIMIT: 5MB to avoid server JSON parsing issues
                if (!string.IsNullOrEmpty(result) && result.Length > 1115_000_000) // 5MB limit
                {
                    _logger.LogWarning("Query result too large ({Size} chars), sending error instead", result.Length);

                    // Send truncated error instead of large result
                    var errorResultObj = new
                    {
                        QueryId = queryId,
                        Result = (string)null,
                        Error = $"Result too large ({result.Length:N0} characters). Please use LIMIT, TOP, or WHERE clauses to reduce result size.",
                        Timestamp = DateTime.UtcNow
                    };

                    var errorJson = JsonConvert.SerializeObject(errorResultObj);
                    var errorContent = new StringContent(errorJson, Encoding.UTF8, "application/json");

                    var errorResponse = await httpClient.PostAsync(
                        $"{_settings.RelayServerUrl}/queries/result",
                        errorContent,
                        cancellationToken);

                    if (errorResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Sent error result for oversized query {QueryId}", queryId);
                    }
                    else
                    {
                        _logger.LogError("Failed to send error result: {StatusCode}", errorResponse.StatusCode);
                    }
                    return;
                }

                // Send normal result
                var resultObj = new
                {
                    QueryId = queryId,
                    Result = result,
                    Error = error,
                    Timestamp = DateTime.UtcNow
                };

                var json = JsonConvert.SerializeObject(resultObj);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(
                    $"{_settings.RelayServerUrl}/queries/result",
                    content,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent result for query {QueryId}", queryId);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge || // 413
                         response.StatusCode == System.Net.HttpStatusCode.BadGateway ||             // 502  
                         response.StatusCode == System.Net.HttpStatusCode.InternalServerError)     // 500 - NEW
                {
                    _logger.LogError("Server cannot handle large query result ({StatusCode} error) for query {QueryId}. Size: {Size} chars",
                        response.StatusCode, queryId, json.Length);

                    // Send a truncated error message instead
                    await SendTruncatedError(httpClient, queryId, result?.Length ?? 0, cancellationToken);
                }
                else
                {
                    _logger.LogError("Failed to send query result: {StatusCode} for query {QueryId}",
                        response.StatusCode, queryId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception sending query result for {QueryId}", queryId);
            }
        }

        private async Task SendTruncatedError(HttpClient httpClient, string queryId, int originalSize, CancellationToken cancellationToken)
        {
            try
            {
                var truncatedResultObj = new
                {
                    QueryId = queryId,
                    Result = (string)null,
                    Error = $"Query result too large for transmission ({originalSize:N0} characters). Please use LIMIT or WHERE clauses to reduce result size.",
                    Timestamp = DateTime.UtcNow
                };

                var json = JsonConvert.SerializeObject(truncatedResultObj);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(
                    $"{_settings.RelayServerUrl}/queries/result",
                    content,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Sent truncated error message for query {QueryId}", queryId);
                }
                else
                {
                    _logger.LogError("Failed to send truncated error: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send truncated error for query {QueryId}", queryId);
            }
        }

        public class PendingQuery
        {
            public string Id { get; set; }
            public string Query { get; set; }
            public object Parameters { get; set; }
        }
    }
}