using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlTunnelWebClient.Models;
using SqlTunnelWebClient.Services;
using System.Text;

namespace SqlTunnelWebClient.Controllers
{
    public class SqlController : Controller
    {
        private readonly ILogger<SqlController> _logger;
        private readonly IHttpClientFactory _clientFactory;
        private readonly SettingsService _settingsService;

        public SqlController(
            ILogger<SqlController> logger,
            IHttpClientFactory clientFactory,
            SettingsService settingsService)
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _settingsService = settingsService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string serviceId = null)
        {
            var settings = _settingsService.GetSettings();

            var model = new SqlViewModel
            {
                RelayServerUrl = settings.RelayServerUrl,
                ApiKey = settings.ApiKey,
                ServiceId = serviceId
            };

            if (!string.IsNullOrEmpty(serviceId))
            {
                try
                {
                    var agents = await GetActiveAgents(settings.RelayServerUrl, settings.ApiKey);
                    var selectedAgent = agents.FirstOrDefault(a => a.ServiceId == serviceId);

                    if (selectedAgent != null)
                    {
                        model.SelectedAgent = selectedAgent;
                        _logger.LogInformation("🎯 Selected agent: {AgentName} (v{Version})",
                            selectedAgent.DisplayName, selectedAgent.Version);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error loading agent details");
                }
            }

            return View(model);
        }

        [HttpGet("Sql/Agent/{serviceId}")]
        public async Task<IActionResult> Agent(string serviceId)
        {
            var settings = _settingsService.GetSettings();

            var model = new SqlViewModel
            {
                RelayServerUrl = settings.RelayServerUrl,
                ApiKey = settings.ApiKey,
                ServiceId = serviceId
            };

            if (!string.IsNullOrEmpty(serviceId))
            {
                try
                {
                    var agents = await GetActiveAgents(settings.RelayServerUrl, settings.ApiKey);
                    var selectedAgent = agents.FirstOrDefault(a => a.ServiceId == serviceId);

                    if (selectedAgent != null)
                    {
                        model.SelectedAgent = selectedAgent;
                        _logger.LogInformation("🚀 Agent selected: {ServiceId}", serviceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error loading agent details for {ServiceId}", serviceId);
                }
            }

            return View("Index", model);
        }

        [HttpPost]
        public IActionResult SaveSettings(SqlViewModel model)
        {
            try
            {
                _logger.LogInformation("💾 Saving settings - RelayServerUrl: {RelayServerUrl}", model.RelayServerUrl);

                _settingsService.SaveSettings(new ClientSettings
                {
                    RelayServerUrl = model.RelayServerUrl,
                    ApiKey = model.ApiKey
                });

                TempData["SuccessMessage"] = "⚡ Settings saved successfully! Ready for high-performance streaming.";
                _logger.LogInformation("✅ Settings saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error saving settings");
                TempData["ErrorMessage"] = $"❌ Error saving settings: {ex.Message}";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteQuery(SqlViewModel model)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("🚀 Starting optimized query execution");

                // Save settings
                _settingsService.SaveSettings(new ClientSettings
                {
                    RelayServerUrl = model.RelayServerUrl,
                    ApiKey = model.ApiKey
                });

                if (string.IsNullOrWhiteSpace(model.Query))
                {
                    model.Error = "Please enter a SQL query";
                    return View("Index", model);
                }

                Dictionary<string, object> parameters = null;

                if (model.ShowParameters && model.Parameters != null && model.Parameters.Any())
                {
                    parameters = new Dictionary<string, object>();

                    foreach (var param in model.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                    {
                        object value = param.IsNull ? null : ConvertParameterValue(param.Value, param.Type);
                        parameters.Add(param.Name, value);
                    }
                }

                // 🚀 OPTIMIZED: Execute with performance logging
                _logger.LogInformation("⚡ Executing query via optimized streaming - Length: {QueryLength} chars",
                    model.Query.Length);

                var result = await ExecuteSqlQuery(model.RelayServerUrl, model.ApiKey, model.Query, parameters, model.ServiceId);

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("🎉 Query completed in {Duration:F2} seconds", duration.TotalSeconds);

                model.Result = result;
                model.Error = null;

                // Refresh agent info if selected
                if (!string.IsNullOrEmpty(model.ServiceId))
                {
                    try
                    {
                        var agents = await GetActiveAgents(model.RelayServerUrl, model.ApiKey);
                        var selectedAgent = agents.FirstOrDefault(a => a.ServiceId == model.ServiceId);

                        if (selectedAgent != null)
                        {
                            model.SelectedAgent = selectedAgent;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "⚠️ Error refreshing agent details");
                    }
                }

                // 🚀 Enhanced success message with performance metrics
                var resultSize = result?.Length ?? 0;
                TempData["SuccessMessage"] = $"🎉 Query executed successfully in {duration.TotalSeconds:F1}s! " +
                    $"📊 Result size: {FormatBytes(resultSize)} • ⚡ Streamed with unlimited capacity";
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "❌ Error executing query after {Duration:F2} seconds", duration.TotalSeconds);

                model.Error = $"❌ Error: {ex.Message}";
                model.Result = null;

                TempData["ErrorMessage"] = $"❌ Query failed after {duration.TotalSeconds:F1}s: {ex.Message}";
            }

            return View("Index", model);
        }

        private object ConvertParameterValue(string value, string type)
        {
            if (string.IsNullOrEmpty(value)) return value;

            switch (type)
            {
                case "Int":
                    return int.Parse(value);
                case "Decimal":
                    return decimal.Parse(value);
                case "DateTime":
                    return DateTime.Parse(value);
                case "Bool":
                    return bool.Parse(value);
                default:
                    return value;
            }
        }

        private async Task<string> ExecuteSqlQuery(
         string serverUrl,
         string apiKey,
         string query,
         Dictionary<string, object> parameters,
         string serviceId = null)
        {
            using (var httpClient = _clientFactory.CreateClient())
            {
                serverUrl = serverUrl.TrimEnd('/');

                // 🚀 OPTIMIZED: Enhanced HTTP client configuration
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                httpClient.Timeout = TimeSpan.FromMinutes(30); // Extended timeout for large queries

                var request = new
                {
                    Query = query,
                    Parameters = parameters,
                    ServiceId = serviceId
                };

                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var fullUrl = $"{serverUrl}/sql/execute";

                _logger.LogInformation("📤 Sending optimized streaming request to: {Url} | ServiceId: {ServiceId} | Size: {Size} bytes",
                    fullUrl, serviceId ?? "auto-select", json.Length);

                var response = await httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("❌ Server returned {StatusCode}: {Error}", response.StatusCode, errorContent);
                    throw new Exception($"Server returned status {response.StatusCode}: {errorContent}");
                }

                var result = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("✅ Received response: {Size} bytes", result.Length);

                return result;
            }
        }

        private async Task<List<SqlAgent>> GetActiveAgents(string relayServerUrl, string apiKey)
        {
            using (var httpClient = _clientFactory.CreateClient())
            {
                relayServerUrl = relayServerUrl.TrimEnd('/');
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

                var response = await httpClient.GetAsync($"{relayServerUrl}/status");

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Server returned status {response.StatusCode}: {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var agents = JsonConvert.DeserializeObject<List<SqlAgent>>(content) ?? new List<SqlAgent>();

                _logger.LogInformation("📊 Retrieved {Count} active agents", agents.Count);

                return agents;
            }
        }

        [HttpPost]
        public IActionResult AddParameter(SqlViewModel model)
        {
            if (model.Parameters == null)
                model.Parameters = new List<SqlParameter>();

            model.Parameters.Add(new SqlParameter { Type = "String" });
            model.ShowParameters = true;

            _logger.LogDebug("➕ Added parameter - Total: {Count}", model.Parameters.Count);

            return View("Index", model);
        }

        [HttpPost]
        public IActionResult RemoveParameter(SqlViewModel model, int index)
        {
            if (model.Parameters != null && index >= 0 && index < model.Parameters.Count)
            {
                model.Parameters.RemoveAt(index);
                _logger.LogDebug("➖ Removed parameter at index {Index} - Total: {Count}", index, model.Parameters.Count);
            }
            return View("Index", model);
        }

        // 🚀 ENHANCED: QueryResults with better error handling and performance metrics
        public async Task<IActionResult> QueryResults()
        {
            var startTime = DateTime.UtcNow;

            try
            {
                var settings = _settingsService.GetSettings();
                var relayUrl = settings.RelayServerUrl.TrimEnd('/');

                _logger.LogInformation("🔍 Loading query results from: {Url}", relayUrl);

                using var httpClient = _clientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("X-API-Key", settings.ApiKey);
                httpClient.Timeout = TimeSpan.FromMinutes(5); // Extended timeout

                var response = await httpClient.GetAsync($"{relayUrl}/query-results");

                if (!response.IsSuccessStatusCode)
                {
                    var duration = DateTime.UtcNow - startTime;
                    _logger.LogError("❌ Failed to load query results after {Duration:F2}s - Status: {StatusCode}",
                        duration.TotalSeconds, response.StatusCode);

                    TempData["ErrorMessage"] = $"❌ Server returned {response.StatusCode} after {duration.TotalSeconds:F1}s. " +
                        "Please check if the SQL Relay Server is running.";
                    return RedirectToAction("Index", "Sql");
                }

                var html = await response.Content.ReadAsStringAsync();
                var duration2 = DateTime.UtcNow - startTime;

                _logger.LogInformation("✅ Query results loaded successfully in {Duration:F2}s - Size: {Size} bytes",
                    duration2.TotalSeconds, html.Length);

                ViewBag.QueryHtml = html;

                // 🚀 Add performance metrics to ViewBag
                ViewBag.LoadTime = duration2.TotalSeconds;
                ViewBag.DataSize = FormatBytes(html.Length);
                ViewBag.LastUpdated = DateTime.Now;

                return View();
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogError(ex, "❌ Exception loading query results after {Duration:F2}s", duration.TotalSeconds);

                TempData["ErrorMessage"] = $"❌ Error loading query results: {ex.Message} (after {duration.TotalSeconds:F1}s)";
                return RedirectToAction("Index", "Sql");
            }
        }

        // 🚀 NEW: Helper method for formatting bytes
        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
    }
}