# Roslyn Duplication Audit

**Started**: 2026-08-22
**Purpose**: Audit RoslynSentinel's Roslyn-based refactoring/analysis tools against functionality
already available natively in the Roslyn compiler and `Microsoft.CodeAnalysis*` NuGet packages,
to identify hand-rolled logic that duplicates (and risks diverging from, or being buggier than)
an existing public Roslyn API.

## Context

RoslynSentinel references only public Workspaces-layer packages:
`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
`Microsoft.CodeAnalysis.CSharp` (v5.9.0). It does **not** reference
`Microsoft.CodeAnalysis.Features` / `Microsoft.CodeAnalysis.CSharp.Features`, which hold Roslyn's
internal editor-oriented refactoring services (extract method, etc.) — those types are mostly
`internal` and reachable only via MEF composition inside an editor host (VS/VS Code), not
practically reusable from a stateless RPC-style MCP tool. This constrains what "just call Roslyn
instead" can mean in practice: many tools' custom logic is a legitimate consequence of that
architecture, not carelessness.

## Legend

Detailed findings (#1-3) used the original three-way taxonomy:
- **Duplicate (avoidable)** — a public Roslyn API exists and could directly replace the hand-rolled logic.
- **Duplicate (unavoidable)** — logic parallels something Roslyn does internally, but no public/reusable API exposes it given RoslynSentinel's architecture (stateless, no MEF/editor host).
- **Unique** — no meaningful Roslyn equivalent; the problem RoslynSentinel solves (e.g. stateless snippet-based selection) doesn't exist in Roslyn's own API surface.
- Action: **keep** / **todo** / **fixed** / **removed**

**From #4 onward**, per-tool review is intentionally brief (given the ~100+ tool surface) and uses a
simplified finding: **Duplicate** (parallels a Roslyn-internal or public service — whether avoidable
is noted inline, not tracked as a separate axis), **Unique** (no Roslyn equivalent), or **Needs
investigation** (couldn't classify confidently in a brief pass; needs a deeper look like #1-3 got).

## Findings

| # | Tool | File | Finding | Action | Notes |
|---|------|------|---------|--------|-------|
| 1 | RenameSymbol | RefactoringEngine.cs | Unique (glue) / correct delegation | fixed | Correctly delegates to `Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync`. Removed dead never-called helper `FindIdentifierInSnippet` + orphaned `IsIdentChar`. Internal `ComputeRenameHunks` duplicates line-diff logic already in `RoslynSentinel.Common.DiffEngine.CreateDiff` — flagged, not consolidated (deferred by user). |
| 2 | ExtractMethodSafe | MsToolAugmentEngine.cs | Mixed: snippet resolution is Unique; data-flow-based signature inference is Duplicate (unavoidable) | keep | `ContextHelper.FindSnippetPosition` has no Roslyn equivalent (stateless snippet protocol vs. editor `TextSpan`). `model.AnalyzeDataFlow`-based param/return-type inference parallels Roslyn's internal (non-reusable) extract-method service; hand-rolling was effectively forced by the architecture. Three historical "silently wrong extraction" bugs (documented in the method's header comments) show the real cost of this. Unrelated: fixed a `ct`→`cancellationToken` text-corruption bug found in this file's doc comments (bad prior find/replace), spanning ~15 words; verified via build (0 errors). |

| 3 | ChangeSignature | RefactoringEngine.cs | Duplicate (unavoidable) | keep | Confirmed via reflection over `Microsoft.CodeAnalysis(.CSharp).Features` v4.14.0: every `ChangeSignature`-related type (`AbstractChangeSignatureService`, `ChangeSignatureCodeRefactoringProvider`, `IChangeSignatureOptionsService`, `SignatureChange`, etc.) is `internal`, reachable only via MEF host composition — no public API to delegate to. Hand-rolled reorder-declaration + reorder-call-sites logic is the only option given the architecture. **Risk noted (not fixed, out of scope for this audit):** call-site reorder at RefactoringEngine.cs:205 skips any invocation where `args.Count != parameters.Count` (named args, omitted optional args, `params` expansion) — after the declaration is reordered, such call sites are silently left stale/mismatched with no error surfaced. Worth a follow-up ticket. |

| 4 | ExtractLocalVariable | RefactoringEngine.cs:1503 | Duplicate | keep | Snippet resolution via `ContextHelper` is Unique (no Roslyn equivalent for stateless text targeting); the actual extraction logic parallels Roslyn's internal `IntroduceVariable`/extract-local-variable service (internal, MEF-only, same as ChangeSignature). |
| 5 | InlineVariable | SemanticRefactoringLibrary.cs:25 | Duplicate | keep | Hand-rolled single-assignment inline + reference replacement, parallels Roslyn's internal `InlineTemporaryCodeRefactoringProvider` (internal, MEF-only). |
| 6 | InlineField | GranularRefactoringEngine.cs:198 | Duplicate | keep | Same shape as InlineVariable — no public Roslyn API for field inlining. |
| 7 | InlineParameter | GranularRefactoringEngine.cs:257 | Duplicate | keep | Not deep-read this pass; same file/pattern as InlineField, high confidence by consistency with siblings. |
| 8 | InlineMethod | RefinementEngine.cs:142 | Duplicate | keep | Solution-wide call-site replacement + declaration removal, parallels Roslyn's internal `InlineMethodCodeRefactoringProvider` (internal, MEF-only). |
| 9 | InlineClass | AdvancedStructuralEngine.cs:355 | Unique | keep | Cross-file member migration + solution-wide type-reference rename glue — Roslyn has no "inline class" refactoring at all, not even internally. RoslynSentinel-specific composite operation. |
| 10 | ExtractMembersToPartial | GranularRefactoringEngine.cs:866 | Needs investigation | keep | Not read this pass; likely Unique (splitting a class into a partial is not a standard Roslyn refactoring) but unverified — flagged rather than guessed. |
| 11 | IntroduceParameterObject | GranularRefactoringEngine.cs:931 | Needs investigation | keep | Not read this pass; description (group params into a C# 12 record, rewrite body references, leave call sites as a manual TODO) suggests Unique, but unverified. |
| 12 | SafeDeleteUnusedSymbol | StructuralRefinementEngine.cs:75 | Unique (glue) / correct delegation | keep | Correctly uses `SymbolFinder.FindReferencesAsync` (public API) to verify zero usages before deleting; the deletion itself is trivial tree editing with no meaningful Roslyn equivalent needed. Same "correct delegation" shape as RenameSymbol. |
| 13 | PullUpMember | StructuralRefinementEngine.cs:351 | **Bug, not duplication** | **todo** | `PullUpMemberAsync` is an unimplemented stub (body is a comment + empty-dict return) but is wired to a fully-described, user-facing MCP tool that always fails with a misleading "not found" error. Logged as a real bug in `docs/TODO.md` (separate from this audit's duplication tracking). Sibling `PushMembersDownAsync` (line 360) has the same stub shape but isn't exposed to any tool — lower priority. |
| 14 | SyncInterface | RefactoringEngine.cs:4249 | Needs investigation | keep | Not read this pass; "sync interface to implementation" sounds Unique (no direct Roslyn equivalent) but unverified. |

| 15 | ConvertOutParamsToValueTuple | OutParamRefactoringEngine.cs:30-307 | **Duplicate (avoidable)** | **todo** | Composition (`DocumentEditor`, `SymbolFinder.FindReferencesAsync`) is correct public-API usage, but every node it produces — tuple return type (lines 94-114), all three call-site rewrite branches (lines 224-254), and the `ReturnRewriter`'s return statement (lines 343-370) — is built by string-interpolating C# source and re-parsing via `SyntaxFactory.ParseTypeName`/`ParseStatement` instead of constructing `TupleTypeSyntax`/`TupleExpressionSyntax` nodes directly. Unlike #2-8/14, there is no internal-API wall here — public `SyntaxFactory` node constructors cover this directly. Concrete bug found, not just style: line ~112's `originalReturn.Replace("Task<", "").TrimEnd('>')` corrupts nested generics (`Task<List<int>>` → `List<int`). Full writeup and fix sketch: `docs/current/proposal_outparam_tuple_construction_syntaxfactory.md`. |
| 16 | ParamToPropertyRewriter (helper inside a param-to-property refactor) | GranularRefactoringEngine.cs:1186-1204 | Duplicate (avoidable), minor | keep | `VisitIdentifierName` does `SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node)` where `replacement` is a plain string from a `Dictionary<string,string>` substitution map — reparses a string for what's a simple identifier/member-access swap instead of building the replacement node directly (`SyntaxFactory.IdentifierName`/`SyntaxGenerator.MemberAccessExpression`). Low severity, no known correctness bug; not written up as a separate proposal. |
| 17 | LogLevel expression construction | ModernLoggingEngine.cs:72 | Duplicate (avoidable), cosmetic | keep | `SyntaxFactory.ParseExpression($"LogLevel.{levelStr}")` instead of `SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("LogLevel"), SyntaxFactory.IdentifierName(levelStr))`. Trivial one-liner; not written up separately. |

**Checked and confirmed clean** (proper tree-based `CSharpSyntaxRewriter` usage, no string-reparse
smell despite matching search terms): `AsyncBatchEngine.cs:1474` (`BridgeCallRewriter`),
`AdvancedLogicEngine.cs:603` (`IndexedAccessRewriter`), `SyntaxUpgradeEngine.cs:1001`
(`BracesRewriter`). No instances found of manual brace/offset span-math, reimplemented
`SymbolFinder`-style text search, or hand-built modifier lists duplicating `DeclarationModifiers`.

## Candidates not yet reviewed

MoveType, MoveAllTypesToFiles, ConvertAnonymousToNamed, WrapRange, Introduce (generate variants),
and the remaining tool surface (~90+ tools total — attribute/modifier/base-type/accessibility
mutators, scan/analysis tools, async-migration tools, etc.).
