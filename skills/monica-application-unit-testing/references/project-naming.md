# Application test project ownership and naming

A runnable application test project is named `Test.{ProductionProjectName}`, using the exact stem and casing of the production `.csproj` that owns the behavior being protected. Domain/role ownership remains stable when deployments combine, split, or move roles between AppIds; test names follow that ownership.

## Select the owner

| Protected behavior | Production owner | Test project example |
| --- | --- | --- |
| Entities, domain rules, repositories, persistence mappings, or domain-owned passive synchronization | Domain core | `Test.Domains.User` |
| Commands, queries, role-owned HTTP adapters, subscriptions, jobs, or workers | Role implementation | `Test.Roles.User`, `Test.Roles.User.Realtime` |
| Published DTOs, request contracts, or serialization rules owned by shared contracts | Protocol project | `Test.Platform.Protocol` |
| Shared technical behavior or product infrastructure adapters | Exact shared library | `Test.Platform.BuildingBlocks`, `Test.Platform.Infrastructure` |
| Actual executable composition, provider selection, startup inputs, local profile, generated manifest, role activation or ingress wiring | Business host and its Composition folder | `Test.Product.Host` |
| Presentation behavior, when UI tests are explicitly requested | Actual UI project | `Test.Product.UI` |
| Migration behavior or a tool's own behavior | Actual migration or tool project | `Test.Migrations.DomainUser`, `Test.MigrationRunner` |

These are examples, not a required project inventory. Create a test project only when it contains real tests. Keep one runnable project per production owner by default; do not generate empty mirrors of every domain and role.

Determine ownership from the assertion and production implementation, not from the test factory, the number of project references, a namespace retained during migration, or the host used to execute the scenario. A role test may load its domain, a real host manifest, and collaborating roles without becoming a host test. Test-only composition does not authorize equivalent cross-domain dependencies in production code.

A scenario protecting a cross-domain use case belongs to the production project that initiates or coordinates that use case. It may verify the real participating boundaries together. Put independent tests of a participant's own rules under that participant. Reserve host tests for executable composition and activation contracts; using a host or HTTP transport alone does not establish host ownership. If no owner can be identified, define the protected contract before adding a catch-all test project.

## Project identity and layout

Keep the directory name, `.csproj` stem, `AssemblyName`, and `RootNamespace` equal to the complete test project name. Prefer SDK defaults when they already produce that identity. Namespaces below the root follow the test folders. Preserve the production project's spelling, including specialized role segments and casing.

The repository chooses one test root, such as `tests/` or `src/Tests/`; its project specification records the choice. Keep runnable projects flat beneath it:

```text
src/Tests/
  Test.Domains.User/
    Test.Domains.User.csproj
    Entities/
    Repositories/
  Test.Roles.User/
    Test.Roles.User.csproj
    HandlersCommand/
    HandlersQuery/
    Factories/
    TestDoubles/
  Test.Roles.User.Realtime/
  Test.Platform.Infrastructure/
  Test.Product.Host/
```

Mirror the owned production features inside the project. `Factories/`, `Builders/`, `TestDoubles/`, and `TestData/` hold local test support. Class and scenario naming follows the [testing standards](standards.md); a factory name describes its boundary, for example `UserDomainTestApplicationFactory`, `UserRoleTestApplicationFactory`, or `ProductHostTestApplicationFactory`.

Do not derive project names from a logical AppId, deployment environment, service replica, or former service/layer layout. Do not append `API`, `Domain`, `Unit`, `Integration`, or `E2E` merely to classify tests; retain such a segment only when it is part of the actual production project name. Direct logic, hosted scenarios, and database-backed behavior can coexist under the same owner. Test selection and external-resource requirements belong in the repository's test configuration and scenario metadata.

## Shared test support

Reusable test infrastructure is an ordinary library, not a runnable `Test.*` project. Use the product's shared prefix with `.Testing`, for example `Platform.Testing`; Monica's existing support libraries are `Monica.Testing` and `Monica.Testing.UI`. A support library has no test entrypoint and is not referenced by production projects. Runnable test projects do not reference one another.

Reuse the repository's existing support library while migrating its name. Rename an existing `Platform.UnitTests` library in place when adopting `Platform.Testing`; do not create a parallel toolkit or compatibility alias. Keep role-specific factories and doubles local until there is actual reuse, and do not move concrete business composition into the shared toolkit merely to share a name.

## Migrate existing projects

1. Inventory each test class's protected behavior and actual production owner. Record mixed-owner projects explicitly in the project specification.
2. Rename a single-owner project to the exact target. Split mixed-owner projects, and merge duplicate projects for the same owner while preserving distinct behavior coverage. A filename replacement is not an ownership review.
3. Update the directory, project identity, namespaces, solution entries, project references, friend-assembly declarations, test discovery settings, CI selectors, and documented commands together. Preserve production namespaces and durable runtime identities.
4. Preserve `REQ` identities and verify `Unit` links. A `Unit` trait names the production project and runtime key, so a test-only rename does not itself require changing that value. Keep ambiguous class-to-unit associations explicit.
5. Compare discovered test cases before and after movement, accounting for renamed fully qualified names. Do not treat missing or skipped tests as successful migration. Build the affected solution and run the covering projects using the repository's verification policy.

The project specification owns concrete old-to-new mappings and unresolved migration gaps. New work follows the target convention; existing names remain evidence of work still to migrate, not additional supported naming styles.
