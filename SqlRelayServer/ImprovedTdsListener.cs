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
using System.IO;
using System.Data;

namespace SqlRelayServer
{
    public class ImprovedTdsListener : IHostedService
    {
        private readonly ILogger<ImprovedTdsListener> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _apiKey;
        private readonly string _relayUrl;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<string, ServiceInfo> _registeredServices;

        public ImprovedTdsListener(
            ILogger<ImprovedTdsListener> logger,
            IHttpClientFactory httpClientFactory,
            IOptions<SimpleTdsSettings> settings,
            ConcurrentDictionary<string, ServiceInfo> registeredServices)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _apiKey = settings.Value.ApiKey;
            _relayUrl = settings.Value.RelayInternalUrl;
            _registeredServices = registeredServices;

            _logger.LogInformation("Creating Improved TDS Listener with relay URL: {RelayUrl}", _relayUrl);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Improved TDS Listener on port 11433");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Create TCP listener on port 11433
            _listener = new TcpListener(IPAddress.Any, 11433);
            _listener.Start();

            // Start accepting connections
            _ = AcceptConnectionsAsync(_cts.Token);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Improved TDS Listener");

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

                    string clientEndpoint = client.Client.RemoteEndPoint.ToString();
                    _logger.LogInformation("New client connected from {EndPoint}", clientEndpoint);

                    // Process each client in a separate task
                    _ = ProcessClientAsync(client, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination
                _logger.LogInformation("TDS Listener stopped accepting connections");
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
                    var connectionId = Guid.NewGuid();

                    // We'll use this to store the TDS conversation state
                    var sessionState = new TdsSessionState
                    {
                        ConnectionId = connectionId,
                        ClientEndpoint = client.Client.RemoteEndPoint.ToString()
                    };

                    _logger.LogInformation("Connection {ConnectionId}: Starting TDS conversation", connectionId);

                    // TDS Protocol Negotiation
                    if (!await HandlePreLoginPhase(stream, sessionState, token))
                    {
                        _logger.LogWarning("Connection {ConnectionId}: PRELOGIN phase failed", connectionId);
                        return;
                    }

                    _logger.LogInformation("Connection {ConnectionId}: PRELOGIN phase completed successfully", connectionId);

                    // Login phase
                    if (!await HandleLoginPhase(stream, sessionState, token))
                    {
                        _logger.LogWarning("Connection {ConnectionId}: LOGIN phase failed", connectionId);
                        return;
                    }

                    _logger.LogInformation("Connection {ConnectionId}: LOGIN phase completed successfully", connectionId);

                    // Process SQL batches
                    await ProcessSqlBatchesAsync(stream, sessionState, token);

                    // Μετά το LOGIN phase
                    _logger.LogInformation("Connection {ConnectionId}: Waiting for SQL batch...", connectionId);
                    var nextPacket = await ReadTdsPacketAsync(stream, token);
                    _logger.LogInformation("Connection {ConnectionId}: Received packet type: 0x{Type:X2}, Length: {Length}",
                        connectionId, nextPacket?.Type ?? 0, nextPacket?.Length ?? 0);

                }
                catch (IOException ex)
                {
                    // This is often just a client disconnection
                    _logger.LogInformation("Client connection closed: {Message}", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing client");
                }
            }
        }

        private async Task<bool> HandlePreLoginPhase(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            try
            {
                Console.WriteLine("\n=== PRELOGIN PHASE ===\n");

                // Read the PRELOGIN packet
                var preLoginPacket = await ReadTdsPacketAsync(stream, token);
                if (preLoginPacket == null || preLoginPacket.Type != 0x12)
                {
                    Console.WriteLine($"Expected PRELOGIN packet (0x12), received: {preLoginPacket?.Type.ToString("X2") ?? "null"}");
                    return false;
                }

                // Parse client PRELOGIN options
                var clientOptions = ParsePreLoginOptions(preLoginPacket.Data);

                // Build PRELOGIN response with ENCRYPT_OFF
                byte[] responseData = BuildPreLoginResponse(clientOptions);

                // Βρες και τροποποίησε την τιμή ENCRYPTION
              

             

                // Στείλε την απάντηση
                await SendTdsPacketAsync(stream, new TdsPacket
                {
                    Type = 0x12,    // ← PRELOGIN Response
                    Status = 0x01, // EOM
                    Spid = 0,
                    PacketId = 1,
                    Data = responseData
                }, token);

                Console.WriteLine("PRELOGIN phase completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during PRELOGIN phase: {ex.Message}");
                return false;
            }
        }

        public class TdsSessionState
        {
            public Guid ConnectionId { get; set; }
            public string ClientEndpoint { get; set; }
            public string ClientHostname { get; set; }
            public string Username { get; set; }
            public string Database { get; set; }
            public bool EncryptionRequired { get; set; } // Πρόσθεσε αυτή τη γραμμή
            public TdsPacket NextPacket { get; set; }
        }

