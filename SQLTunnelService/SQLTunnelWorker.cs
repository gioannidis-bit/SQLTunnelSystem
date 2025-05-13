using System;
using System.Collections.Generic;
using System.Data;
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

namespace SQLTunnelService
{
    public class SQLTunnelWorker : BackgroundService
    {
        private readonly ILogger<SQLTunnelWorker> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ServiceSettings _settings;
        private string _serverInfo;

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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker service is running");

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
                            _logger.LogInformation("Found {Count} pending queries", pendingQueries.Length);

                            foreach (var query in pendingQueries)
                            {
                                try
                                {
                                    // Execute the SQL query
                                    var result = await ExecuteSqlQueryAsync(
                                        query.Query, query.Parameters, stoppingToken);

                                    // Send results back to relay server
                                    await SendQueryResultAsync(
                                        httpClient, query.Id, result, null, stoppingToken);

                                    _logger.LogInformation("Successfully executed query ID: {QueryId}", query.Id);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error executing query ID: {QueryId}", query.Id);
                                    await SendQueryResultAsync(
                                        httpClient, query.Id, null, ex.Message, stoppingToken);
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
                    // Normal cancellation, just break the loop
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in polling loop");
                }

                // Wait before next check
                await Task.Delay(_settings.PollingIntervalMs, stoppingToken);
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
            _logger.LogDebug("Executing SQL query: {Query}", query);

            using var connection = new SqlConnection(_settings.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = new SqlCommand(query, connection);

            // Add parameters if any
            if (parameters != null)
            {
                var paramDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                    JsonConvert.SerializeObject(parameters));

                if (paramDict != null)
                {
                    foreach (var param in paramDict)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // Convert results to JSON
            var dt = new DataTable();
            dt.Load(reader);
            return JsonConvert.SerializeObject(dt);
        }

        private async Task SendQueryResultAsync(
            HttpClient httpClient,
            string queryId,
            string result,
            string error,
            CancellationToken cancellationToken)
        {
            var resultObj = new
            {
                QueryId = queryId,
                Result = result,
                Error = error,
                Timestamp = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(resultObj);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            await httpClient.PostAsync(
                $"{_settings.RelayServerUrl}/queries/result",
                content,
                cancellationToken);
        }
    }

    public class PendingQuery
    {
        public string Id { get; set; }
        public string Query { get; set; }
        public object Parameters { get; set; }
    }
}