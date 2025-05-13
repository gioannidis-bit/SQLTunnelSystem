using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SqlTunnelWebClient.Models;
using SqlTunnelWebClient.Services;
using System.Diagnostics;

namespace SqlTunnelWebClient.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IHttpClientFactory _clientFactory;
        private readonly SettingsService _settingsService;

        public HomeController(
            ILogger<HomeController> logger,
            IHttpClientFactory clientFactory,
            SettingsService settingsService)
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _settingsService = settingsService;
        }

        public async Task<IActionResult> Index()
        {
            var model = new AgentViewModel();

            try
            {
                var settings = _settingsService.GetSettings();

                if (string.IsNullOrEmpty(settings.RelayServerUrl))
                {
                    TempData["ErrorMessage"] = "Relay Server URL is not configured. Please go to SQL Query page and configure the connection settings.";
                    return View(model);
                }

                _logger.LogInformation($"Using Relay Server URL: {settings.RelayServerUrl}");
                var agents = await GetActiveAgents(settings.RelayServerUrl, settings.ApiKey);
                model.Agents = agents;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading agents");
                TempData["ErrorMessage"] = $"Error loading agents: {ex.Message}";
            }

            return View(model);
        }

        private async Task<List<SqlAgent>> GetActiveAgents(string relayServerUrl, string apiKey)
        {
            using (var httpClient = _clientFactory.CreateClient())
            {
                // Ensure server URL ends with a slash
                relayServerUrl = relayServerUrl.TrimEnd('/');

                // Ρύθμιση των headers
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

                // Λήψη των διαθέσιμων agents
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

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}