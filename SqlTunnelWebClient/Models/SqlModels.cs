namespace SqlTunnelWebClient.Models
{
    public class SqlQuery
    {
        public string Query { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class SqlParameter
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public bool IsNull { get; set; }
    }

    public class SqlViewModel
    {
        public string Query { get; set; }
        public List<SqlParameter> Parameters { get; set; } = new List<SqlParameter>();
        public string Result { get; set; }
        public string Error { get; set; }
        public bool ShowParameters { get; set; }
        public string RelayServerUrl { get; set; } = "https://your-relay-server.com/api";
        public string ApiKey { get; set; } = "YOUR_SECURE_API_KEY";
    }
}