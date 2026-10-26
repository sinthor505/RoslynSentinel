# Plan: selectedHash - a copy-paste way to pick one candidate when a name is ambiguous

**Status:** DRAFT 2026-10-25. Approach agreed in chat (hash shown in the ambiguity error, optional `selectedHash` parameter, retry calls via `ToolCallHint`); nothing built yet.

## Problem
Step 9 of [plan_first_match_ambiguity_audit.md](closed/plan_first_match_ambiguity_audit.md) made name-keyed lookups refuse an ambiguous name (`NameTargetLookup.RequireSingle` throws `ToolAmbiguousMatchException`). Two tools then have no way to say which candidate is meant:

- `ChangeSignature` takes only `filePath`, `methodName`, `parameters` (`AdvancedRefactoringTools.ChangeSignatureTool`). Live check on 2026-10-25: a dry run on `AdvancedRefactoringEngine` (four constructors) returned `Ambiguous` listing all four, but the tool cannot select one.
- `GetCallGraph` still takes the first overload (`SymbolNavigationEngine.GetCallGraphAsync`, `// TODO overloads`); refusing without a selector would make overloaded methods unusable.
- The `RequireSingle` message ends "narrow the target with contextSnippet or a containing type when the tool accepts one", which names a parameter these tools do not have (`NameTargetLookup.cs`).

Quoting source text back (`contextSnippet`) is a known weak-model failure (ReplaceSnippet transcription errors); copying a short token from an error is not.

## Decision
`RequireSingle` computes a short stable hash per candidate, shows it in the error, and accepts an optional `selectedHash` that picks the candidate with that hash. The error carries one ready-made retry call per candidate rendered with `ToolCallHint.Next`.

- **Hash input:** containing type's fully qualified name, declaration kind, name, type-parameter count and normalised parameter types. No line numbers, whitespace, parameter names or modifiers, so the hash survives edits elsewhere in the file and identical multi-target copies (already collapsed by `RequireSingle`) share a hash. The hash is computed live on every call; the server stores nothing.
- **Length:** 4 hex characters; lengthened (6) only when two candidates of the same call collide.
- **Selection:** a `selectedHash` that matches one candidate returns it. One that matches none throws the ambiguity error again with current hashes (not "not found").
- **Opt-in:** only tools that declare `selectedHash` show hashes and retry calls. Other tools keep the current message, and the generic "contextSnippet" sentence is shown only to tools that accept one.
- **Where retry calls are built:** `ToolAmbiguousMatchException` already carries `Candidates` (`AmbiguousCandidate`); the hash is added there and the tool layer, which knows the tool name and arguments, renders the retry calls.
- **Rollout:** `ChangeSignature` and `GetCallGraph` first; the other converted picks keep the existing message unless a tool lacks narrowing.

## Execution rules
- Implementation is dispatched; C# edit dispatches run one at a time (see CLAUDE.md). Tier limits from `.claude/implementer-tiers.json`.
- One shared `ToolParams` constant for the `selectedHash` description, kept short: the claude-lean schema is already over budget in other work, so measure before and after (`CoreSurface_TotalSchemaSize_IsWithinBudget`). `ChangeSignature` is in the on-demand `declarations` toolset; check whether it counts toward the lean core before adding text.
- Each step builds with 0 errors and runs the `NameTargetLookupTests` and `AmbiguousNameTarget` categories; the `FirstMatchRatchetTests` baseline may only shrink.

## Steps

### Step 1 - Hashing and selection in NameTargetLookup
- Files: `RoslynSentinel.Common/NameTargetLookup.cs`, `RoslynSentinel.Common/ToolException.cs` (`AmbiguousCandidate` gains a `Hash`), `RoslynSentinel.Tests/NameTargetLookupTests.cs`.
- Change: `RequireSingle` takes an optional `selectedHash` and a flag saying the calling tool supports it; with the flag the error lists `[hash]` per candidate and `AmbiguousCandidate.Hash` is set; a matching `selectedHash` returns that candidate; a stale one throws ambiguity with fresh hashes.
- Done when: unit tests cover stable hash across line shifts, differing hash for different overloads, same hash for multi-target copies, collision lengthening, stale hash, and interface-plus-implementation still counting as one.

### Step 2 - ChangeSignature takes selectedHash
- Files: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs`, `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs`, `RoslynSentinel.Common/ToolParams.cs`.
- Change: new optional `selectedHash` parameter through `ChangeSignatureTool` and `ChangeSignatureAsync`; the tool's catch renders one `ToolCallHint.Next("ChangeSignature", ...)` per candidate with the same arguments plus `selectedHash`.
- Done when: `AmbiguousNameTargetToolTests` gains a retry-with-hash test that succeeds for one constructor and a stale-hash test; schema-size test result recorded.

### Step 3 - GetCallGraph uses RequireSingle plus selectedHash
- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tools.Advanced/IntelligenceTools.cs`.
- Change: replace the class-over-interface `FirstOrDefault` and its `// TODO overloads` with `RequireSingle` (interface plus one implementation already collapses) and the same optional parameter.
- Done when: overloaded method returns the ambiguity error with hashes; the hash retry returns that overload's call graph; existing call-graph tests pass.

### Step 4 - Honest narrowing sentence
- Files: `RoslynSentinel.Common/NameTargetLookup.cs`.
- Change: the generic "contextSnippet or a containing type" sentence appears only for tools that pass that they accept it; tools with neither selector get "rename one, or edit each declaration directly".
- Done when: a test pins both wordings; no converted tool advertises a parameter it lacks.

## Out of scope
- Hashes for tools that already take `contextSnippet`/`containerName` (they keep their selector).
- Set-level detection of a newly added overload; the retry error lists current candidates anyway.
- `FilterByContainingType` silent widening (separate owner decision, see the audit plan).

## Risks and open decisions
- Schema budget: the extra parameter text must fit; unverified whether `declarations` counts toward the lean core.
- Hash input normalisation (generics, `params`, optional parameters, ref kinds, nullable annotations) needs tests; a normalisation miss shows as two overloads with equal hashes, which must throw rather than pick.
- Whether `ToolCallHint.Next` can render an array-valued `parameters` argument for `ChangeSignature`; if not, the retry hint says "same call plus selectedHash".
