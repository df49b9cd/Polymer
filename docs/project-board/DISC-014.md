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
