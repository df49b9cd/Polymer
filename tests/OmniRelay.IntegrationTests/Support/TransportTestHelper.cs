using System.Net.Sockets;

namespace OmniRelay.IntegrationTests.Support;

internal static class TransportTestHelper
{
    private const int MaxAttempts = 100;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(20);

    public static Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken) =>
        WaitForEndpointAsync(address, DefaultConnectTimeout, DefaultSettleDelay, DefaultRetryDelay, cancellationToken);

    public static Task WaitForHttpEndpointReadyAsync(Uri address, CancellationToken cancellationToken) =>
        WaitForEndpointAsync(address, DefaultConnectTimeout, DefaultSettleDelay, DefaultRetryDelay, cancellationToken);

    private static async Task WaitForEndpointAsync(
        Uri address,
        TimeSpan connectTimeout,
        TimeSpan settleDelay,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(connectTimeout, cancellationToken);

                await Task.Delay(settleDelay, cancellationToken);
                return;
            }
            catch (SocketException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new TimeoutException($"Endpoint {address} did not accept connections within {MaxAttempts} attempts.");
    }
}
