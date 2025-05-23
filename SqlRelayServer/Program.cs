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
using System.Collections.Generic;
using System.Threading;





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
                options.MaximumReceiveMessageSize = 500L * 1024 * 1024; // 500MB - INCREASED!
                options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                options.ClientTimeoutInterval = TimeSpan.FromMinutes(30); // EXTENDED for large queries
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

                // 🚀 RESTORED & OPTIMIZED: Traditional POST endpoint with internal streaming
                endpoints.MapPost("/api/sql/execute", async context =>
                {
                    try
                    {
                        logger.LogInformation("🎯 SQL Execute endpoint called - using internal streaming");

                        // ✅ Same authentication as before
                        if (!ValidateClientAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        // ✅ Read request (same as before)
                        using var reader = new StreamReader(context.Request.Body);
                        var requestBody = await reader.ReadToEndAsync();
                        var sqlRequest = JsonConvert.DeserializeObject<SqlRequest>(requestBody);

                        if (string.IsNullOrEmpty(sqlRequest?.Query))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Invalid request. Query is required.");
                            return;
                        }

                        // ✅ Find available services (same as before)
                        var availableServices = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .ToList();

                        if (availableServices.Count == 0)
                        {
                            context.Response.StatusCode = 503;
                            await context.Response.WriteAsync("No SQL services currently available");
                            return;
                        }

                        // ✅ Select target service (same as before)
                        ServiceInfo targetService;
                        if (!string.IsNullOrEmpty(sqlRequest.ServiceId) &&
                            availableServices.Any(s => s.ServiceId == sqlRequest.ServiceId))
                        {
                            targetService = availableServices.First(s => s.ServiceId == sqlRequest.ServiceId);
                        }
                        else
                        {
                            targetService = availableServices.First();
                        }

                        // 🚀 NEW: Internal streaming collection
                        var queryId = string.IsNullOrEmpty(sqlRequest.Id) ?
                            Guid.NewGuid().ToString() : sqlRequest.Id;

                        logger.LogInformation("🔄 Processing query {QueryId} via internal streaming for service {ServiceId}",
                            queryId, targetService.ServiceId);

                        // 🚀 Create streaming channel for internal collection
                        var channel = Channel.CreateUnbounded<string>();
                        SqlTunnelHub.QueryResultChannels[queryId] = channel;

                        // ✅ Add to pending queries
                        var queryInfo = new QueryInfo
                        {
                            Id = queryId,
                            Query = sqlRequest.Query,
                            Parameters = sqlRequest.Parameters,
                            ServiceId = targetService.ServiceId,
                            CreatedAt = DateTime.UtcNow
                        };

                        PendingQueries.TryAdd(queryId, queryInfo);
                        logger.LogInformation("✅ Added query {QueryId} to pending queue", queryId);

                        // 🚀 OPTIMIZED: Collect streaming results efficiently
                        var finalResult = await CollectStreamingResults(channel, queryId, logger);

                        // 📊 Store result for history
                        var resultInfo = new QueryResultInfo
                        {
                            QueryId = queryId,
                            Query = sqlRequest.Query,
                            Result = finalResult.IsError ? null : finalResult.Data,
                            Error = finalResult.IsError ? finalResult.Data : null,
                            Timestamp = DateTime.UtcNow,
                            ClientEndPoint = context.Connection.RemoteIpAddress?.ToString()
                        };

                        // 🗑️ Cleanup and store history
                        PendingQueries.TryRemove(queryId, out _);
                        SqlTunnelHub.QueryResultChannels.TryRemove(queryId, out _);

                        // Maintain recent results (limit to 50)
                        while (RecentQueryResults.Count >= 50)
                        {
                            var oldestKey = RecentQueryResults.OrderBy(kv => kv.Value.Timestamp).FirstOrDefault().Key;
                            if (!string.IsNullOrEmpty(oldestKey))
                            {
                                RecentQueryResults.TryRemove(oldestKey, out _);
                            }
                            else break;
                        }
                        RecentQueryResults.TryAdd(queryId, resultInfo);

                        // ✅ Return response (same format as before)
                        context.Response.ContentType = "application/json";

                        if (finalResult.IsError)
                        {
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Error = finalResult.Data }));
                        }
                        else
                        {
                            await context.Response.WriteAsync(finalResult.Data);
                        }

                        logger.LogInformation("✅ Query {QueryId} completed successfully", queryId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error processing SQL execution request");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // ✅ Keep streaming endpoint for future direct streaming needs
                endpoints.MapGet("/api/sql/execute-stream", async context =>
                {
                    logger.LogInformation("🔄 Direct streaming endpoint called");

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

                        if (string.IsNullOrEmpty(query))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Query parameter required");
                            return;
                        }

                        var availableServices = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .ToList();

                        if (availableServices.Count == 0)
                        {
                            context.Response.StatusCode = 503;
                            await context.Response.WriteAsync("No SQL services currently available");
                            return;
                        }

                        var targetService = string.IsNullOrEmpty(serviceId)
                            ? availableServices.First()
                            : availableServices.FirstOrDefault(s => s.ServiceId == serviceId) ?? availableServices.First();

                        var queryId = Guid.NewGuid().ToString();
                        var channel = Channel.CreateUnbounded<string>();
                        SqlTunnelHub.QueryResultChannels[queryId] = channel;

                        var queryInfo = new QueryInfo
                        {
                            Id = queryId,
                            Query = query,
                            Parameters = null,
                            ServiceId = targetService.ServiceId,
                            CreatedAt = DateTime.UtcNow
                        };

                        PendingQueries.TryAdd(queryId, queryInfo);

                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("[");
                        bool isFirst = true;

                        await foreach (var chunk in channel.Reader.ReadAllAsync())
                        {
                            if (!isFirst) await context.Response.WriteAsync(",");
                            await context.Response.WriteAsync(chunk);
                            await context.Response.Body.FlushAsync();
                            isFirst = false;
                        }

                        await context.Response.WriteAsync("]");
                        SqlTunnelHub.QueryResultChannels.TryRemove(queryId, out _);
                        PendingQueries.TryRemove(queryId, out _);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error in direct streaming endpoint");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Streaming error: {ex.Message}");
                    }
                });

                // ✅ STATUS ENDPOINT (unchanged)
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

                // ✅ SERVICE ENDPOINTS (unchanged but optimized)
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

                        RegisteredServices.AddOrUpdate(serviceId,
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
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Error processing service heartbeat");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

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

                        var pendingQueries = PendingQueries.Values
                            .Where(q => q.ServiceId == serviceId && q.CreatedAt > DateTime.UtcNow.AddMinutes(-10)) // Extended timeout
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

                // ✅ QUERY RESULTS HTML PAGE (unchanged)
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
                    await context.Response.WriteAsync("<!DOCTYPE html>\n<html><head><title>SQL Query Results</title>");
                    await context.Response.WriteAsync("<style>body{font-family:Arial,sans-serif;margin:20px;} table{border-collapse:collapse;width:100%;} th,td{border:1px solid #ddd;padding:8px;text-align:left;} th{background-color:#f2f2f2;} tr:nth-child(even){background-color:#f9f9f9;} .query{margin-bottom:30px;border:1px solid #ccc;padding:10px;border-radius:5px;} .query-text{font-family:monospace;background:#eee;padding:5px;margin:10px 0;} .timestamp{color:#666;font-size:0.9em;} </style></head><body>");
                    await context.Response.WriteAsync("<h1>Recent SQL Query Results</h1>");

                    if (RecentQueryResults.Count == 0)
                    {
                        await context.Response.WriteAsync("<p>No query results available yet.</p>");
                    }
                    else
                    {
                        var sortedResults = RecentQueryResults.Values.OrderByDescending(r => r.Timestamp).ToList();
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
                                    await context.Response.WriteAsync("<table><tr>");
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
                            }
                            catch
                            {
                                await context.Response.WriteAsync($"<pre>{WebUtility.HtmlEncode(result.Result)}</pre>");
                            }
                            await context.Response.WriteAsync("</div>");
                        }
                    }
                    await context.Response.WriteAsync("<script>setTimeout(function() { location.reload(); }, 120000);</script></body></html>");
                });
            });
        }

        // 🚀 NEW: Optimized streaming result collection
        private async Task<StreamingResult> CollectStreamingResults(Channel<string> channel, string queryId, ILogger logger)
        {
            try
            {
                var chunks = new List<string>();
                var timeout = TimeSpan.FromMinutes(15); // Extended timeout for large queries
                var startTime = DateTime.UtcNow;

                logger.LogInformation("🔄 Starting to collect streaming results for query {QueryId}", queryId);

                await foreach (var chunk in channel.Reader.ReadAllAsync())
                {
                    // Check timeout
                    if (DateTime.UtcNow - startTime > timeout)
                    {
                        logger.LogWarning("⏰ Query {QueryId} timed out after {Minutes} minutes", queryId, timeout.TotalMinutes);
                        return new StreamingResult { IsError = true, Data = "Query execution timed out" };
                    }

                    chunks.Add(chunk);

                    // Log progress for very large result sets
                    if (chunks.Count % 100 == 0)
                    {
                        logger.LogDebug("📊 Collected {ChunkCount} chunks for query {QueryId}", chunks.Count, queryId);
                    }
                }

                logger.LogInformation("✅ Collected {ChunkCount} chunks for query {QueryId}", chunks.Count, queryId);

                // 🚀 OPTIMIZED: Process chunks efficiently
                return ProcessStreamingChunks(chunks, queryId, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Error collecting streaming results for query {QueryId}", queryId);
                return new StreamingResult { IsError = true, Data = $"Error collecting results: {ex.Message}" };
            }
        }

        // 🚀 NEW: Efficient chunk processing
        private StreamingResult ProcessStreamingChunks(List<string> chunks, string queryId, ILogger logger)
        {
            try
            {
                if (chunks.Count == 0)
                {
                    return new StreamingResult { IsError = false, Data = "[]" };
                }

                var allData = new List<DataTable>();
                var hasError = false;
                string errorMessage = null;

                foreach (var chunk in chunks)
                {
                    // Handle different chunk types
                    if (chunk.StartsWith("Starting") || chunk.StartsWith("Query execution completed"))
                    {
                        continue; // Skip status messages
                    }
                    else if (chunk.StartsWith("SCHEMA:"))
                    {
                        continue; // Skip schema for now
                    }
                    else if (chunk.StartsWith("BATCH:"))
                    {
                        // Extract batch data
                        var parts = chunk.Split(':', 3);
                        if (parts.Length == 3)
                        {
                            try
                            {
                                var batchData = JsonConvert.DeserializeObject<DataTable>(parts[2]);
                                if (batchData != null && batchData.Rows.Count > 0)
                                {
                                    allData.Add(batchData);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning("⚠️ Error parsing batch for query {QueryId}: {Error}", queryId, ex.Message);
                            }
                        }
                    }
                    else if (chunk.StartsWith("SUMMARY:"))
                    {
                        continue; // Skip summary
                    }
                    else if (chunk.StartsWith("Error:"))
                    {
                        hasError = true;
                        errorMessage = chunk.Substring(6);
                        break;
                    }
                }

                if (hasError)
                {
                    return new StreamingResult { IsError = true, Data = errorMessage };
                }

                // 🚀 OPTIMIZED: Combine all DataTables efficiently
                if (allData.Count == 0)
                {
                    return new StreamingResult { IsError = false, Data = "[]" };
                }

                var combinedTable = allData[0].Clone(); // Create schema
                foreach (var table in allData)
                {
                    foreach (DataRow row in table.Rows)
                    {
                        combinedTable.ImportRow(row);
                    }
                }

                var result = JsonConvert.SerializeObject(combinedTable);
                logger.LogInformation("✅ Combined {TableCount} batches into {RowCount} total rows for query {QueryId}",
                    allData.Count, combinedTable.Rows.Count, queryId);

                return new StreamingResult { IsError = false, Data = result };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Error processing chunks for query {QueryId}", queryId);
                return new StreamingResult { IsError = true, Data = $"Error processing results: {ex.Message}" };
            }
        }

        private bool ValidateClientAuth(HttpContext context, ILogger logger)
        {
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
            {
                return false;
            }
            return apiKey == "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";
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

    // 🚀 NEW: Streaming result wrapper
    public class StreamingResult
    {
        public bool IsError { get; set; }
        public string Data { get; set; }
    }

    // ✅ DATA MODELS (unchanged)
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