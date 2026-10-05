# Design: generic gated tool that tags test methods/fixtures with the types they directly call

**Status:** DESIGNED 2026-10-05. Nothing built; open items resolved 2026-10-05 (removal, defaults, collisions, placement). Parameter names are still proposals.

## Problem

Tests for engines, tools and servers are spread across dozens of test projects and files. Some files
are tied to one type; many are junk drawers (the battery projects, the bugfix files). Fixture names
are not a reliable indicator of what a test exercises - they drift and produce near-miss duplicates -
so there is no way to run "all tests that exercise `BaseTypeTextEditBuilder`" across projects.

NUnit (`[Category]`) and the other frameworks support filtering by category
(`dotnet test --filter "Category=BaseTypeTextEditBuilder"`), but the categories do not exist. Applying
them by hand across the suite is not realistic; the data to derive them is already in the compiler
(direct references from test methods to non-test symbols, the same data Visual Studio's CodeLens
"N references / M passing" lens shows).

## Decision

A gated, mutating MCP tool derives categories from **direct** references and applies them as
framework-appropriate attributes. It is solution-agnostic: what counts as a category source is given
by parameters, not hard-coded to RoslynSentinel namespaces.

- One pass over test code, not one `FindReferences` per type: for each test method, walk its body
  with the semantic model, collect referenced symbols, group by containing type, keep those in the
  `targets` scope.
- Method level: one category attribute per distinct target type the method touches (multiple
  attributes per method are fine).
- Class level: a fixture gets a class-level attribute for a type only when that type is touched by
  at least `classLevelThreshold` of the fixture's test methods. Junk-drawer fixtures get method-level
  attributes only.
- Indirect callers (through helpers/fixtures/call-graph walking) are out of scope. Tests that reach
  code only by an MCP tool-name string have no direct reference and are reported, not guessed.
- Visual Studio CodeLens itself is not usable: it is IDE-only with no public query API. Roslyn
  reproduces the same direct-reference result.
- Fixture names are not used as a signal.
- Re-running also removes stale generated categories (references that no longer exist, or types now
  excluded). Only attributes carrying the generated marker (see Algorithm) are ever removed;
  hand-written categories are never touched.
- Simple type name is the category name; on a simple-name collision between target types, a
  namespace qualifier is added (see Algorithm).
- Dry run is the default; the report is the review step before any edit.

## Shape

### Tool surface (names are proposals)

| Param | Meaning | Default |
| --- | --- | --- |
| `targets` | CSV of projects or namespace prefixes whose types become categories, e.g. `RoslynSentinel.Engines.Basic,RoslynSentinel.Tools.Basic`. | required |
| `testScope` | CSV of test projects to scan. | projects referencing a known test framework |
| `excludedTargets` | CSV of namespaces/types never used as categories, e.g. `RoslynSentinel.Common`. Excludes callees (the would-be categories), not test callers. | none |
| `excludedTests` | CSV of test projects/namespaces/types skipped as callers (helpers, a project you do not want touched). | none |
| `maxTestShare` | A type touched by more than this fraction of all scanned tests is auto-excluded as ubiquitous (e.g. a workspace manager, result types). | 0.25 |
| `classLevelThreshold` | Minimum share of a fixture's test methods touching a type for a class-level attribute. | 0.5 |
| `framework` | `auto`, `nunit`, `xunit`, `mstest`. | `auto` |
| `dryRun` | Report only. | true |
| `reportProject` | Test project whose detail section to return. Omitted: return only the per-project summary table. | none |

`targets` is mandatory (no sane default for an arbitrary solution). `maxTestShare` and
`classLevelThreshold` are numeric with defaults so the first call works, but the report echoes the
values used so the caller can adjust them.

### Framework mapping

| Framework | Attribute | Test-method markers |
| --- | --- | --- |
| NUnit | `[Category("X")]` | `[Test]`, `[TestCase]`, `[TestCaseSource]`, `[Theory]`-equivalents |
| xUnit | `[Trait("Category", "X")]` | `[Fact]`, `[Theory]` |
| MSTest | `[TestCategory("X")]` | `[TestMethod]`, `[DataTestMethod]` |

`auto` resolves per test project from its package references, so a mixed solution works. This repo's
test projects are all NUnit (`NUnit` 4.6.1 in `RoslynSentinel.Tests*.csproj`).

### Algorithm

1. Resolve `testScope`; find test methods by the framework markers above.
2. For each test method, walk invocation, object-creation, member-access, `typeof`/`nameof` and
   attribute-argument nodes; resolve to symbols; take the containing type. Interface members map to
   the implementing type only when exactly one implementation is in `targets`; otherwise the
   interface is the category.
3. Keep only types declared in `targets`, minus `excludedTargets`; drop test callers in
   `excludedTests`.
4. Compute per-type test share; auto-exclude those above `maxTestShare` (listed in the report).
5. Per fixture: compute each remaining type's share of the fixture's test methods; assign class-level
   attributes at or above `classLevelThreshold`; assign method-level attributes for the remaining
   (method, type) pairs not already covered by a class-level attribute.
6. Skip any attribute already present (idempotent re-runs).
7. Remove stale generated attributes: any attribute carrying the generated marker whose
   (test, category) pair is not in the computed set from steps 1-5.
8. `dryRun: false`: apply through `ValidateAndApplyHelper.ValidateAndApplyAsync`, batched per test
   project so a compile failure points at a bounded set of files.

