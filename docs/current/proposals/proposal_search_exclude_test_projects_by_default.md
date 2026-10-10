# Search / ListAll / FindReferences: exclude test projects by default at the tool layer

**Status:** PROPOSED 2026-10-10. Awaiting review; nothing built.

## Motivation

An agent searching for a widely used name or type gets hits from every test project mixed in with the
main code. Most tasks target the main projects, so those hits cost tokens without helping. Today none
of the read/search paths can exclude test projects:

| Path | Enumerates | Existing filter |
| --- | --- | --- |
| `Search(mode: text)` -> `SearchSolutionText`, `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs:446` | every document of `solution.Projects` | `fileGlob` only |
| `Search(<kind>)` / `ListAll` -> `ListAll`, `WorkspaceReadNavigationImpl.cs:332` | every document of `solution.Projects` | `projectName` (exact, one project) |
| `Search(mode: symbol)` -> `LocateSymbolAsync`, `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs:218` | `solution.Projects` | `projectName` (one project) |
| `Search(mode: references)` / `FindReferences` -> `FindCallersAsync` / `FindImplementationsForMemberAsync`, same engine | solution-wide | none |

The only test-project detection in the codebase is the private `TestRunEngine.IsTestProject`
(`RoslynSentinel.Engines.Basic/TestRunEngine.cs:253`), which checks the `.csproj` for
`Microsoft.NET.Test.Sdk`. No search path can reach it.

**Measured share of test hits (2026-10-10, `Search(mode: text)`, test projects approximated by the glob
`RoslynSentinel.Tests*/**/*.cs`):**

| Name | All hits | Hits in test projects | Share |
| --- | --- | --- | --- |
| `ToolCallReason` | 221 in 53 files | 21 in 6 files | 9.5% |
| `SentinelCallToolResult` | 1241 in 65 files | 40 in 14 files | 3.2% |
| `PersistentWorkspaceManager` | 423 in 150 files | 392 in 138 files | 92.7% |

The share depends on the name. Tool-surface types (`ToolCallReason`, `SentinelCallToolResult`) are
used mostly in production code, so the saving is modest. A type that every test fixture constructs
(`PersistentWorkspaceManager`) is almost entirely test hits: 392 of 423, and 138 of 150 files, so the
main-project answer is about 31 hits in 12 files. That is the case this proposal targets: the names
an agent most often needs to look up (core services under test) are the ones where test hits bury
the answer. I did not measure `references`/`callers` or the declaration-kind listings; the saving
there is still a hypothesis. The measurement is the first implementation step.

## Proposal

### Parameter and layering (decided)

Add `bool includeTests` to `Search`, `ListAll` and `FindReferences`.

- **Tool layer (`[McpServerTool]` methods): default `false`.** An agent searching gets main-project
  results unless it opts in.
- **Impl and engine layers (`WorkspaceReadNavigationImpl`, `SymbolRelationshipImpl`,
  `SymbolNavigationImpl`, `SymbolNavigationEngine`): default `true`.** Internal callers and
  tools not touched by this change keep searching everything, so nothing silently narrows.
- `DispatchSearch` (`RoslynSentinel.Tools.Basic/WorkspaceTools.cs:674`) forwards the tool-layer value
  explicitly to every branch.
- Mutating tools (`RenameSymbol`, `ReplaceSnippet`, signature edits, etc.) are out of scope and keep
  full-solution behaviour. A rename or signature change must reach test call sites.

### Test-project detection (decided)

One shared helper, `IsTestProject(Project)`, in `Engines.Basic`, replacing the private copy in
`TestRunEngine` (which then calls the shared one). Keep the `Microsoft.NET.Test.Sdk` project-file
check and its rationale (a tool project that references a test project transitively is not itself
runnable). Cache per project file path, invalidated on the file's last-write time, so the `.csproj`
is not re-read on each call. `Tools.Basic` can reference `Engines.Basic`, so no layering change is needed.

### Where the filter applies

Filter at project enumeration, not after collecting hits, so excluded projects are never scanned:

- `SearchSolutionText`: filter `solution.Projects` before the `Parallel.ForEachAsync`. Skipped
  test documents also make the scan faster.
