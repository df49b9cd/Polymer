using YARPCore.Transport.Http.Middleware;

namespace YARPCore.Transport.Http;

internal interface IHttpOutboundMiddlewareSink
{
    void Attach(string service, HttpOutboundMiddlewareRegistry registry);
}
