# Design: `SearchSolutionText` always searches both literal and regex

## Problem

`SearchSolutionText` currently takes a mandatory `searchMode: literal | regex` parameter.
Models frequently pick the wrong one — searching literally for what was meant as a regex, or
searching as regex when a literal substring was meant — because training data heavily favors
regex-shaped thinking for "search," while this codebase's own content (C# source) is full of
regex metacharacters (`.`, `(`, `[`, `*`) that occur naturally and legitimately in literal
searches (`.ExitCode`, `Foo(bar)`, `arr[0]`).

This is the second time this parameter's shape has caused real friction:
- Originally an optional `isRegex: bool`, defaulting to `false` and silently used — fixed by
  making the choice a mandatory enum ([resolved: searchmode_literal_override_and_iserror_flag](../blockers/resolved/blocking_error_searchmode_literal_override_and_iserror_flag.md)).
- The enum fix closed the "silent override" bug but didn't close the underlying problem: the
  model still has to *correctly guess* which of two modes it wants, and gets zero results with
  no automatic recovery when it guesses wrong (see [blocking_error_searchsolutiontext_02_phase1_types_and_engine_fix.md](../blockers/blocking_error_searchsolutiontext_02_phase1_types_and_engine_fix.md),
  a real occurrence of exactly this: `searchMode: literal` + regex-escaped pattern `\.ExitCode`
  correctly found nothing, because the caller wanted `.ExitCode`).

## Decision

Remove `searchMode` entirely. `SearchSolutionText` always evaluates **both** interpretations of
`pattern` — as a literal substring and (when it compiles) as a regex — in a single pass over the
solution text, and returns both result sets. There is no mode to get wrong.

## Result shape

Extend the existing `TextSearchMatch` record with a match-kind tag:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchKind { Literal, Regex }

public record TextSearchMatch(
    FilePath filePath, int Line, int Column, string Preview,
    MatchKind MatchedAs, string? EnclosingMember = null);
```

The scan produces one flat list of matches, each tagged with which test it satisfied. A line that
satisfies both the literal substring test and the regex test emits **two** rows (one per kind) —
this keeps the per-kind split a plain equality filter (`Where(r => r.MatchedAs == kind)`) instead
of needing a flags enum, at the cost of the same `(file, line, col)` appearing in both raw lists.

That overlap is then resolved once, after the scan, favoring the literal list as the
always-complete "safe default" and reporting regex as "what extra matches did pattern-matching
find beyond plain substring search":

```csharp
var rawLiteral = results.Where(r => r.MatchedAs == MatchKind.Literal).ToList();
var rawRegex   = results.Where(r => r.MatchedAs == MatchKind.Regex).ToList();

var literalKeys = rawLiteral
    .Select(r => (r.filePath, r.Line, r.Column))
    .ToHashSet();

var regexOnly = rawRegex
    .Where(r => !literalKeys.Contains((r.filePath, r.Line, r.Column)))
    .ToList();
```

Response fields:
- `literalResults: TextSearchMatch[]` — always the full literal substring match set.
- `regexResults: TextSearchMatch[]` — regex matches **not already present** in `literalResults`
  (empty when the pattern has no metacharacters, since every regex match is then also a literal
  match — the common case, and this keeps the payload from doubling for it).
- `regexOverlapCount: int` — how many regex matches were suppressed as duplicates of a literal
  match, so the model can tell "regex found nothing extra" apart from "regex found nothing at
  all."
- `regexPatternValid: bool` — `false` when `pattern` doesn't compile as a regex (e.g. an unmatched
  `(` from real code like `Foo(bar`). `regexResults` is `[]` and `regexOverlapCount` is `0` in
  that case; a warning explains the parse failure. Literal search is unaffected.

Zero-match handling: the call now only reports `NoMatches` when **both** `literalResults` and
`regexResults` are empty (post-dedup) — i.e. neither interpretation of the pattern found anything.

## Scan implementation: single pass, not two round-trips

Per line, compute both tests instead of branching on mode:

```csharp
var literalCol = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
if (literalCol >= 0)
{
    results.Add(new TextSearchMatch(docPath.Absolute, i + 1, literalCol + 1, preview, MatchKind.Literal, enclosingMember));
}

if (regex != null)
{
    try
    {
        var m = regex.Match(line);
        if (m.Success)
        {
            results.Add(new TextSearchMatch(docPath.Absolute, i + 1, m.Index + 1, preview, MatchKind.Regex, enclosingMemberForRegexMatch));
        }
    }
    catch (RegexMatchTimeoutException) { }
}
```

`regex` is compiled **once**, before the parallel document scan, wrapped in a try/catch —
`RegexParseException` sets `regexPatternValid = false` and leaves `regex` null, so the per-line
loop just skips the regex test for every line without re-checking validity per line.

`maxResults` gates the combined raw list (`results.Count < maxResults`), matching the current
"cap total scan work" framing rather than capping each output bucket independently. `hasMorePages`
reporting stays a single top-level flag consistent with today's behavior.

## Breaking change

This removes the `searchMode` parameter and the `TextSearchMode` enum from the public tool
surface — the third contract change to this tool's mode handling. No back-compat shim: per
project convention, no feature flags/optional-legacy-parameter for a tool surface change like
this — just update the signature, its tests, and its description.

## Files touched

- `RoslynSentinel.Common/ToolEnums.cs` — remove `TextSearchMode`; add `MatchKind`.
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` — `TextSearchMatch` record gets
  `MatchedAs`; tool method signature drops `searchMode` param/attribute.
- `RoslynSentinel.Server.Basic/WorkspaceReadNavigationTools.cs` — thin wrapper signature drops
  `searchMode` param/attribute.
- `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs` — actual scan logic: dual-test
  per-line loop, post-scan dedup, new response fields, updated tool `[Description]`.
- `RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs` — existing 6 `SearchSolutionText` tests
  updated to drop `searchMode:` args and assert against `literalResults`/`regexResults`; add
  cases for: pattern matching both (dedup), pattern matching neither (`NoMatches` from both being
  empty), and invalid-regex pattern (`regexPatternValid: false`, literal still works).
