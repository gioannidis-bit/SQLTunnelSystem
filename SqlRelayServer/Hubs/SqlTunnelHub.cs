using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Linq; // ✅ ADDED - Missing

namespace SqlRelayServer.Hubs
{
    public class SqlTunnelHub : Hub
    {
        // Track connected SQL Tunnel Services
        public static ConcurrentDictionary<string, SqlTunnelServiceInfo> ConnectedServices = new();

        // Channels for streaming large query results
        public static ConcurrentDictionary<string, Channel<string>> QueryResultChannels = new();

        // Called by SQLTunnelService to register
        public async Task RegisterSqlService(string serviceId, string displayName, string version)
        {
            var serviceInfo = new SqlTunnelServiceInfo
            {
                ServiceId = serviceId,
                DisplayName = displayName,
                Version = version,
                ConnectionId = Context.ConnectionId,
                LastHeartbeat = DateTime.UtcNow,
                IsOnline = true
            };

            ConnectedServices[serviceId] = serviceInfo;
            await Groups.AddToGroupAsync(Context.ConnectionId, serviceId);

            Console.WriteLine($"✅ SQL Tunnel Service registered: {serviceId} with connection {Context.ConnectionId}");
        }

        // Called by SQLTunnelService to send data chunks (like CloudRelayService)
        public async Task SendDataChunk(string queryId, string chunk, bool isLastChunk = false)
        {
            Console.WriteLine($"📦 Received chunk for query {queryId} (size: {chunk.Length} bytes, last: {isLastChunk})");

            // Write to the channel if registered
            if (QueryResultChannels.TryGetValue(queryId, out var channel))
            {
                try
                {
                    await channel.Writer.WriteAsync(chunk);
                    Console.WriteLine($"✅ Successfully wrote chunk to channel for query {queryId}");

                    if (isLastChunk)
                    {
                        Console.WriteLine($"🏁 Last chunk received, completing channel for query {queryId}");
                        channel.Writer.Complete();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error writing to channel for query {queryId}: {ex.Message}");
                    if (isLastChunk)
                    {
                        try
                        {
                            channel.Writer.Complete(ex);
                        }
                        catch { }
                    }
                }
            }
            else
            {
                Console.WriteLine($"❌ No channel found for query {queryId}");
            }
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var service = ConnectedServices.Values.FirstOrDefault(s => s.ConnectionId == Context.ConnectionId);
            if (service != null)
            {
                service.IsOnline = false;
                Console.WriteLine($"❌ SQL Tunnel Service disconnected: {service.ServiceId}");
            }
            await base.OnDisconnectedAsync(exception);
        }
    }

    public class SqlTunnelServiceInfo
    {
        public string ServiceId { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string ConnectionId { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsOnline { get; set; }
    }
}