- `ListAll`: filter `projects` (it already builds a filtered enumerable for `projectName`).
- `LocateSymbolAsync`: same pattern as its existing `projectName` filter.
- References / callers / implementations: see the open question on `SymbolFinder`.

### Overrides and feedback (decided)

- An explicit `projectName` naming a test project, or an explicit `filePath` inside one, wins over
  `includeTests: false`. A model that names a test project gets it.
- When the filter hid results, say so: the status message states how many matches in how many test
  projects were excluded and how to include them (`includeTests: true`). A zero-match result caused
  only by the filter says so too, so a model can tell "does not exist" from "filtered out". This reuses
  the existing `statusMessage` / `listSummary.byProject` channels; no new response fields.
- The parameter description is one short sentence, with the same wording on all three tools, since
  every active tool's schema costs prompt tokens each session.

## Alternatives considered, not pursued

- **Default `false` at every layer.** Rejected: any internal caller or future tool would silently
  skip tests with no signal. Defaulting to `true` below the tool boundary keeps the narrowing a
  deliberate, visible choice made only where an agent's token budget is at stake.
- **No default below the tool layer (required parameter).** Safer against forgotten call sites, but
  touches every existing caller in one change. Open question below.
- **Drop test hits after the search runs.** Rejected: pays the full scan cost and a `maxResults` cap
  can fill up with test hits that are then discarded, returning fewer main-project hits than exist.
- **Name-suffix heuristic (`*.Tests`).** Rejected: misses projects such as `Tests.Tools.Basic` and
  has false positives; the SDK-reference check is what already defines a test project here.
- **A `testsOnly` mode / separate tool.** Not needed: `includeTests: true` plus `projectName` covers
  working inside tests.

## Open questions

- **References path:** how do `FindCallersAsync` / `FindImplementationsForMemberAsync` call
  `SymbolFinder`? If they can pass a document set, scope it to non-test documents (cheaper); if not,
  filter returned locations by owning project. Unverified; read the engine before choosing.
- **Size win for `references`/`callers`:** measure it with a test-heavy name such as
  `PersistentWorkspaceManager` before fixing the default there. The text-search evidence above
  already supports the default for `Search(text)`.
- **Required vs defaulted impl parameter:** the repo prefers mandatory parameters to close footgun
  round-trips (memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`). This proposal
  follows the requester's preference for `true` at the impl layer; revisit if forgotten call sites
  turn up in review.
- **Tool-schema ripple:** `Search` has a long shared description, and `ListAll`'s description is
  shared with the kind-listing path; confirm the schema-size tests and generated tool docs
  (`docs/generated/`) need regenerating.
- **Other read tools** that also enumerate solution-wide (`QuerySymbolRelationships`, call graph
  tools) are unaddressed; decide whether they join in a later slice.

## Cost / risk

- **Touches:** three tool signatures; `WorkspaceReadNavigationImpl`, `SymbolRelationshipImpl`,
  `SymbolNavigationImpl`, `SymbolNavigationEngine`; `TestRunEngine` (helper extraction); tests;
  generated tool docs. Roughly a medium slice, splittable per path (text / ListAll / symbol /
  references).
- **Risk - hidden test callers:** `references` with `callers` is used to judge blast radius, and a
  default that hides test callers can understate it. Mitigated by the exclusion summary line; for a
  blast-radius check the agent must pass `includeTests: true`. Consider naming this in the
  `FindReferences` parameter description.
- **Risk - agent looks for a test helper and finds nothing:** mitigated by the zero-match hint above.
- **Cost:** project-file reads (cached) on first use per project.
- **Tests needed:** each path with the flag both ways; explicit `projectName` on a test project
  overrides the default; exclusion count appears in the status message; impl-layer default still
  returns everything; `TestRunEngine` behaviour unchanged after the helper extraction.

## Related

- `RoslynSentinel.Engines.Basic/TestRunEngine.cs:253` - existing `IsTestProject`.
- `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` - `SearchSolution`, `DispatchSearch`, `ListAll`.
- Memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`.
- `docs/current/proposals/proposal_runtest_tests_for_members.md` - adjacent test-awareness work.
