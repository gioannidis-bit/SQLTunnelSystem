namespace SQLTunnelService
{
    public class ServiceSettings
    {
        public string RelayServerUrl { get; set; } = "http://localhost:5175/api";
        public string ServiceId { get; set; } = "sql-service-01";
        public string SecretKey { get; set; } = "";
        public string SqlConnectionString { get; set; } = "";
        public int PollingIntervalMs { get; set; } = 5000;
        public string DisplayName { get; set; } = "SQL Tunnel Service";
        public string Description { get; set; } = "SQL Server Tunnel";
        public string Version { get; set; } = "1.0.0";


        // NEW: Memory management settings
        public int StreamingBatchSize { get; set; } = 1000;
        public int MaxRowsPerQuery { get; set; } = 100000;
        public bool EnableMemoryOptimization { get; set; } = true;
        public int GCAfterRowsThreshold { get; set; } = 10000;
    }
}