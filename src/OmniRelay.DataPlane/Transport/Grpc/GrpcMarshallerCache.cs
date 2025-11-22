using Grpc.Core;

namespace OmniRelay.Transport.Grpc;

internal static class GrpcMarshallerCache
{
    public static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create(
        serializer: static payload => payload,
        deserializer: static payload => payload);
}
