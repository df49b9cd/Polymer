# DISC-014 – Sample & Documentation Refresh

## Goal
Update samples, README files, and docker assets to demonstrate the new discovery plane, transport defaults (gRPC/HTTP3), and configuration workflows.

## Scope
- Revise `samples/ResourceLease.MeshDemo` README + docker instructions to highlight gossip, leadership, shard APIs, and HTTP/3 defaults.
- Update appsettings/docker-compose to consume routing metadata from the mesh instead of static URLs.
- Document new CLI commands (`omnirelay mesh ...`) and control-plane endpoints in relevant guides.
- Add diagrams/flowcharts showing bootstrap, transport negotiation, and failover.

## Requirements
1. **Accuracy** – Documentation must match the latest architecture sections; include cross-links to `docs/architecture/service-discovery.md`.
2. **Examples** – Provide step-by-step instructions for enabling HTTP/3, running the CLI, inspecting shard maps, and performing rebalances.
3. **Consistency** – Ensure terminology (clusters, shards, leadership tokens) matches architecture docs.
4. **Validation** – Run `docs` lint/Markdown checks; ensure screenshots/diagrams updated.

## Deliverables
- Updated README files, docs pages, and diagrams.
- Sample configuration files demonstrating new features.
- Verification checklist ensuring instructions succeed end-to-end.

## Acceptance Criteria
- Following the refreshed README allows a new engineer to launch the mesh lab with discovery plane features enabled.
- HTTP/3 default + downgrade behavior explained and observable via provided commands.
- Docs reviewed/approved by product + SRE stakeholders.

## References
- `docs/architecture/service-discovery.md`, `docs/architecture/omnirelay-rpc-mesh.md`.

## Testing Strategy

### Unit tests
- Run markdown/link linting plus Vale/DocsStyle rules to catch outdated references, terminology mismatches, or broken cross-links introduced by the refresh.
- Validate sample configuration files (`appsettings`, docker-compose) with schema/unit tests to guarantee JSON/YAML stays in sync with transport, gossip, and leadership expectations.
- Add doctest-like checks that execute embedded CLI snippets (where feasible) to ensure flag names and outputs stay current.

### Integration tests
- Execute the updated `ResourceLease.MeshDemo` instructions on macOS/Linux runners, confirming containers start, gossip/leadership/shard APIs light up, and HTTP/3 negotiation is observable via CLI/metrics.
- Run docker-compose/Kubernetes walkthroughs end to end, verifying the docs accurately describe prerequisites, environment variables, and cleanup steps.
- Validate documentation-driven flows (e.g., performing a rebalance, diffing shards) by following the README and ensuring screenshots/log snippets match real output.

### Feature tests

#### OmniRelay.FeatureTests
- Conduct usability/buddy-testing sessions where new engineers follow the refreshed docs from scratch, logging ambiguities and confirming every step executes as written.
- Schedule periodic spot checks that replay the documented workflows with the latest CLI to detect drift between instructions and reality.

#### OmniRelay.HyperscaleFeatureTests
- Run parallel documentation validations across multiple OS/cluster combinations to ensure the guidance scales beyond the standard developer setup.
- Review localization or role-specific variants (SRE vs product) in large batches to confirm terminology, links, and diagrams remain accurate across dozens of documents.
