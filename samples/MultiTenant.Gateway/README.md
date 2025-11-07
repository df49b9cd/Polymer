# Multi-Tenant Gateway Sample

Gateway that hosts multiple tenants inside one OmniRelay dispatcher. Each request carries an `x-tenant-id` header; middleware enforces per-tenant quotas and logs calls, while the handler fans out to tenant-specific HTTP outbounds. This is a lightweight starting point for SaaS/banking platforms that need tenant isolation without duplicating infrastructure.

## Run

```bash
dotnet run --project samples/MultiTenant.Gateway
```

HTTP inbound: `http://127.0.0.1:7210/yarpc/v1/order::place`

> The sample uses placeholder downstream URIs (`http://localhost:7201` and `http://localhost:7202`). Update them to real services or start mock servers locally to see end-to-end traffic.

## Key behaviors

- **Tenant routing:** `x-tenant-id` header selects which outbound to call. Missing or unknown tenants fail with a descriptive error.
- **Per-tenant middleware:** `TenantQuotaMiddleware` tracks request counts and enforces configurable quotas, while `TenantLoggingMiddleware` logs each call with tenant context.
- **Isolated peer sets:** Each tenant registers its own HTTP outbound and can use different peer choosers/middleware as needed.

## Extending it

- Replace the placeholder HTTP outbounds with real gRPC or HTTP services per tenant.
- Swap the quota middleware with a real limiter (Redis, token bucket, etc.).
- Bind tenants and quotas from configuration instead of the hard-coded array to match production needs.
