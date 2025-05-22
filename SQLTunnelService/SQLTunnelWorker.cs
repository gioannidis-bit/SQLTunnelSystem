using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
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
            _logger.LogInformation("🚀 ===========================================");
            _logger.LogInformation("🚀 SQL Tunnel Service OPTIMIZED VERSION starting");
            _logger.LogInformation("🚀 Version: {Version} | Service ID: {ServiceId}", _settings.Version, _settings.ServiceId);
            _logger.LogInformation("🚀 Pure SignalR Streaming Mode - Maximum Performance");
            _logger.LogInformation("🚀 Relay Server: {RelayServerUrl}", _settings.RelayServerUrl);
            _logger.LogInformation("🚀 ===========================================");

            // 🚀 CRITICAL: Setup SignalR connection first
            await SetupSignalRConnection();

            // Get SQL Server information
            try
            {
                _serverInfo = await GetSqlServerInfoAsync(cancellationToken);
                _logger.LogInformation("✅ Connected to SQL Server: {ServerVersion}",
                    _serverInfo.Split('\n')[0].Trim());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to connect to SQL Server");
                _serverInfo = "Connection failed";
                throw; // Don't start if SQL Server is not accessible
            }

            await base.StartAsync(cancellationToken);
        }

        private async Task SetupSignalRConnection()
        {
            try
            {
                var hubUrl = _settings.RelayServerUrl.Replace("/api", "/sqlTunnelHub");
                _logger.LogInformation("🔗 Setting up SignalR connection to: {HubUrl}", hubUrl);

                _signalRConnection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Headers.Add("X-Service-ID", _settings.ServiceId);
                        options.Headers.Add("X-Service-Key", _settings.SecretKey);

                        // 🚀 OPTIMIZED: Enhanced connection settings
                        options.SkipNegotiation = true;
                        options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;

                        // For development - accept self-signed certificates
                        options.HttpMessageHandlerFactory = handler =>
                        {
                            if (handler is HttpClientHandler clientHandler)
                            {
                                clientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                            }
                            return handler;
                        };
                    })
                    .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
                    .Build();

                // 🚀 ENHANCED: Better connection handling
                _signalRConnection.Reconnecting += error =>
                {
                    _logger.LogWarning("🔄 SignalR reconnecting: {Error}", error?.Message);
                    _signalRConnected = false;
                    return Task.CompletedTask;
                };

                _signalRConnection.Reconnected += connectionId =>
                {
                    _logger.LogInformation("✅ SignalR reconnected: {ConnectionId}", connectionId);
                    _signalRConnected = true;
                    return _signalRConnection.InvokeAsync("RegisterSqlService", _settings.ServiceId, _settings.DisplayName, _settings.Version);
                };

                _signalRConnection.Closed += error =>
                {
                    _logger.LogError("❌ SignalR connection closed: {Error}", error?.Message);
                    _signalRConnected = false;
                    return Task.CompletedTask;
                };

                await _signalRConnection.StartAsync();
                await _signalRConnection.InvokeAsync("RegisterSqlService",
                    _settings.ServiceId, _settings.DisplayName, _settings.Version);

                _signalRConnected = true;
                _logger.LogInformation("✅ SignalR connection established - Ready for UNLIMITED streaming!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ CRITICAL: Failed to setup SignalR connection");
                _signalRConnected = false;
                throw; // Don't start if SignalR fails
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Worker service running - PURE SignalR streaming mode");

            using var httpClient = CreateHttpClient();

            // Initial heartbeat
            await SendHeartbeatAsync(httpClient, stoppingToken);
            var lastHeartbeatTime = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 💓 Send heartbeat every 20 seconds (reduced frequency)
                    if ((DateTime.UtcNow - lastHeartbeatTime).TotalSeconds >= 20)
                    {
                        await SendHeartbeatAsync(httpClient, stoppingToken);
                        lastHeartbeatTime = DateTime.UtcNow;
                    }

                    // 🚀 PURE SignalR: Check for pending queries
                    if (!_signalRConnected)
                    {
                        _logger.LogWarning("⚠️ SignalR not connected - attempting reconnection");
                        await SetupSignalRConnection();
                        await Task.Delay(5000, stoppingToken); // Wait before retrying
                        continue;
                    }

                    var response = await httpClient.GetAsync($"{_settings.RelayServerUrl}/queries/pending", stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(stoppingToken);
                        var pendingQueries = JsonConvert.DeserializeObject<PendingQuery[]>(content);

                        if (pendingQueries != null && pendingQueries.Length > 0)
                        {
                            _logger.LogInformation("🎯 Found {Count} pending queries - processing via PURE SignalR streaming", pendingQueries.Length);

                            // 🚀 OPTIMIZED: Process queries in parallel for better performance
                            var tasks = pendingQueries.Select(query => ProcessQueryAsync(query, stoppingToken)).ToArray();
                            await Task.WhenAll(tasks);
                        }
                        else
                        {
                            _logger.LogDebug("😴 No pending queries found");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Failed to check for pending queries. Status: {StatusCode}", response.StatusCode);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error in polling loop");
                }

                // 🚀 OPTIMIZED: Reduced polling interval for better responsiveness
                await Task.Delay(Math.Max(_settings.PollingIntervalMs, 1000), stoppingToken);
            }
        }

        // 🚀 NEW: Optimized query processing
        private async Task ProcessQueryAsync(PendingQuery query, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("⚡ Processing query {QueryId} via SignalR streaming", query.Id);
                await ExecuteStreamingQuery(query, cancellationToken);
                _logger.LogInformation("✅ Successfully completed query {QueryId}", query.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing query {QueryId}", query.Id);

                // Send error via SignalR
                try
                {
                    await _signalRConnection.SendAsync("SendDataChunk", query.Id, $"Error: {ex.Message}", true);
                }
                catch (Exception signalREx)
                {
                    _logger.LogError(signalREx, "❌ Failed to send error via SignalR for query {QueryId}", query.Id);
                }
            }
        }

        private async Task ExecuteStreamingQuery(PendingQuery query, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("🚀 Executing UNLIMITED streaming query {QueryId}: {QueryPreview}",
                query.Id, query.Query.Length > 100 ? query.Query.Substring(0, 100) + "..." : query.Query);

            using var connection = new SqlConnection(_settings.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(query.Query, connection);

            // 🚀 OPTIMIZED: Extended timeout for large queries
            command.CommandTimeout = _settings.MaxRowsPerQuery > 100000 ? 1800 : 600; // 30 min for huge queries, 10 min for normal

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

            // 🚀 UNLIMITED STREAMING - No limits at all!
            await StreamUnlimitedResults(query.Id, reader, cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("⚡ Query {QueryId} completed in {Duration} seconds", query.Id, duration.TotalSeconds);
        }

        private async Task StreamUnlimitedResults(string queryId, SqlDataReader reader, CancellationToken cancellationToken)
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

                // 🚀 OPTIMIZED: Dynamic batch sizing based on settings
                int batchSize = _settings.StreamingBatchSize;
                int totalRows = 0;
                int batchNumber = 0;
                bool hasMoreRows = true;

                var dt = new DataTable();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dt.Columns.Add(columnNames[i], reader.GetFieldType(i));
                }

                _logger.LogInformation("🚀 Starting UNLIMITED streaming for query {QueryId} - Batch size: {BatchSize}", queryId, batchSize);

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

                        // 🚀 OPTIMIZED: Dynamic progress logging
                        if (batchNumber <= 10 || batchNumber % 20 == 0)
                        {
                            _logger.LogInformation("📊 Streamed {BatchNumber} batches, {TotalRows} total rows for query {QueryId}",
                                batchNumber, totalRows, queryId);
                        }

                        // 🚀 OPTIMIZED: Smart memory management
                        if (_settings.EnableMemoryOptimization && totalRows % _settings.GCAfterRowsThreshold == 0)
                        {
                            GC.Collect(0, GCCollectionMode.Optimized); // Quick generation 0 cleanup
                            _logger.LogDebug("🧹 Performed optimized GC after {TotalRows} rows", totalRows);
                        }
                    }
                }

                // Send summary
                await _signalRConnection.SendAsync("SendDataChunk", queryId,
                    $"SUMMARY:{{\"totalRows\":{totalRows},\"batches\":{batchNumber}}}", false);

                // Send completion
                await _signalRConnection.SendAsync("SendDataChunk", queryId, "Query execution completed", true);

                _logger.LogInformation("🎉 Successfully streamed {TotalRows} rows in {BatchCount} batches via SignalR (UNLIMITED!)",
                    totalRows, batchNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in SignalR streaming for query {QueryId}", queryId);
                await _signalRConnection.SendAsync("SendDataChunk", queryId, $"Error: {ex.Message}", true);
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🛑 SQL Tunnel Service stopping...");

            if (_signalRConnection != null)
            {
                try
                {
                    await _signalRConnection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Error disposing SignalR connection");
                }
            }

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("🛑 SQL Tunnel Service stopped successfully");
        }

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient("SQLTunnelClient");
            client.DefaultRequestHeaders.Add("X-Service-ID", _settings.ServiceId);
            client.DefaultRequestHeaders.Add("X-Service-Key", _settings.SecretKey);

            // 🚀 OPTIMIZED: Extended timeout for large result polling
            client.Timeout = TimeSpan.FromMinutes(2);

            return client;
        }

        private async Task<string> GetSqlServerInfoAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("🔍 Retrieving SQL Server info...");

            using var connection = new SqlConnection(_settings.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand("SELECT @@VERSION", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            return result?.ToString() ?? "Unknown";
        }

        private async Task SendHeartbeatAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            try
            {
                var heartbeatData = new
                {
                    DisplayName = _settings.DisplayName,
                    Description = _settings.Description,
                    Version = _settings.Version,
                    ServerInfo = _serverInfo,
                    // 🚀 NEW: Performance metrics
                    StreamingBatchSize = _settings.StreamingBatchSize,
                    MaxRowsPerQuery = _settings.MaxRowsPerQuery,
                    MemoryOptimization = _settings.EnableMemoryOptimization
                };

                var json = JsonConvert.SerializeObject(heartbeatData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(
                    $"{_settings.RelayServerUrl}/services/heartbeat",
                    content,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("💓 Heartbeat sent successfully");
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to send heartbeat. Status: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending heartbeat");
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