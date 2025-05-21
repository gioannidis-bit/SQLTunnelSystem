internal class Bridge
{
    private int listenPort;
    private string serverAddress;

    public Bridge(int listenPort, string serverAddress)
    {
        this.listenPort = listenPort;
        this.serverAddress = serverAddress;
    }
}