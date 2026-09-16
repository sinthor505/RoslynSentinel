# Plan: consolidate JSON serializer options into a shared helper

## If any step below admits more than one reasonable reading

Stop before acting on it. State: (1) the ambiguity itself, (2) which reading you chose, (3) why -
as a visible note in your final report (or a code comment if it affects a design choice), not just
internal reasoning. Do not silently pick one interpretation after going back and forth - an
unstated tie-break is invisible to whoever reviews your work afterward, even when your reasoning
was sound.

## Background

`System.Text.Json`'s default `JsonSerializerOptions.Encoder` escapes `<`, `>`, `&`, `'`, and all
non-ASCII characters using `\uXXXX` sequences (its default encoder is HTML-safe). This means every
MCP tool response containing ordinary C# syntax - e.g. `List<(int index, string name)>` - gets
serialized as `List<(int index, string name)>`, not the literal characters.

A live model-eval run showed a local model (qwen3.5-9b-coder) reading such a tool response,
narrating its own decode of the `<`/`>` escape sequences back into `<`/`>`, and silently
dropping adjacent literal parentheses during that mental reconstruction - producing a corrupted
`ReplaceSnippet` call that failed for 9+ turns with no clear path to recovery. See
`docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`'s companion investigation for
the full repro (isolated single-turn test: the model's own chain-of-thought says "I need to decode
those: < is '<' and > is '>'... " immediately followed by the paren-dropped output).

This exact defect class was already flagged once, narrowly, in
`docs/obsolete/spec-migration-scan-summary-patch-v1.md` §P2 (for `+` rendering as `+` in a
bucket key), which recommended setting `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` "consistently...
not just the scan summary" and explicitly warned to check whether it was already set before adding
it again. It was never applied, and the doc was archived to `docs/obsolete/` without the fix
landing - confirmed by grep: no `.cs` file in the repo currently references
`UnsafeRelaxedJsonEscaping`.

**Current state - 6 independent `JsonSerializerOptions` instances, already drifted:**

| File | Field | Settings |
| --- | --- | --- |
| `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:66` | `_jsonOptions` (static) | `WriteIndented, PropertyNameCaseInsensitive, JsonStringEnumConverter` |
| `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs:45` | `_jsonOptions` (static) | `WriteIndented, PropertyNameCaseInsensitive, JsonStringEnumConverter` |
| `RoslynSentinel.Common/LargeResultHelper.cs:10` | `JsonOptions` (internal static) | `WriteIndented, PropertyNameCaseInsensitive, JsonStringEnumConverter` |
| `RoslynSentinel.Common/PersistentWorkspaceManager.cs:138` | `_jsonOptions` (static) | `PropertyNameCaseInsensitive` only |
| `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs:24` | `_jsonOptions` (instance) | `PropertyNameCaseInsensitive, JsonStringEnumConverter` |
| `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs:33` | `_debugDumpOptions` (static) | `WriteIndented` only |
| `RoslynSentinel.Server.Advanced/SentinelQualityTools.cs:24` | `_jsonOptions` (instance) | `PropertyNameCaseInsensitive` only - **dead field, never used anywhere else in the file** |

None of the 7 has the relaxed encoder. The 3-way and 2-way splits in settings (`WriteIndented`
present/absent, `JsonStringEnumConverter` present/absent) look like accidental copy-paste drift,
not deliberate design - this plan folds all of them into one shared instance except
`_debugDumpOptions`, which serves a genuinely distinct purpose (human-readable debug dump files on
disk, not MCP tool responses) and stays separate.

## Change

### 1. Create the shared helper

Create a new file `RoslynSentinel.Common/SharedJsonOptions.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Single shared <see cref="JsonSerializerOptions"/> for all MCP tool request/response
/// serialization. UnsafeRelaxedJsonEscaping stops System.Text.Json's default HTML-safe encoder
/// from escaping printable ASCII (notably `&lt;`/`&gt;` as </>) into MCP tool output -
/// escaped angle brackets forced a model to mentally decode C# generic syntax like
/// List&lt;(int, string)&gt; back into literal characters, and adjacent literal parentheses were
/// silently dropped during that reconstruction (see plan_replacesnippet_whitespace_tolerant_match.md's
/// companion investigation for the confirmed repro). None of this output crosses into HTML, so the
/// default encoder's escaping serves no purpose here and only adds a transcription hazard.
/// </summary>
public static class SharedJsonOptions
{
    public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}
