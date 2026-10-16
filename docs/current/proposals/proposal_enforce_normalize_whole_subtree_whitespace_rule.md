# Enforcing the NormalizeWholeSubtreeWhitespace risk rule (item C)

**Status:** DESIGN AGREED 2026-10-15. Naming is final; the structure (two guarded new-node methods,
operation-named existing-node helpers, consolidation first, ratchet test) is agreed. Slices not started. Remaining open questions are
listed below and are scoped to individual slices, not to the design.

> **Reading guide.** Facts were traced by exploration agents on 2026-10-13 and 2026-10-15 and were
> NOT re-verified when this doc was written. Line numbers may have drifted by a few lines; re-anchor
> with `Search` before acting. Each claim is tagged **[verified]** (traced to source or by text search),
> **[hypothesis]** (reasoned or untested), or **[unverified]** (named in a scoping report but not
> read). Code snippets in the Proposal section are a design sketch only: not compiled, not tested.

## Motivation

`RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace` is a pure pass-through to Roslyn's
`SyntaxNode.NormalizeWhitespace`. It replaces all formatting in the node it is given. Its doc remarks
say so, but only as advice: the parameter type is plain `SyntaxNode`, so nothing stops a caller passing a
whole file's root.

**[verified]** Definition: `RoslynSentinel.Common/RoslynFormattingHelper.cs:328`:
`NormalizeWholeSubtreeWhitespace(SyntaxNode node, string indentation = "    ", string eol = "\n", bool elasticTrivia = true)`.
The remarks (~lines 297-326) define Safe = a freshly synthesized node with no pre-existing siblings, and
Risky = an existing tree root or container (a whole file root, `newRoot`, `CompilationUnitSyntax`),
because every sibling's whitespace is reformatted. The default `eol` is `"\n"`, not the target file's EOL.

**Origin.** The MoveMember bug: a one-method move produced a 1178-line diff
(`docs/current/blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`).
MoveMember was fixed by a different design (minimal text edits against the original `SourceText`,
`Engines.Basic/MoveMemberTextEdits.cs`; commits 4dedc78, a23c581, 4aa2dc2, 389ae1b). This proposal is
the remaining "guard still advisory" follow-up: stop NEW risky calls appearing, make safe vs risky
call sites distinguishable, and give the existing risky sites a safe method to migrate to.

### What the damage looks like (ILLUSTRATIVE, written from the blocker description, not captured output)

Input:

```
namespace Demo;

/// <summary>See <see cref="Helper"/>.</summary>
public record Order(
    int Id,
    string Name);

public class Helper
{
    public int Total(int a,
                     int b) => a + b;
}
```

Output after normalizing the whole root (illustrative): the `namespace Demo;` line is glued to the doc
comment; the record is joined to one line, `public record Order(int Id, string Name);`; the cref becomes
`cref = "Helper"`; the `Total` signature is joined onto one line; every blank line is gone; there is no
final newline; line endings are forced to `\n`. Result: a diff touching every line of a file the customer
asked us to change one line in.

### Inventory of formatting code **[traced by exploration agent 2026-10-15]**

**Shared helpers in `Common/RoslynFormattingHelper.cs`:**

| Method | Line | What it actually does | Callers |
| --- | --- | --- | --- |
| `ReplaceNodeFormattedAsync` | :52 | Annotates the new node, `ReplaceNode`, `Formatter.FormatAsync` scoped to the annotation, trivia transplant, `NormalizeEol`. Cannot replace the root. | 47 in 14 files |
| `ReplaceNodesFormattedAsync` | :122 | Batch version of the above | 1 |
| `RemoveNodeFormattedAsync` | :165 | **Does not format.** `KeepExteriorTrivia` removal, boundary-token trivia restore, `NormalizeEol`. | 2 |
| `InsertMemberFormattedAsync` | :235 | Builds blank-line trivia, annotates the new member, scoped `FormatAsync`, `NormalizeEol` | 3 |
| `NormalizeWholeSubtreeWhitespace` | :328 | Pass-through to `NormalizeWhitespace`, no provenance or EOL awareness | 80 in 18 files |

**Drifted copies:**

- Three private remove helpers, each with one or two callers: `AdvancedStructuralEngine.cs:288`,
  `SemanticRefactoringEngine.cs:23` and `StructuralRefinementEngine.cs:33`. They annotate the whole
  former container and format it, fall back to a whole-document format, and do no EOL normalization.
