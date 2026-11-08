# Conditional-required-param discoverability gap — broader audit follow-up — CLOSED 2026-09-06

## Resolution

All ~20 grep-flagged sites were triaged. Verdicts:

- **Fixed (real gap, same style as Member):**
  - `SentinelWorkspaceTools.cs` `ListSolutionItems` — added `REQUIRED PARAMS BY KIND` line
  - `SentinelWorkspaceTools.cs` `GetDiagnostics` — added `REQUIRED PARAMS BY SCOPE` line
  - `GitTools.cs` `Git` — added `REQUIRED PARAMS BY OPERATION` line (per-operation blocks were
    already fairly clear, but the summary closes the gap fully)
  - `SentinelAdvancedRefactoringTools.cs` `SyncInterface` — added `REQUIRED PARAMS BY ACTION` line
  - `SentinelAdvancedRefactoringTools.cs` `Inline` — added `REQUIRED PARAMS BY KIND` line; this one
    previously didn't mention `methodName`'s requirement for `kind=parameter` in the description at
    all (a real, not just cosmetic, gap)
  - `SentinelAdvancedRefactoringTools.cs` `WrapRange` — added `REQUIRED PARAMS BY WRAPPER` line
    (per-wrapper text already said "required", but now has the upfront summary too)
  - `SentinelScanTools.cs` `GetPublicApiSurface` — added `REQUIRED PARAMS BY MODE` line. This was
    actually the **inverse** shape: `projectName` is declared `[Consumes(..., required: true)]` in
    the schema (schema-required) but the parameter has a `= null` default and
    `BreakingChangeEngine.GetPublicApiSurfaceAsync` genuinely accepts `projectName: null` when
    `persistBaseline=true` — only the `persistBaseline=false` branch enforces it at runtime. The
    description didn't call out this asymmetry at all.

- **Not fixed — already adequate on inspection** (each states its requirement plainly, near the
  top of the description, not buried mid-paragraph — matches the target bar, just with different
  phrasing than the `Member` fix used):
  - `SentinelWorkspaceTools.cs` `ApplyDiff` — 2nd sentence already says "filepath and unifiedDiff
    are BOTH REQUIRED... omitting it is a common mistake" in caps
  - `SentinelWorkspaceTools.cs` line ~1014 (`confirmationCode`) — dead code, block-commented out
    (`ApplyDiffWithConfirmationCode`, superseded by `ApplyDiff`), not a live tool; the 1059/1165/1174
    grep hits were duplicate lines inside that same commented block
  - `SentinelAdvancedRefactoringTools.cs` `ExtractMembers` — already states per-`as`-value
    requirements inline ("interface (...requires newTypeName)", "partial (...requires
    memberNames)", "superclass (...requires newTypeName...)")
  - `SentinelCommentingTools.cs` `BulkComment` — already uses an explicit bulleted layout with
    "project — ...; projectName required" / "file — ...; filePath required"

No further action needed; this doc can be treated as resolved. See
[issue_member_containername_conditional_required_gap.md](./issue_member_containername_conditional_required_gap.md)
for the originating fix.

## Original context

[issue_member_containername_conditional_required_gap.md](./issue_member_containername_conditional_required_gap.md)
fixed `Member`'s `containerName` gap (2026-09-06) by adding an upfront
`REQUIRED PARAMS BY OPERATION — ...` line to its `[Description]`.
The same session then audited every other tool in `SentinelRefactoringTools.cs` sharing the shape
"multi-operation tool, one parameter schema-optional but runtime-required for a subset of operation
values" and applied the identical fix to four more tools, all now closed:

- `UsingDirective` — `namespaceName` required for add/remove, not view
- `SummaryComment` — `summaryText` required for add only (also had zero `[Consumes]`/`[Description]`
  attribute at all; added one)
- `ConstructorParameter` — `paramName` required for add/remove; `paramType` required for add only
- `MethodSignature` — `paramName` required for add/remove; `paramType` required for add only
  (`nullDefault`/`defaultValue` mutual-exclusion left as-is — already well-documented per-parameter)

## Not yet audited — broader grep hits

