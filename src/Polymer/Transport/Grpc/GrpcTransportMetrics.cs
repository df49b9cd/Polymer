using System.Diagnostics.Metrics;

namespace Polymer.Transport.Grpc;

internal static class GrpcTransportMetrics
{
    private static readonly Meter Meter = new("Polymer.Transport.Grpc");

    public static readonly Histogram<double> ClientUnaryDuration =
        Meter.CreateHistogram<double>("polymer.grpc.client.unary.duration", unit: "ms", description: "Duration of gRPC client unary calls.");

    public static readonly Histogram<double> ServerUnaryDuration =
        Meter.CreateHistogram<double>("polymer.grpc.server.unary.duration", unit: "ms", description: "Duration of gRPC server unary calls.");
}