- About 15 inline "annotate, `ReplaceNode`, scoped `FormatAsync`" copies of `ReplaceNodeFormattedAsync`
  (AdvancedStructural, IDEStyle, Instrumentation, Mapping, MsToolAugment, SyntaxUpgrade, Semantic,
  BasicRefactoring, MemberRefactoring, ConstructorCallSiteFinder). None do the trivia transplant or the
  invariants, and most skip EOL normalization. SyntaxUpgradeEngine :549 annotates a whole rebuilt class,
  so untouched siblings inside it get reformatted.
- NewRoot has two competing approaches. One is `NormalizeWholeSubtreeWhitespace` (BasicRefactoring :795
  with `elasticTrivia:false`, AdvancedStructural :126 and :1108; :1108 also does a
  `.Replace(";namespace", ";\n\nnamespace")` string hack). The other is whole-document
  `Formatter.FormatAsync(newDoc, null)` in MoveTypeToFile (:561/:564) and MoveAllTypesToFiles (:621/:627).

**Two risky populations, not one:**

1. **`NormalizeWholeSubtreeWhitespace` on an existing root: about 65-70 sites** (the original target of
   this proposal). The biggest counts are AdvancedStructuralEngine 16, AsyncOptimizationEngine 12,
   LogicOptimizationEngine 12, ModernizationEngine 8 and CodeStyleEngine 6. The rest are SyntaxUpgrade,
   AsyncBatch, CodeHealing, Documentation, IDEStyle, ApiGeneration, ProjectStructure and MsToolAugment.
   About 8 sites are genuinely NewChild: CodeGeneration :972/:997, AdvancedRefactoring :335/:366,
   MemberRefactoring :4569/:4633 and Mapping :91.
2. **Whole-document `Formatter.FormatAsync` on an existing file: about 21 sites, 13 of them with `null`
   options.** These are BasicRefactoring x8, CodeGeneration x3, AdvancedStructural :1462 and
   MemberRefactoring :4484. Whole-tree `Formatter.Format` appears in `MsToolAugmentEngine.FormatDocumentSafeAsync`
   (:701). These reformat a whole file just as normalizing does, by different rules. This population was
   not in the original proposal and is now in scope.

**Non-writing uses of raw `NormalizeWhitespace` (legitimate, to allowlist):** `ContentHasher.cs:14`
(hashing), `PersistentWorkspaceManager.cs:1668/:1669` (pre-image comparison) and
`AttributeTextEditBuilder.cs:49/:94` (a synthesized attribute turned into a `TextEdit`).
`PersistentWorkspaceManager.cs:1695` calls `Formatter.Format` only for a debug log.

**Reachability is UNKNOWN.** LogicOptimizationEngine and its siblings flow to
`Tools.Experimental/CodeTransformTools.cs:329` and `SourceTransformResult` (:604). Whether
`CodeTransformTools` is registered and active, and which tool consumes the text, was NOT checked. Dead
code should be deleted rather than migrated.

### What catches it today **[verified]**

- **EolChangeGuard** (`Common/EolChangeGuard.cs`) refuses only a changed line-ending style on an existing
  `.cs` file. It runs in `ValidateAndApplyHelper.ValidateAndApplyAsync` (:47) and as a backstop in
  `PersistentWorkspaceManager.ApplyProposedChangesAsync` (~:1585-1592). It catches a CRLF file normalized
  with `"\n"`. It does NOT catch a reformatted LF file, or a mixed file that is already LF-dominant.
- **The compile gate does not catch it.** An unresolved cref is only warning CS1574.
- **LineChanges** makes the damage visible but refuses nothing.
- **No test, analyzer, BannedSymbols file or editorconfig rule references the helper.** Nothing tests
  that unrelated lines stay unchanged.
- **`TreatWarningsAsErrors` / `WarningsAsErrors` is not set** in any `.props`, `.targets` or `.csproj`
  (searched 2026-10-15), so `[Obsolete]` raises warnings, not errors.

## Terminology (glossary)

All of these are Roslyn `SyntaxNode`s. **The CLR type does not record provenance**; the difference is
where the node came from. The four provenance values form a 2x2 grid of {Existing, New} x {Root, Child}:

