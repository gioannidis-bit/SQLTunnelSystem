using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;

namespace SqlRelayServer
{
    // Αυτή η κλάση επεκτείνει τον SqlRelayServer με λειτουργικότητα TDS Listener
    public class TdsListener : IHostedService
    {
        private readonly ILogger<TdsListener> _logger;
        private readonly TdsSettings _settings;
        private readonly IHttpClientFactory _httpClientFactory;
        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentDictionary<string, TdsClientSession> _activeSessions = new ConcurrentDictionary<string, TdsClientSession>();
        private readonly ConcurrentDictionary<string, string> _queryResults = new ConcurrentDictionary<string, string>();

        // Μια αναφορά στο ConcurrentDictionary του SqlRelayServer που περιέχει τα εγγεγραμμένα SQLTunnelServices
        private readonly ConcurrentDictionary<string, ServiceInfo> _registeredServices;

        public TdsListener(
            ILogger<TdsListener> logger,
            IOptions<TdsSettings> settings,
            IHttpClientFactory httpClientFactory,
            ConcurrentDictionary<string, ServiceInfo> registeredServices)
        {
            _logger = logger;
            _settings = settings.Value;
            _httpClientFactory = httpClientFactory;
            _registeredServices = registeredServices;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting TDS Listener on {Address}:{Port}",
                _settings.ListenAddress, _settings.ListenPort);

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Δημιουργία του TCP listener
            _listener = new TcpListener(IPAddress.Parse(_settings.ListenAddress), _settings.ListenPort);
            _listener.Start();

            // Ξεκίνημα του task που περιμένει για συνδέσεις
            _ = AcceptConnectionsAsync(_cancellationTokenSource.Token);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping TDS Listener");

            _cancellationTokenSource?.Cancel();

            // Κλείσιμο όλων των ενεργών συνεδριών
            foreach (var session in _activeSessions.Values)
            {
                try
                {
                    session.TcpClient?.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing client connection");
                }
            }

            _listener?.Stop();

            return Task.CompletedTask;
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Αποδοχή νέας σύνδεσης
                    var client = await _listener.AcceptTcpClientAsync();

                    var clientEndPoint = client.Client.RemoteEndPoint;
                    var sessionId = Guid.NewGuid().ToString();

                    _logger.LogInformation("New client connected from {EndPoint}, assigned session ID: {SessionId}",
                        clientEndPoint, sessionId);

                    // Δημιουργία νέας συνεδρίας
                    var session = new TdsClientSession
                    {
                        SessionId = sessionId,
                        TcpClient = client,
                        ClientEndPoint = clientEndPoint.ToString(),
                        CreatedAt = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow
                    };

                    _activeSessions.TryAdd(sessionId, session);

                    // Επεξεργασία της σύνδεσης σε ξεχωριστό task
                    _ = ProcessClientSessionAsync(session, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Κανονικός τερματισμός
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in connection acceptance loop");
            }
        }

        private async Task ProcessClientSessionAsync(TdsClientSession session, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Starting processing for client session {SessionId} from {ClientEndPoint}",
                    session.SessionId, session.ClientEndPoint);

