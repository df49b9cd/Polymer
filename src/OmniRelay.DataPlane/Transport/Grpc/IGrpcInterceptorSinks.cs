using OmniRelay.Transport.Grpc.Interceptors;

namespace OmniRelay.Transport.Grpc;

internal interface IGrpcClientInterceptorSink
{
    void AttachGrpcClientInterceptors(string service, GrpcClientInterceptorRegistry registry);
}

internal interface IGrpcServerInterceptorSink
{
    void AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry);
}