- **ExistingRoot:** the `CompilationUnitSyntax` of a file that exists on disk, or that root after a
  rewriter edited it. Every untouched line belongs to the user.
- **ExistingChild:** a member or statement taken from that tree, possibly modified (e.g.
  `target`/`newTarget` at AdvancedStructuralEngine :1460). Its interior formatting and leading trivia
  belong to the user.
- **NewChild:** a member or statement built from scratch with `SyntaxFactory`, never part of a user file.
- **NewRoot:** a `CompilationUnitSyntax` built from scratch for a file that does not exist yet. It has the
  same CLR type as an ExistingRoot, so the rule cannot be "reject `CompilationUnitSyntax`".
- **Rule:** normalizing replaces formatting; it is safe only where there is none to lose (New*).

**Verbs (decided):**

- **Format** means Roslyn's `Formatter`, which applies document options and can be scoped to an
  annotation or a span. Every `Format` name carries its scope.
- **NormalizeWhitespace** means `SyntaxNode.NormalizeWhitespace`, which discards all formatting and
  rebuilds a canonical layout. It is used only on New* nodes and always as the full phrase.
- **"Normalize" is never used alone.** `EolUtilities.NormalizeEol` already uses it for line endings, an
  unrelated operation (renaming it is optional and deferred).

## Proposal

### Why not new node types

- **Subclassing `SyntaxNode` is impossible [Roslyn knowledge, not re-checked in this repo].** Its only
  constructor is `internal` and takes an internal `GreenNode`, and it has `internal abstract` members.
  The same applies to `CSharpSyntaxNode` and the concrete types. Even if it were allowed, Roslyn builds
  the "red" node objects itself, so `With*`, `ReplaceNode` and `Visit` would return plain Roslyn types
  and the provenance would be lost on the first edit.
- **Wrapper types** (`ExistingRootSyntaxNode`, `NewChildSyntaxNode<T>`, ...) would give compile-time
  enforcement. They were rejected in favour of no new types. The trade-off: a provenance mismatch is
  caught on the first test run that reaches it, not at compile time. In exchange there is one shared
  guard and a runtime check of the actual hazard (user formatting present).
- **The annotation marker** from the earlier sketch is dropped. It existed only to catch a default
  struct, and the content and structural checks below cover the cases it would have caught.

### Naming convention (decided)

Grammar: `<Operation><Operand>And<Formatting><Scope>Async`.

1. **Operand** = the Roslyn parameter type with `Syntax` dropped (`MemberDeclarationSyntax` gives
   `MemberDeclaration`). Use `Node` only when the parameter really is any `SyntaxNode`. Do not use bare
   `Member`, which is ambiguous with the `Member` tool and member-body tools.
2. **`And`** joins the two actions. Do not use a participle (`...Formatted`), which reads as describing
   the node.
3. **Scope is always named** (`Replacement`, `Insertion`, `AnnotatedSpans`, `WholeDocument`), because
   scope is what makes a call safe or risky.
4. **Name the primary action and the formatting scope only.** Secondary steps (trivia transplant, EOL
   normalization, post-write invariants) go in a class-level contract: every helper in
   `RoslynFormattingHelper` returns text in the document's dominant EOL.
5. **"New" means provenance** (synthesized). Do not use it for "the replacement node"; hence
   `Replacement`.
6. `Async` only on async methods.

**Renames:**

| Current | New name | Operand | Formatting action |
| --- | --- | --- | --- |
| `ReplaceNodeFormattedAsync` | `ReplaceNodeAndFormatReplacementAsync` | `SyntaxNode` | `Formatter`, replacement only |
| `ReplaceNodesFormattedAsync` | `ReplaceNodesAndFormatReplacementsAsync` | `SyntaxNode` (batch) | `Formatter`, replacements only |
| `InsertMemberFormattedAsync` | `InsertMemberDeclarationAndFormatInsertionAsync` | `MemberDeclarationSyntax` | `Formatter`, inserted member only |
| `RemoveNodeFormattedAsync` | `RemoveNodePreservingTriviaAsync` | `SyntaxNode` | None (it never formatted) |
| `NormalizeWholeSubtreeWhitespace` | Kept for the legacy sites only (see Enforcement); new code uses `NormalizeNewChildWhitespace` / `NormalizeNewRootWhitespace` | | |

**New methods:**

