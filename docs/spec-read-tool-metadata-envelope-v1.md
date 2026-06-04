# RoslynSentinel — Read-Tool Metadata Envelope Spec
<!-- v1 -->

**Date:** 2026-05-30
**Purpose:** Add a lean, structured metadata envelope to all source-reading tools (`get_method_source`, `get_file_outline`, and any future read aperture). The envelope gives the agent the file's scope up front (enabling an informed read-strategy decision) and honestly signals truncation (preventing the read/search-disagreement spiral). This is the read-side equivalent of `BatchResultSummary` from the batch-first spec — a shared, versioned return contract for retrieval.
**Target server:** RoslynSentinel (source available locally)
**Mode:** Implementation spec — hand to an agent with workspace access to the RoslynSentinel source.

---

## Background and rationale

Coding agents commonly receive silently-truncated file reads. The model forms an accurate-but-incomplete belief ("this file is lines 1–250"), then a later search returns a line number in the unread region, and the two observations are mutually impossible under the model's corrupted world model. A careful reasoner correctly flags the contradiction and burns large amounts of inference trying to resolve a problem the tooling created and hid. The fix is cheap: tell the model the truth about what it received, in a structured field it can act on.

Two distinct benefits, served by two parts of the envelope:

1. **Honesty (anti-spiral):** a truncation flag plus the returned range reconciles the read/search contradiction the moment it would otherwise arise. This is the load-bearing part.
2. **Strategy signal (informed dispatch):** scope (line/byte count) delivered *before* the agent consumes content lets it choose read-whole vs. read-range vs. fetch-outline vs. switch-to-search, instead of blindly paginating a large file 200 lines at a time hoping to find something.

This applies specifically because RoslynSentinel is agentic — the agent chooses what to read, so a leading scope signal is an actionable decision, not a courtesy.

---

## Design principles (carried from prior specs)

- **Lean envelope, heavy detail behind a pointer.** The envelope ships on *every* read and is therefore a fixed per-call tax. It carries only what the agent needs to make the *next* decision. Richer structure (a full symbol outline) is a *separate* call the agent makes only when the envelope tells it the file is large enough to warrant the parse.
- **Conditional richness keyed on size.** Small file → bare envelope, agent reads whole. Large file → envelope flags that an outline is available. The envelope is uniform; what it *advertises* scales with the file.
- **Ship the free fields first.** Line count and byte count fall out of reading the file at all. Symbol count requires a parse — cheap with Roslyn, but not zero. Ship the free fields, observe whether the agent still churns on large files, add the parse-dependent fields when they earn their place.
- **Positively stamp completeness.** A complete read is marked complete, not merely *un*-marked. Forcing the agent to reason about absence-of-a-truncation-flag is weaker than an explicit `isComplete = true`. Every fact the tool can state is a fact the agent should not have to infer.
- **Structured field, not inline string.** Metadata lives in its own field, never as bracketed text prepended/appended to the content. Inline markers can be misattributed to file content (a log line or source comment that looks like a marker). A separate field removes the ambiguity by construction.
- **Version the envelope.** A `SchemaVersion` field from day one so a consumer can detect a shape it doesn't fully understand instead of silently reading a missing field as null.

---

## The envelope type

```csharp
public class ReadEnvelope
{
    public int     SchemaVersion   { get; set; }   // start at 1; bump on shape change

    // Scope — the strategy signal (always present, free to compute)
    public int     LineCount       { get; set; }   // total lines in the file on disk
    public long    ByteCount       { get; set; }   // total bytes in the file on disk

    // Truncation — the anti-spiral honesty pair
    public bool    IsComplete      { get; set; }   // true if the full file/method was returned
    public int     ReturnedFromLine { get; set; }  // first line included (1-based)
    public int     ReturnedToLine   { get; set; }  // last line included (1-based)
    public int?    ContinuationOffset { get; set; } // next line to request; null if IsComplete

    // Conditional-richness advertisement (no parse required to set this)
    public bool    OutlineAvailable { get; set; }  // true when file exceeds the outline threshold

    public string? Error           { get; set; }   // non-null only on hard failure
}
```

### Field notes