        private async Task<bool> HandleLoginPhase(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            try
            {
                Console.WriteLine("\n=== LOGIN PHASE ===\n");

                Console.WriteLine("Waiting for LOGIN7 packet...");
                var loginPacket = await ReadWithTimeoutAsync(stream, 10000, token);

                if (loginPacket == null)
                {
                    Console.WriteLine("No LOGIN7 packet received (timeout)");
                    return false;
                }

                Console.WriteLine($"Received packet with type 0x{loginPacket.Type:X2} for LOGIN phase");

                // Αν είναι TLS πακέτο αντί για LOGIN7
                if (loginPacket.Type == 0x12)
                {
                    Console.WriteLine("Warning: Received TLS/PRELOGIN packet instead of LOGIN7!");
                    Console.WriteLine("Client might be trying to use encryption despite ENCRYPT_OFF");

                    // Στείλε καθαρό μήνυμα σφάλματος
                    await SendErrorResponseAsync(stream, session,
                        "This server requires connections with encryption disabled. " +
                        "Please use 'Encrypt=False' in your connection string.", token);
                    return false;
                }

                if (loginPacket.Type != 0x10) // LOGIN7
                {
                    Console.WriteLine($"Error: Expected LOGIN7 (0x10) packet, received 0x{loginPacket.Type:X2}");
                    return false;
                }

                // Extract login info
                Console.WriteLine("Extracting login information...");
                ExtractLoginInfo(loginPacket.Data, session);

                Console.WriteLine($"Client login info - Username: {session.Username}, Database: {session.Database}");

                // Build the response (with extra debugging)
                Console.WriteLine("Building LOGIN response...");
                byte[] loginResponse = BuildLoginResponse(session);

                Console.WriteLine($"LOGIN response full hex dump:");
                for (int i = 0; i < loginResponse.Length; i += 16)
                {
                    int len = Math.Min(16, loginResponse.Length - i);
                    Console.WriteLine($"{i:X4}: {BitConverter.ToString(loginResponse, i, len)}");
                }

                Console.WriteLine("Sending LOGIN response to client...");
                await SendTdsPacketAsync(stream, new TdsPacket
                {
                    Type = 0x04, // Tabular result
                    Status = 0x01, // EOM
                    Spid = 0,
                    PacketId = 1,
                    Data = loginResponse
                }, token);

                Console.WriteLine("LOGIN phase completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during LOGIN phase: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // Βοηθητική συνάρτηση για ανάγνωση με μεγαλύτερο timeout
        private async Task<TdsPacket> ReadWithTimeoutAsync(NetworkStream stream, int timeoutMs, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);

            try
            {
                return await ReadTdsPacketAsync(stream, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Read operation timed out after {TimeoutMs}ms", timeoutMs);
                return null;
            }
        }

        private async Task ProcessSqlBatchesAsync(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            try
            {
                Console.WriteLine("\n=== SQL BATCH PROCESSING ===\n");

                while (!token.IsCancellationRequested)
                {
                    Console.WriteLine("Waiting for SQL batch packet...");
                    var packet = await ReadTdsPacketAsync(stream, token);

                    if (packet == null)
                    {
                        Console.WriteLine("Client closed connection or timeout occurred");
                        break;
                    }

                    Console.WriteLine($"Received packet type: 0x{packet.Type:X2}, Length: {packet.Length}");

                    if (packet.Type == 0x01) // SQL Batch
                    {
                        string sqlQuery = ExtractSqlQuery(packet.Data);
                        Console.WriteLine($"SQL Query: {sqlQuery}");

                        if (!string.IsNullOrEmpty(sqlQuery))
                        {
                            try
                            {
                                Console.WriteLine("Executing SQL query through API...");
                                string result = await ExecuteSqlQueryAsync(sqlQuery, session, token);
                                Console.WriteLine($"API response: {(result?.Length > 100 ? result.Substring(0, 100) + "..." : result)}");

                                Console.WriteLine("Sending query results to client...");
                                await SendQueryResultsAsync(stream, session, result, token);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error executing query: {ex.Message}");
                                await SendErrorResponseAsync(stream, session, ex.Message, token);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Empty SQL query - sending empty result");
                            await SendEmptyResultAsync(stream, session, token);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Unexpected packet type: 0x{packet.Type:X2} - ignoring");
                        await SendEmptyResultAsync(stream, session, token);
                    }
                }

                Console.WriteLine("SQL batch processing ended");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SQL batch processing: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task SendQueryResultsAsync(NetworkStream stream, TdsSessionState session, string jsonResult, CancellationToken token)
        {
            try
            {
                _logger.LogDebug("Connection {ConnectionId}: Preparing to send result: {JsonLength} bytes",
                    session.ConnectionId, jsonResult?.Length ?? 0);

                // Αν δεν έχουμε αποτέλεσμα, στείλε ένα κενό αποτέλεσμα
                if (string.IsNullOrEmpty(jsonResult) || jsonResult == "[]" || jsonResult == "{}")
                {
                    await SendEmptyResultAsync(stream, session, token);
                    return;
                }

                // Προσπάθεια αποκωδικοποίησης JSON σε DataTable
                DataTable dataTable;
                try
                {
                    dataTable = JsonConvert.DeserializeObject<DataTable>(jsonResult);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Connection {ConnectionId}: Error deserializing JSON result", session.ConnectionId);
                    await SendErrorResponseAsync(stream, session, "Invalid result format", token);
                    return;
                }

                if (dataTable == null || dataTable.Columns.Count == 0)
                {
                    await SendEmptyResultAsync(stream, session, token);
                    return;
                }

                // Δημιουργία του TDS response με τα αποτελέσματα
                List<byte> responseData = new List<byte>();

                // COLMETADATA token (0x81)
                responseData.Add(0x81); // Token type

                // Count of columns (2 bytes)
                ushort columnCount = (ushort)dataTable.Columns.Count;
                responseData.Add((byte)(columnCount & 0xFF));       // LSB
                responseData.Add((byte)(columnCount >> 8));         // MSB

                // Για κάθε στήλη, πρόσθεσε μεταδεδομένα
                for (int i = 0; i < dataTable.Columns.Count; i++)
                {
                    DataColumn column = dataTable.Columns[i];

                    // UserType (2 bytes) - typically 0
                    responseData.Add(0x00);
                    responseData.Add(0x00);

                    // Flags (2 bytes)
                    responseData.Add(0x00);
                    responseData.Add(0x00);

                    // TYPE_INFO - Αναλόγως του τύπου δεδομένων
                    byte typeInfo = GetSqlTypeInfo(column.DataType);
                    responseData.Add(typeInfo);

                    // Πρόσθετες πληροφορίες τύπου, αναλόγως του τύπου
                    AddTypeSpecificInfo(responseData, column.DataType);

                    // Column name (Unicode string)
                    byte[] columnNameBytes = Encoding.Unicode.GetBytes(column.ColumnName);
                    responseData.Add((byte)columnNameBytes.Length); // Length
                    responseData.AddRange(columnNameBytes);
                }

                // ROW tokens - Για κάθε γραμμή δεδομένων
                foreach (DataRow row in dataTable.Rows)
                {
                    responseData.Add(0xD1); // ROW token

                    // Για κάθε κελί στη γραμμή
                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        object value = row[i];

                        if (value == DBNull.Value || value == null)
                        {
                            // NULL value
                            responseData.Add(0xFF); // NULL marker
                        }
                        else
                        {
                            // Encode value based on the column type
                            AddEncodedValue(responseData, value, dataTable.Columns[i].DataType);
                        }
                    }
                }

                // DONE token (0xFD)
                responseData.Add(0xFD);

                // Status (2 bytes) - 0x0001 = DONE_FINAL + 0x0010 = DONE_COUNT = 0x0011
                responseData.Add(0x11);
                responseData.Add(0x00);

                // CurCmd (2 bytes)
                responseData.Add(0x00);
                responseData.Add(0x00);

                // RowCount (8 bytes) - Number of rows affected
                long rowCount = dataTable.Rows.Count;
                byte[] rowCountBytes = BitConverter.GetBytes(rowCount);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(rowCountBytes);

                responseData.AddRange(rowCountBytes);

                // Διαίρεση του response σε πακέτα, αν είναι αναγκαίο
                const int maxPacketSize = 4096;
                for (int offset = 0; offset < responseData.Count; offset += maxPacketSize)
                {
                    int chunkSize = Math.Min(maxPacketSize, responseData.Count - offset);
                    byte[] chunk = new byte[chunkSize];
                    responseData.CopyTo(offset, chunk, 0, chunkSize);

                    // Έλεγχος αν είναι το τελευταίο πακέτο
                    bool isLastPacket = (offset + chunkSize >= responseData.Count);

                    await SendTdsPacketAsync(stream, new TdsPacket
                    {
                        Type = 0x04, // Tabular result
                        Status = isLastPacket ? (byte)0x01 : (byte)0x00, // EOM μόνο για το τελευταίο
                        Spid = 0,
                        PacketId = (byte)((offset / maxPacketSize) + 1), // Αύξηση του PacketId
                        Data = chunk
                    }, token);
                }

                _logger.LogInformation("Connection {ConnectionId}: Sent query results, {RowCount} rows, {TotalBytes} bytes",
                    session.ConnectionId, rowCount, responseData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error sending query results", session.ConnectionId);
                await SendErrorResponseAsync(stream, session, "Error processing results: " + ex.Message, token);
            }
        }

        private byte GetSqlTypeInfo(Type type)
        {
            // Mapping .NET types to SQL Server types
            if (type == typeof(string))
                return 0xE7; // NVARCHAR
            else if (type == typeof(int) || type == typeof(Int32))
                return 0x26; // INT
            else if (type == typeof(long) || type == typeof(Int64))
                return 0x6C; // BIGINT
            else if (type == typeof(short) || type == typeof(Int16))
                return 0x28; // SMALLINT
            else if (type == typeof(byte))
                return 0x30; // TINYINT
            else if (type == typeof(bool))
                return 0x68; // BIT
            else if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
                return 0x6F; // FLOAT
            else if (type == typeof(DateTime))
                return 0x6D; // DATETIME
            else if (type == typeof(Guid))
                return 0x24; // GUID
            else
                return 0xE7; // Default to NVARCHAR
        }

        private void AddTypeSpecificInfo(List<byte> buffer, Type type)
        {
            if (type == typeof(string))
            {
                // NVARCHAR
                buffer.Add(0xFF); // Max length
                buffer.Add(0xFF);
                buffer.Add(0x00); // Collation (simplified)
                buffer.Add(0x00);
                buffer.Add(0x00);
                buffer.Add(0x00);
                buffer.Add(0x00);
            }
            else if (type == typeof(int) || type == typeof(Int32) ||
                     type == typeof(long) || type == typeof(Int64) ||
                     type == typeof(short) || type == typeof(Int16) ||
                     type == typeof(byte) || type == typeof(bool) ||
                     type == typeof(decimal) || type == typeof(double) || type == typeof(float) ||
                     type == typeof(DateTime) || type == typeof(Guid))
            {
                // Αυτοί οι τύποι δεν έχουν πρόσθετες πληροφορίες
            }
            else
            {
                // Για άγνωστους τύπους, χρησιμοποιούμε NVARCHAR
                buffer.Add(0xFF); // Max length
                buffer.Add(0xFF);
                buffer.Add(0x00); // Collation (simplified)
                buffer.Add(0x00);
                buffer.Add(0x00);
                buffer.Add(0x00);
                buffer.Add(0x00);
            }
        }

        private void AddEncodedValue(List<byte> buffer, object value, Type type)
        {
            if (type == typeof(string))
            {
                // NVARCHAR
                string str = value.ToString();
                byte[] bytes = Encoding.Unicode.GetBytes(str);

                // Προσθήκη μήκους (όπως VARBINARY)
                if (bytes.Length <= 255)
                {
                    buffer.Add((byte)bytes.Length);
                }
                else
                {
                    buffer.Add(0xFE); // Multi-byte length
                    buffer.Add((byte)(bytes.Length & 0xFF));
                    buffer.Add((byte)(bytes.Length >> 8));
                }

                // Προσθήκη δεδομένων
                buffer.AddRange(bytes);
            }
            else if (type == typeof(int) || type == typeof(Int32))
            {
                // INT
                int intValue = Convert.ToInt32(value);
                byte[] bytes = BitConverter.GetBytes(intValue);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                buffer.AddRange(bytes);
            }
            else if (type == typeof(long) || type == typeof(Int64))
            {
                // BIGINT
                long longValue = Convert.ToInt64(value);
                byte[] bytes = BitConverter.GetBytes(longValue);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                buffer.AddRange(bytes);
            }
            else if (type == typeof(short) || type == typeof(Int16))
            {
                // SMALLINT
                short shortValue = Convert.ToInt16(value);
                byte[] bytes = BitConverter.GetBytes(shortValue);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                buffer.AddRange(bytes);
            }
            else if (type == typeof(byte))
            {
                // TINYINT
                buffer.Add(Convert.ToByte(value));
            }
            else if (type == typeof(bool))
            {
                // BIT
                buffer.Add(Convert.ToBoolean(value) ? (byte)1 : (byte)0);
            }
            else if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            {
                // FLOAT
                double doubleValue = Convert.ToDouble(value);
                byte[] bytes = BitConverter.GetBytes(doubleValue);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                buffer.AddRange(bytes);
            }
            else if (type == typeof(DateTime))
            {
                // DATETIME
                DateTime dt = Convert.ToDateTime(value);

                // Convert to TDS DATETIME format (simplistic implementation)
                int days = (int)(dt - new DateTime(1900, 1, 1)).TotalDays;
                int timePart = (int)((dt.TimeOfDay.TotalSeconds * 300) % 86400000);

                byte[] daysBytes = BitConverter.GetBytes(days);
                byte[] timeBytes = BitConverter.GetBytes(timePart);

                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(daysBytes);
                    Array.Reverse(timeBytes);
                }

                buffer.AddRange(timeBytes);
                buffer.AddRange(daysBytes);
            }
            else if (type == typeof(Guid))
            {
                // GUID
                Guid guid = (Guid)value;
                buffer.AddRange(guid.ToByteArray());
            }
            else
            {
                // Fallback to string for unknown types
                string str = value.ToString();
                byte[] bytes = Encoding.Unicode.GetBytes(str);

                // Length
                if (bytes.Length <= 255)
                {
                    buffer.Add((byte)bytes.Length);
                }
                else
                {
                    buffer.Add(0xFE); // Multi-byte length
                    buffer.Add((byte)(bytes.Length & 0xFF));
                    buffer.Add((byte)(bytes.Length >> 8));
                }

                // Data
                buffer.AddRange(bytes);
            }
        }

        // Για κενά αποτελέσματα
        private async Task SendEmptyResultAsync(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            // Μόνο ένα DONE token
            byte[] response = new byte[] {
        0xFD, // DONE token
        0x10, 0x00, // Status (0x0010 = DONE_COUNT)
        0x00, 0x00, // CurCmd
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // RowCount = 0
    };

            await SendTdsPacketAsync(stream, new TdsPacket
            {
                Type = 0x04, // Tabular result
                Status = 0x01, // EOM
                Spid = 0,
                PacketId = 1,
                Data = response
            }, token);
        }


        private async Task<string> ExecuteSqlQueryAsync(string sqlQuery, TdsSessionState session, CancellationToken token)
        {
            try
            {
                // Check if there are any registered services
                var availableServices = _registeredServices.Values
                    .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                    .ToList();

                if (availableServices.Count == 0)
                {
                    throw new InvalidOperationException("No SQL services are currently available");
                }

                // Choose a service (for now, just pick the first one)
                var targetService = availableServices.First();

                // Create the HTTP client
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(_relayUrl);
                client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);

                // Create the request object
                var request = new
                {
                    Id = Guid.NewGuid().ToString(),
                    Query = sqlQuery,
                    Parameters = new { },
                    ServiceId = targetService.ServiceId
                };

                // Serialize the request
                var content = new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json");

                // Send the request
                _logger.LogInformation("Connection {ConnectionId}: Forwarding query to service {ServiceId}",
                    session.ConnectionId, targetService.ServiceId);

                var response = await client.PostAsync("/api/sql/execute", content, token);

                // Check if the request was successful
                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Connection {ConnectionId}: API error: {StatusCode}, {Error}",
                        session.ConnectionId, response.StatusCode, errorContent);
                    throw new Exception($"SQL relay API error: {response.StatusCode}, {errorContent}");
                }

                // Read the response content
                string responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Connection {ConnectionId}: Received API response: {Response}",
                    session.ConnectionId, responseContent.Length > 100 ? responseContent.Substring(0, 100) + "..." : responseContent);

                // Return the JSON result
                return responseContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error executing SQL query", session.ConnectionId);
                throw;
            }
        }

        private async Task<TdsPacket> ReadTdsPacketAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                var headerBuffer = new byte[8];

                Console.WriteLine(">>> WAITING FOR CLIENT PACKET <<<");

                int bytesRead = await ReadExactlyAsync(stream, headerBuffer, 0, 8, cts.Token);
                if (bytesRead < 8)
                {
                    Console.WriteLine($"CLIENT: Incomplete header received ({bytesRead}/8 bytes)");
                    return null;
                }

                // Parse header
                byte type = headerBuffer[0];
                byte status = headerBuffer[1];
                ushort packetLength = (ushort)((headerBuffer[2] << 8) | headerBuffer[3]); // Άλλαξα το "length" σε "packetLength"
                ushort spid = (ushort)((headerBuffer[4] << 8) | headerBuffer[5]);
                byte packetId = headerBuffer[6];
                byte window = headerBuffer[7];

                Console.WriteLine($"CLIENT PACKET HEADER: Type=0x{type:X2}, Status=0x{status:X2}, Length={packetLength}, SPID={spid}, PacketID={packetId}");
                Console.WriteLine($"CLIENT HEADER HEX: {BitConverter.ToString(headerBuffer)}");

                if (packetLength < 8)
                {
                    Console.WriteLine($"CLIENT: Invalid packet length: {packetLength} (too small)");
                    return null;
                }

                // Διάβασε το payload
                int dataSize = packetLength - 8;
                var dataBuffer = new byte[dataSize];

                bytesRead = await ReadExactlyAsync(stream, dataBuffer, 0, dataSize, cts.Token);
                if (bytesRead < dataSize)
                {
                    Console.WriteLine($"CLIENT: Incomplete payload received ({bytesRead}/{dataSize} bytes)");
                    return null;
                }

                Console.WriteLine($"CLIENT PAYLOAD: {bytesRead} bytes");
                Console.WriteLine($"CLIENT PAYLOAD HEX: {BitConverter.ToString(dataBuffer)}");

                // Ειδική επεξεργασία ανά τύπο πακέτου
                if (type == 0x12) // PRELOGIN
                {
                    Console.WriteLine("CLIENT PACKET TYPE: PRELOGIN (0x12)");
                    try
                    {
                        // Προσπάθησε να αναλύσεις τα PRELOGIN options
                        var options = ParsePreLoginOptions(dataBuffer);
                        Console.WriteLine("CLIENT PRELOGIN OPTIONS:");
                        if (options.Version != null)
                            Console.WriteLine($"  VERSION: {BitConverter.ToString(options.Version)}");
                        if (options.Encryption != null)
                            Console.WriteLine($"  ENCRYPTION: {BitConverter.ToString(options.Encryption)}");
                        if (options.Mars != null)
                            Console.WriteLine($"  MARS: {BitConverter.ToString(options.Mars)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing PRELOGIN options: {ex.Message}");
                    }
                }
                else if (type == 0x10) // LOGIN7
                {
                    Console.WriteLine("CLIENT PACKET TYPE: LOGIN7 (0x10)");
                    // Προσπάθησε να εξάγεις βασικές πληροφορίες
                    try
                    {
                        if (dataBuffer.Length > 36)
                        {
                            int loginLength = BitConverter.ToInt32(dataBuffer, 0); // Άλλαξα το "length" σε "loginLength"
                            uint tdsVersion = BitConverter.ToUInt32(dataBuffer, 4);
                            int packetSize = BitConverter.ToInt32(dataBuffer, 8);
                            byte optionFlags1 = dataBuffer.Length > 24 ? dataBuffer[24] : (byte)0;
                            byte optionFlags2 = dataBuffer.Length > 25 ? dataBuffer[25] : (byte)0;

                            Console.WriteLine($"  TDS Version: 0x{tdsVersion:X8}");
                            Console.WriteLine($"  Packet Size: {packetSize}");
                            Console.WriteLine($"  Option Flags1: 0x{optionFlags1:X2}");
                            Console.WriteLine($"  Option Flags2: 0x{optionFlags2:X2}");
                            Console.WriteLine($"  Encryption Flag: {(optionFlags2 & 0x80) != 0}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing LOGIN7 data: {ex.Message}");
                    }
                }
                else if (type == 0x01) // SQL Batch
                {
                    Console.WriteLine("CLIENT PACKET TYPE: SQL Batch (0x01)");
                    try
                    {
                        string sqlQuery = ExtractSqlQuery(dataBuffer);
                        Console.WriteLine($"  SQL Query: {sqlQuery}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error extracting SQL query: {ex.Message}");
                    }
                }

                return new TdsPacket
                {
                    Type = type,
                    Status = status,
                    Length = packetLength, // Χρησιμοποιώ το packetLength εδώ
                    Spid = spid,
                    PacketId = packetId,
                    Window = window,
                    Data = dataBuffer
                };
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("TDS packet read operation timed out");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading TDS packet: {ex.Message}");
                return null;
            }
        }

        private async Task SendTdsPacketAsync(NetworkStream stream, TdsPacket packet, CancellationToken token)
        {
            // Calculate full packet length
            ushort totalLength = (ushort)(8 + packet.Data.Length); // Άλλαξα το "length" σε "totalLength"

            // Create buffer for the complete packet
            var buffer = new byte[totalLength];

            // Build header
            buffer[0] = packet.Type;
            buffer[1] = packet.Status;
            buffer[2] = (byte)(totalLength >> 8);    // high byte (big-endian)
            buffer[3] = (byte)(totalLength & 0xFF);  // low byte (big-endian)
            buffer[4] = (byte)(packet.Spid >> 8);   // high byte
            buffer[5] = (byte)(packet.Spid & 0xFF); // low byte
            buffer[6] = packet.PacketId;
            buffer[7] = packet.Window;

            // Copy the payload
            Buffer.BlockCopy(packet.Data, 0, buffer, 8, packet.Data.Length);

            Console.WriteLine(">>> SENDING TO CLIENT <<<");
            Console.WriteLine($"SERVER PACKET: Type=0x{packet.Type:X2}, Status=0x{packet.Status:X2}, Length={totalLength}, SPID={packet.Spid}, PacketID={packet.PacketId}");
            Console.WriteLine($"SERVER HEADER HEX: {BitConverter.ToString(buffer, 0, 8)}");
            Console.WriteLine($"SERVER PAYLOAD HEX: {BitConverter.ToString(buffer, 8, buffer.Length - 8)}");

            // Ανάλυση των tokens που στέλνουμε (για το LOGIN response)
            if (packet.Type == 0x04 && packet.Data.Length > 0) // Tabular result
            {
                int offset = 0;
                while (offset < packet.Data.Length)
                {
                    byte tokenType = packet.Data[offset];
                    if (tokenType == 0xAD) // LOGIN_ACK
                    {
                        int tokenLen = packet.Data[offset + 1] | (packet.Data[offset + 2] << 8);
                        Console.WriteLine($"  Token: LOGIN_ACK (0xAD), Length: {tokenLen}");
                        offset += 3 + tokenLen;
                    }
                    else if (tokenType == 0xE3) // ENVCHANGE
                    {
                        int tokenLen = packet.Data[offset + 1] | (packet.Data[offset + 2] << 8);
                        byte envType = offset + 3 < packet.Data.Length ? packet.Data[offset + 3] : (byte)0;
                        string envTypeDesc = envType == 1 ? "Database" :
                                            envType == 2 ? "Language" :
                                            envType == 3 ? "Charset" :
                                            envType == 4 ? "Packet Size" :
                                            $"Unknown ({envType})";
                        Console.WriteLine($"  Token: ENVCHANGE (0xE3), Length: {tokenLen}, Type: {envTypeDesc}");
                        offset += 3 + tokenLen;
                    }
                    else if (tokenType == 0xAB) // INFO
                    {
                        int tokenLen = packet.Data[offset + 1] | (packet.Data[offset + 2] << 8);
                        Console.WriteLine($"  Token: INFO (0xAB), Length: {tokenLen}");
                        offset += 3 + tokenLen;
                    }
                    else if (tokenType == 0xFD) // DONE
                    {
                        Console.WriteLine($"  Token: DONE (0xFD)");
                        offset += 13; // DONE token είναι πάντα 13 bytes
                    }
                    else
                    {
                        Console.WriteLine($"  Token: Unknown (0x{tokenType:X2})");
                        break; // Δεν μπορούμε να προχωρήσουμε περισσότερο
                    }
                }
            }

            // Send the packet
            await stream.WriteAsync(buffer, 0, buffer.Length, token);
            await stream.FlushAsync(token);

            Console.WriteLine("PACKET SENT TO CLIENT SUCCESSFULLY");
        }

        private async Task SendErrorResponseAsync(NetworkStream stream, TdsSessionState session, string errorMessage, CancellationToken token)
        {
            // Δημιουργία TDS ERROR token (0xAA)
            List<byte> errorPacket = new List<byte>();

            // ERROR token
            errorPacket.Add(0xAA);

            // Πληροφορίες σφάλματος
            byte[] msgBytes = Encoding.Unicode.GetBytes(errorMessage);
            byte[] serverBytes = Encoding.Unicode.GetBytes("SQL Relay Server");

            // Length (data after length field)
            int length = 4 + 1 + 1 + 2 + msgBytes.Length + 2 + serverBytes.Length + 2 + 4;
            errorPacket.Add((byte)(length & 0xFF));
            errorPacket.Add((byte)(length >> 8));

            // Error number (18456 - login failed)
            errorPacket.Add(0x18);
            errorPacket.Add(0x48);
            errorPacket.Add(0x00);
            errorPacket.Add(0x00);

            // State & Class
            errorPacket.Add(0x01); // State 
            errorPacket.Add(0x10); // Class = 16 (user error)

            // Message
            errorPacket.Add((byte)(msgBytes.Length / 2)); // Character count
            errorPacket.Add(0x00);
            errorPacket.AddRange(msgBytes);

            // Server name
            errorPacket.Add((byte)(serverBytes.Length / 2));
            errorPacket.Add(0x00);
            errorPacket.AddRange(serverBytes);

            // Procedure name (empty) & line number
            errorPacket.Add(0x00);
            errorPacket.Add(0x00);
            errorPacket.Add(0x01);
            errorPacket.Add(0x00);
            errorPacket.Add(0x00);
            errorPacket.Add(0x00);

            // DONE token with DONE_ERROR flag
            errorPacket.Add(0xFD);
            errorPacket.Add(0x03); // Status = DONE_ERROR | DONE_FINAL
            errorPacket.Add(0x00);
            errorPacket.Add(0x00); // CurCmd
            errorPacket.Add(0x00);

            // Row count (0)
            for (int i = 0; i < 8; i++)
            {
                errorPacket.Add(0x00);
            }

            // Αποστολή του πακέτου
            await SendTdsPacketAsync(stream, new TdsPacket
            {
                Type = 0x04, // Tabular result
                Status = 0x01, // EOM
                Spid = 0,
                PacketId = 1,
                Data = errorPacket.ToArray()
            }, token);

            _logger.LogInformation("Sent error response: {Message}", errorMessage);
        }

        private TdsPreLoginOptions ParsePreLoginOptions(byte[] packet)
        {
            var options = new TdsPreLoginOptions();
            int offset = 0;

            while (offset < packet.Length)
            {
                byte tokenType = packet[offset++];
                if (tokenType == 0xFF) // Terminator
                    break;

                if (offset + 4 > packet.Length)
                    break;

                ushort tokenOffset = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                ushort tokenLength = (ushort)((packet[offset + 2] << 8) | packet[offset + 3]);
                offset += 4;

                if (tokenOffset + tokenLength > packet.Length)
                    continue;

                byte[] tokenData = new byte[tokenLength];
                Buffer.BlockCopy(packet, tokenOffset, tokenData, 0, tokenLength);

                switch (tokenType)
                {
                    case 0x00: // VERSION
                        options.Version = tokenData;
                        break;
                    case 0x01: // ENCRYPTION
                        options.Encryption = tokenData;
                        break;
                    case 0x02: // INSTOPT
                        options.InstanceName = tokenData;
                        break;
                    case 0x03: // THREADID
                        options.ThreadId = tokenData;
                        break;
                    case 0x04: // MARS
                        options.Mars = tokenData;
                        break;
                    case 0x05: // TRACEID
                        options.TraceId = tokenData;
                        break;
                    case 0x06: // FEDAUTHREQUIRED
                        options.FedAuthRequired = tokenData;
                        break;
                    case 0x07: // NONCEOPT
                        options.Nonce = tokenData;
                        break;
                }
            }

            return options;
        }

        private void LogPreLoginOptions(Guid connectionId, TdsPreLoginOptions options, string direction)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Connection {connectionId}: {direction} PRELOGIN options:");

            if (options.Version != null)
            {
                if (options.Version.Length >= 6)
                {
                    int major = (options.Version[0] << 24) | (options.Version[1] << 16) | (options.Version[2] << 8) | options.Version[3];
                    int minor = options.Version[4];
                    int build = options.Version[5];
                    sb.AppendLine($"  VERSION: {major}.{minor}.{build}");
                }
                else
                {
                    sb.AppendLine($"  VERSION: {BitConverter.ToString(options.Version)}");
                }
            }

            if (options.Encryption != null && options.Encryption.Length > 0)
            {
                string encType = options.Encryption[0] switch
                {
                    0x00 => "ENCRYPT_OFF",
                    0x01 => "ENCRYPT_ON",
                    0x02 => "ENCRYPT_NOT_SUP",
                    0x03 => "ENCRYPT_REQ",
                    _ => $"UNKNOWN (0x{options.Encryption[0]:X2})"
                };
                sb.AppendLine($"  ENCRYPTION: {encType}");
            }

            if (options.InstanceName != null)
            {
                sb.AppendLine($"  INSTOPT: {BitConverter.ToString(options.InstanceName)}");
            }

            if (options.ThreadId != null)
            {
                sb.AppendLine($"  THREADID: {BitConverter.ToString(options.ThreadId)}");
            }

            if (options.Mars != null && options.Mars.Length > 0)
            {
                string marsOption = options.Mars[0] switch
                {
                    0x00 => "MARS_DISABLED",
                    0x01 => "MARS_ENABLED",
                    _ => $"UNKNOWN (0x{options.Mars[0]:X2})"
                };
                sb.AppendLine($"  MARS: {marsOption}");
            }

            if (options.TraceId != null)
            {
                sb.AppendLine($"  TRACEID: {BitConverter.ToString(options.TraceId)}");
            }

            if (options.FedAuthRequired != null)
            {
                sb.AppendLine($"  FEDAUTHREQUIRED: {BitConverter.ToString(options.FedAuthRequired)}");
            }

            if (options.Nonce != null)
            {
                sb.AppendLine($"  NONCEOPT: {BitConverter.ToString(options.Nonce)}");
            }

            _logger.LogInformation(sb.ToString());
        }