| Name | Covers | Action |
| --- | --- | --- |
| `NormalizeNewChildWhitespace<T>` | NewChild | Guard, then `NormalizeWhitespace` with the caller's EOL |
| `NormalizeNewRootWhitespace` | NewRoot | Same; takes `CompilationUnitSyntax` only |
| `FormatAnnotatedSpansAsync` | ExistingRoot | `Formatter` limited to nodes carrying `Formatter.Annotation` |
| `FormatWholeDocumentAsync` | Explicit whole-file format only (the format-document tool) | `Formatter` on the whole document. "Whole" flags the risk. |

**No provenance enum.** The method name states the caller's intent. An enum (`SyntaxNodeOrigin`) was
adopted and then reversed; see Alternatives.

### Structure (decided)

Two paths, split on the existing/new axis, because the two need different inputs.

- **New-node path:** two sync methods, no `Document`, sharing one private guard. This is where
  provenance is checked. It is not a dispatcher: the two methods do the same normalize, every call
  site knows statically which kind it built, and the existing path does not pass through it, so a
  dispatcher would add an enum and a switch without adding a check.
- **Existing-node path:** operation-named async helpers that require a `Document`. Insert, replace and
  remove are operations on an existing root, not provenance categories, so they stay separate methods.
  Forcing them behind a dispatcher would bring back per-case parameters (container, index, remove
  options).
- **ExistingChild has no formatter of its own.** It is covered by
  `ReplaceNodeAndFormatReplacementAsync`, or by annotate-then-root: the caller does
  `root.ReplaceNode(old, replacement.WithAdditionalAnnotations(Formatter.Annotation))` and then calls
  `FormatAnnotatedSpansAsync`.
- **ExistingRoot migration pattern** (for the ~65 rewriter sites): the rewriter adds `Formatter.Annotation`
  to the nodes it changes, and the call switches from `NormalizeWholeSubtreeWhitespace(newRoot)` to
  `FormatAnnotatedSpansAsync`. This makes the large migration repeatable instead of bespoke per engine
  **[hypothesis: some rewriters may make annotating hard; prove on one engine first]**.

### Design sketch (NOT compiled, NOT tested)

```csharp
// RoslynFormattingHelper - new-node path (sync, no Document accepted)
public static T NormalizeNewChildWhitespace<T>(T node, string eol,
    string indentation = "    ", bool elasticTrivia = true) where T : SyntaxNode
public static CompilationUnitSyntax NormalizeNewRootWhitespace(CompilationUnitSyntax root, string eol,
    string indentation = "    ", bool elasticTrivia = true)
private static void EnsureNodeIsNew(SyntaxNode node)   // checks 2-4 below, shared by both

// RoslynFormattingHelper - existing-node path (async, Document required; existing parameters unchanged)
public static ... ReplaceNodeAndFormatReplacementAsync(...)
public static ... ReplaceNodesAndFormatReplacementsAsync(...)
public static ... InsertMemberDeclarationAndFormatInsertionAsync(...)
public static ... RemoveNodePreservingTriviaAsync(...)
public static Task<Document> FormatAnnotatedSpansAsync(Document document, SyntaxNode rewrittenRoot, CancellationToken ct)
public static Task<Document> FormatWholeDocumentAsync(Document document, CancellationToken ct)
```

- `eol` is **required**, with no `"\n"` default, so a NewRoot gets the target file's or project's EOL.
- Root vs child: `NormalizeNewRootWhitespace` accepts only `CompilationUnitSyntax` (compile time).
  `NormalizeNewChildWhitespace` cannot exclude it by constraint, so it checks at runtime (check 1).

**Checks, in order.** Each failure throws `ArgumentException` with an actionable message. Any check that
suggests existing code ends with: "this looks like existing code; use
`ReplaceNodeAndFormatReplacementAsync`, or annotate the changed nodes with `Formatter.Annotation` and
call `FormatAnnotatedSpansAsync`".

1. (`NormalizeNewChildWhitespace` only) `node is CompilationUnitSyntax`: "use `NormalizeNewRootWhitespace`".
2. `node.Parent is not null`: the node is attached to a tree, so it is not new.
3. `node.SyntaxTree.FilePath` is non-empty **[hypothesis: true for workspace documents; may be null in
   AdhocWorkspace tests]**.