- `LineCount` / `ByteCount` are the **total file** figures, not the returned-slice figures. The returned slice is described by `ReturnedFromLine`/`ReturnedToLine`. This is the distinction that reconciles a later search hit: search returns line 800, envelope said `ReturnedToLine = 250` of `LineCount = 1400` → the agent knows 800 is in the unread region, no contradiction.
- `IsComplete = true` ⟹ `ReturnedFromLine = 1`, `ReturnedToLine = LineCount`, `ContinuationOffset = null`. Set all of them; do not rely on the agent inferring completeness from the line math.
- `ContinuationOffset` is the literal value to pass back to continue reading. Hand the agent the parameter, don't make it compute it.
- `OutlineAvailable` is set by a **size comparison only** — no parse. It is the envelope telling the agent "this file is big enough that calling `get_file_outline` is probably worth it before you read blind."

---

## Return shape — envelope wraps content

The reading tools return the envelope alongside the content, content in its own field:

```csharp
public class MethodSourceResult
{
    public ReadEnvelope Envelope { get; set; }
    public string?      Content  { get; set; }   // raw source text of the returned slice; null on error
    public string       FilePath { get; set; }
    public string       MethodName { get; set; }
    public bool         MethodFound { get; set; }
}

public class FileOutlineResult
{
    public ReadEnvelope        Envelope { get; set; }
    public string              FilePath { get; set; }
    public List<OutlineEntry>  Symbols  { get; set; }   // the parsed structure
}

public class OutlineEntry
{
    public string Kind        { get; set; }   // "class" | "interface" | "method" | "property" | "enum" | "struct"
    public string Name        { get; set; }
    public string Signature   { get; set; }   // for methods: full signature line
    public int    StartLine   { get; set; }
    public int    EndLine     { get; set; }
    public string? ContainingType { get; set; } // null for top-level types
}
```

**Content stays a raw string** for source reads. Do NOT structure the source content itself — the agent reasons over code as text, and the outline (separate call) provides the structure when needed. (The structured-content fork discussed for CSV/tabular data does not apply here; this spec covers source reads only.)

---

## Thresholds (configurable, start generous, tune on observed behavior)

| Threshold | Suggested default | Meaning |
|---|---|---|
| `ReadWholeMaxLines` | 800 | At or below this, default to returning the whole file; truncation should not occur. |
| `OutlineAvailableMinLines` | 400 | At or above this, set `OutlineAvailable = true`. |
| `MaxReturnedLines` | 1200 | Hard cap on lines returned in a single read call; above this the read truncates and sets `IsComplete = false`. |

Notes:
- These are **server-side configurable** (e.g. `read-limits.json` alongside `rate-limits.json`, or environment variables). Do not hardcode. The single highest-impact configurable setting is the read cap; expose it.
- `OutlineAvailableMinLines` is deliberately *below* `ReadWholeMaxLines` so there's a band (400–800 lines) where the file is still read whole but the outline is also advertised — the agent can read whole *or* orient via outline, its choice.
- Tiered behavior: file ≤ `ReadWholeMaxLines` → whole file, `IsComplete = true`. File > `MaxReturnedLines` and no range requested → first `MaxReturnedLines` lines, `IsComplete = false`, `ContinuationOffset` set, `OutlineAvailable = true`.

---

## Tool changes

### `get_method_source`
- Compute the envelope for the **containing file** (so `LineCount`/`ByteCount`/`OutlineAvailable` describe the file, giving the agent file-level scope even when it asked for one method).
- The returned slice is the method body; `ReturnedFromLine`/`ReturnedToLine` are the method's line span.
- If the method exceeds `MaxReturnedLines` (very large method): truncate, `IsComplete = false`, `ContinuationOffset` set.
- `MethodFound = false` → `Content = null`, envelope still populated with file scope, no error thrown.

### `get_file_outline`
- Returns `Symbols` (the parse) plus the envelope. `OutlineAvailable` is moot here (the agent already has the outline) but set it consistently for uniformity.
- Skip generated files' noise per existing conventions (`*.Designer.cs`, `*.g.cs`) — or include with a `Kind`-level marker; implementer's call, but be consistent with how the namespace-mismatch tool handles generated files.

### Any future raw read aperture (e.g. a non-semantic `read_docs` content read)
- Same envelope. Line/byte counts and truncation apply to any file read. `OutlineAvailable` stays `false` for non-source files (no semantic outline to offer).

---

## Implementation order

