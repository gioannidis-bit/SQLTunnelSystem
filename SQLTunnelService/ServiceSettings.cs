namespace SQLTunnelService
{
    public class ServiceSettings
    {
        public string RelayServerUrl { get; set; } = "http://localhost:5175/api";
        public string ServiceId { get; set; } = "sql-service-01";
        public string SecretKey { get; set; } = "";
        public string SqlConnectionString { get; set; } = "";
        public int PollingIntervalMs { get; set; } = 2000; // Reduced for better responsiveness
        public string DisplayName { get; set; } = "SQL Tunnel Service";
        public string Description { get; set; } = "High-Performance SQL Server Tunnel";
        public string Version { get; set; } = "2.0.0"; // Updated version

        // 🚀 OPTIMIZED: Enhanced performance settings
        public int StreamingBatchSize { get; set; } = 2000; // Increased for better throughput
        public int MaxRowsPerQuery { get; set; } = 10000000; // 10M rows - truly unlimited
        public bool EnableMemoryOptimization { get; set; } = true;
        public int GCAfterRowsThreshold { get; set; } = 50000; // More aggressive memory management

        // 🚀 NEW: Advanced performance tuning
        public int ConnectionTimeoutSeconds { get; set; } = 30;
        public int CommandTimeoutSeconds { get; set; } = 1800; // 30 minutes for huge queries
        public bool EnableParallelProcessing { get; set; } = true;
        public int MaxConcurrentQueries { get; set; } = 5; // Process multiple queries in parallel

        // 🚀 NEW: SignalR optimization settings
        public bool UseOptimizedSignalR { get; set; } = true;
        public int SignalRKeepAliveIntervalSeconds { get; set; } = 10;
        public int SignalRTimeoutMinutes { get; set; } = 30;

        // 🚀 NEW: Memory and performance monitoring
        public bool EnablePerformanceLogging { get; set; } = true;
        public int PerformanceLogIntervalSeconds { get; set; } = 60;
        public long MaxMemoryUsageMB { get; set; } = 2048; // 2GB memory limit
    }
}