4. **Content check:** the subtree contains non-elastic whitespace, end-of-line or comment trivia, meaning
   there is user formatting to lose. The message reports the count. **[hypothesis]** Possible false
   positives: nodes built with `ParseStatement`/`ParseMemberDeclaration`, explicit
   `SyntaxFactory.Whitespace(...)`, generated doc comments. Test it against the ~11 known safe sites
   before enabling. If it misfires, demote it to a logged warning or drop it.

A tool boundary must turn the `ArgumentException` into a structured error, not leak it (CLAUDE.md).
Whether every engine's tool wrapper already does this is **[unverified]**.

### Enforcement (decided)

- **Ratchet test** (test-writer). A ratchet tolerates existing debt, refuses new debt, and only moves
  one way.
  - **Scan:** every `.cs` file in the repo, excluding test projects, `*/Worktree/`, `bin` and `obj`.
    Parse each file with `CSharpSyntaxTree.ParseText` (syntax only, no compilation) and count
    `InvocationExpressionSyntax` nodes whose method name matches a watched name. Do not use a regex:
    it would count comments (12 comment-only `NormalizeWhitespace` matches exist) and strings. Matching
    the method name rather than the receiver also catches calls through a `using` alias. An unrelated
    method that shares a watched name is simply counted and absorbed by the allowlist.
  - **Allowlist:** a per-file dictionary `{ "relative/path.cs": count }` in the test source, one per
    watched name. Keyed by file and count, not line, because line numbers shift on unrelated edits.
    Known blind spot: swapping one call for another in the same file keeps the count unchanged; accepted.
  - **The test fails when:**
    - a file's count is above its entry, or an unlisted file has a call (new debt; the message names
      the safe method and links this proposal);
    - a file's count is below its entry (a site was migrated; the message says to lower the number).
      This is what makes it a ratchet: a freed slot cannot be quietly refilled;
    - a listed file no longer exists (stale entry; remove it).
  - An increase to any allowlist number is visible in the commit diff and is a review red flag.

  The three allowlists:

  | Watched name | Entries | Life cycle |
  | --- | --- | --- |
  | `NormalizeWhitespace` | `RoslynFormattingHelper` (the two new-node methods), `ContentHasher.cs` (hashing), `PersistentWorkspaceManager.cs` x2 (pre-image comparison) | **Permanent.** These are legitimate non-writing uses. `AttributeTextEditBuilder.cs` :49/:94 leaves in C3. |
  | `NormalizeWholeSubtreeWhitespace` | The ~65 legacy whole-file-root sites | **Shrinks to zero in C6.** Then the method and this list are deleted. |
  | `FormatAsync` / `Format` (Roslyn `Formatter`) | `RoslynFormattingHelper`, the ~21 whole-document sites and the ~15 inline scoped copies | **Shrinks in C4/C5** to the helper and the legitimate callers of `FormatWholeDocumentAsync`. `PersistentWorkspaceManager.cs:1695` (debug log only) is permanent. |

- **The legacy method is unchanged.** It stays as-is for the allowlisted sites.
- **`[Obsolete]`** on `NormalizeWholeSubtreeWhitespace` is added after the NewChild/NewRoot sites have
  migrated (slice C3), so its ~65 remaining warnings each mark real debt. It is safe because warnings are
  not errors.
- **Runtime guard on existing roots:** not added. The ratchet plus the new-node guard's checks are the
  enforcement. A throw at the legacy sites would take out most of the Advanced optimization tools.

### Slices (C# edits sequential; tiers per `.claude/implementer-tiers.json`)

- **C0:** this proposal approved.
- **C1 (implementer-sweep):** the four renames via `RenameSymbol`, one call per helper (call sites
  update with it). No behaviour change. Acceptance: build green, existing tests unchanged.
- **C2 (implementer-senior):** add `NormalizeNewChildWhitespace`, `NormalizeNewRootWhitespace` and the
  private `EnsureNodeIsNew` guard in `RoslynFormattingHelper`, with tests:
  - each rejection message (checks 1-3);
  - the content check (4) run against fixtures copied from the ~11 known safe sites;
  - the EOL comes from the parameter.