```

Do not add any settings beyond what's listed above - this is a consolidation of existing,
already-agreed-on settings (`WriteIndented`, `PropertyNameCaseInsensitive`,
`JsonStringEnumConverter`) plus the one new fix (`UnsafeRelaxedJsonEscaping`). Do not introduce new
converters or options not already present in at least one of the 7 existing instances.

### 2. Migrate call sites - replace the field, delete the local declaration

For each file below, delete the local `JsonSerializerOptions` field declaration entirely and
replace every reference to it with `SharedJsonOptions.Default`. Add
`using RoslynSentinel.Common;` if the file does not already have it (all of these are inside
`RoslynSentinel.Server.Basic` or `RoslynSentinel.Server.Advanced`, both of which already reference
`RoslynSentinel.Common` per the project's dependency direction - Common <- Basic <- Advanced - so
no project-reference change is needed, only the `using`).

a. **`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`**
   - Delete the `_jsonOptions` field (line 66-74).
   - Replace every use of `_jsonOptions` in this file with `SharedJsonOptions.Default`.

b. **`RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs`**
   - Delete the `_jsonOptions` field (line 45-53).
   - Replace every use of `_jsonOptions` in this file with `SharedJsonOptions.Default` (this
     includes the deserialize call sites at lines 831, 894, 921, 935, 946, 957, 970, 982, 994,
     1006, 1016, 1027, 1041, 1052, 1063, 1076, 1101 - all of them, this is an internal
     serialize/deserialize round-trip so the relaxed encoder is harmless here, but consolidating
     removes a second copy of the same settings).

c. **`RoslynSentinel.Common/LargeResultHelper.cs`**
   - Delete the `JsonOptions` field (line 10-18).
   - Replace every use of `JsonOptions` in this file with `SharedJsonOptions.Default`.
   - `LargeResultHelper.JsonOptions` was `internal` (not `private`) - grep the rest of the solution
     for `LargeResultHelper.JsonOptions` before deleting it, in case another file outside this list
     references it directly rather than through `LargeResultHelper`'s own methods. If you find such
     a reference, update it to `SharedJsonOptions.Default` too and note it in your final report
     since it's not in this table.

d. **`RoslynSentinel.Common/PersistentWorkspaceManager.cs`**
   - Delete the `_jsonOptions` field (line 138-141).
   - Replace the one use at line 1080 with `SharedJsonOptions.Default`.

e. **`RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs`**
   - Delete the `_jsonOptions` field (line 24-31).
   - Replace the one use at line 204 with `SharedJsonOptions.Default`.
   - Do **not** touch `_debugDumpOptions` (line 33) or its two use sites (lines 1059, 1105) - that
     field writes human-readable debug dump files to disk, a distinct purpose from MCP tool
     response serialization, and is out of scope for this plan.

f. **`RoslynSentinel.Server.Advanced/SentinelQualityTools.cs`**
   - Delete the `_jsonOptions` field (line 24-27) outright. It is dead code - grep the file for
     `_jsonOptions` first to confirm it has zero other references before deleting (it did at the
     time this plan was written; confirm this is still true, since the file may have changed).
     Do not replace it with `SharedJsonOptions.Default` anywhere, since there is nothing to migrate.

### 3. Do not change anything else

- Do not modify `_debugDumpOptions` in `SentinelAsyncifyTools.cs`.
- Do not add `SharedJsonOptions` usage to any file not listed above - if you find another
  hand-rolled `JsonSerializerOptions` instance anywhere else in the solution while working on this,
  note it in your final report as a follow-up candidate, but do not migrate it as part of this
  plan (out of scope; keeps this change's diff reviewable against the table above).
- Do not change the shape of any serialized output (property names, casing, indentation) - the
  only observable behavior change is that `<`, `>`, `&`, `'`, and other previously-HTML-escaped
  printable ASCII characters now appear literally instead of as `\uXXXX` escapes in tool output.

## Verification

- Build the solution (0 errors, 0 new warnings).
- Run the full `RoslynSentinel.Tests` project and confirm all pre-existing tests still pass. If any
  test asserts on a literal `<`/`>`/`&`/`'`-escaped string in tool output, that
  assertion is expected to now fail and must be updated to expect the literal character instead -
  this is the intended behavior change, not a regression. List every such test you had to update in
  your final report.
- Manually verify the fix: call `ReadFile` (or any tool) against a file containing a C# generic
  with a tuple type, e.g. `List<(int, string)>`, and confirm the returned JSON contains the literal
  `<` and `>` characters, not `<`/`>`.
- State explicitly, in your final report, the final count of `JsonSerializerOptions` instances
  remaining in the solution (expected: 2 - `SharedJsonOptions.Default` and
  `SentinelAsyncifyTools._debugDumpOptions`) and confirm no other stray instance was introduced or
  left behind.

## Out of scope

- Do not add `SharedJsonOptions` to any project other than `RoslynSentinel.Common`.
- Do not change `_debugDumpOptions`'s settings or merge it into `SharedJsonOptions`.
- Do not attempt to fix or audit the `docs/obsolete/spec-migration-scan-summary-patch-v1.md`
  document itself - it is archived and out of scope for this change.
- Do not add any settings to `SharedJsonOptions.Default` beyond `WriteIndented`,
  `PropertyNameCaseInsensitive`, `UnsafeRelaxedJsonEscaping`, and `JsonStringEnumConverter`.
