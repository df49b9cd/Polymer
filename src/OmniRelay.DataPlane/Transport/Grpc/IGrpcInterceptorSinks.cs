using OmniRelay.Transport.Grpc.Interceptors;

namespace OmniRelay.Transport.Grpc;

public interface IGrpcClientInterceptorSink
{
    void AttachGrpcClientInterceptors(string service, GrpcClientInterceptorRegistry registry);
}

public interface IGrpcServerInterceptorSink
{
    void AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry);
}