- **C2b (done, follow-up to C2):** check 4 now exempts a whitespace trivia that is exactly one space
  (`SyntaxFactory.Space`, parsed single-line snippets). `ElasticWhitespace(text)` carries no elastic annotation and
  cannot be told from `Whitespace(text)`, so longer whitespace is still counted. Newlines, comments and indentation
  are still rejected. Of the 12 fixtures first pinned as false positives, 7 are now accepted (CodeGeneration :972
  with parameters, AdvancedRefactoring :335/:366, MemberRefactoring :4569, AttributeTextEditBuilder detached :49
  and :94, `ParseMemberDeclaration` single-line). Still rejected, so C3 must change the call site (pinned as
  `PinnedNeedsC3_*`): MemberRefactoring ExtractMethod call site :4633 (drop the copied indentation trivia),
  AttributeTextEditBuilder :49 (the parsed list is still attached to the dummy class; detach it), BasicRefactoring
  :795 (non-elastic `MemberTrivia`; use elastic trivia), AdvancedStructural :126 (embeds existing properties; detach
  or rebuild), and any site that builds multi-character whitespace with `ElasticWhitespace(text)`.
- **C3 (implementer, <=3 Engines files per dispatch):** migrate the NewChild sites (CodeGeneration
  :972/:997, AdvancedRefactoring :335/:366, MemberRefactoring :4569/:4633, Mapping :91, and the raw
  `NormalizeWhitespace` calls on synthesized attributes at `AttributeTextEditBuilder.cs:49/:94`) and NewRoot sites
  (BasicRefactoring :795, AdvancedStructural :126/:1108, verify provenance first). Pick one NewRoot
  approach and fold MoveTypeToFile/MoveAllTypesToFiles into it; remove the :1108 string hack. Then add
  `[Obsolete]`.
- **C4 (implementer, behaviour-changing):** consolidate the drifted copies into the shared helpers: the
  three private removes (needs a `SyntaxRemoveOptions` parameter on `RemoveNodePreservingTriviaAsync`)
  and the ~15 inline replace copies. Each needs a byte-exact test showing that untouched lines stay
  unchanged.
- **C5:** add `FormatAnnotatedSpansAsync` and `FormatWholeDocumentAsync`; route the whole-document
  `FormatAsync` sites. Then the ratchet test (test-writer).
- **C6 (separate, feature-sized):** reachability check of the ~65 legacy root sites and the
  `CodeTransformTools` path; delete dead engines; migrate the live ones engine by engine with the
  annotate-then-`FormatAnnotatedSpansAsync` pattern, starting with one engine as proof.

## Alternatives considered, not pursued

- **`SyntaxNode` subclasses:** impossible (see "Why not new node types").
- **Wrapper types with compile-time enforcement:** rejected in favour of no new types; a mismatch is
  caught at runtime on first test instead of at compile time.
- **New-node dispatcher with a required `SyntaxNodeOrigin` enum** (`ExistingRoot`, `ExistingChild`,
  `NewChild`, `NewRoot`) as an intent cross-check: adopted, then reversed. Once the existing path became
  operation-named helpers, the dispatcher had about 11-15 engine call sites, no helper callers, and two
  targets with identical behaviour. Every caller knows its kind statically, so two named methods state
  the same intent, are as greppable, and give compile-time root enforcement. The enum had no remaining
  job.
- **One dispatcher for all four origins:** rejected. The existing path needs a `Document` (and sometimes
  an original node, a container or remove options) and is async; the new path needs none of these and
  is sync. One signature would need nullable-by-case parameters.
- **An existing-node dispatcher:** rejected. Insert, replace and remove are operations, not provenance
  values.
- **Annotation marker as the provenance proof:** dropped. It is not needed without a default struct, and
  the content check targets the hazard directly.
- **`IsNew` property on the node:** impossible (sealed types, internal constructors).
- **Runtime guard throwing on existing roots now:** rejected. It would break ~65 live sites. It may
  become possible after C6.
- **Roslyn analyzer instead of the ratchet test:** more precise but heavier to build and maintain.
  Revisit if text matching proves too easy to fool.
- **Leave advisory:** the state that let the MoveMember bug ship.

## Open questions (slice-scoped)

1. Does the content check (4) hold up without false positives? Decided by the C2 tests; C2b exempts single-space
   whitespace, the remaining rejections are listed under C2b.
2. Which `Formatter` options apply when `null` is passed (13 sites)? Trace before C5.
3. Are the ~65 legacy root sites reachable? (C6, first step.)
4. What is the provenance of AdvancedStructuralEngine :126, :1108 and :1674? AdvancedStructuralEngine
   :1460 normalizes a node derived from an existing one and then formats the whole document at :1462.
   Migrate it to `ReplaceNodeAndFormatReplacementAsync` in C4 or C5.
