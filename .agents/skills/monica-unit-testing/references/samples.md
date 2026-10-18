# Monica Testing Samples

Use these projects as structural references:

- `Monica.Testing`
  - Host-owned scenario factory and application lifetime
  - Raw ProjectUnit fast-path fixture
  - Result assertions, database helpers, and deterministic boundary doubles
- `tests/Test.Monica.Core`
  - Pure module-system and result tests
- `tests/Test.Monica.JobScheduler`
  - Module composition, provider, facade, validator, and full-host scenario tests
- `tests/Test.Monica.UI`
  - UI foundation tests
- `tests/Test.Monica.JobScheduler.UI`
  - bUnit component and page-shell tests
- `tests/Test.Monica.Generators.AutoController`
  - Raw Roslyn `GeneratorDriver` coverage for request-owned controller and API-client generation
  - Same-compilation binding, diagnostics, incremental determinism, and no-file-write assertions

## Recommended First Targets

For infrastructure modules:

- module composition or runtime registration behavior
- public facade behavior
- one stable provider, validator, or support type
- one full-host ownership scenario when the module participates in host lifecycle

For UI modules:

- module registration or dependency behavior
- one stable component
- one page-shell state
- one pure support or mapping type

For source generators:

- one exact happy-path generated surface
- one same-compilation dependency scenario
- invalid-input diagnostics rather than runtime fallbacks
- repeated-run determinism and no-file-write coverage

## Reuse Guidance

- Put genuinely cross-project infrastructure in `Monica.Testing`; keep scenario data and service-specific doubles local.
- Copy only the structure of a relevant sample. Choose direct, raw ProjectUnit, full-host, or bUnit boundaries independently for each behavior.
- Use the `monica-application-unit-testing` skill for business-service project layout and scenario-host templates.
