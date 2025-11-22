# WORK-017C – Packaging, Completions, and AOT Publish

## Goal
Package the CLI with shell completions and ensure AOT publish across target RIDs.

## Scope
- Build/publish pipeline; completions for bash/zsh/pwsh.
- AOT publish and smoke tests.

## Acceptance Criteria
- Packages available per RID; completions install tested.
- AOT publish green; smoke tests pass.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