5. Do all engine tool wrappers map an `ArgumentException` to a structured error? **Still open after C2/C2c.**
   The new helpers throw `ArgumentException` (and `ArgumentNullException`) for a rejected node or a bad `eol`; answer
   this in C3, when the first caller migrates, by tracing that caller's tool wrapper and adding a test.
6. Does the test project that will host the ratchet already reference `Microsoft.CodeAnalysis.CSharp`
   for `CSharpSyntaxTree.ParseText`? (C5.)
7. Rename `EolUtilities.NormalizeEol` (e.g. to `ApplyDominantEol`)? Optional, deferred.
8. A reformatted LF file is caught by nothing at runtime. Byte-exact tests per engine (C4) are the
   chosen mitigation, not a new guard.
9. Item B (collateral-change tripwire) was decided 2026-10-15 as MoveMember-only. This item is
   independent of it.

## Decided vs undecided

- **Decided:** the verbs and naming convention; the four renames and four new method names; no new
  node types and no provenance enum; two paths (two guarded new-node methods, operation-named existing
  helpers); ratchet scan by syntax parse with file-and-count allowlists that must be lowered on
  migration; ExistingChild collapsed into replace or annotate-then-root; scope
  widened to whole-document `Formatter` calls; ratchet test as the enforcement; `[Obsolete]` after C3;
  consolidation before migration; MoveMember uses text edits (done); Item B is MoveMember-only.
- **Undecided:** the Open questions above, each owned by the slice named.

## Cost / risk

- C1-C3 touch `Common/RoslynFormattingHelper.cs`, call sites via rename, ~7 Engines files and a new
  test file. C4 touches ~12 Engines files and changes behaviour. C6 touches ~15 Engines files and is
  feature-sized.
- Risk: choosing a `NormalizeNew*` method is still a caller's claim. A node derived from existing code (`With*` on an
  existing node) passes checks 2 and 3; only the content check (4) catches it, and that check is
  unproven.
- Risk: C4 consolidation changes the output of tools that use the drifted copies. This is mitigated by
  byte-exact tests.
- Risk: the ratchet allowlists can be bulk-edited to silence them; the failure message points here.
- Does not fix the legacy root sites until C6; it stops the count growing and gives them a target.

## Discussion log

1. Census of call sites and explanation of the issue.
2. Owner asked about the node type: two types for new vs existing, or an `IsNew` property.
3. Wrapper + annotation sketch presented.
4. Owner asked to adjust before finalizing.
5. Terminology clarified (document root, existing node, synthesized node, new-file root).
6. Deferred to a fresh session.
7. 2026-10-15: owner prioritized clarity of names; 2x2 {Existing, New} x {Root, Child} grid adopted.
8. Subclassing `SyntaxNode` confirmed impossible; owner preferred no new types. An enum as a required
   parameter was adopted as an intent cross-check, plus runtime checks (content, structural, type).
9. A dispatcher split by Document vs no-Document was proposed. The formatting-code inventory showed the
   existing helpers are organized by operation, two categories have no correct implementation, and
   whole-document `Formatter` calls are a second risky population. The design became: dispatcher for new
   nodes only, operation-named helpers for existing nodes, consolidation first.
10. Verbs fixed: Format = `Formatter`, NormalizeWhitespace = `SyntaxNode.NormalizeWhitespace`.
    `<Operation><Operand>And<Formatting><Scope>Async` grammar and final names agreed; `MemberDeclaration`
    chosen over bare `Member` to avoid confusion with member tools.
11. Dispatcher reconsidered: with about 11-15 call sites, no helper callers and two identical targets
    it added nothing over two named methods. Replaced by `NormalizeNewChildWhitespace` /
    `NormalizeNewRootWhitespace` with a shared private guard; `SyntaxNodeOrigin` dropped. Ratchet
    mechanics specified (syntax parse, file-and-count, fail on decrease, three allowlists).

## Related

- `docs/current/blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`
- `docs/current/finding_normalizewhitespace_container_reformat_risk_inventory.md` (per-file table,
  updated 2026-10-13)
- `docs/current/TODO.md`, NormalizeWhitespace sweep entry
- `docs/current/proposals/proposal_parallel_change_deprecated_wrappers.md` (migration-first workflow)
