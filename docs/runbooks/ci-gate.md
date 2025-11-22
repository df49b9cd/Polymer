# CI Gate (WORK-005)

Run locally:

```
./eng/run-ci-gate.sh
```

Env vars:
- `RID` (default `linux-x64`)
- `CONFIG` (default `Release`)
- `SKIP_AOT=1` to skip AOT publishes (not for CI)

What it does:
1) `dotnet build OmniRelay.slnx`
2) Fast tests: Dispatcher.UnitTests, Core.UnitTests
3) AOT publish DataPlane, ControlPlane, CLI (self-contained, single-file) unless skipped

Prereqs: banned-API check and SBOM/signing toggles already wired via Directory.Build.targets/props.
