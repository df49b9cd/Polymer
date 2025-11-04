# Shadowing & Tee Outbounds

OmniRelay supports shadow traffic (also called teeing) by wrapping a primary outbound with a secondary outbound that receives mirrored calls. This is useful for validating new services or transports against production workloads before promoting them fully.

## Quick start

```csharp
var primary = new GrpcOutbound(primaryAddress, "users");
var shadow = new GrpcOutbound(shadowAddress, "users");

var teeOptions = new TeeOptions
{
    SampleRate = 0.05,                // mirror 5% of calls
    ShadowOnSuccessOnly = true,       // only tee successful responses
    ShadowHeaderName = "rpc-shadow",
    ShadowHeaderValue = "users-migration"
};

dispatcherOptions.AddTeeUnaryOutbound("users", null, primary, shadow, teeOptions);
```

When the dispatcher starts, the tee outbound starts both child outbounds. Each call flows to the primary outbound first; the shadow invocation runs in the background without influencing the response returned to the caller. Failures in the shadow path are logged at debug level and never bubble up to the caller.

## TeeOptions

| Property | Description |
| -------- | ----------- |
| `SampleRate` | Probability (0.0 – 1.0) that a call is mirrored. Defaults to `1.0`. |
| `ShadowOnSuccessOnly` | If `true`, only tee successful primary calls. Defaults to `true`. |
| `Predicate` | Optional filter (`Func<RequestMeta, bool>`) that must return `true` for a call to be shadowed. |
| `ShadowHeaderName` / `ShadowHeaderValue` | Header injected into the shadow request to aid observability. Disabled when the name is blank. |
| `LoggerFactory` | Provides loggers used to record shadow failures without polluting the primary call path. |

Two wrapper types are available:

| Wrapper | Interface | Notes |
| ------- | --------- | ----- |
| `TeeUnaryOutbound` | `IUnaryOutbound` | Mirrors unary RPCs and returns the primary response. |
| `TeeOnewayOutbound` | `IOnewayOutbound` | Mirrors oneway RPCs and returns the primary ack. |

The dispatcher exposes extensions (`AddTeeUnaryOutbound`, `AddTeeOnewayOutbound`) so registrations remain declarative. Introspection surfaces both the primary and shadow diagnostics plus sampling metadata through `TeeOutboundDiagnostics`.