                using (session.TcpClient)
                {
                    var stream = session.TcpClient.GetStream();
                    var buffer = new byte[8192];

                    // Βρόχος επεξεργασίας των TDS πακέτων
                    while (!cancellationToken.IsCancellationRequested && session.TcpClient.Connected)
                    {
                        _logger.LogDebug("Waiting for data from client {SessionId}", session.SessionId);

                        // Ρύθμιση timeout ανάγνωσης
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        var timeoutTask = Task.Delay(30000, cancellationToken); // 30 δευτερόλεπτα timeout

                        var completedTask = await Task.WhenAny(readTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                            _logger.LogWarning("Read timeout for session {SessionId}", session.SessionId);
                            break; // Τερματισμός λόγω timeout
                        }

                        var bytesRead = await readTask;

                        if (bytesRead == 0)
                        {
                            _logger.LogInformation("Client disconnected for session {SessionId}", session.SessionId);
                            break; // Η σύνδεση έκλεισε
                        }

                        session.LastActivity = DateTime.UtcNow;

                        // Αποκωδικοποίηση του TDS πακέτου
                        var tdspacket = new byte[bytesRead];
                        Array.Copy(buffer, tdspacket, bytesRead);

                        _logger.LogDebug("Received {BytesRead} bytes from client {SessionId}", bytesRead, session.SessionId);

                        // Dump του πακέτου για debugging (προαιρετικά)
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Packet dump: {PacketHex}", BitConverter.ToString(tdspacket));
                        }

                        var result = await ProcessTdsPacketAsync(session, tdspacket, cancellationToken);

                        if (result != null && result.Length > 0)
                        {
                            _logger.LogDebug("Sending response of {ResponseLength} bytes to client {SessionId}",
                                result.Length, session.SessionId);

                            // Dump της απάντησης για debugging (προαιρετικά)
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Response dump: {ResponseHex}", BitConverter.ToString(result));
                            }

                            await stream.WriteAsync(result, 0, result.Length, cancellationToken);
                            await stream.FlushAsync(cancellationToken); // Εξασφάλιση ότι τα δεδομένα αποστέλλονται αμέσως
                        }
                        else
                        {
                            _logger.LogDebug("No response to send for session {SessionId}", session.SessionId);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Session {SessionId} processing canceled", session.SessionId);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO Error processing client session {SessionId}: {ErrorMessage}",
                    session.SessionId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing client session {SessionId}: {ErrorMessage}",
                    session.SessionId, ex.Message);
            }
            finally
            {
                // Αφαίρεση της συνεδρίας από τις ενεργές
                _activeSessions.TryRemove(session.SessionId, out _);
                _logger.LogInformation("Client session {SessionId} ended", session.SessionId);
            }
        }

        private async Task<byte[]> ProcessTdsPacketAsync(TdsClientSession session, byte[] packet, CancellationToken cancellationToken)
        {
            try
            {
                if (packet == null || packet.Length < 8)
                    return null;

                byte packetType = packet[0];

                // Χειρισμός διαφορετικών τύπων πακέτων
                switch (packetType)
                {
                    case 0x12: // PreLogin
                        _logger.LogDebug("Received PreLogin packet from {ClientEndPoint}", session.ClientEndPoint);
                        // Δημιουργία έγκυρης απάντησης PreLogin
                        return CreateDetailedPreLoginResponse();

                    case 0x10: // Login
                        _logger.LogInformation("Received Login packet from {ClientEndPoint}", session.ClientEndPoint);
                        return CreateDetailedLoginResponse();

                    case 0x01: // SQL Batch
                        string sqlQuery = TdsProtocolHelper.DecodeTdsPacket(packet);

                        if (string.IsNullOrEmpty(sqlQuery))
                            return null;

                        _logger.LogInformation("Received SQL query from {ClientEndPoint}: {Query}",
                            session.ClientEndPoint, sqlQuery);

                        // Εκτέλεση του SQL query
                        string jsonResult = await ExecuteSqlQueryAsync(session, sqlQuery, cancellationToken);

                        // Μετατροπή του αποτελέσματος σε TDS πακέτο
                        return TdsProtocolHelper.EncodeTdsResult(jsonResult);

                    default:
                        _logger.LogWarning("Received unknown packet type {PacketType} from {ClientEndPoint}",
                            packetType, session.ClientEndPoint);
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing TDS packet from {ClientEndPoint}", session.ClientEndPoint);
                return TdsProtocolHelper.EncodeTdsError($"Internal error: {ex.Message}");
            }
        }

        private byte[] CreateDetailedPreLoginResponse()
        {
            // Η απάντηση PreLogin πρέπει να ακολουθεί συγκεκριμένη μορφή
            // Ref: https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-tds/60f56408-0188-4cd5-8b90-25c6f2423868

            // Δημιουργία του token stream με τις απαντήσεις για κάθε option
            var options = new List<byte[]>();

            // VERSION (option 0x00)
            byte[] versionOption = new byte[] {
        0x00, // OPTION_TOKEN = VERSION
        0x00, 0x08, // Offset (θα συμπληρωθεί αργότερα)
        0x00, 0x04  // Length = 4 bytes
    };
            byte[] versionValue = new byte[] {
        0x0C, // Major Version = 12 (SQL Server 2014)
        0x00, // Minor Version = 0
        0x07, 0xD0 // Build Number = 2000
    };
            options.Add(versionOption);

            // ENCRYPTION (option 0x01)
            byte[] encryptionOption = new byte[] {
        0x01, // OPTION_TOKEN = ENCRYPTION
        0x00, 0x0C, // Offset (θα συμπληρωθεί αργότερα)
        0x00, 0x01  // Length = 1 byte
    };
            byte[] encryptionValue = new byte[] {
        0x02 // ENCRYPT_NOT_SUP (no encryption)
    };
            options.Add(encryptionOption);

            // INSTANCE (option 0x02) - Προαιρετικό, το παραλείπουμε

            // THREADID (option 0x03)
            byte[] threadidOption = new byte[] {
        0x03, // OPTION_TOKEN = THREADID
        0x00, 0x0D, // Offset (θα συμπληρωθεί αργότερα)
        0x00, 0x04  // Length = 4 bytes
    };
            byte[] threadidValue = new byte[] {
        0x00, 0x00, 0x00, 0x00 // ThreadID = 0
    };
            options.Add(threadidOption);

            // MARS (option 0x04)
            byte[] marsOption = new byte[] {
        0x04, // OPTION_TOKEN = MARS
        0x00, 0x11, // Offset (θα συμπληρωθεί αργότερα)
        0x00, 0x01  // Length = 1 byte
    };
            byte[] marsValue = new byte[] {
        0x00 // MARS_DISABLED
    };
            options.Add(marsOption);

            // TERMINATOR (option 0xFF)
            byte[] terminatorOption = new byte[] {
        0xFF // OPTION_TOKEN = TERMINATOR
    };
            options.Add(terminatorOption);

            // Υπολογισμός των offsets
            int currentOffset = 0;

            // Υπολογισμός του συνολικού μεγέθους για τα options
            foreach (var option in options)
            {
                currentOffset += option.Length;
            }

            // Υπολογισμός των offsets για τα values
            int valueOffset = currentOffset;
            int i = 0;
            foreach (var option in options)
            {
                if (option[0] != 0xFF) // Παραλείπουμε το TERMINATOR
                {
                    // Ενημέρωση του offset στο option
                    option[1] = (byte)((valueOffset >> 8) & 0xFF);
                    option[2] = (byte)(valueOffset & 0xFF);

                    // Ενημέρωση του valueOffset για το επόμενο option
                    switch (option[0])
                    {
                        case 0x00: // VERSION
                            valueOffset += versionValue.Length;
                            break;
                        case 0x01: // ENCRYPTION
                            valueOffset += encryptionValue.Length;
                            break;
                        case 0x03: // THREADID
                            valueOffset += threadidValue.Length;
                            break;
                        case 0x04: // MARS
                            valueOffset += marsValue.Length;
                            break;
                    }
                }

                i++;
            }

            // Συνολικό μέγεθος του πακέτου
            int totalLength = 8 + // TDS Header
                             currentOffset + // Options
                             versionValue.Length +
                             encryptionValue.Length +
                             threadidValue.Length +
                             marsValue.Length;

            // Δημιουργία του συνολικού πακέτου
            byte[] response = new byte[totalLength];

            // TDS Header
            response[0] = 0x04; // Type: Tabular Result
            response[1] = 0x01; // Status: EOM (End of Message)
            BitConverter.GetBytes((ushort)totalLength).CopyTo(response, 2); // Length
            BitConverter.GetBytes((ushort)1).CopyTo(response, 4); // SPID = 1
            response[6] = 1; // PacketID = 1
            response[7] = 0; // Window = 0

            // Αντιγραφή των options
            int offset = 8;
            foreach (var option in options)
            {
                Buffer.BlockCopy(option, 0, response, offset, option.Length);
                offset += option.Length;
            }

            // Αντιγραφή των values
            Buffer.BlockCopy(versionValue, 0, response, offset, versionValue.Length);
            offset += versionValue.Length;

            Buffer.BlockCopy(encryptionValue, 0, response, offset, encryptionValue.Length);
            offset += encryptionValue.Length;

            Buffer.BlockCopy(threadidValue, 0, response, offset, threadidValue.Length);
            offset += threadidValue.Length;

            Buffer.BlockCopy(marsValue, 0, response, offset, marsValue.Length);

            return response;
        }

        // Πιο λεπτομερής απάντηση Login που ακολουθεί το πρωτόκολλο TDS
        private byte[] CreateDetailedLoginResponse()
        {
            // Δημιουργία ενός απλοποιημένου LOGIN_ACK πακέτου
            // Ref: https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-tds/5773c946-cf15-42b2-8556-7eb041513442

            // Το όνομα του server (προσομοιωμένο)
            string serverName = "SQL Relay Server";
            byte[] serverNameBytes = Encoding.Unicode.GetBytes(serverName);

            // Υπολογισμός του συνολικού μεγέθους
            int totalLength = 8 + // TDS Header
                             1 + // Token Type
                             2 + // Length
                             1 + // Interface
                             4 + // TDS Version
                             1 + // Server Version Length
                             serverNameBytes.Length; // Server Name

            byte[] response = new byte[totalLength];

            // TDS Header
            response[0] = 0x04; // Type: Tabular Result
            response[1] = 0x01; // Status: EOM (End of Message)
            BitConverter.GetBytes((ushort)totalLength).CopyTo(response, 2); // Length
            BitConverter.GetBytes((ushort)1).CopyTo(response, 4); // SPID = 1
            response[6] = 1; // PacketID = 1
            response[7] = 0; // Window = 0

            // LOGIN_ACK Token
            response[8] = 0xAD; // Token Type: LOGIN_ACK

            // Token Length (2 bytes) - Μήκος των υπόλοιπων δεδομένων
            int tokenLength = 1 + // Interface
                             4 + // TDS Version
                             1 + // Server Version Length
                             serverNameBytes.Length; // Server Name
            BitConverter.GetBytes((ushort)tokenLength).CopyTo(response, 9);

            // Interface
            response[11] = 0x00; // SQL Server

            // TDS Version (4 bytes)
            response[12] = 0x74; // TDS 7.4 (SQL Server 2012/2014)
            response[13] = 0x00;
            response[14] = 0x00;
            response[15] = 0x00;

            // Server Version Length (1 byte)
            response[16] = (byte)(serverNameBytes.Length / 2); // Unicode characters count

            // Server Name (variable)
            Buffer.BlockCopy(serverNameBytes, 0, response, 17, serverNameBytes.Length);

            return response;
        }


        private async Task<string> ExecuteSqlQueryAsync(TdsClientSession session, string sqlQuery, CancellationToken cancellationToken)
        {
            // Δημιουργία ενός μοναδικού ID για αυτό το query
            string queryId = Guid.NewGuid().ToString();

            // Εύρεση ενός διαθέσιμου SQLTunnelService
            var availableServices = _registeredServices.Values
                .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                .ToList();

            if (availableServices.Count == 0)
            {
                _logger.LogError("No SQL Tunnel Services available");
                throw new InvalidOperationException("No SQL services currently available");
            }

            // Επιλογή του πρώτου διαθέσιμου service
            var targetService = availableServices.First();

            // Δημιουργία του αιτήματος
            var sqlRequest = new
            {
                Id = queryId,
                Query = sqlQuery,
                Parameters = new object(), // Κενό object για τώρα, θα μπορούσε να εξαχθεί από το TDS πακέτο
                ServiceId = targetService.ServiceId
            };

            // Δημιουργία HTTP client για επικοινωνία με το εσωτερικό API του SqlRelayServer
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_settings.RelayInternalUrl);

            // Αποστολή του query
            var content = new StringContent(
                JsonConvert.SerializeObject(sqlRequest),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync("/api/sql/execute", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Error executing SQL query: {Error}", errorContent);
                throw new Exception($"Error executing SQL query: {errorContent}");
            }

            // Ανάγνωση του αποτελέσματος
            var jsonResult = await response.Content.ReadAsStringAsync(cancellationToken);
            return jsonResult;
        }
    }

    public class TdsClientSession
    {
        public string SessionId { get; set; }
        public TcpClient TcpClient { get; set; }
        public string ClientEndPoint { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivity { get; set; }
    }

    public class TdsSettings
    {
        public string ListenAddress { get; set; } = "0.0.0.0";
        public int ListenPort { get; set; } = 11433; // Προεπιλεγμένο port για SQL Server
        public string RelayInternalUrl { get; set; } = "http://localhost:5175";
    }
}