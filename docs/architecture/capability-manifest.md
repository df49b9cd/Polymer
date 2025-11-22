# Capability Manifest

Artifacts (packages, images, binaries) SHOULD ship with a `capabilities` manifest describing available features and build context.

## Example
See `docs/capabilities/manifest-example.json`.

Fields:
- `build.version` – artifact version.
- `build.mode` – InProc | Sidecar | Edge.
- `build.rid` – target RID (e.g., linux-x64).
- `capabilities` – feature flags (http, grpc, http3:conditional, aot-safe, ext-*).
- `security.tls` – notes on TLS requirements.
- `security.signatures` – whether extensions/config must be signed.

For now the manifest is static; wire-up to publish pipeline when signing is available.