        private byte[] BuildPreLoginResponse(TdsPreLoginOptions clientOptions)
        {
            // Το πακέτο peer1_0 από το sql.txt δείχνει ότι χρειάζεται ένα διαφορετικό format
            byte[] response = new byte[54]; // Αλλάξτε το μέγεθος αν χρειάζεται
            int offset = 0;

            // Tokens
            response[offset++] = 0x00; // VERSION
            response[offset++] = 0x00;
            response[offset++] = 0x24;
            response[offset++] = 0x00;
            response[offset++] = 0x06;

            response[offset++] = 0x01; // ENCRYPTION
            response[offset++] = 0x00;
            response[offset++] = 0x2A;
            response[offset++] = 0x00;
            response[offset++] = 0x01;

            response[offset++] = 0x02; // INSTOPT
            response[offset++] = 0x00;
            response[offset++] = 0x2B;
            response[offset++] = 0x00;
            response[offset++] = 0x01;

            response[offset++] = 0x03; // THREADID
            response[offset++] = 0x00;
            response[offset++] = 0x2C;
            response[offset++] = 0x00;
            response[offset++] = 0x00; // Αλλαγή από 0x04 σε 0x00

            response[offset++] = 0x04; // MARS
            response[offset++] = 0x00;
            response[offset++] = 0x2C;
            response[offset++] = 0x00;
            response[offset++] = 0x01;

            response[offset++] = 0x05; // TRACEID
            response[offset++] = 0x00;
            response[offset++] = 0x2D;
            response[offset++] = 0x00;
            response[offset++] = 0x00;

            response[offset++] = 0x06; // FEDAUTH
            response[offset++] = 0x00;
            response[offset++] = 0x2D;
            response[offset++] = 0x00;
            response[offset++] = 0x01;

            // End of tokens
            response[offset++] = 0xFF;

            // Data
            // VERSION (0x0F 0x00 0x0B 0x0D 0x00 0x00)
            response[0x24] = 0x0F;
            response[0x25] = 0x00;
            response[0x26] = 0x0B;
            response[0x27] = 0x0D;
            response[0x28] = 0x00;
            response[0x29] = 0x00;

            // ENCRYPTION (0x02) - Αλλάξτε το σε 0x00 ή 0x02
            response[0x2A] = 0x02; // ENCRYPT_NOT_SUP

            // INSTOPT (0x00)
            response[0x2B] = 0x00;

            // THREADID - Κενό

            // MARS (0x00)
            response[0x2C] = 0x00;

            // TRACEID - Κενό

            // FEDAUTH (0x00)
            response[0x2D] = 0x00;

            return response;
        }

