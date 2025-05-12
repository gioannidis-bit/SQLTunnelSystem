using System;
using System.Data;
using System.Data.SqlClient;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Configuration;
using Newtonsoft.Json;
using System.IO;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using System.Collections.Generic;

namespace SQLTunnelService
{
    // Κύρια κλάση υπηρεσίας Windows
    public class SQLTunnelService : ServiceBase
    {
        private CancellationTokenSource _cts;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _relayServerUrl;
        private readonly string _serviceId;
        private readonly string _secretKey;
        private readonly string _connectionString;
        private readonly int _pollingIntervalMs;

        public SQLTunnelService()
        {
            ServiceName = "SQLTunnelService";

            // Φόρτωση ρυθμίσεων από το app.config
            _relayServerUrl = ConfigurationManager.AppSettings["RelayServerUrl"] ?? "https://your-relay-server.com/api";
            _serviceId = ConfigurationManager.AppSettings["ServiceId"] ?? Guid.NewGuid().ToString();
            _secretKey = ConfigurationManager.AppSettings["SecretKey"] ?? GenerateRandomKey();
            _connectionString = ConfigurationManager.AppSettings["SqlConnectionString"] ?? "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrongPassword;";
            _pollingIntervalMs = int.Parse(ConfigurationManager.AppSettings["PollingIntervalMs"] ?? "5000");

            ConfigureLogging();
        }

        private string GenerateRandomKey()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                var bytes = new byte[32];
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes);
            }
        }

        private void ConfigureLogging()
        {
            var config = new LoggingConfiguration();
            var fileTarget = new FileTarget("file")
            {
                FileName = "${basedir}/logs/sqltunnel-${shortdate}.log",
                Layout = "${longdate} ${level:uppercase=true} ${message} ${exception:format=toString}"
            };

            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fileTarget);
            LogManager.Configuration = config;
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();

            Logger.Info($"SQL Tunnel Service starting. ServiceID: {_serviceId}");
            Logger.Info($"Using relay server: {_relayServerUrl}");

            // Έναρξη του polling σε ξεχωριστό νήμα
            Task.Run(() => StartPollingForQueries(_cts.Token), _cts.Token);
        }

        protected override void OnStop()
        {
            Logger.Info("SQL Tunnel Service stopping...");
            _cts?.Cancel();
            Logger.Info("SQL Tunnel Service stopped");
        }

        private async Task StartPollingForQueries(CancellationToken cancellationToken)
        {
            // Μόνο για ανάπτυξη - ΜΗΝ το χρησιμοποιείτε σε παραγωγή
            var httpClientHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            using (var httpClient = new HttpClient(httpClientHandler))
            {
                // Προσθήκη headers αυθεντικοποίησης
                httpClient.DefaultRequestHeaders.Add("X-Service-ID", _serviceId);
                httpClient.DefaultRequestHeaders.Add("X-Service-Key", _secretKey);

                Logger.Info("Starting to poll for queries");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Έλεγχος για νέα αιτήματα SQL
                        Logger.Info($"Checking for pending queries at {_relayServerUrl}/queries/pending");
                        var response = await httpClient.GetAsync($"{_relayServerUrl}/queries/pending", cancellationToken);
                        Logger.Info($"Response status: {response.StatusCode}");

                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            var pendingQueries = JsonConvert.DeserializeObject<PendingQuery[]>(content);

                            if (pendingQueries != null && pendingQueries.Length > 0)
                            {
                                Logger.Info($"Found {pendingQueries.Length} pending queries");

                                foreach (var query in pendingQueries)
                                {
                                    try
                                    {
                                        // Εκτέλεση του SQL query
                                        var result = await ExecuteSqlQuery(query.Query, query.Parameters);

                                        // Αποστολή των αποτελεσμάτων πίσω στον relay server
                                        await SendQueryResult(httpClient, query.Id, result, null, cancellationToken);

                                        Logger.Info($"Successfully executed query ID: {query.Id}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error(ex, $"Error executing query ID: {query.Id}");
                                        await SendQueryResult(httpClient, query.Id, null, ex.Message, cancellationToken);
                                    }
                                }
                            }
                            else
                            {
                                Logger.Debug("No pending queries found");
                            }
                        }
                        else
                        {
                            Logger.Warn($"Failed to check for pending queries. Status: {response.StatusCode}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Κανονική διακοπή - αγνοούμε
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error in polling loop");
                    }

                    // Αναμονή πριν τον επόμενο έλεγχο
                    await Task.Delay(_pollingIntervalMs, cancellationToken);
                }
            }

            Logger.Info("Polling stopped");
        }

        private async Task<string> ExecuteSqlQuery(string query, object parameters)
        {
            Logger.Debug($"Executing SQL query: {query}");

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand(query, connection))
                {
                    // Προσθήκη παραμέτρων αν υπάρχουν
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

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        // Μετατροπή των αποτελεσμάτων σε JSON
                        var dt = new DataTable();
                        dt.Load(reader);
                        return JsonConvert.SerializeObject(dt);
                    }
                }
            }
        }

        private async Task SendQueryResult(
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

            await httpClient.PostAsync($"{_relayServerUrl}/queries/result", content, cancellationToken);
        }

        // Προσθέστε αυτές τις μεθόδους στην κλάση SQLTunnelService
        public void Start(string[] args)
        {
            OnStart(args);
        }

        public void Stop()
        {
            OnStop();
        }

   

        private void InitializeComponent()
        {

        }
    }

    public class PendingQuery
    {
        public string Id { get; set; }
        public string Query { get; set; }
        public object Parameters { get; set; }
    }
}