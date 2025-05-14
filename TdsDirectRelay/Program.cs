using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TdsDebugProxy
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Create host builder
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    // Configure settings from command line or environment variables
                    string targetServer = args.Length > 0 ? args[0] : "195.46.18.171,1433";

                    services.Configure<TdsProxySettings>(options =>
                    {
                        options.TargetSqlServer = targetServer;
                        options.ListenPort = args.Length > 1 ? int.Parse(args[1]) : 11433;
                        options.EnablePacketLogging = true;
                        options.LogPacketPayload = true;
                    });

                    // Register the TDS proxy service
                    services.AddHostedService<TdsDebugProxyService>();
                })
                .Build();

            Console.WriteLine("TDS Debug Proxy Application");
            Console.WriteLine("Press Ctrl+C to stop the service");

            // Set up console cancellation
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // Run the host until cancellation
            await host.RunAsync(cts.Token);
        }
    }

    public class TdsDebugProxyService : IHostedService
    {
        private readonly ILogger<TdsDebugProxyService> _logger;
        private readonly TdsProxySettings _settings;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public TdsDebugProxyService(
            ILogger<TdsDebugProxyService> logger,
            IOptions<TdsProxySettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting TDS Debug Proxy on port {ListenPort} -> {TargetServer}",
                _settings.ListenPort, _settings.TargetSqlServer);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _listener = new TcpListener(IPAddress.Any, _settings.ListenPort);
            _listener.Start();

            _ = AcceptConnectionsAsync(_cts.Token);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping TDS Debug Proxy");

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
                    var clientConnection = await _listener.AcceptTcpClientAsync();

                    _logger.LogInformation("New client connected from {EndPoint}",
                        clientConnection.Client.RemoteEndPoint);

                    _ = ProcessConnectionAsync(clientConnection, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connections");
            }
        }

        private async Task ProcessConnectionAsync(TcpClient clientConnection, CancellationToken token)
        {
            Guid connectionId = Guid.NewGuid();
            _logger.LogInformation("Connection {ConnectionId} established", connectionId);

            try
            {
                // Split the SQL Server address and port
                string[] targetParts = _settings.TargetSqlServer.Split(',');
                string targetHost = targetParts[0];
                int targetPort = targetParts.Length > 1 ? int.Parse(targetParts[1]) : 1433;

                // Connect to the actual SQL Server
                using var sqlServerConnection = new TcpClient();
                await sqlServerConnection.ConnectAsync(targetHost, targetPort, token);

                _logger.LogInformation("Connection {ConnectionId}: Connected to SQL Server at {Host}:{Port}",
                    connectionId, targetHost, targetPort);

                // Bidirectional data forwarding with TDS packet inspection
                using (clientConnection)
                {
                    var clientToServerTask = RelayAndInspectTdsAsync(
                        clientConnection, sqlServerConnection, "Client -> Server", connectionId, token);

                    var serverToClientTask = RelayAndInspectTdsAsync(
                        sqlServerConnection, clientConnection, "Server -> Client", connectionId, token);

                    await Task.WhenAny(clientToServerTask, serverToClientTask);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}: Error in connection relay", connectionId);
            }
        }

        private async Task RelayAndInspectTdsAsync(TcpClient source, TcpClient destination,
            string direction, Guid connectionId, CancellationToken token)
        {
            const int TDS_HEADER_SIZE = 8;  // TDS packet header is 8 bytes

            var sourceStream = source.GetStream();
            var destinationStream = destination.GetStream();

            try
            {
                // We'll use a memory pool to efficiently handle buffers
                byte[] headerBuffer = new byte[TDS_HEADER_SIZE];
                Memory<byte> headerMemory = headerBuffer.AsMemory();

                while (!token.IsCancellationRequested && source.Connected && destination.Connected)
                {
                    // First read just the TDS header (8 bytes)
                    int headerBytesRead = await ReadExactlyAsync(sourceStream, headerMemory, token);

                    if (headerBytesRead < TDS_HEADER_SIZE)
                    {
                        _logger.LogInformation("Connection {ConnectionId}, {Direction}: Connection closed during header read",
                            connectionId, direction);
                        break;
                    }

                    // Parse the TDS header
                    var packetType = headerBuffer[0];
                    var packetStatus = headerBuffer[1];
                    var packetLength = (headerBuffer[2] << 8) | headerBuffer[3]; // big-endian
                    var packetSpid = (headerBuffer[4] << 8) | headerBuffer[5];   // big-endian
                    var packetSeqNum = headerBuffer[6];
                    var packetWindow = headerBuffer[7];

                    if (packetLength < TDS_HEADER_SIZE)
                    {
                        _logger.LogWarning("Connection {ConnectionId}, {Direction}: Invalid TDS packet length {Length}",
                            connectionId, direction, packetLength);
                        break;
                    }

                    // Read the payload (packet length includes the header)
                    int payloadSize = packetLength - TDS_HEADER_SIZE;
                    byte[] payloadBuffer = new byte[payloadSize];
                    Memory<byte> payloadMemory = payloadBuffer.AsMemory();

                    int payloadBytesRead = await ReadExactlyAsync(sourceStream, payloadMemory, token);

                    if (payloadBytesRead < payloadSize)
                    {
                        _logger.LogInformation("Connection {ConnectionId}, {Direction}: Connection closed during payload read",
                            connectionId, direction);
                        break;
                    }

                    // Forward the complete packet (header + payload)
                    await destinationStream.WriteAsync(headerMemory, token);
                    await destinationStream.WriteAsync(payloadMemory, token);
                    await destinationStream.FlushAsync(token);

                    // Log the packet information
                    LogTdsPacket(connectionId, direction, packetType, packetStatus, packetLength,
                        packetSpid, packetSeqNum, packetWindow, payloadBuffer);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Connection {ConnectionId}, {Direction}: IO error: {Message}",
                    connectionId, direction, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection {ConnectionId}, {Direction}: Error relaying data",
                    connectionId, direction);
            }
        }

        private async Task<int> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
        {
            int totalBytesRead = 0;
            int bytesRemaining = buffer.Length;

            while (bytesRemaining > 0)
            {
                int bytesRead = await stream.ReadAsync(buffer.Slice(totalBytesRead, bytesRemaining), token);

                if (bytesRead == 0)
                    return totalBytesRead; // End of stream

                totalBytesRead += bytesRead;
                bytesRemaining -= bytesRead;
            }

            return totalBytesRead;
        }

        private void LogTdsPacket(Guid connectionId, string direction, byte packetType, byte status,
            int length, int spid, byte seqNum, byte window, byte[] payload)
        {
            string packetTypeStr = GetTdsPacketTypeName(packetType);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Connection {connectionId}, {direction}: TDS Packet");
            sb.AppendLine($"  Type: {packetTypeStr} (0x{packetType:X2})");
            sb.AppendLine($"  Status: 0x{status:X2} ({GetTdsStatusFlags(status)})");
            sb.AppendLine($"  Length: {length} bytes");
            sb.AppendLine($"  SPID: {spid}");
            sb.AppendLine($"  Sequence: {seqNum}");
            sb.AppendLine($"  Window: {window}");

            // Special handling for PRELOGIN packets
            if (packetType == 0x12 && _settings.LogPacketPayload)
            {
                sb.AppendLine("  --- PRELOGIN Packet ---");
                ParsePreLoginPacket(sb, payload);
            }

            // Log full packet content if enabled
            if (_settings.LogPacketPayload && packetType != 0x12) // Skip if we already parsed PRELOGIN specifically
            {
                sb.AppendLine("  Payload:");

                // Try to detect if it's likely binary or text data
                bool isBinary = ContainsBinaryData(payload);

                if (isBinary)
                {
                    // Hex dump for binary data
                    sb.Append(HexDump(payload));
                }
                else
                {
                    // ASCII representation for mostly text data
                    sb.Append("  ASCII: ");
                    foreach (byte b in payload)
                    {
                        char c = (char)b;
                        sb.Append(char.IsControl(c) ? '.' : c);
                    }
                }
            }

            _logger.LogInformation(sb.ToString());
        }

        private string GetTdsPacketTypeName(byte packetType)
        {
            return packetType switch
            {
                0x01 => "SQL Batch",
                0x02 => "Pre-TDS7 Login",
                0x03 => "RPC",
                0x04 => "Tabular Result",
                0x06 => "Attention Signal",
                0x07 => "Bulk Load Data",
                0x0F => "Session State",
                0x10 => "NTLM Authentication",
                0x11 => "Login",
                0x12 => "PRELOGIN",
                0x13 => "FEDAUTH",
                0x14 => "SSPI",
                0x15 => "TDS8 FEDAUTH",
                0x16 => "TDS7 FEDAUTH",
                0x17 => "RESET CONNECTION",
                0x18 => "Feature Extension Ack",
                _ => "Unknown"
            };
        }

        private string GetTdsStatusFlags(byte status)
        {
            List<string> flags = new List<string>();

            if ((status & 0x01) != 0) flags.Add("EOM (End of Message)");
            if ((status & 0x02) != 0) flags.Add("IGNORE");
            if ((status & 0x04) != 0) flags.Add("RESETCONNECTION");
            if ((status & 0x08) != 0) flags.Add("RESETCONNECTIONSKIPTRAN");

            return string.Join(", ", flags);
        }

        private void ParsePreLoginPacket(StringBuilder sb, byte[] payload)
        {
            try
            {
                Dictionary<byte, string> preloginTypes = new Dictionary<byte, string>
                {
                    { 0x00, "VERSION" },
                    { 0x01, "ENCRYPTION" },
                    { 0x02, "INSTOPT" },
                    { 0x03, "THREADID" },
                    { 0x04, "MARS" },
                    { 0x05, "TRACEID" },
                    { 0x06, "FEDAUTHREQUIRED" },
                    { 0x07, "NONCEOPT" },
                    { 0x08, "TERMINATOR" }
                };

                int offset = 0;

                // Process each token until we hit TERMINATOR (0xFF)
                while (offset < payload.Length && payload[offset] != 0xFF)
                {
                    byte tokenType = payload[offset++];

                    // Check if we've run out of data
                    if (offset + 4 > payload.Length)
                        break;

                    int tokenOffset = (payload[offset] << 8) | payload[offset + 1];
                    int tokenLength = (payload[offset + 2] << 8) | payload[offset + 3];
                    offset += 4;

                    string tokenName = preloginTypes.ContainsKey(tokenType)
                        ? preloginTypes[tokenType]
                        : $"UNKNOWN (0x{tokenType:X2})";

                    sb.AppendLine($"    Token: {tokenName}");
                    sb.AppendLine($"      Offset: {tokenOffset}, Length: {tokenLength}");

                    // Token-specific value parsing
                    if (tokenOffset + tokenLength <= payload.Length)
                    {
                        byte[] tokenData = new byte[tokenLength];
                        Array.Copy(payload, tokenOffset, tokenData, 0, tokenLength);

                        switch (tokenType)
                        {
                            case 0x00: // VERSION
                                if (tokenLength >= 6)
                                {
                                    int major = (tokenData[0] << 24) | (tokenData[1] << 16) | (tokenData[2] << 8) | tokenData[3];
                                    int minor = tokenData[4];
                                    int build = tokenData[5];
                                    sb.AppendLine($"      Version: {major}.{minor}.{build}");
                                }
                                break;

                            case 0x01: // ENCRYPTION
                                if (tokenLength >= 1)
                                {
                                    string encType = tokenData[0] switch
                                    {
                                        0x00 => "ENCRYPT_OFF",
                                        0x01 => "ENCRYPT_ON",
                                        0x02 => "ENCRYPT_NOT_SUP",
                                        0x03 => "ENCRYPT_REQ",
                                        _ => $"UNKNOWN (0x{tokenData[0]:X2})"
                                    };
                                    sb.AppendLine($"      Encryption: {encType}");
                                }
                                break;

                            case 0x04: // MARS
                                if (tokenLength >= 1)
                                {
                                    string marsOption = tokenData[0] switch
                                    {
                                        0x00 => "MARS_DISABLED",
                                        0x01 => "MARS_ENABLED",
                                        _ => $"UNKNOWN (0x{tokenData[0]:X2})"
                                    };
                                    sb.AppendLine($"      MARS: {marsOption}");
                                }
                                break;

                            default:
                                sb.AppendLine($"      Data: {BitConverter.ToString(tokenData)}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    Error parsing PRELOGIN: {ex.Message}");
            }
        }

        private bool ContainsBinaryData(byte[] data, double threshold = 0.3)
        {
            if (data.Length == 0)
                return false;

            int controlChars = 0;

            foreach (byte b in data)
            {
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) // Not a printable char or tab/CR/LF
                    controlChars++;
            }

            return (double)controlChars / data.Length > threshold;
        }

        private string HexDump(byte[] bytes, int bytesPerLine = 16)
        {
            StringBuilder hexDump = new StringBuilder();

            for (int i = 0; i < bytes.Length; i += bytesPerLine)
            {
                // Address
                hexDump.AppendFormat("  {0:X6}: ", i);

                // Hex values
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < bytes.Length)
                        hexDump.AppendFormat("{0:X2} ", bytes[i + j]);
                    else
                        hexDump.Append("   ");

                    if (j == 7)
                        hexDump.Append(" ");
                }

                // ASCII representation
                hexDump.Append(" | ");

                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < bytes.Length)
                    {
                        char c = (char)bytes[i + j];
                        hexDump.Append(char.IsControl(c) ? '.' : c);
                    }
                }

                hexDump.AppendLine();
            }

            return hexDump.ToString();
        }
    }

    public class TdsProxySettings
    {
        public string TargetSqlServer { get; set; } = "localhost,1433";
        public int ListenPort { get; set; } = 11433;
        public bool EnablePacketLogging { get; set; } = true;
        public bool LogPacketPayload { get; set; } = true;
    }
}