        private void ExtractLoginInfo(byte[] loginData, TdsSessionState session)
        {
            try
            {
                // Το LOGIN7 packet έχει ελάχιστο μέγεθος ~36 bytes
                if (loginData.Length < 36)
                {
                    _logger.LogWarning("LOGIN7 packet too short: {Length} bytes", loginData.Length);
                    return;
                }

                _logger.LogDebug("Parsing LOGIN7 packet of {Length} bytes", loginData.Length);

                // Τα πρώτα 4 bytes είναι το μήκος (little-endian)
                int length = BitConverter.ToInt32(loginData, 0);

                // Τα επόμενα 4 bytes είναι η έκδοση TDS (συνήθως 0x74000004 για TDS 7.4)
                uint tdsVersion = BitConverter.ToUInt32(loginData, 4);

                // Packet size (next 4 bytes)
                int packetSize = BitConverter.ToInt32(loginData, 8);

                // Client version (4 bytes)
                uint clientVersion = BitConverter.ToUInt32(loginData, 12);

                // Client PID (4 bytes)
                int clientPid = BitConverter.ToInt32(loginData, 16);

                // Connection ID (4 bytes)
                int connectionId = BitConverter.ToInt32(loginData, 20);

                // Option Flags 1 (1 byte)
                byte optionFlags1 = loginData[24];

                // Option Flags 2 (1 byte)
                byte optionFlags2 = loginData[25];

                // Type Flags (1 byte)
                byte typeFlags = loginData[26];

                // Option Flags 3 (1 byte)
                byte optionFlags3 = loginData[27];

                // CRITICAL: Check if the client requires encryption
                bool fSecurity = (optionFlags2 & 0x80) != 0;  // Bit 7 in optionFlags2
                bool fODBC = (optionFlags2 & 0x02) != 0;      // Bit 1 in optionFlags2
                bool fReadOnlyIntent = (optionFlags2 & 0x20) != 0; // Bit 5

                _logger.LogDebug("LOGIN7 Option Flags: Security={Security}, ODBC={ODBC}, ReadOnly={ReadOnly}",
                    fSecurity, fODBC, fReadOnlyIntent);

                // Offsets for variable-length fields (from byte 36 onwards)
                int ibHostName = BitConverter.ToInt16(loginData, 28);
                int cchHostName = BitConverter.ToInt16(loginData, 30);
                int ibUserName = BitConverter.ToInt16(loginData, 32);
                int cchUserName = BitConverter.ToInt16(loginData, 34);
                int ibPassword = BitConverter.ToInt16(loginData, 36);
                int cchPassword = BitConverter.ToInt16(loginData, 38);
                int ibAppName = BitConverter.ToInt16(loginData, 40);
                int cchAppName = BitConverter.ToInt16(loginData, 42);
                int ibServerName = BitConverter.ToInt16(loginData, 44);
                int cchServerName = BitConverter.ToInt16(loginData, 46);

                // Extract actual values (all text is Unicode = 2 bytes per character)
                if (cchHostName > 0 && ibHostName + cchHostName * 2 <= loginData.Length)
                {
                    session.ClientHostname = Encoding.Unicode.GetString(loginData, ibHostName, cchHostName * 2).TrimEnd('\0');
                    _logger.LogDebug("Extracted hostname: {Hostname}", session.ClientHostname);
                }

                if (cchUserName > 0 && ibUserName + cchUserName * 2 <= loginData.Length)
                {
                    session.Username = Encoding.Unicode.GetString(loginData, ibUserName, cchUserName * 2).TrimEnd('\0');
                    _logger.LogDebug("Extracted username: {Username}", session.Username);
                }

                // More code to extract other fields...

                // Store encryption requirements in session
                session.EncryptionRequired = fSecurity;

                _logger.LogInformation("LOGIN7 packet parsed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing LOGIN7 packet");
            }
        }

