using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }

    public class Startup
    {
        // Αποθήκευση των εκκρεμών SQL queries
        private static readonly ConcurrentDictionary<string, QueryInfo> PendingQueries = new ConcurrentDictionary<string, QueryInfo>();

        // Αποθήκευση των αποτελεσμάτων των SQL queries
        private static readonly ConcurrentDictionary<string, QueryResult> QueryResults = new ConcurrentDictionary<string, QueryResult>();

        // Αποθήκευση των εγγεγραμμένων SQL services
        private static readonly ConcurrentDictionary<string, ServiceInfo> RegisteredServices = new ConcurrentDictionary<string, ServiceInfo>();

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddLogging(configure => configure.AddConsole());
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

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
                        var availableService = RegisteredServices.Values
                            .Where(s => s.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2))
                            .FirstOrDefault();

                        if (availableService == null)
                        {
                            context.Response.StatusCode = 503;
                            await context.Response.WriteAsync("No SQL services currently available");
                            return;
                        }

                        // Δημιουργία μοναδικού ID για το query
                        var queryId = Guid.NewGuid().ToString();

                        // Αποθήκευση του query στα εκκρεμή
                        var queryInfo = new QueryInfo
                        {
                            Id = queryId,
                            Query = sqlRequest.Query,
                            Parameters = sqlRequest.Parameters,
                            ServiceId = availableService.ServiceId,
                            CreatedAt = DateTime.UtcNow
                        };

                        PendingQueries.TryAdd(queryId, queryInfo);

                        logger.LogInformation($"Added query {queryId} to pending queue for service {availableService.ServiceId}");

                        // Περιμένουμε για το αποτέλεσμα (με timeout)
                        var startTime = DateTime.UtcNow;
                        var timeout = TimeSpan.FromSeconds(30);

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
                        logger.LogError(ex, "Error processing SQL execution request");
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
                                s.LastHeartbeat,
                                IsActive = true,
                                 TimeSinceLastHeartbeat = (DateTime.UtcNow - s.LastHeartbeat).TotalSeconds
                            })
                            .ToList();

                        logger.LogInformation($"Active services: {activeServices.Count}");

                        if (activeServices.Count == 0)
                        {
                            foreach (var service in RegisteredServices.Values)
                            {
                                logger.LogInformation($"Inactive service: {service.ServiceId}, Last heartbeat: {service.LastHeartbeat}, Seconds ago: {(DateTime.UtcNow - service.LastHeartbeat).TotalSeconds}");
                            }
                        }

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

                        // Ενημέρωση ή προσθήκη της υπηρεσίας
                        RegisteredServices.AddOrUpdate(
                            serviceId,
                            id => new ServiceInfo
                            {
                                ServiceId = id,
                                LastHeartbeat = DateTime.UtcNow
                            },
                            (id, existing) => {
                                existing.LastHeartbeat = DateTime.UtcNow;
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
                        PendingQueries.TryRemove(queryResult.QueryId, out _);

                        // Αποθήκευση του αποτελέσματος
                        QueryResults.TryAdd(queryResult.QueryId, new QueryResult
                        {
                            QueryId = queryResult.QueryId,
                            Result = queryResult.Result,
                            Error = queryResult.Error,
                            Timestamp = queryResult.Timestamp
                        });

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
        public string Query { get; set; }
        public object Parameters { get; set; }
    }
}