A grep across `RoslynSentinel.Server.Basic` and `RoslynSentinel.Server.Advanced` for the same
runtime-check phrasing (`is required for operation`, `is required when`) found the identical
dispatcher shape in more tools, **not yet given the three-question audit** (description reviewed,
runtime check confirmed, verdict reached) that the five fixed tools received — only the existence
of a matching runtime check was confirmed via grep/surrounding-line-context, not the full
description text or parameter attributes. Triage candidates, in file order:

| File | Line(s) | Tool (inferred) | Param(s) / condition |
|---|---|---|---|
| `RoslynSentinel.Server.Basic/GitTools.cs` | 562 | Git commit dispatcher | `message` required when `operation=commit` |
| `RoslynSentinel.Server.Basic/GitTools.cs` | 597 | Git revert dispatcher | `commitHash` required when `operation=revert` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 191 | (kind-dispatch tool) | `projectName` required when `kind=files` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 236 | (kind-dispatch tool) | `projectName` required when `kind=dependencies` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 653, 1059 | (changesetFormat-dispatch, 2 call sites) | `changes` required when `changesetFormat=files` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 763, 1165 | `ApplyDiff` | `filepath` required when `changesetFormat=diff` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 772, 1174 | `ApplyDiff` | `unifiedDiff` required when `changesetFormat=diff` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 1014 | (action-dispatch tool) | `confirmationCode` required when `action=confirmationCode` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 1482 | (scope-dispatch tool) | `scopeName` (as filePath) required when `scope=file` |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` | 1496 | (scope-dispatch tool, same as above) | `scopeName` (as projectName) required when `scope=project` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 611 | (`as`-dispatch tool, likely `ExtractMembers`/`Introduce`-family) | `newTypeName` required when `as=interface` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 634 | same tool | `memberNames` required when `as=partial` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 649 | same tool | `newTypeName` required when `as=superclass` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 700 | `SyncInterface`-family (`action=implement`) | `className` required when `action=implement` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 724 | same tool (`action=sync`) | `className` required when `action=sync` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 819 | (`kind`-dispatch tool, likely `AddCancellationToken`/`PropagateCancellationToken`-family) | `methodName` required when `kind=parameter` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 887, 943 | `WrapRange` (`wrapper=using`) | `name` (disposalName) required when `wrapper=using` |
| `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` | 905, 961 | `WrapRange` (`wrapper=region`) | `name` (regionName) required when `wrapper=region` |
| `RoslynSentinel.Server.Advanced/SentinelCommentingTools.cs` | 84 | (scope-dispatch tool) | `projectName` required when `scope=project` |
| `RoslynSentinel.Server.Advanced/SentinelCommentingTools.cs` | 93 | same tool | `filePath` required when `scope=file` |
| `RoslynSentinel.Server.Advanced/SentinelScanTools.cs` | 850 | (baseline-related tool) | `projectName` required when `persistBaseline=false` |

No other phrasing templates turned up (checked "cannot be null when", "must be provided", "only
valid for", "mutually exclusive" as well) — the codebase consistently uses "X is required for
operation 'Y'" / "X is required when Y=Z" for this class of check.

## Next steps for a follow-up session

For each row above, apply the same three-question audit used on the five fixed tools:
1. Confirm which param(s) are conditionally required for which enum/bool value(s).
2. Check whether the current `[Description]` already states this prominently (upfront summary
   line) or only buries it in per-branch prose (the gap pattern).
3. Check whether the description anywhere reinforces a false "usually optional" impression right
   next to the true requirement.

Where the gap is confirmed, apply the identical fix style: prepend a
`"REQUIRED PARAMS BY OPERATION/KIND/SCOPE — ..."` (or equivalent axis name) sentence to the tool's
`[Description]`, and add a `[Consumes]`/`[Description]` attribute to any parameter that currently
has neither (as was needed for `SummaryComment.summaryText`).

`SentinelWorkspaceTools.cs`'s `ApplyDiff` and the `changesetFormat`/`scope`-dispatch entries are
likely worth doing first — `ApplyDiff` is one of the most heavily-used tools in the suite per prior
dog-fooding feedback, so a discoverability papercut there has higher frequency impact than a
rarely-called Advanced-tier tool.