        private byte[] BuildLoginResponse(TdsSessionState session)
        {
            List<byte> response = new List<byte>();

            // 1. ENVCHANGE token για Database (0xE3)
            response.Add(0xE3); // ENVCHANGE token
            response.Add(0x1B); // Length LSB
            response.Add(0x00); // Length MSB
            response.Add(0x01); // Type = DATABASE
            response.Add(0x06); // Length of new value
                                // "protel" σε Unicode
            byte[] dbNameBytes = Encoding.Unicode.GetBytes("protel");
            response.AddRange(dbNameBytes);
            response.Add(0x06); // Length of old value
                                // "master" σε Unicode
            byte[] oldDbNameBytes = Encoding.Unicode.GetBytes("master");
            response.AddRange(oldDbNameBytes);

            // 2. INFO message για Database change (0xAB)
            response.Add(0xAB); // INFO token
            response.Add(0x66); // Length LSB
            response.Add(0x00); // Length MSB
            response.Add(0x45); // Error number LSB
            response.Add(0x16); // Error number MSB
            response.Add(0x00); // Error number (3rd byte)
            response.Add(0x00); // Error number (4th byte)
            response.Add(0x02); // State
            response.Add(0x00); // Class (information)
            response.Add(0x25); // Length of message (37 chars)
            response.Add(0x00); // MSB
                                // "Changed database context to 'protel'." σε Unicode
            byte[] dbChangeMsg = Encoding.Unicode.GetBytes("Changed database context to 'protel'.");
            response.AddRange(dbChangeMsg);
            // "DEMOSRV" σε Unicode (server name)
            response.Add(0x07); // Length of server name
            response.Add(0x00); // MSB
            byte[] serverIdBytes = Encoding.Unicode.GetBytes("DEMOSRV");
            response.AddRange(serverIdBytes);
            response.Add(0x00); // Line number LSB
            response.Add(0x01); // Line number (2nd byte)
            response.Add(0x00); // Line number (3rd byte)
            response.Add(0x00); // Line number (4th byte)

            // 3. ENVCHANGE token για PACKET SIZE (0xE3)
            response.Add(0xE3);
            response.Add(0x08);
            response.Add(0x00);
            response.Add(0x07); // Type = PACKET SIZE
            response.Add(0x05); // Length of new value
                                // "8000" σε ASCII
            response.Add(0x38); // '8'
            response.Add(0x00);
            response.Add(0x30); // '0'
            response.Add(0x00);
            response.Add(0x30); // '0'
            response.Add(0x00);
            response.Add(0x30); // '0'
            response.Add(0x00);
            response.Add(0x00); // Length of old value = 0

            // 4. ENVCHANGE token για LANGUAGE (0xE3)
            response.Add(0xE3);
            response.Add(0x17);
            response.Add(0x00);
            response.Add(0x02); // Type = LANGUAGE
            response.Add(0x0A); // Length of new value
                                // "us_english" σε Unicode
            byte[] langBytes = Encoding.Unicode.GetBytes("us_english");
            response.AddRange(langBytes);
            response.Add(0x00); // Length of old value = 0

            // 5. INFO message για Language change
            response.Add(0xAB);
            response.Add(0x6A);
            response.Add(0x00);
            response.Add(0x47); // Error number
            response.Add(0x16);
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x01); // State
            response.Add(0x00); // Class (information)
            response.Add(0x27); // Length of message
            response.Add(0x00);
            // "Changed language setting to us_english." σε Unicode
            byte[] langChangeMsg = Encoding.Unicode.GetBytes("Changed language setting to us_english.");
            response.AddRange(langChangeMsg);
            // "DEMOSRV" σε Unicode (server name)
            response.Add(0x07);
            response.Add(0x00);
            byte[] serverIdBytes2 = Encoding.Unicode.GetBytes("DEMOSRV");
            response.AddRange(serverIdBytes2);
            response.Add(0x00); // Line number LSB
            response.Add(0x01); // Line number (2nd byte)
            response.Add(0x00); // Line number (3rd byte)
            response.Add(0x00); // Line number (4th byte)

