using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http;

namespace SqlRelayServer
{
    public class SimpleTdsListener : IHostedService
    {
        private readonly ILogger<SimpleTdsListener> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _apiKey;
        private readonly string _relayUrl;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public SimpleTdsListener(
            ILogger<SimpleTdsListener> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<SimpleTdsSettings> settings)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _apiKey = settings.Value.ApiKey;
            _relayUrl = settings.Value.RelayInternalUrl;

            _logger.LogInformation("Creating Simple TDS Listener with relay URL: {RelayUrl}", _relayUrl);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Simple TDS Listener on port 11433");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Δημιουργία TCP listener στη θύρα 11433
            _listener = new TcpListener(IPAddress.Any, 11433);
            _listener.Start();

            // Εκκίνηση του task για αποδοχή συνδέσεων
            _ = AcceptConnectionsAsync(_cts.Token);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Simple TDS Listener");

            _cts?.Cancel();
            _listener?.Stop();

            return Task.CompletedTask;
        }

        private async Task AcceptConnectionsAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync();

                    _logger.LogInformation("New client connected from {EndPoint}",
                        client.Client.RemoteEndPoint);

                    // Επεξεργασία κάθε πελάτη σε ξεχωριστό task
                    _ = ProcessClientAsync(client, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Κανονικός τερματισμός
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connections");
            }
        }

        private async Task ProcessClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];

                    // 1. Αναμονή για PreLogin πακέτο
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                    if (bytesRead == 0)
                    {
                        _logger.LogInformation("Client disconnected before sending data");
                        return;
                    }

                    // Καταγραφή του πακέτου
                    _logger.LogDebug("Received {BytesRead} bytes: {PacketHex}",
                        bytesRead, BitConverter.ToString(buffer, 0, Math.Min(bytesRead, 64)));

                    // Έλεγχος αν είναι PreLogin πακέτο (type = 0x12)
                    if (buffer[0] == 0x12)
                    {
                        _logger.LogInformation("Received PreLogin packet");

                        // Αποστολή σταθερής απάντησης PreLogin (δοκιμασμένη από την εφαρμογή TDS Test)
                        byte[] preLoginResponse = new byte[] {
                            0x04, 0x01, 0x00, 0x1B, 0x00, 0x00, 0x01, 0x00, // Header
                            0x00, 0x00, 0x0B, 0x00, 0x06, 0x01, 0x00, 0x11, // Version token
                            0xFF, 0x08, 0x00, 0x01, 0x55, 0x00, 0x00, 0x00  // Terminator, Version value, Encryption
                        };

                        _logger.LogDebug("Sending PreLogin response: {ResponseHex}",
                            BitConverter.ToString(preLoginResponse));

                        await stream.WriteAsync(preLoginResponse, 0, preLoginResponse.Length, token);
                        await stream.FlushAsync(token);

                        // 2. Αναμονή για Login πακέτο
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                        if (bytesRead == 0)
                        {
                            _logger.LogInformation("Client disconnected after PreLogin");
                            return;
                        }

                        // Καταγραφή του πακέτου
                        _logger.LogDebug("Received {BytesRead} bytes: {PacketHex}",
                            bytesRead, BitConverter.ToString(buffer, 0, Math.Min(bytesRead, 64)));

                        // Έλεγχος αν είναι Login πακέτο (type = 0x10)
                        if (buffer[0] == 0x10)
                        {
                            _logger.LogInformation("Received Login packet");

                            // Αποστολή σταθερής απάντησης Login (δοκιμασμένη από την εφαρμογή TDS Test)
                            byte[] loginResponse = new byte[] {
                                0x04, 0x01, 0x00, 0x27, 0x00, 0x00, 0x01, 0x00, // Header
                                0xAD, 0x00, 0x16, 0x00, 0x04, 0x00, 0x00, 0x00, // LOGIN_ACK token
                                0x0D, 0x53, 0x00, 0x51, 0x00, 0x4C, 0x00, 0x20, // "SQL " (Unicode)
                                0x00, 0x53, 0x00, 0x65, 0x00, 0x72, 0x00, 0x76, // "Serv" (Unicode)
                                0x00, 0x65, 0x00, 0x72, 0x00, 0xFD, 0x00, 0x00, // "er" + DONE token
                                0x00, 0x00, 0x00, 0x00, 0x00, 0x00             // DONE token data
                            };

                            _logger.LogDebug("Sending Login response: {ResponseHex}",
                                BitConverter.ToString(loginResponse));

                            await stream.WriteAsync(loginResponse, 0, loginResponse.Length, token);
                            await stream.FlushAsync(token);

                            // 3. Αναμονή για SQL Batch πακέτα
                            while (!token.IsCancellationRequested && client.Connected)
                            {
                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                                if (bytesRead == 0)
                                {
                                    _logger.LogInformation("Client disconnected after Login");
                                    return;
                                }

                                // Καταγραφή του πακέτου
                                _logger.LogDebug("Received {BytesRead} bytes: {PacketHex}",
                                    bytesRead, BitConverter.ToString(buffer, 0, Math.Min(bytesRead, 64)));

                                // Έλεγχος αν είναι SQL Batch πακέτο (type = 0x01)
                                if (buffer[0] == 0x01)
                                {
                                    _logger.LogInformation("Received SQL Batch packet");

                                    // Εξαγωγή του SQL ερωτήματος
                                    string sqlQuery = ExtractSqlQuery(buffer, bytesRead);

                                    if (!string.IsNullOrEmpty(sqlQuery))
                                    {
                                        _logger.LogInformation("Extracted SQL query: {Query}", sqlQuery);

                                        try
                                        {
                                            // Εκτέλεση του ερωτήματος μέσω του HTTP API
                                            string result = await ExecuteSqlQueryAsync(sqlQuery, token);

                                            _logger.LogInformation("Query result: {Result}",
                                                result.Length > 100 ? result.Substring(0, 100) + "..." : result);

                                            // Αποστολή απάντησης DONE (επιτυχία)
                                            byte[] doneResponse = new byte[] {
                                                0x04, 0x01, 0x00, 0x0D, 0x00, 0x00, 0x01, 0x00, // Header
                                                0xFD, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, // DONE token (with DONE_COUNT)
                                                0x00
                                            };

                                            _logger.LogDebug("Sending DONE response: {ResponseHex}",
                                                BitConverter.ToString(doneResponse));

                                            await stream.WriteAsync(doneResponse, 0, doneResponse.Length, token);
                                            await stream.FlushAsync(token);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Error executing SQL query");

                                            // Αποστολή απάντησης ERROR
                                            byte[] errorResponse = new byte[] {
                                                0x04, 0x01, 0x00, 0x0D, 0x00, 0x00, 0x01, 0x00, // Header
                                                0xFD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // DONE token (with error status)
                                                0x00
                                            };

                                            await stream.WriteAsync(errorResponse, 0, errorResponse.Length, token);
                                            await stream.FlushAsync(token);
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Failed to extract SQL query");

                                        // Αποστολή απάντησης ERROR
                                        byte[] errorResponse = new byte[] {
                                            0x04, 0x01, 0x00, 0x0D, 0x00, 0x00, 0x01, 0x00, // Header
                                            0xFD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // DONE token (with error status)
                                            0x00
                                        };

                                        await stream.WriteAsync(errorResponse, 0, errorResponse.Length, token);
                                        await stream.FlushAsync(token);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Received unknown packet type: {PacketType}", buffer[0]);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Expected Login packet, but received packet type {PacketType}", buffer[0]);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Expected PreLogin packet, but received packet type {PacketType}", buffer[0]);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Κανονικός τερματισμός
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing client");
                }
            }
        }

        private string ExtractSqlQuery(byte[] buffer, int bytesRead)
        {
            try
            {
                // Για SQL Batch πακέτα, το ερώτημα συνήθως ξεκινά μετά από 8 bytes επικεφαλίδα
                // και 4 bytes transaction descriptor
                int offset = 12;

                // Εξασφάλιση ότι υπάρχουν αρκετά bytes
                if (bytesRead <= offset)
                    return null;

                // Δοκιμή με Unicode (UTF-16 LE)
                try
                {
                    string query = Encoding.Unicode.GetString(buffer, offset, bytesRead - offset);
                    query = query.TrimEnd('\0').Trim();

                    if (!string.IsNullOrWhiteSpace(query))
                        return query;
                }
                catch { }

                // Εναλλακτικά, δοκιμή με διαφορετικό offset (8)
                try
                {
                    string query = Encoding.Unicode.GetString(buffer, 8, bytesRead - 8);
                    query = query.TrimEnd('\0').Trim();

                    if (!string.IsNullOrWhiteSpace(query))
                        return query;
                }
                catch { }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting SQL query");
                return null;
            }
        }

        private async Task<string> ExecuteSqlQueryAsync(string sqlQuery, CancellationToken token)
        {
            try
            {
                // Δημιουργία HttpClient
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(_relayUrl);
                client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);

                // Δημιουργία αιτήματος
                var request = new
                {
                    Id = Guid.NewGuid().ToString(),
                    Query = sqlQuery,
                    Parameters = new { }
                };

                var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");

                // Αποστολή αιτήματος
                var response = await client.PostAsync("/api/sql/execute", content, token);

                // Έλεγχος απάντησης
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(token);
                    throw new Exception($"SQL execution failed: {response.StatusCode}, {errorContent}");
                }

                // Ανάγνωση αποτελέσματος
                return await response.Content.ReadAsStringAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing SQL query through HTTP API");
                throw;
            }
        }
    }

  

    public class SimpleTdsSettings
    {
        public string ListenAddress { get; set; } = "0.0.0.0";
        public int ListenPort { get; set; } = 11433; // Προεπιλεγμένο port για SQL Server
        public string RelayInternalUrl { get; set; } = "http://localhost:5175";
        public string ApiKey { get; set; } = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";
    }
}