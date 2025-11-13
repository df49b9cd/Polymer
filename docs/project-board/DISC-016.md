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

## Testing Strategy

### Unit tests
- Cover command routing, option binding, and validation to ensure each verb handles required/optional parameters, interactive confirmations, and mutually exclusive flags.
- Add golden-file tests for table/JSON output so formatting remains consistent when new fields are introduced.
- Test auth/token loaders to guarantee missing or expired credentials raise actionable errors and never leak sensitive values to logs.

### Integration tests
- Run CLI commands against mocked and real registry/controller endpoints, asserting pagination, streaming (`--watch`), and destructive flows (drain/promote/rebalance) behave as documented.
- Execute `config validate` against curated config suites (valid + invalid) to confirm transport/encoding policies and shard expectations are enforced before deployment.
- Validate completion scripts and cross-platform packaging by executing commands on macOS/Linux/Windows CI runners.

### Feature tests

#### OmniRelay.FeatureTests
- Script an end-to-end operator workflow using only the CLI: list peers, cordon/drain one, inspect shards, initiate a rebalance, and monitor progress via `--watch`, ensuring no portal access is needed.
- Replay historical incidents such as config drift to confirm CLI commands surface warnings, suggested remediations, and correct exit codes.

#### OmniRelay.HyperscaleFeatureTests
- Run CLI stress tests that paginate through tens of thousands of objects, stream long-running operations, and manage simultaneous drains/promotions from multiple terminals.
- Execute cross-platform matrix runs (macOS/Linux/Windows) in parallel CI jobs to ensure auth caching, completion scripts, and output rendering stay consistent at scale.
