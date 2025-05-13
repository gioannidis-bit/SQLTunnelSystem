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
        private readonly string _displayName;
        private readonly string _description;
        private readonly string _version;
        private readonly string _serverInfo;

        private readonly ServiceSettings _settings;

        public SQLTunnelService()
        {
            // Ρυθμίσεις του service
            ServiceName = "SQLTunnelService";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;

            // Φόρτωση ρυθμίσεων από JSON αρχείο
            _settings = LoadSettings();

            // Ορισμός βασικών ιδιοτήτων
            _relayServerUrl = _settings.RelayServerUrl;
            _serviceId = _settings.ServiceId;
            _secretKey = _settings.SecretKey;
            _connectionString = _settings.SqlConnectionString;
            _pollingIntervalMs = _settings.PollingIntervalMs;
            _displayName = _settings.DisplayName;
            _description = _settings.Description;
            _version = _settings.Version;

            // Λήψη πληροφοριών για τον SQL Server
            _serverInfo = GetSqlServerInfo();

            ConfigureLogging();
        }

        private ServiceSettings LoadSettings()
        {
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    return JsonConvert.DeserializeObject<ServiceSettings>(json) ?? CreateDefaultSettings();
                }
                catch (Exception ex)
                {
                    // Σε περίπτωση σφάλματος, δημιουργούμε προεπιλεγμένες ρυθμίσεις
                    Console.WriteLine($"Error loading settings: {ex.Message}");
                    return CreateDefaultSettings();
                }
            }
            else
            {
                // Αν δεν υπάρχει το αρχείο, δημιουργούμε προεπιλεγμένες ρυθμίσεις
                var defaultSettings = CreateDefaultSettings();
                try
                {
                    // Αποθηκεύουμε τις προεπιλεγμένες ρυθμίσεις για μελλοντική χρήση
                    string json = JsonConvert.SerializeObject(defaultSettings, Formatting.Indented);
                    File.WriteAllText(settingsPath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving default settings: {ex.Message}");
                }
                return defaultSettings;
            }
        }

        private ServiceSettings CreateDefaultSettings()
        {
            return new ServiceSettings
            {
                RelayServerUrl = "http://192.168.14.121:5175/api",
                ServiceId = "sql-service-01",
                SecretKey = @"\ql4CkI!{sI\W[*_1x]{A+Gw[vw+A\ti",
                SqlConnectionString = "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrongPassword;",
                PollingIntervalMs = 5000,
                DisplayName = "SQL Tunnel Service",
                Description = "SQL Server Tunnel",
                Version = "1.0.0"
            };
        }

        // Κλάση για τις ρυθμίσεις
        public class ServiceSettings
        {
            public string RelayServerUrl { get; set; }
            public string ServiceId { get; set; }
            public string SecretKey { get; set; }
            public string SqlConnectionString { get; set; }
            public int PollingIntervalMs { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string Version { get; set; }
        }

        private string GetSqlServerInfo()
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = new SqlCommand("SELECT @@VERSION", connection))
                    {
                        return command.ExecuteScalar()?.ToString() ?? "Unknown";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get SQL Server info");
                return "Unknown";
            }
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

        private async Task SendHeartbeat(HttpClient httpClient, CancellationToken cancellationToken)
        {
            try
            {
                var heartbeatData = new
                {
                    DisplayName = _displayName,
                    Description = _description,
                    Version = _version,
                    ServerInfo = _serverInfo
                };

                var json = JsonConvert.SerializeObject(heartbeatData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                Logger.Info($"Sending heartbeat to {_relayServerUrl}/services/heartbeat with data: {json}");

                var response = await httpClient.PostAsync($"{_relayServerUrl}/services/heartbeat", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Debug("Heartbeat sent successfully");
                }
                else
                {
                    Logger.Warn($"Failed to send heartbeat. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error sending heartbeat");
            }
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

                // Αρχικό heartbeat
                await SendHeartbeat(httpClient, cancellationToken);

                var lastHeartbeatTime = DateTime.UtcNow;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Στέλνουμε heartbeat κάθε 30 δευτερόλεπτα
                        if ((DateTime.UtcNow - lastHeartbeatTime).TotalSeconds >= 30)
                        {
                            await SendHeartbeat(httpClient, cancellationToken);
                            lastHeartbeatTime = DateTime.UtcNow;
                        }


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