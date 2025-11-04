using System.Net;
using System.Net.Sockets;

namespace YARPCore.Tests;

internal static class TestPortAllocator
{
    public static int GetRandomPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
