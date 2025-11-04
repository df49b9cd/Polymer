using Grpc.Core;

namespace YARPCore.Transport.Grpc;

internal static class GrpcMarshallerCache
{
    public static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create(
        serializer: static payload => payload ?? [],
        deserializer: static payload => payload ?? []);
}