1. **`ReadEnvelope` type + a shared `BuildEnvelope(filePath, returnedFrom, returnedTo, isComplete)` helper.** Compute `LineCount`/`ByteCount` once, set the truncation fields, set `OutlineAvailable` by threshold comparison. Build and unit-test this first — it's the shared foundation, same role `DocPathGuard.ResolveSafe` played in the documentation spec.
2. **Wire into `get_method_source`.** Lower risk; validates the envelope against a tool that returns a sub-file slice.
3. **Wire into `get_file_outline`.** Validates the envelope against a tool that returns structure.
4. **Make thresholds configurable** (`read-limits.json` or env vars) with the defaults above.
5. **Fold the canonical return contract back into the tools-review doc** as the shared read-side shape (sibling to `BatchResultSummary`).

---

## Tests required

### `BuildEnvelope` helper
1. File ≤ `ReadWholeMaxLines` → `IsComplete = true`, `ReturnedFromLine = 1`, `ReturnedToLine = LineCount`, `ContinuationOffset = null`.
2. File > `MaxReturnedLines`, whole-file requested → `IsComplete = false`, `ReturnedToLine = MaxReturnedLines`, `ContinuationOffset = MaxReturnedLines + 1`.
3. File in the 400–800 band → `IsComplete = true` AND `OutlineAvailable = true` (read whole but outline advertised).
4. File < `OutlineAvailableMinLines` → `OutlineAvailable = false`.
5. `LineCount`/`ByteCount` reflect the **total file**, not the returned slice, in every truncated case.
6. `SchemaVersion` populated on every envelope.
7. Empty file (0 lines) → `IsComplete = true`, counts zero, no error.

### `get_method_source`
8. Small method in small file → whole method returned, `IsComplete = true`, envelope describes the *file's* scope.
9. Method not found → `MethodFound = false`, `Content = null`, envelope still populated, no throw.
10. Method larger than `MaxReturnedLines` → truncated, `IsComplete = false`, `ContinuationOffset` set.
11. Method in a 1,400-line file → envelope shows `LineCount = 1400`, `OutlineAvailable = true`, slice fields describe the method span (verifies the read/search reconciliation scenario).

### `get_file_outline`
12. Outline lists all top-level types and their members with correct `StartLine`/`EndLine`.
13. File-scoped namespace (C# 10 `namespace X;`) parsed correctly.
14. Generated files handled per chosen convention (excluded or marked), consistently.
15. Envelope present and populated alongside the outline.

### Cross-cutting
16. Envelope is a distinct field — content never contains the metadata as inline text (guard against any regression to bracketed-string markers).

---

## Design decisions documented

### Why a structured field, not an inline marker
Inline `[truncated: ...]` text shares a channel with file content and can be misattributed to the data (a log line or comment resembling a marker). A separate field is unambiguous by construction. Inline markers were the cheap first idea; the envelope is the correct one.

### Why total-file counts live next to slice ranges
The read/search contradiction is reconciled only when the agent can see *both* "I received lines 1–250" *and* "the file is 1,400 lines." Either alone is insufficient: slice-only doesn't reveal there's more; total-only doesn't reveal where the boundary was. Both, in distinct fields, reconcile instantly.

### Why `IsComplete` is stamped, not inferred
A complete read marked complete is stronger than a complete read left unmarked. Inferring completeness from `ReturnedToLine == LineCount` works until an off-by-one or an encoding quirk makes it not work; an explicit flag does not. Same principle as the enumerated skip reasons and `IsClean` bool elsewhere — state the fact, don't make the consumer derive it.

### Why symbol count is NOT in the first-cut envelope
Line and byte counts are free (you read the file anyway). A symbol count requires parsing. With Roslyn that's cheap but not zero, and the hypothesis [flagged as hypothesis] is that below the read-whole threshold the symbol count changes no behavior because the agent just reads the file. Ship the free fields; the `OutlineAvailable` flag + the separate `get_file_outline` call already cover the large-file case without putting a parse on every read. Add a symbol count to the envelope only if observation shows the agent needs it before deciding whether to fetch the outline.

### Why thresholds are configurable
The marginal cost of exposing `maxReadLines` is near zero (these tools already carry many settings) and it is the single highest-leverage knob for both token cost and coherence. The conservative-default era (4–8K context) is over; defaults should be generous and the operator should be able to tune to the codebase.

---

## Out of scope (flagged for separate work)

- **Structured content for tabular/data files** (CSV returned as parsed rows rather than a blob). Real and valuable, but a different aperture than source reads — RoslynSentinel reads source, not data files. Note it; don't build it here.
- **Consumer-upload-path metadata** (the assistant-file-upload case). Architecturally different — one-shot injection with no agency loop, so the strategy-signal half doesn't apply. Not a RoslynSentinel concern.

<!-- v1 -->
