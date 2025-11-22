# OmniRelay Quickstart (Temporarily Paused)

The previous quickstart referenced the `samples/Quickstart.Server` host, but the entire `samples/` tree has been removed during the Native AOT and source-generated configuration migration. A refreshed quickstart will return once the new hosting patterns are finalized. In the meantime:

- Use the manual dispatcher wiring example in `README.md` as a starting point.
- Prefer `AddOmniRelayDispatcherFromConfig("appsettings.dispatcher.json")` with source-generated binding to load trimming-safe dispatcher options.
- Consult `docs/architecture/aot-guidelines.md` and `docs/knowledge-base/samples-and-workflows.md` for current guidance.
