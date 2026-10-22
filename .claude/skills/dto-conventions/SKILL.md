---
name: dto-conventions
description: Reuse-first rule, NEW-DTO justification format, suffix table and wire-safety rules for DTOs (records/classes/structs that carry data) in RoslynSentinel. Load when designing, planning or implementing anything that adds or changes a DTO, request/result type or tool output shape, or when the dto-justification hook blocks a call.
---

# DTO conventions

The repo has 400+ DTOs with overlapping shapes and inconsistent suffixes. Survey, measurements and the
phased plan: `docs/current/proposals/proposal_dto_ownership_role_lineage.md`. Engine result shape decision:
`docs/current/findings/finding_engine_result_shapes_primitives_and_wrapper_usage.md`.

## 1. Reuse before creating

- Use an existing DTO as-is, or extend it (one added property), when it fits.
- A layer that can see an upstream type reuses it. Layering is `Common <- Engines.* <- Tools.* <- Server.*`,
  so Tools can use any Engines/Common DTO directly. Never mirror an upstream DTO field-for-field.
- Schema tags are not a reason to mirror: `ProducesAttribute` lives in `Common`, so put `[Produces]` on the
  original type.
- Where to look: `Search(mode: record)` / `Search(mode: class)` with a name fragment, and
  `Search(mode: symbol)` for a candidate. Until the generated DTO catalog lands (proposal Phase 1), there is
  no shape search; check the 2-3 most likely names by role and domain (e.g. for a git result, the
  `Git*Result` family in `RoslynSentinel.Tools.Basic/GitImpl.cs`).
- Known gap: `Search(mode: references)` on a type name without `filePath` returns NotFound
  (`docs/current/blockers/blocking_error_search_references_by_name_excludes_types.md`). Pass the declaring
  file's `filePath` to list a type's usages.

## 2. A new DTO needs a justification

Report it in the brief, the agent report, the commit message, and the `reason` of the tool call that
creates the type, in this form:

```
NEW-DTO: <Name> (<role>, <layer>.<domain>)
Evaluated: <Existing1> - <why not>; <Existing2> - <why not>
Wire impact: none | new tool output field(s)
```

- `role` is one of `Request`, `Options`, `Result`, `Item`, `View`.
- `Evaluated` names at least one real existing type and a concrete reason: wrong role, wrong shape,
  not visible from this layer, or a deliberate wire projection (e.g. token trimming).
- No justification, no new DTO. "Nothing similar exists" without naming what was checked is not a
  justification.

Example:

```
NEW-DTO: StashListResult (Result, Tools.Basic.Git)
Evaluated: GitLogResult - commit-shaped, no stash index; GitStatusResult - working-tree entries only
Wire impact: new tool output field(s)
```

## 3. Suffix shows role (new types only)

| Role | Suffix | Example (illustrative name) |
| --- | --- | --- |
| Input to an engine or tool | `*Request` | `MoveTypeRequest` |
| Optional knobs on an input | `*Options` | `SearchOptions` |
| Output of an operation | `*Result` | `RenameResult` |
| Element inside a result or request | plain noun | `SymbolLocation`, `Finding` |
| Deliberate Tools-side wire projection | `*View` | `CompactDiagnosticView` |

Do not add new `*Info`, `*Data`, `*Entry`, `*Report`, `*Outcome`, `*Summary` or `*Response` types. Existing
ones are not renamed for this rule alone (see section 5).

## 4. Result shape

Every operation result implements the status interface `IOperationResult` (renamed from
`IResultStatus`): `Status` and a derived `IsError`, `Code`, `Message`. Use a DTO for 2+ fields; deriving from
`EngineResultBase` gives the interface for free. Do not add new uses of `EngineResultWrapper<T>`.

## 5. Wire safety

- Property names are the JSON contract (System.Text.Json, camelCase). Renaming a property changes tool output.
- `LargeResultInfo(resultType: typeof(X).Name)` emits the type's short name, so renaming a type can change
  output too.
- Moving a DTO to another namespace or assembly is wire-safe (no `$type`, no polymorphic metadata).

## 6. Build a DTO from another in one place

Repeated field copies (e.g. `ApplyOutcome` -> `AppliedChangeSummary`, 83 inline sites today) belong in a
factory or `From(...)` method on the target type, not inline at each call site. If you need a copy that
does not exist yet, add the factory and use it.

## 7. The dto-justification hook

A `PreToolUse` hook (`.claude/hooks/enforce-dto-justification.ps1`) inspects type-creating MCP calls
(`Member` addTopLevelType/addMember/replace, `CreateFile`, `WriteFile`, `ReplaceSnippet` including each
`batchEdits[]` entry, `ApplyDiff` in both formats) on production `.cs` files. A type counts as new only if
its name is not already declared in that file on disk and not in the same edit's removed text; a type moved
into another file is new there. When the call declares a new record, or a non-static class/struct whose name does not look like a service (Engine,
Tools, Impl, Helper, Extensions, ...), it requires the call's `reason` to contain either a `NEW-DTO:` block
or `DTO-EXEMPT: <one-line why>`.

If it blocks you:

1. **It is a DTO:** do section 1 first, then retry the same call with the `NEW-DTO:` block in `reason`.
2. **It is not a DTO** (a service, helper, exception, test double, enum-like holder): retry the same call
   with `DTO-EXEMPT: <why>` in `reason`. That is the intended escape, not a workaround.
3. **The hook misfired** (wrong type detected, an edit that adds no type, a test project path): retry with
   `DTO-EXEMPT: hook false positive - <what it got wrong>`, append a journal line
   (`- HH:mm - dto-hook: <what it got wrong>`), and put a `HOOK-FALSE-POSITIVE:` line in your report.

Never work around the hook: do not switch tools, split the declaration across edits, or rename it to dodge the suffix check. Every trigger is logged to
`.claude/journal/dto-hook.jsonl`, so misfires get fixed from the log.
