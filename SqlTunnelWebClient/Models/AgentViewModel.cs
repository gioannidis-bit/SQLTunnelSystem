namespace SqlTunnelWebClient.Models
{
    public class AgentViewModel
    {
        public List<SqlAgent> Agents { get; set; } = new List<SqlAgent>();
    }

    public class SqlAgent
    {
        public string ServiceId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string ServerInfo { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsActive { get; set; }
        public double TimeSinceLastHeartbeat { get; set; }
               
        public SqlAgent SelectedAgent { get; set; }
    }
}