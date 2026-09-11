# Test Design Reference

Status: initial guidance researched against Microsoft .NET testing guidance and xUnit.net documentation on 2026-08-26. Prefer the linked first-party sources when framework or runner behavior changes.

## Decision Rule

Test observable behavior at the narrowest boundary that provides meaningful confidence. Keep fast, isolated unit tests as the default for pure rules, parsers, resolvers, and model behavior. Use a real temporary filesystem only when filesystem behavior is the behavior under test. Reserve process, network, native ABI, archive-extraction, and UI checks for separate integration or UI suites.

A test is worth keeping when it is fast, isolated, repeatable, self-checking, and clear about the behavior it protects. When a test requires global state, the real user profile, timing, a running process, or a network service, first ask whether the production code needs an explicit dependency seam. If the behavior is inherently infrastructural, classify it as an integration test instead of disguising it as a unit test.

## Test Boundaries

| Behavior | Test type | Test boundary |
| --- | --- | --- |
| Variable expansion and symbolization | Unit | Variable-scope abstraction |
| Definition and path rules | Unit | Validation component |
| Dependency and selection rules | Unit | Evaluator, parser, and selection engine |
| Configuration precedence | Unit | Resolver with model builders |
| Derived model properties and notifications | Unit | Model instance |
| JSON store schema, backup, and atomic writes | Persistence test | Store with an injected temporary root |
| Archive extraction and download | Integration | Real archive/filesystem or HTTP test server |
| Process launching and exit handling | Integration | Process boundary |
| Native DLL and C ABI behavior | Integration | Managed/native boundary and native binaries |
| Application views and UI behavior | UI test | Running application and UI automation |

Do not add a UI or integration dependency merely to test a pure decision. Extract a small pure collaborator when that makes the behavior independently testable and improves the production design.

## Test Shape

Use Arrange, Act, Assert. Keep the Act to one meaningful operation and make the expected behavior visible in the test name.

Use the form `Method_Scenario_ExpectedResult`:

- `Parse_ValidInput_ReturnsExpectedResult`
- `Resolve_TraversalInput_ReturnsFailure`
- `Evaluate_MissingDependency_ReturnsFalse`

Use `[Fact]` for one named scenario and `[Theory]` for a compact set of inputs with the same behavior. Prefer boundary cases over large random collections. Keep the test data minimal and name constants that carry domain meaning; avoid unexplained magic strings.

Do not duplicate the production algorithm in the test. A test should supply inputs, invoke the public behavior, and assert the result or an intentional interaction. Test private methods through their public callers. Use explicit assertions for the contract, not incidental collection order, object identity, or serializer formatting unless those are part of the contract.

## Isolation and Lifetime

Every test starts with fresh state and can run in any order. Do not share mutable fixtures between tests. Avoid current time, current culture, current directory, environment variables, machine configuration, and real user-data directories unless the test explicitly owns and controls that dependency.

Filesystem tests create a unique temporary root per test or fixture and clean it up in disposal. They must tolerate parallel execution by using unique names and must not modify checked-in fixtures, build output, or user data. Use checked-in fixtures for stable parser inputs; use focused inline XML when the test is about one parser rule.

Tests must be deterministic. Do not use sleeps, retries, random values without a recorded seed, network services, or assertions that depend on filesystem enumeration order. If a concurrency contract is important, test it with explicit synchronization rather than timing.

## Test Doubles

Use the smallest controllable substitute:

- A **stub** supplies predetermined data needed by the system under test.
- A **fake** is a lightweight working implementation, such as an in-memory file-state provider.
- A **mock** is used only when verifying an interaction is itself part of the behavior.

Prefer hand-written stubs and fakes for small interfaces. Do not add a mocking package before a real interaction-verification requirement exists. Do not verify implementation details such as private calls, call order, or incidental logging.

## Async and Exceptions

Await asynchronous production methods. Do not call `.Result`, `.Wait()`, or `async void` in tests. Assert exceptions with the test framework's async assertion API and verify the exception type first; assert message text only when it is a user-facing or documented contract.

Keep cancellation, timeout, and retry behavior in a separate test only when the production API exposes that behavior. Do not make ordinary unit tests slower to simulate asynchronous work.

## Filesystem and Environment Seams

Production services that persist data should accept a root path or path provider with a production default. Tests supply a temporary root. Prefer explicit constructor dependencies over static mutation or changing process-wide environment variables.

