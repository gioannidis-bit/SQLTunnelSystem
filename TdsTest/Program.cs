using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using System.Net.Http;
using System.Threading;

namespace TdsTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("TDS and HTTP Test Client");
            Console.WriteLine("------------------------");

            string host = "hit.com.gr";
            int tdsProt = 11433;
            int httpPort = 5175;
            string apiKey = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";

            Console.Write($"Server host [{host}]: ");
            var inputHost = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(inputHost))
                host = inputHost;

            Console.Write($"TDS port [{tdsProt}]: ");
            var inputTdsPort = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(inputTdsPort) && int.TryParse(inputTdsPort, out var parsedTdsPort))
                tdsProt = parsedTdsPort;

            Console.Write($"HTTP port [{httpPort}]: ");
            var inputHttpPort = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(inputHttpPort) && int.TryParse(inputHttpPort, out var parsedHttpPort))
                httpPort = parsedHttpPort;

            Console.Write($"API Key [{apiKey}]: ");
            var inputApiKey = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(inputApiKey))
                apiKey = inputApiKey;

            Console.WriteLine("\nSelect test option:");
            Console.WriteLine("1. Test TDS Protocol Only");
            Console.WriteLine("2. Test HTTP API Only");
            Console.WriteLine("3. Test Both TDS and HTTP");

            Console.Write("Option (1-3): ");
            var option = Console.ReadLine();

            try
            {
                switch (option)
                {
                    case "1":
                        await TestTdsProtocol(host, tdsProt);
                        break;
                    case "2":
                        await TestHttpApi(host, httpPort, apiKey);
                        break;
                    case "3":
                        await TestBoth(host, tdsProt, httpPort, apiKey);
                        break;
                    default:
                        Console.WriteLine("Invalid option. Running TDS test by default.");
                        await TestTdsProtocol(host, tdsProt);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        // Test TDS Protocol
        static async Task TestTdsProtocol(string host, int port)
        {
            Console.WriteLine($"\nTesting TDS Protocol on {host}:{port}");
            Console.WriteLine("-------------------------------------");

            using (var client = new TcpClient())
            {
                try
                {
                    await client.ConnectAsync(host, port);
                    Console.WriteLine("Connected!");

                    using (var stream = client.GetStream())
                    {
                        // 1. Send PreLogin packet
                        byte[] preLoginPacket = new byte[] {
                            0x12, 0x01, 0x00, 0x2F, 0x00, 0x00, 0x01, 0x00,  // Header
                            0x00, 0x00, 0x15, 0x00, 0x06, 0x01, 0x00, 0x1B,  // VERSION token
                            0xFF, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,  // TERMINATOR; VERSION data
                            0x00, 0x00, 0x00, 0x00, 0x00, 0x00               // ENCRYPTION data
                        };

                        Console.WriteLine("Step 1: Sending PreLogin packet...");
                        PrintPacketSummary(preLoginPacket, "SEND");

                        await stream.WriteAsync(preLoginPacket, 0, preLoginPacket.Length);

                        byte[] responseBuffer = new byte[4096];
                        int bytesRead = await ReadWithTimeoutAsync(stream, responseBuffer, 5000);

                        if (bytesRead > 0)
                        {
                            byte[] response = new byte[bytesRead];
                            Array.Copy(responseBuffer, response, bytesRead);
                            Console.WriteLine($"Received PreLogin response: {bytesRead} bytes");
                            PrintPacketSummary(response, "RECV");

                            // Check if we got a valid response
                            if (response[0] == 0x04) // Tabular Result
                            {
                                Console.WriteLine("\nStep 2: Sending Login packet...");

                                // Create basic login packet
                                byte[] loginPacket = CreateBasicLoginPacket("sa", "hitprotel");

                                PrintPacketSummary(loginPacket, "SEND");
                                await stream.WriteAsync(loginPacket, 0, loginPacket.Length);

                                bytesRead = await ReadWithTimeoutAsync(stream, responseBuffer, 5000);

                                if (bytesRead > 0)
                                {
                                    response = new byte[bytesRead];
                                    Array.Copy(responseBuffer, response, bytesRead);
                                    Console.WriteLine($"Received Login response: {bytesRead} bytes");
                                    PrintPacketSummary(response, "RECV");

                                    if (response[0] == 0x04) // Tabular Result
                                    {
                                        Console.WriteLine("\nStep 3: Sending SQL query...");

                                        byte[] queryPacket = CreateSimpleSqlBatchPacket("SELECT * from lizenz");

                                        PrintPacketSummary(queryPacket, "SEND");
                                        await stream.WriteAsync(queryPacket, 0, queryPacket.Length);

                                        bytesRead = await ReadWithTimeoutAsync(stream, responseBuffer, 5000);

                                        if (bytesRead > 0)
                                        {
                                            response = new byte[bytesRead];
                                            Array.Copy(responseBuffer, response, bytesRead);
                                            Console.WriteLine($"Received Query response: {bytesRead} bytes");
                                            PrintPacketSummary(response, "RECV");
                                            Console.WriteLine("\nTDS Protocol test completed successfully!");
                                        }
                                        else
                                        {
                                            Console.WriteLine("No response to SQL query (timeout).");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("Login response was not successful.");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("No response to Login packet (timeout).");
                                }
                            }
                            else
                            {
                                Console.WriteLine("PreLogin response was not successful.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("No PreLogin response received (timeout).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"TDS Protocol Test Error: {ex.Message}");
                }
            }
        }

        // Test HTTP API
        static async Task TestHttpApi(string host, int port, string apiKey)
        {
            Console.WriteLine($"\nTesting HTTP API on {host}:{port} with API Key");
            Console.WriteLine("--------------------------------------------");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

            // Test Status API
            try
            {
                Console.WriteLine("Testing API: GET /api/status");
                var response = await httpClient.GetAsync($"http://{host}:{port}/api/status");

                Console.WriteLine($"Response Status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response Content: {content}");
                    Console.WriteLine("HTTP API test passed!");
                }
                else
                {
                    Console.WriteLine("HTTP API test failed. Invalid response status code.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HTTP API Test Error: {ex.Message}");
            }
        }

        // Test Both TDS and HTTP
        static async Task TestBoth(string host, int tdsPort, int httpPort, string apiKey)
        {
            Console.WriteLine("\nTesting both TDS Protocol and HTTP API");
            Console.WriteLine("--------------------------------------");

            // First test HTTP API
            await TestHttpApi(host, httpPort, apiKey);

            // Then test TDS Protocol
            await TestTdsProtocol(host, tdsPort);

            Console.WriteLine("\nBoth tests completed!");
        }

        // Helper method to read with timeout
        static async Task<int> ReadWithTimeoutAsync(NetworkStream stream, byte[] buffer, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                return await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Read operation timed out after {timeoutMs}ms");
                return 0;
            }
        }

        // Print packet summary
        static void PrintPacketSummary(byte[] packet, string direction)
        {
            if (packet == null || packet.Length < 8)
            {
                Console.WriteLine("Invalid packet (too short)");
                return;
            }

            byte type = packet[0];
            byte status = packet[1];
            ushort length = BitConverter.ToUInt16(packet, 2);
            ushort spid = BitConverter.ToUInt16(packet, 4);
            byte packetId = packet[6];

            string typeDesc = GetPacketTypeDesc(type);

            Console.WriteLine($"{direction} TDS Packet:");
            Console.WriteLine($"  Type: 0x{type:X2} ({typeDesc})");
            Console.WriteLine($"  Status: 0x{status:X2} (EOM: {((status & 0x01) == 0x01)})");
            Console.WriteLine($"  Length: {packet.Length} bytes");
            Console.WriteLine($"  SPID: {spid}");
            Console.WriteLine($"  Packet ID: {packetId}");

            PrintHexDump(packet, 64);
        }

        // Create basic login packet
        static byte[] CreateBasicLoginPacket(string username, string password)
        {
            // Simplified login packet
            byte[] packet = new byte[] {
                0x10, 0x01, 0x00, 0x90, 0x00, 0x00, 0x01, 0x00,  // Header (type=Login7, status=EOM)
                0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x70,  // Login data
                0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xE0, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x44, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00,
                0x44, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00,
                0x44, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00,
                0x44, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x73, 0x00, 0x61, 0x00, 0x00, 0x00, 0x00, 0x00,  // "sa" (username)
                0x65, 0xC5, 0x69, 0xC5, 0x74, 0xC5, 0x70, 0xC5,  // Obfuscated "hitprotel" (password)
                0x72, 0xC5, 0x6F, 0xC5, 0x74, 0xC5, 0x65, 0xC5,
                0x6C, 0xC5, 0x00, 0x00
            };

            // Update packet length
            BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 2);

            return packet;
        }

        // Create a simple SQL Batch packet
        static byte[] CreateSimpleSqlBatchPacket(string query)
        {
            // Convert query to Unicode bytes
            byte[] queryBytes = Encoding.Unicode.GetBytes(query);

            // Create packet (header + query)
            byte[] packet = new byte[8 + 4 + queryBytes.Length];

            // Header
            packet[0] = 0x01;  // Type: SQL Batch
            packet[1] = 0x01;  // Status: EOM
            BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 2);  // Length
            packet[4] = 0x00;  // SPID (high byte)
            packet[5] = 0x00;  // SPID (low byte)
            packet[6] = 0x01;  // Packet ID
            packet[7] = 0x00;  // Window

            // SQL Batch header
            packet[8] = 0x00;  // Transaction descriptor (4 bytes)
            packet[9] = 0x00;
            packet[10] = 0x00;
            packet[11] = 0x00;

            // Query text
            Buffer.BlockCopy(queryBytes, 0, packet, 12, queryBytes.Length);

            return packet;
        }

        // Get packet type description
        static string GetPacketTypeDesc(byte type)
        {
            return type switch
            {
                0x01 => "SQL Batch",
                0x04 => "Tabular Result",
                0x10 => "Login7",
                0x12 => "PreLogin",
                _ => "Unknown"
            };
        }

        // Print hex dump
        static void PrintHexDump(byte[] data, int maxLength)
        {
            int length = Math.Min(data.Length, maxLength);
            const int bytesPerLine = 16;

            for (int i = 0; i < length; i += bytesPerLine)
            {
                // Print hex values
                Console.Write("    ");
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < length)
                        Console.Write($"{data[i + j]:X2} ");
                    else
                        Console.Write("   ");

                    // Extra space after 8 bytes
                    if (j == 7)
                        Console.Write(" ");
                }

                // Print ASCII representation
                Console.Write(" | ");
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < length)
                    {
                        char c = (char)data[i + j];
                        // Replace non-printable characters with a dot
                        if (c < 32 || c > 126)
                            Console.Write(".");
                        else
                            Console.Write(c);
                    }
                    else
                    {
                        Console.Write(" ");
                    }
                }

                Console.WriteLine();
            }

            if (data.Length > maxLength)
            {
                Console.WriteLine($"    ... ({data.Length - maxLength} more bytes)");
            }
        }
    }
}