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
            IOptions<ImprovedTdsListener> settings,
            ConcurrentDictionary<string, ServiceInfo> registeredServices)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _apiKey = settings.Value._apiKey;
            _relayUrl = settings.Value._relayUrl;
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
                // Read the PRELOGIN packet
                var preLoginPacket = await ReadTdsPacketAsync(stream, token);
                if (preLoginPacket == null || preLoginPacket.Type != 0x12) // 0x12 = PRELOGIN
                {
                    _logger.LogWarning("Connection {ConnectionId}: Expected PRELOGIN packet, received: {PacketType}",
                        session.ConnectionId, preLoginPacket?.Type.ToString("X2") ?? "null");
                    return false;
                }

                _logger.LogInformation("Connection {ConnectionId}: Received PRELOGIN packet, length: {Length}",
                    session.ConnectionId, preLoginPacket.Data.Length);

                // Parse client PRELOGIN options
                var clientOptions = ParsePreLoginOptions(preLoginPacket.Data);
                LogPreLoginOptions(session.ConnectionId, clientOptions, "Client");

                // Create server PRELOGIN response
                var serverOptions = new TdsPreLoginOptions
                {
                    Version = new byte[] { 0x0C, 0x00, 0x10, 0x00, 0x00, 0x00 }, // SQL Server 2017
                    Encryption = new byte[] { 0x00 }, // ENCRYPT_OFF
                    ThreadId = new byte[] { 0x00, 0x00, 0x00, 0x00 },
                    Mars = new byte[] { 0x00 }, // MARS disabled
                    TraceId = null
                };

                // Build the response packet
                var responseData = BuildPreLoginResponse(serverOptions);

                // Send PRELOGIN response
                await SendTdsPacketAsync(stream, new TdsPacket
                {
                    Type = 0x04, // Tabular result
                    Status = 0x01, // EOM
                    Spid = 0,
                    PacketId = 1,
                    Data = responseData
                }, token);

                _logger.LogInformation("Connection {ConnectionId}: Sent PRELOGIN response, length: {Length}",
                    session.ConnectionId, responseData.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error during PRELOGIN phase", session.ConnectionId);
                return false;
            }
        }

        private async Task<bool> HandleLoginPhase(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            try
            {
                // Read the LOGIN7 packet
                var loginPacket = await ReadTdsPacketAsync(stream, token);
                if (loginPacket == null || loginPacket.Type != 0x10) // 0x10 = LOGIN7
                {
                    _logger.LogWarning("Connection {ConnectionId}: Expected LOGIN7 packet, received: {PacketType}",
                        session.ConnectionId, loginPacket?.Type.ToString("X2") ?? "null");
                    return false;
                }

                _logger.LogInformation("Connection {ConnectionId}: Received LOGIN7 packet, length: {Length}",
                    session.ConnectionId, loginPacket.Data.Length);

                // Parse login packet to extract username, database, etc.
                // This is simplified - a full implementation would parse the entire LOGIN7 structure
                ExtractLoginInfo(loginPacket.Data, session);

                // Build login response with LOGIN_ACK and DONE tokens
                byte[] loginResponse = BuildLoginResponse(session);

                // Send LOGIN response
                await SendTdsPacketAsync(stream, new TdsPacket
                {
                    Type = 0x04, // Tabular result
                    Status = 0x01, // EOM
                    Spid = 0,
                    PacketId = 1,
                    Data = loginResponse
                }, token);

                _logger.LogInformation("Connection {ConnectionId}: Sent LOGIN7 response, length: {Length}",
                    session.ConnectionId, loginResponse.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error during LOGIN phase", session.ConnectionId);
                return false;
            }
        }

        private async Task ProcessSqlBatchesAsync(NetworkStream stream, TdsSessionState session, CancellationToken token)
        {
            try
            {
                // Keep reading SQL batch packets until the connection is closed or cancelled
                while (!token.IsCancellationRequested)
                {
                    var packet = await ReadTdsPacketAsync(stream, token);
                    if (packet == null)
                    {
                        _logger.LogInformation("Connection {ConnectionId}: Client closed connection", session.ConnectionId);
                        break;
                    }

                    // 0x01 = SQL Batch
                    if (packet.Type == 0x01)
                    {
                        string sqlQuery = ExtractSqlQuery(packet.Data);
                        _logger.LogInformation("Connection {ConnectionId}: Received SQL Batch: {Query}",
                            session.ConnectionId, sqlQuery);

                        if (!string.IsNullOrEmpty(sqlQuery))
                        {
                            try
                            {
                                // Execute query through API
                                string result = await ExecuteSqlQueryAsync(sqlQuery, session, token);

                                // Process the results
                                _logger.LogInformation("Connection {ConnectionId}: Query executed successfully", session.ConnectionId);

                                // Send result rows (simplified - would actually convert JSON to TDS data rows)
                                byte[] resultPacket = BuildQueryResultPacket(result);
                                await SendTdsPacketAsync(stream, new TdsPacket
                                {
                                    Type = 0x04, // Tabular result
                                    Status = 0x01, // EOM
                                    Spid = 0,
                                    PacketId = 1,
                                    Data = resultPacket
                                }, token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Connection {ConnectionId}: Error executing query", session.ConnectionId);
                                await SendErrorResponseAsync(stream, session, ex.Message, token);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Connection {ConnectionId}: Could not extract SQL query from packet", session.ConnectionId);
                            await SendErrorResponseAsync(stream, session, "Invalid SQL query format", token);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Connection {ConnectionId}: Unexpected packet type: 0x{PacketType:X2}",
                            session.ConnectionId, packet.Type);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
                _logger.LogInformation("Connection {ConnectionId}: Operation cancelled", session.ConnectionId);
            }
            catch (IOException ex)
            {
                // This is often just a client disconnection
                _logger.LogInformation("Connection {ConnectionId}: IO exception: {Message}", session.ConnectionId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error processing SQL batches", session.ConnectionId);
            }
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
            var headerBuffer = new byte[8]; // TDS header is 8 bytes

            // Read the header
            int bytesRead = await ReadExactlyAsync(stream, headerBuffer, 0, 8, token);
            if (bytesRead < 8)
            {
                return null; // Connection closed
            }

            // Parse the header
            byte type = headerBuffer[0];
            byte status = headerBuffer[1];
            ushort length = (ushort)((headerBuffer[2] << 8) | headerBuffer[3]); // big-endian
            ushort spid = (ushort)((headerBuffer[4] << 8) | headerBuffer[5]);   // big-endian
            byte packetId = headerBuffer[6];
            byte window = headerBuffer[7];

            // Sanity check on packet length
            if (length < 8 || length > 32768)
            {
                throw new InvalidDataException($"Invalid TDS packet length: {length}");
            }

            // Read the payload
            var dataBuffer = new byte[length - 8]; // Header size is included in length
            bytesRead = await ReadExactlyAsync(stream, dataBuffer, 0, dataBuffer.Length, token);
            if (bytesRead < dataBuffer.Length)
            {
                return null; // Connection closed during payload read
            }

            return new TdsPacket
            {
                Type = type,
                Status = status,
                Length = length,
                Spid = spid,
                PacketId = packetId,
                Window = window,
                Data = dataBuffer
            };
        }

        private async Task SendTdsPacketAsync(NetworkStream stream, TdsPacket packet, CancellationToken token)
        {
            // Calculate full packet length (header + data)
            ushort length = (ushort)(8 + packet.Data.Length);

            // Create buffer for the complete packet
            var buffer = new byte[length];

            // Build header
            buffer[0] = packet.Type;
            buffer[1] = packet.Status;
            buffer[2] = (byte)(length >> 8);    // high byte (big-endian)
            buffer[3] = (byte)(length & 0xFF);  // low byte (big-endian)
            buffer[4] = (byte)(packet.Spid >> 8);   // high byte (big-endian)
            buffer[5] = (byte)(packet.Spid & 0xFF); // low byte (big-endian)
            buffer[6] = packet.PacketId;
            buffer[7] = packet.Window;

            // Copy the payload
            Buffer.BlockCopy(packet.Data, 0, buffer, 8, packet.Data.Length);

            // Send the packet
            await stream.WriteAsync(buffer, 0, buffer.Length, token);
            await stream.FlushAsync(token);
        }

        private async Task SendErrorResponseAsync(NetworkStream stream, TdsSessionState session, string errorMessage, CancellationToken token)
        {
            // Build error message packet (TDS ERROR token + DONE token)
            byte[] errorPacket = BuildErrorPacket(errorMessage);

            await SendTdsPacketAsync(stream, new TdsPacket
            {
                Type = 0x04, // Tabular result
                Status = 0x01, // EOM
                Spid = 0,
                PacketId = 1,
                Data = errorPacket
            }, token);

            _logger.LogInformation("Connection {ConnectionId}: Sent error response: {Message}",
                session.ConnectionId, errorMessage);
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

        private byte[] BuildPreLoginResponse(TdsPreLoginOptions options)
        {
            // Calculate data and offset section sizes
            int dataSize = 0;
            int tokenCount = 0;

            if (options.Version != null) { dataSize += options.Version.Length; tokenCount++; }
            if (options.Encryption != null) { dataSize += options.Encryption.Length; tokenCount++; }
            if (options.InstanceName != null) { dataSize += options.InstanceName.Length; tokenCount++; }
            if (options.ThreadId != null) { dataSize += options.ThreadId.Length; tokenCount++; }
            if (options.Mars != null) { dataSize += options.Mars.Length; tokenCount++; }
            if (options.TraceId != null) { dataSize += options.TraceId.Length; tokenCount++; }
            if (options.FedAuthRequired != null) { dataSize += options.FedAuthRequired.Length; tokenCount++; }

            // Add terminator
            tokenCount++;

            // Token header = 1 byte type + 2 bytes offset + 2 bytes length
            int headerSize = tokenCount * 5;

            // Total size = headers + data
            byte[] response = new byte[headerSize + dataSize];

            // Current position for writing tokens
            int tokenPos = 0;

            // Offset where data will start
            int dataOffset = headerSize;

            // Helper to add a token to the response
            void AddToken(byte tokenType, byte[] data)
            {
                if (data == null)
                    return;

                // Write token type
                response[tokenPos++] = tokenType;

                // Write data offset (big-endian)
                response[tokenPos++] = (byte)(dataOffset >> 8);
                response[tokenPos++] = (byte)(dataOffset & 0xFF);

                // Write data length (big-endian)
                response[tokenPos++] = (byte)(data.Length >> 8);
                response[tokenPos++] = (byte)(data.Length & 0xFF);

                // Copy data to its offset
                Buffer.BlockCopy(data, 0, response, dataOffset, data.Length);

                // Update data offset for next token
                dataOffset += data.Length;
            }

            // Add all tokens
            if (options.Version != null) AddToken(0x00, options.Version);
            if (options.Encryption != null) AddToken(0x01, options.Encryption);
            if (options.InstanceName != null) AddToken(0x02, options.InstanceName);
            if (options.ThreadId != null) AddToken(0x03, options.ThreadId);
            if (options.Mars != null) AddToken(0x04, options.Mars);
            if (options.TraceId != null) AddToken(0x05, options.TraceId);
            if (options.FedAuthRequired != null) AddToken(0x06, options.FedAuthRequired);

            // Add terminator
            response[tokenPos] = 0xFF;

            return response;
        }

        private void ExtractLoginInfo(byte[] loginData, TdsSessionState session)
        {
            // A real implementation would parse all the login fields
            // This is a simplified version that just extracts the hostname
            try
            {
                if (loginData.Length < 36)
                    return;

                // The login packet has a complex structure
                // This is just a placeholder for real parsing code
                session.ClientHostname = "client";
                session.Username = "user";
                session.Database = "master";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting login info");
            }
        }

        private byte[] BuildLoginResponse(TdsSessionState session)
        {
            // This is a simplified login response with LOGIN_ACK and DONE tokens

            // LOGIN_ACK token (0xAD)
            // - Token type (1 byte): 0xAD
            // - Token length (2 bytes): Length of data after this field
            // - Interface (1 byte): 0 for SQL Server
            // - TDS Version (4 bytes): Major, Minor, BuildNumHi, BuildNumLo
            // - ProgName (variable): Program name "SQL Server" in Unicode
            // - Major Version (1 byte): e.g., 15 for SQL Server 2019
            // - Minor Version (1 byte): e.g., 0 for SQL Server 2019
            // - BuildNum (2 bytes): e.g., 4153 for a specific build

            // DONE token (0xFD)
            // - Token type (1 byte): 0xFD
            // - Status (2 bytes): 0x0000 for success
            // - CurCmd (2 bytes): 0x0001 for LOGIN
            // - DoneRowCount (8 bytes): 0 for LOGIN

            byte[] programName = Encoding.Unicode.GetBytes("SQL Server");

            // Calculate sizes
            int loginAckSize = 1 + 2 + 1 + 4 + programName.Length + 4;
            int doneSize = 1 + 2 + 2 + 8;

            byte[] response = new byte[loginAckSize + doneSize];
            int pos = 0;

            // LOGIN_ACK token
            response[pos++] = 0xAD; // Token type

            // Token length (2 bytes, little-endian)
            int dataLength = loginAckSize - 3; // Excluding token type and length
            response[pos++] = (byte)(dataLength & 0xFF);
            response[pos++] = (byte)(dataLength >> 8);

            // Interface type
            response[pos++] = 0; // SQL Server

            // TDS Version (4 bytes)
            response[pos++] = 4; // 7.4
            response[pos++] = 0;
            response[pos++] = 0;
            response[pos++] = 0;

            // Program name
            Buffer.BlockCopy(programName, 0, response, pos, programName.Length);
            pos += programName.Length;

            // Version info
            response[pos++] = 15; // Major Version (SQL Server 2019)
            response[pos++] = 0;  // Minor Version
            response[pos++] = 0;  // Build Number (high byte)
            response[pos++] = 0;  // Build Number (low byte)

            // DONE token
            response[pos++] = 0xFD; // Token type

            // Status (2 bytes, little-endian) - 0x0000 for success
            response[pos++] = 0;
            response[pos++] = 0;

            // Current command (2 bytes, little-endian) - 0x0001 for LOGIN
            response[pos++] = 1;
            response[pos++] = 0;

            // Done row count (8 bytes, little-endian) - 0 for LOGIN
            for (int i = 0; i < 8; i++)
            {
                response[pos++] = 0;
            }

            return response;
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

    public class TdsSessionState
    {
        public Guid ConnectionId { get; set; }
        public string ClientEndpoint { get; set; }
        public string ClientHostname { get; set; }
        public string Username { get; set; }
        public string Database { get; set; }
    }
}