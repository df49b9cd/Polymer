# DISC-019 – Chaos Automation & Reporting

## Goal
Automate nightly chaos runs and integrate results into CI/CD gating, including scenario definitions, reporting, and ticketing.

## Scope
- Define `chaos-scenarios.yaml` describing experiments (fault sequence, expected SLOs, metrics to watch).
- Build orchestration scripts/pipelines (GitHub Actions/Azure Pipelines) that deploy the chaos environment (DISC-018), execute scenarios, and collect metrics/logs.
- Generate machine-readable reports (JUnit/JSON) plus human-friendly summaries; upload artifacts to storage.
- Automatically raise tickets/alerts when SLO breaches occur, linking to logs + dashboards.

## Requirements
1. **Scenario DSL** – Support specifying prerequisites, steps (faults), expected convergence time, and rollback steps.
2. **CI Integration** – Chaos job must be triggerable on demand and nightly; gating rules prevent releases when critical scenarios fail.
3. **Metrics ingestion** – Parse Prometheus/Grafana data to compute convergence times and compare against thresholds.
4. **Notifications** – Publish summary to Slack/Teams/email; open GitHub issues/Jira when failures occur.
5. **History** – Maintain historical dashboard of chaos outcomes for trend analysis.

## Deliverables
- Scenario definitions, orchestration scripts, CI pipeline configuration.
- Report generator + artifact uploader.
- Documentation for adding new scenarios and interpreting results.

## Acceptance Criteria
- Nightly chaos pipeline runs automatically, storing reports and notifying stakeholders.
- Release pipeline blocks when chaos results fail until manually overridden.
- Engineers can add a new scenario by editing YAML + pushing, with validation ensuring correctness.

## References
- `docs/architecture/service-discovery.md` – “Testing & chaos validation”, “Implementation backlog item 9”.

## Testing Strategy

### Unit tests
- Cover the scenario DSL parser so prerequisites, fault steps, expected SLOs, and rollback instructions are validated with helpful error messages before pipelines run.
- Test report generators to ensure JSON/JUnit outputs include scenario metadata, pass/fail indicators, convergence metrics, and artifact links.
- Validate gating logic that interprets chaos results, guaranteeing severity thresholds align with release-blocking rules and notifications.

### Integration tests
- Execute the orchestration scripts inside CI (GitHub Actions/Azure Pipelines) to deploy the DISC-018 environment, run sample scenarios, and collect/upload artifacts automatically.
- Run failure-injection cases to confirm pipelines mark builds unstable, block downstream release stages, and emit notifications via Slack/Teams/email plus ticket creation.
- Verify historical dashboards ingest stored reports, enabling trend charts over time with correct linking into Grafana/Prometheus data.

### Feature tests

#### OmniRelay.FeatureTests
- Schedule nightly chaos runs that execute the full scenario catalog, publish summaries, open tickets for regressions, and require explicit override before releases proceed.
- Perform onboarding tests where an engineer adds a new scenario to `chaos-scenarios.yaml`, triggers validation, and observes it running in CI with results captured end to end.

#### OmniRelay.HyperscaleFeatureTests
- Run parallel CI pipelines that fan out across multiple clusters/environments to ensure artifact uploads, gating logic, and notifications scale with dozens of simultaneous scenarios.
- Stress historical reporting by ingesting large archives of chaos outcomes, generating trend dashboards, and verifying release gating logic references long-term data accurately.
