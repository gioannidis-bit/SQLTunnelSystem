using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Net;
using SqlRelayServer.Hubs;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace SqlRelayServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
      Host.CreateDefaultBuilder(args)
          .UseWindowsService(options =>
          {
              options.ServiceName = "SQL Relay Server";
          })
          .ConfigureWebHostDefaults(webBuilder =>
          {
              webBuilder.UseStartup<Startup>();
          });
    }

    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private static readonly ConcurrentDictionary<string, QueryInfo> PendingQueries = new ConcurrentDictionary<string, QueryInfo>();
        private static readonly ConcurrentDictionary<string, QueryResult> QueryResults = new ConcurrentDictionary<string, QueryResult>();
        private static readonly ConcurrentDictionary<string, QueryResultInfo> RecentQueryResults =
      new ConcurrentDictionary<string, QueryResultInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ServiceInfo> RegisteredServices = new ConcurrentDictionary<string, ServiceInfo>();

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddLogging(configure => configure.AddConsole());
            services.AddHttpClient();

            services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 100L * 1024 * 1024; // 100MB
            });

            services.AddSingleton(RegisteredServices);
            services.AddSingleton(PendingQueries);
            services.AddSingleton(QueryResults);
            services.AddSingleton(RecentQueryResults);
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                // ✅ SignalR Hub
                endpoints.MapHub<SqlTunnelHub>("/sqlTunnelHub");

                // ✅ SINGLE STREAMING ENDPOINT - FIXED URL PATH
                endpoints.MapGet("/api/sql/execute-stream", async context =>
                {
                    logger.LogInformation("🔄 Streaming endpoint called");

                    try
                    {
                        if (!ValidateClientAuth(context, logger))
                        {
                            logger.LogWarning("❌ Streaming endpoint - unauthorized access");
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        var query = context.Request.Query["query"];
                        var serviceId = context.Request.Query["serviceId"];

                        logger.LogInformation("📝 Streaming request - Query length: {Length}, ServiceId: {ServiceId}",
                            query.ToString().Length, serviceId.ToString());

                        if (string.IsNullOrEmpty(query))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Query parameter required");
                            return;
                        }

                        // Find available service
                        var availableServices = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .ToList();

                        if (availableServices.Count == 0)
                        {
                            logger.LogWarning("⚠️ No SQL services available for streaming");
                            context.Response.StatusCode = 503;
                            await context.Response.WriteAsync("No SQL services currently available");
                            return;
                        }

                        var targetService = string.IsNullOrEmpty(serviceId)
                            ? availableServices.First()
                            : availableServices.FirstOrDefault(s => s.ServiceId == serviceId) ?? availableServices.First();

                        var queryId = Guid.NewGuid().ToString();
                        logger.LogInformation("🆔 Creating streaming query {QueryId} for service {ServiceId}", queryId, targetService.ServiceId);

                        // Create channel for streaming results
                        var channel = Channel.CreateUnbounded<string>();
                        SqlTunnelHub.QueryResultChannels[queryId] = channel;

                        // Add to pending queries
                        var queryInfo = new QueryInfo
                        {
                            Id = queryId,
                            Query = query,
                            Parameters = null,
                            ServiceId = targetService.ServiceId,
                            CreatedAt = DateTime.UtcNow
                        };

                        PendingQueries.TryAdd(queryId, queryInfo);
                        logger.LogInformation("✅ Added streaming query {QueryId} to pending queue", queryId);

                        // Set response for streaming
                        context.Response.ContentType = "application/json";

                        // Start streaming response
                        await context.Response.WriteAsync("["); // Start JSON array
                        bool isFirst = true;

                        // Read from channel and stream to client
                        await foreach (var chunk in channel.Reader.ReadAllAsync())
                        {
                            if (!isFirst)
                                await context.Response.WriteAsync(",");

                            await context.Response.WriteAsync(chunk);
                            await context.Response.Body.FlushAsync();
                            isFirst = false;
                        }

                        await context.Response.WriteAsync("]"); // End JSON array

                        // Cleanup
                        SqlTunnelHub.QueryResultChannels.TryRemove(queryId, out _);
                        PendingQueries.TryRemove(queryId, out _);

                        logger.LogInformation("✅ Streaming query {QueryId} completed", queryId);

                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error in streaming endpoint");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Streaming error: {ex.Message}");
                    }
                });

                // ✅ STATUS ENDPOINT
                endpoints.MapGet("/api/status", async context =>
                {
                    try
                    {
                        if (!ValidateClientAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        logger.LogInformation($"📊 Total registered services: {RegisteredServices.Count}");

                        var activeServices = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .Select(s => new
                            {
                                s.ServiceId,
                                s.DisplayName,
                                s.Description,
                                s.Version,
                                s.ServerInfo,
                                s.LastHeartbeat,
                                IsActive = true,
                                TimeSinceLastHeartbeat = (DateTime.UtcNow - s.LastHeartbeat).TotalSeconds
                            })
                            .ToList();

                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(activeServices));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error processing status request");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // ✅ SERVICE HEARTBEAT
                endpoints.MapPost("/api/services/heartbeat", async context =>
                {
                    try
                    {
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        var serviceId = context.Request.Headers["X-Service-ID"].ToString();

                        string requestBody;
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            requestBody = await reader.ReadToEndAsync();
                        }

                        var heartbeatData = JsonConvert.DeserializeObject<dynamic>(requestBody);

                        RegisteredServices.AddOrUpdate(
                            serviceId,
                                   id => new ServiceInfo
                                   {
                                       ServiceId = id,
                                       DisplayName = heartbeatData?.DisplayName ?? id,
                                       Description = heartbeatData?.Description ?? "",
                                       Version = heartbeatData?.Version ?? "1.0",
                                       ServerInfo = heartbeatData?.ServerInfo ?? "",
                                       LastHeartbeat = DateTime.UtcNow
                                   },
                            (id, existing) => {
                                existing.LastHeartbeat = DateTime.UtcNow;
                                if (heartbeatData != null)
                                {
                                    existing.DisplayName = heartbeatData.DisplayName ?? existing.DisplayName;
                                    existing.Description = heartbeatData.Description ?? existing.Description;
                                    existing.Version = heartbeatData.Version ?? existing.Version;
                                    existing.ServerInfo = heartbeatData.ServerInfo ?? existing.ServerInfo;
                                }
                                return existing;
                            });

                        context.Response.StatusCode = 200;
                        await context.Response.WriteAsync("OK");

                        logger.LogInformation($"💓 Service {serviceId} heartbeat received");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error processing service heartbeat");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // ✅ PENDING QUERIES
                endpoints.MapGet("/api/queries/pending", async context =>
                {
                    try
                    {
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        var serviceId = context.Request.Headers["X-Service-ID"].ToString();

                        if (RegisteredServices.TryGetValue(serviceId, out var serviceInfo))
                        {
                            serviceInfo.LastHeartbeat = DateTime.UtcNow;
                        }
                        else
                        {
                            RegisteredServices.TryAdd(
                                serviceId,
                                new ServiceInfo
                                {
                                    ServiceId = serviceId,
                                    LastHeartbeat = DateTime.UtcNow
                                });
                        }

                        var pendingQueries = PendingQueries.Values
                            .Where(q => q.ServiceId == serviceId &&
                                   q.CreatedAt > DateTime.UtcNow.AddMinutes(-5))
                            .ToList();

                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(pendingQueries));

                        logger.LogInformation($"📤 Sent {pendingQueries.Count} pending queries to service {serviceId}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error processing pending queries request");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // ✅ QUERY RESULTS
                endpoints.MapPost("/api/queries/result", async context =>
                {
                    try
                    {
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        string requestBody;
                        QueryResult queryResult;

                        try
                        {
                            using var reader = new StreamReader(context.Request.Body);
                            requestBody = await reader.ReadToEndAsync();

                            if (requestBody.Length > 50_000_000) // 50MB limit
                            {
                                logger.LogError("❌ Request body too large: {Size} characters", requestBody.Length);
                                context.Response.StatusCode = 413;
                                await context.Response.WriteAsync("Request body too large");
                                return;
                            }

                            logger.LogDebug("📝 Received request body of {Size} characters", requestBody.Length);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "❌ Error reading request body");
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Error reading request body");
                            return;
                        }

                        try
                        {
                            queryResult = JsonConvert.DeserializeObject<QueryResult>(requestBody);
                        }
                        catch (JsonException ex)
                        {
                            logger.LogError(ex, "❌ Error deserializing JSON. Body size: {Size}", requestBody.Length);
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Invalid JSON format");
                            return;
                        }

                        if (string.IsNullOrEmpty(queryResult?.QueryId))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Invalid request. QueryId is required.");
                            return;
                        }

                        // Remove from pending
                        PendingQueries.TryRemove(queryResult.QueryId, out var queryInfo);
                        logger.LogInformation("🗑️ Removed query {QueryId} from pending queue", queryResult.QueryId);

                        // Store result
                        QueryResults.TryAdd(queryResult.QueryId, new QueryResult
                        {
                            QueryId = queryResult.QueryId,
                            Result = queryResult.Result,
                            Error = queryResult.Error,
                            Timestamp = queryResult.Timestamp
                        });

                        // Store in recent results
                        if (queryInfo != null)
                        {
                            var resultInfo = new QueryResultInfo
                            {
                                QueryId = queryResult.QueryId,
                                Query = queryInfo.Query,
                                Result = queryResult.Result,
                                Error = queryResult.Error,
                                Timestamp = queryResult.Timestamp,
                                ClientEndPoint = context.Connection.RemoteIpAddress.ToString()
                            };

                            while (RecentQueryResults.Count >= 50)
                            {
                                var oldestKey = RecentQueryResults.OrderBy(kv => kv.Value.Timestamp).FirstOrDefault().Key;
                                if (!string.IsNullOrEmpty(oldestKey))
                                {
                                    RecentQueryResults.TryRemove(oldestKey, out _);
                                }
                                else
                                {
                                    break;
                                }
                            }

                            RecentQueryResults.TryAdd(queryResult.QueryId, resultInfo);
                        }

                        context.Response.StatusCode = 200;
                        await context.Response.WriteAsync("OK");

                        logger.LogInformation("✅ Successfully processed result for query {QueryId}", queryResult.QueryId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Unexpected error processing query result");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // ✅ QUERY RESULTS HTML PAGE
                endpoints.MapGet("/api/query-results", async context =>
                {
                    string apiKeyFromQuery = context.Request.Query["apiKey"];
                    bool hasValidApiKeyHeader = ValidateClientAuth(context, logger);
                    bool hasValidApiKeyQuery = !string.IsNullOrEmpty(apiKeyFromQuery) &&
                                  apiKeyFromQuery == "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";

                    if (!hasValidApiKeyHeader && !hasValidApiKeyQuery)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized");
                        return;
                    }

                    context.Response.ContentType = "text/html";

                    await context.Response.WriteAsync("<!DOCTYPE html>\n");
                    await context.Response.WriteAsync("<html><head><title>SQL Query Results</title>");
                    await context.Response.WriteAsync("<style>body{font-family:Arial,sans-serif;margin:20px;} ");
                    await context.Response.WriteAsync("table{border-collapse:collapse;width:100%;} ");
                    await context.Response.WriteAsync("th,td{border:1px solid #ddd;padding:8px;text-align:left;} ");
                    await context.Response.WriteAsync("th{background-color:#f2f2f2;} ");
                    await context.Response.WriteAsync("tr:nth-child(even){background-color:#f9f9f9;} ");
                    await context.Response.WriteAsync(".query{margin-bottom:30px;border:1px solid #ccc;padding:10px;border-radius:5px;} ");
                    await context.Response.WriteAsync(".query-text{font-family:monospace;background:#eee;padding:5px;margin:10px 0;} ");
                    await context.Response.WriteAsync(".timestamp{color:#666;font-size:0.9em;} ");
                    await context.Response.WriteAsync("</style></head><body>");

                    await context.Response.WriteAsync("<h1>Recent SQL Query Results</h1>");

                    if (RecentQueryResults.Count == 0)
                    {
                        await context.Response.WriteAsync("<p>No query results available yet.</p>");
                    }
                    else
                    {
                        var sortedResults = RecentQueryResults.Values
                            .OrderByDescending(r => r.Timestamp)
                            .ToList();

                        foreach (var result in sortedResults)
                        {
                            await context.Response.WriteAsync($"<div class='query'>");
                            await context.Response.WriteAsync($"<h3>Query ID: {result.QueryId}</h3>");
                            await context.Response.WriteAsync($"<div class='timestamp'>Executed: {result.Timestamp}</div>");
                            await context.Response.WriteAsync($"<div class='query-text'>{WebUtility.HtmlEncode(result.Query)}</div>");

                            if (!string.IsNullOrEmpty(result.Error))
                            {
                                await context.Response.WriteAsync($"<div style='color:red;'>Error: {WebUtility.HtmlEncode(result.Error)}</div>");
                                continue;
                            }

                            if (string.IsNullOrEmpty(result.Result) || result.Result == "[]")
                            {
                                await context.Response.WriteAsync("<div>No results returned</div>");
                                continue;
                            }

                            try
                            {
                                var dataTable = JsonConvert.DeserializeObject<DataTable>(result.Result);
                                if (dataTable != null && dataTable.Rows.Count > 0)
                                {
                                    await context.Response.WriteAsync("<table>");

                                    await context.Response.WriteAsync("<tr>");
                                    foreach (DataColumn column in dataTable.Columns)
                                    {
                                        await context.Response.WriteAsync($"<th>{WebUtility.HtmlEncode(column.ColumnName)}</th>");
                                    }
                                    await context.Response.WriteAsync("</tr>");

                                    foreach (DataRow row in dataTable.Rows)
                                    {
                                        await context.Response.WriteAsync("<tr>");
                                        foreach (DataColumn column in dataTable.Columns)
                                        {
                                            string value = row[column] == DBNull.Value ? "NULL" : row[column].ToString();
                                            await context.Response.WriteAsync($"<td>{WebUtility.HtmlEncode(value)}</td>");
                                        }
                                        await context.Response.WriteAsync("</tr>");
                                    }

                                    await context.Response.WriteAsync("</table>");
                                }
                                else
                                {
                                    await context.Response.WriteAsync("<div>No rows in result</div>");
                                }
                            }
                            catch
                            {
                                await context.Response.WriteAsync("<div>Raw result:</div>");
                                await context.Response.WriteAsync($"<pre>{WebUtility.HtmlEncode(result.Result)}</pre>");
                            }

                            await context.Response.WriteAsync("</div>");
                        }
                    }

                    await context.Response.WriteAsync("<script>");
                    await context.Response.WriteAsync("setTimeout(function() { location.reload(); }, 120000);");
                    await context.Response.WriteAsync("</script>");

                    await context.Response.WriteAsync("</body></html>");
                });
            });
        }

        private bool ValidateClientAuth(HttpContext context, ILogger logger)
        {
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
            {
                logger.LogWarning("❌ No API key found in request headers");
                return false;
            }

            var validApiKey = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";

            logger.LogInformation($"🔑 Received API key: {apiKey}, Valid key: {validApiKey}, Match: {apiKey == validApiKey}");
            return apiKey == validApiKey;
        }

        private bool ValidateServiceAuth(HttpContext context, ILogger logger)
        {
            if (!context.Request.Headers.TryGetValue("X-Service-ID", out var serviceId) ||
                !context.Request.Headers.TryGetValue("X-Service-Key", out var serviceKey))
            {
                return false;
            }

            if (RegisteredServices.TryGetValue(serviceId, out var service))
            {
                return true;
            }

            return !string.IsNullOrEmpty(serviceId) && !string.IsNullOrEmpty(serviceKey);
        }
    }

    // ✅ DATA MODELS
    public class QueryResultInfo
    {
        public string QueryId { get; set; }
        public string Query { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
        public string ClientEndPoint { get; set; }
    }

    public class ServiceInfo
    {
        public string ServiceId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string ServerInfo { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }

    public class QueryInfo
    {
        public string Id { get; set; }
        public string Query { get; set; }
        public object Parameters { get; set; }
        public string ServiceId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class QueryResult
    {
        public string QueryId { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SqlRequest
    {
        public string Id { get; set; }
        public string Query { get; set; }
        public object Parameters { get; set; }
        public string ServiceId { get; set; }
    }
}