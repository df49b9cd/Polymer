using OmniRelay.Transport.Http.Middleware;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Internal hook implemented by HTTP outbounds to receive the resolved middleware registry.
/// </summary>
internal interface IHttpOutboundMiddlewareSink
{
    /// <summary>
    /// Attaches the middleware registry for a service, enabling per-procedure resolution.
    /// </summary>
    /// <param name="service">The service name.</param>
    /// <param name="registry">The resolved middleware registry.</param>
    void Attach(string service, HttpOutboundMiddlewareRegistry registry);
}
