namespace SqlRelayServer
{
    public class SimpleTdsSettings
    {
        public string ListenAddress { get; set; } = "0.0.0.0";
        public int ListenPort { get; set; } = 11433; // Default port for SQL Server
        public string RelayInternalUrl { get; set; } = "http://localhost:5175";
        public string ApiKey { get; set; } = "\\ql4CkI!{sI\\W[*_1x]{A+Gw[vw+A\\ti";
    }
}