**Generated marker.** Each generated attribute is written on its own line with a trailing
`// sentinel:auto-category` comment, e.g.
`[Category("BaseTypeTextEditBuilder")] // sentinel:auto-category`. Removal considers only marked
attributes, which is how generated and hand-written categories are told apart (a category named
after a deleted type is otherwise indistinguishable from a hand-written one). Adding the marker to
a hand-written attribute opts it in to management; removing it opts out. Pre-existing unmarked
attributes that equal a computed category count as "already present" and are left unmarked.

**Category naming.** The category is the simple type name. When two or more target types share a
simple name, every type in the collision group is qualified with the shortest namespace suffix that
makes the group unique (`Basic.Foo` vs `Advanced.Foo`; segment by segment, up to the full
namespace). Qualification is decided across the whole `targets` set, not per run, so adding a
second `Foo` later renames the first one's category; the removal step cleans up the old marked
attribute. The report lists each collision group and the names chosen. Dots are legal in
`--filter "Category=Basic.Foo"`.

### Result shape

Dry run returns: per-fixture dominant type(s) and share; ambiguous fixtures (no type reaches the
class-level threshold); uncategorized tests (no direct target reference); auto-excluded ubiquitous
types with their share; collision groups and the qualified names chosen; stale marked attributes
that would be removed; and the parameter values used. An apply returns the standard applied-change
summary plus counts (class-level, method-level, skipped-existing, removed-stale).

**Paging per test project.** The report is paged by test project. A call without `reportProject`
returns the solution-wide parts (parameter values used, auto-excluded ubiquitous types, collision
groups) and a summary table with one row per test project (tests scanned, fixtures, class-level /
method-level counts, ambiguous fixtures, uncategorized tests, stale to remove). Passing
`reportProject: <name>` returns that project's detail (the per-fixture list, uncategorized test
names, stale attributes). Per-project paging keeps each response bounded, so the large-result offload
(`Common/LargeResultHelper.cs`) remains a backstop only. An apply batches per test project by the
same boundary.

**DI-resolved use.** A reference counts when the test method body names the type or one of its
members: a call or property access on a field/local of that type (including one assigned in
`[SetUp]`), `new X(...)`, `typeof`/`nameof`, or a generic type argument such as
`GetRequiredService<X>()` in the test body. The tool does not trace container wiring, constructor
injection into helpers, or calls that reach `X` only inside a helper method. Such tests surface in
the "uncategorized" list, which measures the gap on the first dry run.

### Registration

Gated, not always-on: it is rarely needed and every active schema costs prompt tokens. Per the
CLAUDE.md "adding a tool" rule it needs the `[McpServerToolType]`/`[McpServerTool]` class, a
`ToolClassRegistry` entry, a DI block in the Server's `ServiceRegistrationExtensions*`, and a
`ToolsetCatalog.ToolsBySet` entry so `McpToolsetControl(toolSet: ..., enabled: true)` can switch it
on. Engine logic stays in an `Engines.*` project with no MCP types.

## Alternatives considered, not pursued

- **Leverage Visual Studio CodeLens.** The reference/test data is not exposed through a public API
  and is IDE-only; useless for an agent or CI. Roslyn reproduces the data.
- **Derive categories from fixture names.** Rejected by the owner: names drift and yield near-miss
  duplicates, and junk-drawer fixtures have no meaningful name.
- **Transitive/indirect callers.** Would tag almost every test with the core types
  (workspace manager, result types). Out of scope; direct callers match the intent ("what does this
  test exercise").
- **One `FindReferences` per target type.** N type lookups over the same test code; a single pass
  per test method is cheaper and yields the multi-category grouping directly.
- **One-off script editing `.cs` directly.** Violates the dog-fooding policy and bypasses the
  compile gate/undo/drift checks. Only the read-only report could plausibly be a standalone utility.
- **Class-level only.** Cannot represent junk-drawer fixtures; method-level multiple attributes are
  required there.

## Files touched

Both the engine and the tool live in Basic (Advanced picks them up additively; no duplicate there).

- `RoslynSentinel.Engines.Basic/...` (new engine) - reference scan, grouping, share computation,
  collision qualification, edit planning (add and remove-stale). No MCP types.
- `RoslynSentinel.Tools.Basic/...` (new `*Tools` class + `*Impl`) - the tool surface and result
  mapping.
- `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` - registry entry.
- `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs` and
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` - DI block.
- `RoslynSentinel.Common/ToolsetCatalog.cs` - on-demand toolset entry.
- `docs/generated/*` - regenerate with `scripts/Generate-ArchitectureMap.ps1`
  (`ArchitectureDocFreshnessTests` fails otherwise).
- New tests project placement: whichever `Tests.*` project matches the engine/tool layer.

## Cost / breaking changes

- First apply over the full suite touches hundreds of test files. Mitigated by dry-run default and
  per-test-project batching; still a very large diff, so commit it separately from the tool.
- Categories are generated, not curated: they go stale as tests change. Re-running is idempotent
  and removes stale marked attributes, so the tool must be re-run to keep categories current
  (no automatic trigger). The marker comment adds one trailing comment per generated attribute.
- Test-method detection and simple-name categories can mis-tag; the report is the control, not
  a guarantee.
- No breaking change to existing tools.

## Open items

- Defaults of `maxTestShare` (0.25) and `classLevelThreshold` (0.5) are accepted; re-check them
  against the first dry run on this repo.
- Revisit the marker-comment mechanism if trailing comments on attributes prove noisy.