            // 6. LOGIN_ACK token (0xAD)
            response.Add(0xAD);
            response.Add(0x36);
            response.Add(0x00);
            response.Add(0x01); // Interface type
            response.Add(0x74); // TDS version
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x04);
            // "Microsoft SQL Server" σε Unicode
            byte[] serverNameBytes = Encoding.Unicode.GetBytes("Microsoft SQL Server");
            response.AddRange(serverNameBytes);
            response.Add(0x00); // Null terminator
            response.Add(0x00);
            // Version 
            response.Add(0x0F);
            response.Add(0x00);
            response.Add(0x11);
            response.Add(0x0D);

            // 7. ENVCHANGE token για CHARSET/CODEPAGE (0xE3)
            response.Add(0xE3);
            response.Add(0x13);
            response.Add(0x00);
            response.Add(0x04); // Type = CHARSET
            response.Add(0x04); // Length of new value
                                // "800" σε Unicode
            byte[] charsetBytes = Encoding.Unicode.GetBytes("800");
            response.AddRange(charsetBytes);
            response.Add(0x04); // Length of old value
                                // "409" σε Unicode (προηγούμενο charset)
            byte[] oldCharsetBytes = Encoding.Unicode.GetBytes("409");
            response.AddRange(oldCharsetBytes);

            // 8. DONE token (0xFD) - ΣΗΜΑΝΤΙΚΟ: Σωστό format
            response.Add(0xFD);
            response.Add(0x10); // Status flags = DONE_COUNT (0x10)
            response.Add(0x00);
            response.Add(0x00); // CurCmd
            response.Add(0x00);
            response.Add(0x00); // RowCount LSB
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x00);
            response.Add(0x00); // RowCount MSB

            return response.ToArray();
        }

        private string ExtractSqlQuery(byte[] batchData)
        {
            try
            {
                // SQL batch data is usually in UTF-16LE format after the header
                // There might be header information before the actual SQL text

                // Try to detect where SQL text starts
                // Simplified approach - in real implementation you'd need to parse the full structure

                // Try starting from different offsets to find valid SQL text
                for (int offset = 0; offset < Math.Min(20, batchData.Length - 2); offset++)
                {
                    try
                    {
                        // Try to decode as UTF-16LE
                        string text = Encoding.Unicode.GetString(batchData, offset, batchData.Length - offset);

                        // Clean up NUL terminators and trim
                        text = text.Replace("\0", "").Trim();

                        // If we found something that looks like SQL, return it
                        if (!string.IsNullOrWhiteSpace(text) && (
                            text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("IF", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("USE", StringComparison.OrdinalIgnoreCase)))
                        {
                            return text;
                        }
                    }
                    catch
                    {
                        // If decoding fails, try the next offset
                        continue;
                    }
                }

                // Fallback: just try decoding the whole packet
                return Encoding.Unicode.GetString(batchData).Replace("\0", "").Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting SQL query from batch data");
                return string.Empty;
            }
        }

        private byte[] BuildQueryResultPacket(string jsonResult)
        {
            // In a real implementation, this would convert the JSON result to TDS tokens
            // (COLMETADATA, ROW, DONE)

            // For simplicity, just return a DONE token indicating success
            byte[] result = new byte[13];

            // DONE token (0xFD)
            result[0] = 0xFD;

            // Status (2 bytes) - 0x0001 for DONE_COUNT (meaning row count is valid)
            result[1] = 0x01;
            result[2] = 0x00;

            // CurCmd (2 bytes) - 0x0000 for current command
            result[3] = 0x00;
            result[4] = 0x00;

            // RowCount (8 bytes) - Number of rows affected
            // In this case we'll just say 1 row was affected
            result[5] = 0x01;
            result[6] = 0x00;
            result[7] = 0x00;
            result[8] = 0x00;
            result[9] = 0x00;
            result[10] = 0x00;
            result[11] = 0x00;
            result[12] = 0x00;

            return result;
        }

        private byte[] BuildErrorPacket(string errorMessage)
        {
            // ERROR token (0xAA)
            // - Token type (1 byte): 0xAA
            // - Length (2 bytes): Length of data after this field
            // - Number (4 bytes): Error number (50000 for custom errors)
            // - State (1 byte): Error state (typically 1)
            // - Class (1 byte): Error severity (16 for user error)
            // - MsgText (variable): Error message text in Unicode
            // - ServerName (variable): Server name in Unicode
            // - ProcName (variable): Procedure name in Unicode
            // - LineNum (4 bytes): Line number where error occurred

            // DONE token (0xFD)
            // - Token type (1 byte): 0xFD
            // - Status (2 bytes): 0x0001 for error
            // - CurCmd (2 bytes): 0x0000
            // - DoneRowCount (8 bytes): 0

            // Convert message to Unicode
            byte[] errorText = Encoding.Unicode.GetBytes(errorMessage);
            byte[] serverName = Encoding.Unicode.GetBytes("SQL Tunnel");
            byte[] procName = Encoding.Unicode.GetBytes("");

            // Calculate error token size
            int errorTokenSize = 1 + 2 + 4 + 1 + 1 + errorText.Length + serverName.Length + procName.Length + 4;

            // DONE token is 13 bytes
            int doneTokenSize = 13;

            byte[] packet = new byte[errorTokenSize + doneTokenSize];
            int pos = 0;

            // ERROR token
            packet[pos++] = 0xAA; // Token type

            // Length (2 bytes, little-endian)
            int errorDataLength = errorTokenSize - 3; // Excluding token type and length
            packet[pos++] = (byte)(errorDataLength & 0xFF);
            packet[pos++] = (byte)(errorDataLength >> 8);

            // Error number (4 bytes, little-endian)
            packet[pos++] = 0x50; // 50000 = 0xC350
            packet[pos++] = 0xC3;
            packet[pos++] = 0x00;
            packet[pos++] = 0x00;

            // State (1 byte)
            packet[pos++] = 1;

            // Class/Severity (1 byte)
            packet[pos++] = 16; // 16 = user error

            // Error message text
            Buffer.BlockCopy(errorText, 0, packet, pos, errorText.Length);
            pos += errorText.Length;

            // Server name
            Buffer.BlockCopy(serverName, 0, packet, pos, serverName.Length);
            pos += serverName.Length;

            // Procedure name
            Buffer.BlockCopy(procName, 0, packet, pos, procName.Length);
            pos += procName.Length;

            // Line number (4 bytes, little-endian)
            packet[pos++] = 1;
            packet[pos++] = 0;
            packet[pos++] = 0;
            packet[pos++] = 0;

            // DONE token
            packet[pos++] = 0xFD; // Token type

            // Status (2 bytes, little-endian) - 0x0001 for error
            packet[pos++] = 0x01;
            packet[pos++] = 0x00;

            // CurCmd (2 bytes, little-endian)
            packet[pos++] = 0x00;
            packet[pos++] = 0x00;

            // Done row count (8 bytes, little-endian)
            for (int i = 0; i < 8; i++)
            {
                packet[pos++] = 0;
            }

            return packet;
        }

        private async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalBytesRead = 0;
            int bytesRemaining = count;

            while (bytesRemaining > 0)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, bytesRemaining, token);

                if (bytesRead == 0)
                    break; // End of stream

                totalBytesRead += bytesRead;
                bytesRemaining -= bytesRead;
            }

            return totalBytesRead;
        }
    }

    // Supporting classes

    public class TdsPacket
    {
        public byte Type { get; set; }
        public byte Status { get; set; }
        public ushort Length { get; set; }
        public ushort Spid { get; set; }
        public byte PacketId { get; set; }
        public byte Window { get; set; }
        public byte[] Data { get; set; }
    }

    public class TdsPreLoginOptions
    {
        public byte[] Version { get; set; }          // 0x00
        public byte[] Encryption { get; set; }       // 0x01
        public byte[] InstanceName { get; set; }     // 0x02
        public byte[] ThreadId { get; set; }         // 0x03
        public byte[] Mars { get; set; }             // 0x04
        public byte[] TraceId { get; set; }          // 0x05
        public byte[] FedAuthRequired { get; set; }  // 0x06
        public byte[] Nonce { get; set; }            // 0x07
    }       
}