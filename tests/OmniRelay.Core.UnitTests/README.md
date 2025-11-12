# OmniRelay Core Unit Tests

Guidance for designing, extending, and running the Core unit-test suite.

## Scope
- Validate individual classes, helpers, and lightweight collaborations inside `src/OmniRelay/Core` without spinning up real transports or dispatchers.
- Focus on deterministic logic—codecs, call abstractions, middleware, metrics adapters, error translation, and utility types.
- Use test doubles or in-memory primitives where external dependencies (network, filesystem) would otherwise be required.

## Goals
- Catch regressions quickly by keeping tests fast (sub-second) and deterministic so they can run on every edit and CI job.
- Exhaustively cover edge cases (null arguments, cancellation tokens, partial writes, retry/backoff math) that are hard to reproduce in integration tests.
- Provide executable documentation for invariants (e.g., `DuplexStreamCall` must surface cancellation as an `OmniRelayStatusCode.Cancelled`).

## Reasoning
- Fine-grained unit tests isolate root causes: when they fail, the defect is in the code-under-test rather than infrastructure wiring.
- They allow targeted fault injection (throwing custom exceptions, simulating cancelled tokens) without the flakiness of full stack environments.
- Maintaining strong unit coverage reduces the number of scenarios integration tests must explore, shortening feedback loops.

## Methodology
- Prefer plain xUnit facts with minimal fixtures; share setup via helper builders only when unavoidable.
- Stub dependencies using simple fakes/mocks implemented directly in the test project to avoid heavy mocking frameworks.
- Leverage `TestContext.Current.CancellationToken` for deterministic cancellation and `TaskCompletionSource` or channels to assert asynchronous behavior.
- Assert both state and side effects (e.g., completion statuses, emitted errors, metrics counters) to capture the full contract of each component.

## Approach
- Start with “happy path” coverage, then add targeted tests for each failure mode or boundary condition discovered during code review/bug fixes.
- Co-locate regression tests with the code they protect (e.g., tests for `DuplexStreamCall` live under `Transport`).
- Keep tests hermetic: no reliance on wall-clock timers, environment variables, or global static state persisting between tests.
- Document unusual patterns or prerequisites in the test file header so future contributors understand why the test exists and how to extend it.