Preserve production filenames, schemas, and default locations while adding a seam. File-backed tests should cover missing data, valid round trips, supported legacy formats, future-schema rejection, backup recovery, atomic-save leftovers, and cleanup. They are persistence tests, not pure unit tests, even when they live in the same test project initially.

## Security-Focused Parsing

Treat serialized data, markup, archive metadata, and imported files as untrusted input. Add focused tests for malformed input, oversized input where the production contract limits it, unknown fields or nodes, duplicate values, path traversal, rooted paths, invalid characters, and XML DTD or external-entity input where XML is used. Assert safe failure behavior: rejection, a warning, or an explicitly documented fallback.

Avoid asserting that an unsupported input merely throws unless throwing is the intended contract. A parser that deliberately degrades with warnings should be tested for both the parsed result and the warning.

## Choosing Unit-Test Targets

Start with behavior that has a small public surface and deterministic inputs:

- Pure transformations: parsing, normalization, expansion, formatting, and conversion.
- Business rules: validation, precedence, selection, filtering, and state transitions.
- Boundary handling: empty, missing, maximum, minimum, malformed, duplicate, and unsupported inputs.
- Model behavior: derived values, invariants, and observable property notifications.
- Resolvers: configuration assembly and precedence using small builders or test data objects.

Keep process execution, network behavior, protocol registration, archive extraction, native interop, and UI behavior in their appropriate integration or UI suites.

## Properties and Notifications

For observable models, assert the externally useful property value and the relevant `PropertyChanged` notification. Subscribe before changing the property, make one change per test, and verify that unrelated properties are not claimed to have changed unless that notification is part of the contract.

For collection behavior, assert the resulting order and derived counts through public members. Do not assert event implementation details when the product behavior is already covered by the final state.

## Coverage and CI

Coverage is a diagnostic for untested behavior, not a quality score by itself. Inspect uncovered branches in validators, parsers, resolvers, error handling, and persistence recovery. Do not add an arbitrary global percentage gate that encourages low-value tests.

The default unit-test command must be usable without the application running, a native DLL, network access, or user-specific data. Test artifacts go to a test-specific output directory and are not mixed with production build output. The suite should be runnable repeatedly from a clean checkout and in CI.

When a test needs a special environment, classify it explicitly and keep it out of the default unit run. Report failures with the individual test name and scenario, not only an aggregate count.

## Windows UI Verification

The managed test project does not replace UI automation. Before a release, run the application on
Windows and verify the following flows with keyboard navigation and Accessibility Insights for
Windows (Narrator where available):

- Light, Dark, and High Contrast themes, including selected list rows, focus visuals, status text,
	validation messages, and disabled controls.
- 100% and enlarged text scaling, narrow and wide window sizes, compact NavigationView, list/details
	drill-in and Back behavior, and persisted splitter widths.
- Tab and Shift+Tab order, list arrows/Home/End, Enter/Space activation, Escape dismissal, Delete,
	Ctrl+F, Ctrl+N, and F6/Shift+F6 region cycling.
- Refresh while an item is selected or expanded, while a flyout/dialog is open, and while an edit is
	in progress. Selection, query text, scroll position, focus, expansion, and draft input must remain
	stable unless the affected item was removed.
- Accessible names and states for navigation, command buttons, icon-only controls, list selection,
	progress indicators, InfoBars, dialogs, splitters, and tree nodes.

Keep these checks separate from the default unit/persistence test run because they require a running
WinUI application and Windows accessibility tooling. Record the Windows version, display scale,
text scale, theme, and test result with release verification artifacts.

## Review Checklist

Before merging a test:

- Does the name describe method, scenario, and expected result?
- Does the test verify behavior through a public API?
- Is there one clear Act and a minimal Arrange section?
- Is the test independent of test order, timing, machine state, and user data?
- Is a stub or fake sufficient instead of a broad mock framework?
- Are boundary and failure cases covered?
- Does parser input include relevant untrusted-input cases?
- Does a filesystem test use a temporary root and clean up?
- Is the test correctly classified as unit, persistence, integration, or UI?
- Does it avoid UI, native ABI, network, and process dependencies when it belongs to the unit suite?

## References

- [Microsoft: Unit testing C# code with xUnit](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-csharp-with-xunit)
- [Microsoft: Best practices for unit testing](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)
- [Microsoft: Testing with `dotnet test`](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test)
- [xUnit.net](https://xunit.net/)
