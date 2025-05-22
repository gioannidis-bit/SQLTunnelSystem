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
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading agent details");
                }
            }

            return View(model);
        }

     
        [HttpGet("Sql/Agent/{serviceId}")]
        public async Task<IActionResult> Agent(string serviceId)
        {
            // Κώδικας για προβολή με συγκεκριμένο agent
            var settings = _settingsService.GetSettings();

            var model = new SqlViewModel
            {
                RelayServerUrl = settings.RelayServerUrl,
                ApiKey = settings.ApiKey,
                ServiceId = serviceId
            };

            // Αν υπάρχει επιλεγμένος agent, φορτώνουμε τις πληροφορίες του
            if (!string.IsNullOrEmpty(serviceId))
            {
                try
                {
                    var agents = await GetActiveAgents(settings.RelayServerUrl, settings.ApiKey);
                    var selectedAgent = agents.FirstOrDefault(a => a.ServiceId == serviceId);

                    if (selectedAgent != null)
                    {
                        model.SelectedAgent = selectedAgent;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading agent details");
                }
            }

            return View("Index", model);
        }

        [HttpPost]
        public IActionResult SaveSettings(SqlViewModel model)
        {
            try
            {
                _logger.LogInformation($"Saving settings - RelayServerUrl: {model.RelayServerUrl}, ApiKey: {model.ApiKey}");

                // Αποθήκευση των ρυθμίσεων
                _settingsService.SaveSettings(new ClientSettings
                {
                    RelayServerUrl = model.RelayServerUrl,
                    ApiKey = model.ApiKey
                });

                // Εμφάνιση μηνύματος επιτυχίας
                TempData["SuccessMessage"] = "Settings saved successfully!";

                _logger.LogInformation("Settings saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings");
                TempData["ErrorMessage"] = $"Error saving settings: {ex.Message}";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteQuery(SqlViewModel model)
        {
            try
            {
                // Αποθήκευση των ρυθμίσεων
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

                // Αποστολή του serviceId στο ExecuteSqlQuery
                var result = await ExecuteSqlQuery(model.RelayServerUrl, model.ApiKey, model.Query, parameters, model.ServiceId);
                model.Result = result;
                model.Error = null;

                // Ανανέωση των πληροφοριών του agent αν είναι επιλεγμένος
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
                        _logger.LogError(ex, "Error refreshing agent details");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing query");
                model.Error = $"Error: {ex.Message}";
                model.Result = null;
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
                // Ensure server URL ends with a slash
                serverUrl = serverUrl.TrimEnd('/');

                // Ρύθμιση των headers
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

                var request = new
                {
                    Query = query,
                    Parameters = parameters,
                    ServiceId = serviceId // Προσθήκη του serviceId στο request
                };

                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var fullUrl = $"{serverUrl}/sql/execute";
                _logger.LogInformation($"Sending request to: {fullUrl}, ServiceId: {serviceId}");

                var response = await httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Server returned status {response.StatusCode}: {errorContent}");
                }

                return await response.Content.ReadAsStringAsync();
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
                return JsonConvert.DeserializeObject<List<SqlAgent>>(content) ?? new List<SqlAgent>();
            }
        }

        [HttpPost]
        public IActionResult AddParameter(SqlViewModel model)
        {
            if (model.Parameters == null)
                model.Parameters = new List<SqlParameter>();

            model.Parameters.Add(new SqlParameter { Type = "String" });
            model.ShowParameters = true;
            return View("Index", model);
        }

        [HttpPost]
        public IActionResult RemoveParameter(SqlViewModel model, int index)
        {
            if (model.Parameters != null && index >= 0 && index < model.Parameters.Count)
            {
                model.Parameters.RemoveAt(index);
            }
            return View("Index", model);
        }

        public async Task<IActionResult> QueryResults()
        {
            var settings = _settingsService.GetSettings();
            var relayUrl = settings.RelayServerUrl.TrimEnd('/');
            using var httpClient = _clientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("X-API-Key", settings.ApiKey);

            var response = await httpClient.GetAsync($"{relayUrl}/query-results");
            if (!response.IsSuccessStatusCode)
            {
                TempData["ErrorMessage"] = $"Server returned {response.StatusCode}";
                return RedirectToAction("Index", "Sql");
            }

            var html = await response.Content.ReadAsStringAsync();
            ViewBag.QueryHtml = html;
            return View();
        }
    }
}