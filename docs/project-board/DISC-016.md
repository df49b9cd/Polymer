# DISC-016 – OmniRelay Mesh CLI Enhancements

## Goal
Expand the `omnirelay mesh` CLI to cover discovery-plane operations: listing peers, leaders, shards, clusters, performing drains/rebalances, validating config, and promoting clusters.

## Scope
- Implement subcommands:
  - `peers list/status/drain/cordon`
  - `leaders status`
  - `shards list/diff/simulate/rebalance`
  - `clusters list/promote/failback`
  - `config validate/show`
  - `transport stats`
- Support JSON and table output, filtering, pagination, and interactive confirmations for destructive actions.
- Integrate with registry + controller APIs introduced in other stories.
- Provide completion scripts (bash/zsh/pwsh) and help docs.

## Requirements
1. **Auth** – CLI must reuse existing OmniRelay auth (token/cert) and surface helpful errors when credentials missing/expired.
2. **UX** – Commands should include progress indicators for long operations, `--watch` flag for streaming updates, and `--dry-run` where applicable.
3. **Testing** – Add integration tests hitting mocked APIs; include golden-file tests for output formatting.
4. **Extensibility** – Command architecture should allow future verbs without refactoring.

## Deliverables
- CLI implementations, tests, and documentation.
- Release notes + version bump for the CLI package.
- Demo scripts for onboarding/training.

## Acceptance Criteria
- CLI workflows exercise registry APIs end-to-end in staging environment.
- Users can drain a peer, monitor progress, and confirm completion via CLI alone.
- `config validate` catches misconfigurations (transport/encoding policy, shard mismatches) before deployment.

## References
- `docs/architecture/service-discovery.md` – “Discoverable peer registry API”, “Transport & encoding strategy”, “Implementation backlog”.
