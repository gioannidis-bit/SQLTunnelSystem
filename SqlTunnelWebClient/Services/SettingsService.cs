// Δημιουργήστε ένα νέο αρχείο Services/SettingsService.cs
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
        public string RelayServerUrl { get; set; } = "https://localhost:7021/api";
        public string ApiKey { get; set; } = "YOUR_SECURE_API_KEY";
    }
}