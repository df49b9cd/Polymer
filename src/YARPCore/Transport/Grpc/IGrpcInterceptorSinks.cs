using YARPCore.Transport.Grpc.Interceptors;

namespace YARPCore.Transport.Grpc;

internal interface IGrpcClientInterceptorSink
{
    void AttachGrpcClientInterceptors(string service, GrpcClientInterceptorRegistry registry);
}

internal interface IGrpcServerInterceptorSink
{
    void AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry);
}
