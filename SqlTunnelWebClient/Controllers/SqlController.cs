using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SqlTunnelWebClient.Models;
using System.Text;

namespace SqlTunnelWebClient.Controllers
{
    public class SqlController : Controller
    {
        private readonly ILogger<SqlController> _logger;
        private readonly IHttpClientFactory _clientFactory;

        public SqlController(ILogger<SqlController> logger, IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _clientFactory = clientFactory;
        }

        public IActionResult Index()
        {
            return View(new SqlViewModel());
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteQuery(SqlViewModel model)
        {
            try
            {
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

                var result = await ExecuteSqlQuery(model.RelayServerUrl, model.ApiKey, model.Query, parameters);
                model.Result = result;
                model.Error = null;
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

        private async Task<string> ExecuteSqlQuery(string serverUrl, string apiKey, string query, Dictionary<string, object> parameters)
        {
            using (var httpClient = _clientFactory.CreateClient())
            {
                // Ensure server URL ends with a slash
                serverUrl = serverUrl.TrimEnd('/');

                // Add headers
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

                var request = new
                {
                    Query = query,
                    Parameters = parameters
                };

                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync($"{serverUrl}/sql/execute", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Server returned status {response.StatusCode}: {errorContent}");
                }

                return await response.Content.ReadAsStringAsync();
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
    }
}