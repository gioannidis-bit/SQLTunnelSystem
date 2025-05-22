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
          // <-- run as a Windows Service when hosted on Windows
          .UseWindowsService(options =>
          {
              options.ServiceName = "SQL Relay Server";
          })
          .ConfigureWebHostDefaults(webBuilder =>
          {
              webBuilder.UseStartup<Startup>();
          });
    }

    public class QueryResultInfo
    {
        public string QueryId { get; set; }
        public string Query { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
        public string ClientEndPoint { get; set; }
    }

    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // Αποθήκευση των εκκρεμών SQL queries
        private static readonly ConcurrentDictionary<string, QueryInfo> PendingQueries = new ConcurrentDictionary<string, QueryInfo>();

        // Αποθήκευση των αποτελεσμάτων των SQL queries
        private static readonly ConcurrentDictionary<string, QueryResult> QueryResults = new ConcurrentDictionary<string, QueryResult>();

        // Αποθήκευση των τελευταίων αποτελεσμάτων ερωτημάτων
        private static readonly ConcurrentDictionary<string, QueryResultInfo> RecentQueryResults =
      new ConcurrentDictionary<string, QueryResultInfo>(StringComparer.OrdinalIgnoreCase);

        // Αποθήκευση των εγγεγραμμένων SQL services
        private static readonly ConcurrentDictionary<string, ServiceInfo> RegisteredServices = new ConcurrentDictionary<string, ServiceInfo>();

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddLogging(configure => configure.AddConsole());

            // Προσθήκη HttpClient Factory
            services.AddHttpClient();

       

       

            // Add RegisteredServices as singleton for the TDS Listener
            services.AddSingleton(RegisteredServices);
            services.AddSingleton(PendingQueries);
            services.AddSingleton(QueryResults);
            services.AddSingleton(RecentQueryResults);

            // Προσθήκη του TDS Listener ως hosted service

         

            // services.AddHostedService<TdsListener>();
        }



        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            // Προσθέστε αυτό στη μέθοδο Configure του Startup.cs
            app.Map("/tds-test", appBuilder =>
            {
                appBuilder.Run(async context =>
                {
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("TDS Listener Test\n");
                    await context.Response.WriteAsync($"TDS Listener active: {true}\n");
                    await context.Response.WriteAsync($"Listening on: {_configuration["TdsSettings:ListenAddress"]}:{_configuration["TdsSettings:ListenPort"]}\n");
                    await context.Response.WriteAsync($"Active sessions: {RegisteredServices.Count}\n");

                    // Εμφάνιση των ενεργών υπηρεσιών
                    await context.Response.WriteAsync("\nActive SQL Tunnel Services:\n");
                    foreach (var service in RegisteredServices.Values.Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2)))
                    {
                        await context.Response.WriteAsync($"- {service.ServiceId}, Last heartbeat: {service.LastHeartbeat}, Version: {service.Version}\n");
                    }
                });
            });


            app.Map("/api/query-results", appBuilder =>
            {
                appBuilder.Run(async context =>
                {
                    // Έλεγχος αν το API key περιλαμβάνεται ως παράμετρος URL
                    string apiKeyFromQuery = context.Request.Query["apiKey"];
                    bool hasValidApiKeyHeader = ValidateClientAuth(context, logger);
                    bool hasValidApiKeyQuery = !string.IsNullOrEmpty(apiKeyFromQuery) &&
                                  apiKeyFromQuery == "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";



                    // Αν δεν υπάρχει έγκυρο API key ούτε στις επικεφαλίδες ούτε ως παράμετρος
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
                        // Εμφάνιση των πιο πρόσφατων πρώτα
                        var sortedResults = RecentQueryResults.Values
                            .OrderByDescending(r => r.Timestamp)
                            .ToList();

                        foreach (var result in sortedResults)
                        {
                            await context.Response.WriteAsync($"<div class='query'>");
                            await context.Response.WriteAsync($"<h3>Query ID: {result.QueryId}</h3>");
                            await context.Response.WriteAsync($"<div class='timestamp'>Executed: {result.Timestamp}</div>");
                            await context.Response.WriteAsync($"<div class='query-text'>{WebUtility.HtmlEncode(result.Query)}</div>");

                            // Αν υπάρχει σφάλμα
                            if (!string.IsNullOrEmpty(result.Error))
                            {
                                await context.Response.WriteAsync($"<div style='color:red;'>Error: {WebUtility.HtmlEncode(result.Error)}</div>");
                                continue;
                            }

                            // Αν δεν υπάρχουν αποτελέσματα
                            if (string.IsNullOrEmpty(result.Result) || result.Result == "[]")
                            {
                                await context.Response.WriteAsync("<div>No results returned</div>");
                                continue;
                            }

                            // Προσπάθεια εμφάνισης των αποτελεσμάτων ως πίνακα
                            try
                            {
                                var dataTable = JsonConvert.DeserializeObject<DataTable>(result.Result);
                                if (dataTable != null && dataTable.Rows.Count > 0)
                                {
                                    await context.Response.WriteAsync("<table>");

                                    // Επικεφαλίδες στηλών
                                    await context.Response.WriteAsync("<tr>");
                                    foreach (DataColumn column in dataTable.Columns)
                                    {
                                        await context.Response.WriteAsync($"<th>{WebUtility.HtmlEncode(column.ColumnName)}</th>");
                                    }
                                    await context.Response.WriteAsync("</tr>");

                                    // Γραμμές δεδομένων
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
                                // Αν δεν μπορεί να μετατραπεί σε DataTable, εμφάνιση του raw JSON
                                await context.Response.WriteAsync("<div>Raw result:</div>");
                                await context.Response.WriteAsync($"<pre>{WebUtility.HtmlEncode(result.Result)}</pre>");
                            }

                            await context.Response.WriteAsync("</div>");
                        }
                    }

                    // Προσθήκη JavaScript για αυτόματη ανανέωση κάθε 5 δευτερόλεπτα
                    await context.Response.WriteAsync("<script>");
                    await context.Response.WriteAsync("setTimeout(function() { location.reload(); }, 120000);");
                    await context.Response.WriteAsync("</script>");

                    await context.Response.WriteAsync("</body></html>");
                });
            });





            // Διαδρομές API για τους πελάτες (clients)
            app.UseEndpoints(endpoints =>
            {
                // Διαδρομή για την εκτέλεση SQL ερωτημάτων
                endpoints.MapPost("/api/sql/execute", async context =>
                {
                    try
                    {
                        // Έλεγχος αυθεντικοποίησης του πελάτη
                        if (!ValidateClientAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        // Ανάγνωση του σώματος του αιτήματος
                        using var reader = new StreamReader(context.Request.Body);
                        var requestBody = await reader.ReadToEndAsync();

                        var sqlRequest = JsonConvert.DeserializeObject<SqlRequest>(requestBody);

                        if (string.IsNullOrEmpty(sqlRequest?.Query))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Invalid request. Query is required.");
                            return;
                        }

                        // Έλεγχος για διαθέσιμες υπηρεσίες SQL
                        var availableServices = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .ToList();

                        if (availableServices.Count == 0)
                        {
                            context.Response.StatusCode = 503;
                            await context.Response.WriteAsync("No SQL services currently available");
                            return;
                        }

                        // Επιλογή συγκεκριμένου agent εάν έχει καθοριστεί
                        ServiceInfo targetService;
                        if (!string.IsNullOrEmpty(sqlRequest.ServiceId) &&
                            availableServices.Any(s => s.ServiceId == sqlRequest.ServiceId))
                        {
                            targetService = availableServices.First(s => s.ServiceId == sqlRequest.ServiceId);
                        }
                        else
                        {
                            // Αν δεν καθορίστηκε κάποιος agent ή δεν υπάρχει, επιλέγουμε τον πρώτο διαθέσιμο
                            targetService = availableServices.First();
                        }

                        // Δημιουργία μοναδικού ID για το query αν δεν έχει ήδη
                        var queryId = string.IsNullOrEmpty(sqlRequest.Id) ?
                            Guid.NewGuid().ToString() : sqlRequest.Id;

                        // Αποθήκευση του query στα εκκρεμή
                        var queryInfo = new QueryInfo
                        {
                            Id = queryId,
                            Query = sqlRequest.Query,
                            Parameters = sqlRequest.Parameters,
                            ServiceId = targetService.ServiceId,
                            CreatedAt = DateTime.UtcNow
                        };

                        PendingQueries.TryAdd(queryId, queryInfo);

                        logger.LogInformation($"Added query {queryId} to pending queue for service {targetService.ServiceId}");

                        // Περιμένουμε για το αποτέλεσμα (με timeout)
                        var startTime = DateTime.UtcNow;
                        var timeout = TimeSpan.FromSeconds(120);

                        while (DateTime.UtcNow - startTime < timeout)
                        {
                            if (QueryResults.TryGetValue(queryId, out var result))
                            {
                                // Αφαιρούμε το αποτέλεσμα από το dictionary
                                QueryResults.TryRemove(queryId, out _);

                                context.Response.ContentType = "application/json";

                                if (result.Error != null)
                                {
                                    context.Response.StatusCode = 500;
                                    await context.Response.WriteAsync(JsonConvert.SerializeObject(new { Error = result.Error }));
                                }
                                else
                                {
                                    await context.Response.WriteAsync(result.Result);
                                }

                                return;
                            }

                            await Task.Delay(200);
                        }

                        // Timeout - αφαιρούμε το εκκρεμές query
                        PendingQueries.TryRemove(queryId, out _);

                        context.Response.StatusCode = 504;
                        await context.Response.WriteAsync("Request timed out");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing SQL execution request from TDS Listener");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // Διαδρομή για την επιστροφή της κατάστασης των υπηρεσιών
                endpoints.MapGet("/api/status", async context =>
                {
                    try
                    {
                        // Έλεγχος αυθεντικοποίησης του πελάτη
                        if (!ValidateClientAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        logger.LogInformation($"Total registered services: {RegisteredServices.Count}");

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
                        logger.LogError(ex, "Error processing status request");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });
            });

            // Διαδρομές API για τις εσωτερικές υπηρεσίες
            app.UseEndpoints(endpoints =>
            {
                // Διαδρομή για εγγραφή/heartbeat της υπηρεσίας
                endpoints.MapPost("/api/services/heartbeat", async context =>
                {
                    try
                    {
                        // Έλεγχος αυθεντικοποίησης της υπηρεσίας
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        var serviceId = context.Request.Headers["X-Service-ID"].ToString();

                        // Διάβασμα των επιπλέον πληροφοριών από το σώμα του request
                        string requestBody;
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            requestBody = await reader.ReadToEndAsync();
                        }

                        var heartbeatData = JsonConvert.DeserializeObject<dynamic>(requestBody);

                        // Ενημέρωση ή προσθήκη της υπηρεσίας
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

                        logger.LogInformation($"Service {serviceId} heartbeat received");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing service heartbeat");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // Διαδρομή για ανάκτηση εκκρεμών queries
                endpoints.MapGet("/api/queries/pending", async context =>
                {
                    try
                    {
                        // Έλεγχος αυθεντικοποίησης της υπηρεσίας
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        var serviceId = context.Request.Headers["X-Service-ID"].ToString();

                        // Ενημέρωση heartbeat της υπηρεσίας
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

                        // Ανάκτηση των εκκρεμών queries για τη συγκεκριμένη υπηρεσία
                        var pendingQueries = PendingQueries.Values
                            .Where(q => q.ServiceId == serviceId &&
                                   q.CreatedAt > DateTime.UtcNow.AddMinutes(-5))
                            .ToList();

                        // Αποστολή των εκκρεμών queries
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(pendingQueries));

                        logger.LogInformation($"Sent {pendingQueries.Count} pending queries to service {serviceId}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing pending queries request");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });

                // Διαδρομή για την αποστολή αποτελεσμάτων από τις υπηρεσίες
                endpoints.MapPost("/api/queries/result", async context =>
                {
                    try
                    {
                        // Έλεγχος αυθεντικοποίησης της υπηρεσίας
                        if (!ValidateServiceAuth(context, logger))
                        {
                            context.Response.StatusCode = 401;
                            await context.Response.WriteAsync("Unauthorized");
                            return;
                        }

                        // Ανάγνωση του σώματος του αιτήματος
                        using var reader = new StreamReader(context.Request.Body);
                        var requestBody = await reader.ReadToEndAsync();

                        var queryResult = JsonConvert.DeserializeObject<QueryResult>(requestBody);

                        if (string.IsNullOrEmpty(queryResult?.QueryId))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Invalid request. QueryId is required.");
                            return;
                        }

                        // Αφαίρεση του query από τα εκκρεμή
                        PendingQueries.TryRemove(queryResult.QueryId, out var queryInfo);

                        // Αποθήκευση του αποτελέσματος
                        QueryResults.TryAdd(queryResult.QueryId, new QueryResult
                        {
                            QueryId = queryResult.QueryId,
                            Result = queryResult.Result,
                            Error = queryResult.Error,
                            Timestamp = queryResult.Timestamp
                        });

                        // Αποθήκευση στα πρόσφατα αποτελέσματα για προβολή
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

                            // Διατήρηση μόνο των τελευταίων 50 αποτελεσμάτων
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

                        logger.LogInformation($"Received result for query {queryResult.QueryId}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing query result");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
                    }
                });
            });
        }

        private bool ValidateClientAuth(HttpContext context, ILogger logger)
        {
            // Απλή υλοποίηση αυθεντικοποίησης με API key
            // Σε παραγωγικό περιβάλλον θα πρέπει να χρησιμοποιηθεί πιο ασφαλής μέθοδος
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
            {
                logger.LogWarning("No API key found in request headers");
                return false;
            }

            // Έλεγχος του API key (θα πρέπει να το αντικαταστήσετε με δικό σας μηχανισμό)
            var validApiKey = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti"; // Αλλάξτε το σε πραγματικό περιβάλλον!

            logger.LogInformation($"Received API key: {apiKey}, Valid key: {validApiKey}, Match: {apiKey == validApiKey}");
            return apiKey == validApiKey;
        }

        private bool ValidateServiceAuth(HttpContext context, ILogger logger)
        {
            // Αυθεντικοποίηση της υπηρεσίας με βάση το ID και το μυστικό κλειδί
            if (!context.Request.Headers.TryGetValue("X-Service-ID", out var serviceId) ||
                !context.Request.Headers.TryGetValue("X-Service-Key", out var serviceKey))
            {
                return false;
            }

            // Απλή υλοποίηση - σε παραγωγικό περιβάλλον θα πρέπει να γίνει πιο ασφαλής έλεγχος
            // Εδώ ελέγχουμε αν έχει καταχωρηθεί η υπηρεσία ή αν είναι η πρώτη της αίτηση
            if (RegisteredServices.TryGetValue(serviceId, out var service))
            {
                // Θα πρέπει να υπάρχει έλεγχος του μυστικού κλειδιού σε πραγματικό περιβάλλον
                return true;
            }

            // Αποδοχή νέων υπηρεσιών (σε παραγωγικό περιβάλλον, θα πρέπει να υπάρχει κάποιος μηχανισμός έγκρισης)
            return !string.IsNullOrEmpty(serviceId) && !string.IsNullOrEmpty(serviceKey);
        }
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
        public string Id { get; set; }  // Προσθήκη αυτής της ιδιότητας
        public string Query { get; set; }
        public object Parameters { get; set; }
        public string ServiceId { get; set; } // Νέο πεδίο για επιλογή συγκεκριμένου agent
    }
}