using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace SqlTunnelWebClient.Services
{
    public class SettingsService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly string _cookieName = "SQLTunnelSettings";

        public SettingsService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public ClientSettings GetSettings()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context != null && context.Request.Cookies.TryGetValue(_cookieName, out var settingsCookie))
            {
                try
                {
                    return JsonConvert.DeserializeObject<ClientSettings>(settingsCookie) ?? new ClientSettings();
                }
                catch (Exception ex)
                {
                    // Καταγραφή του σφάλματος
                    System.Diagnostics.Debug.WriteLine($"Error deserializing settings: {ex.Message}");
                    return new ClientSettings();
                }
            }

            return new ClientSettings();
        }

        public void SaveSettings(ClientSettings settings)
        {
            var context = _httpContextAccessor.HttpContext;
            if (context != null)
            {
                var json = JsonConvert.SerializeObject(settings);
                System.Diagnostics.Debug.WriteLine($"Saving settings: {json}");

                var cookieOptions = new CookieOptions
                {
                    // Το cookie διαρκεί για 1 χρόνο
                    Expires = DateTimeOffset.Now.AddYears(1),
                    // Απενεργοποιούμε το HttpOnly προσωρινά για αποσφαλμάτωση
                    HttpOnly = false,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                };

                context.Response.Cookies.Delete(_cookieName);
                context.Response.Cookies.Append(_cookieName, json, cookieOptions);

                System.Diagnostics.Debug.WriteLine("Settings cookie saved");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("HttpContext is null, cannot save settings");
            }
        }
    }

    public class ClientSettings
    {
        public string RelayServerUrl { get; set; } = "http://192.168.14.121:5175/api";
        public string ApiKey { get; set; } = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";
        public string LastSelectedServiceId { get; set; } // Προσθήκη του τελευταίου επιλεγμένου agent
    }

    // Προσθήκη νέας κλάσης για το ιστορικό ερωτημάτων
    public class QueryHistoryService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly string _sessionKey = "SQLTunnelQueryHistory";
        private readonly int _maxHistoryItems = 20;

        public QueryHistoryService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public List<QueryHistoryItem> GetQueryHistory()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context != null && context.Session.TryGetValue(_sessionKey, out var sessionData))
            {
                try
                {
                    var json = System.Text.Encoding.UTF8.GetString(sessionData);
                    return JsonConvert.DeserializeObject<List<QueryHistoryItem>>(json) ?? new List<QueryHistoryItem>();
                }
                catch
                {
                    return new List<QueryHistoryItem>();
                }
            }

            return new List<QueryHistoryItem>();
        }

        public void AddQueryToHistory(QueryHistoryItem item)
        {
            var history = GetQueryHistory();

            // Αφαίρεση τυχόν διπλών ερωτημάτων
            history.RemoveAll(h => h.Query == item.Query && h.ServiceId == item.ServiceId);

            // Προσθήκη του νέου ερωτήματος στην αρχή
            history.Insert(0, item);

            // Περιορισμός του μεγέθους του ιστορικού
            if (history.Count > _maxHistoryItems)
            {
                history = history.Take(_maxHistoryItems).ToList();
            }

            // Αποθήκευση στο session
            var context = _httpContextAccessor.HttpContext;
            if (context != null)
            {
                var json = JsonConvert.SerializeObject(history);
                var sessionData = System.Text.Encoding.UTF8.GetBytes(json);
                context.Session.Set(_sessionKey, sessionData);
            }
        }
    }

    public class QueryHistoryItem
    {
        public string Query { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string ServiceId { get; set; }
        public string ServiceName { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}