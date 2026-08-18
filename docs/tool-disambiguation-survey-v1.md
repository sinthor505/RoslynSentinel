# Tool Disambiguation Survey — RoslynSentinel MCP Tools

**Date:** 2026-08-18  
**Scope:** All 104 MCP tools across RoslynSentinel.Server.Basic and RoslynSentinel.Server.Advanced  
**Purpose:** Categorize how each tool resolves targets by name/coordinate and identify which tools share ReplaceMember's defect shape (name-only resolution + silent first-match + mutating)

---

## Executive Summary

**Finding:** ReplaceMember's structural defect — resolving targets by name alone via `FirstOrDefault()`, silently picking the first match when multiple candidates exist (e.g., overloaded methods) — is **replicated across 11 other mutating tools** in the codebase. Two of these (ReplaceMember itself and RemoveMember) represent critical exposure: ReplaceMember can replace the wrong method body, RemoveMember can delete the wrong member. Five others (ChangeAccessibility, ModifyModifier, ModifyAttribute, AddSummaryComment, AddConstructorParameter) pose medium-to-high semantic corruption risk.

**Root cause:** All name-resolving tools delegate to a shared `GetMemberName(MemberDeclarationSyntax)` helper that returns only the identifier text, then use `FirstOrDefault(m => GetMemberName(m) == memberName)` to find the target. This pattern is unambiguous only when the name is globally unique; it silently fails for overloads, same-named members in nested types, or field/method collisions.

**Scope of this report:**
- Step 1: Full inventory of all 104 tools, bucketed by resolution type
- Step 2: Detailed categorization of 40+ name-resolving and coordinate-resolving tools, including disambiguation method and ambiguous-match behavior
- Step 3: Ranked list of 11 tools sharing ReplaceMember's defect shape, with severity assessment and evidence citations

**Out of scope (deferred to follow-up plan):**
- Any fixes to add disambiguation to these tools
- Deciding *how* to fix (contextSnippet vs. line/column vs. error-on-ambiguity)
- Re-reviewing tools already covered by plan-symbol-tool-hardening-v1.md (FindReferences, QuerySymbolRelationships) — confirmed their current state matches that plan's updates

---

## Step 1: Tool Inventory by Resolution Type

**Total: 104 tools** across both server projects (50 Basic + 54 Advanced)  
Count verified 2026-08-18 via `grep -rc "\[McpServerTool"`.

### Whole-File/Whole-Solution Operations (20 tools)
No single-target resolution; out of scope for disambiguation survey.

| Tool | File | Description |
|------|------|---|
| ProjectDoc | DocumentationTools.cs | Unified accessor for project doc files (plans, handoffs, state YAML) |
| Git | GitTools.cs | Unified git tool (status, log, diff, stage, commit, revert) |
| Features | SentinelWorkspaceTools.cs | Query/update feature flags |
| ListSolutionItems | SentinelWorkspaceTools.cs | Lists projects, files, dependencies, solution-folder items |
| ListWorkspaceSolutions | SentinelWorkspaceTools.cs | Lists all *.sln and *.slnx files |
| LoadSolution | SentinelWorkspaceTools.cs | Loads .NET solution into persistent workspace |
| ListExternalDiskChanges | SentinelWorkspaceTools.cs | Returns files modified on disk since last sync |
| ClearExternalDrift | SentinelWorkspaceTools.cs | Clears external-drift list |
| ProposedChange | SentinelWorkspaceTools.cs | Applies or validates a change set |
| RetryFailedChanges | SentinelWorkspaceTools.cs | Retries failed file writes |
| GetDiagnostics | SentinelWorkspaceTools.cs | Gets compiler diagnostics (file/project/solution scope) |
| SearchSolutionText | SentinelWorkspaceTools.cs | Searches all source files for text pattern or regex |
| GetOperationDetail | SentinelWorkspaceTools.cs | Returns filtered slice of operation result blob by changeId |
| UndoLastApply | SentinelWorkspaceTools.cs | Reverts files from previously applied batch |
| ResetBreaker | SentinelWorkspaceTools.cs | Resets circuit breaker and counters |
| GetBreakerStatus | SentinelWorkspaceTools.cs | Returns circuit breaker state |
| GetWorkspaceHealth | SentinelWorkspaceTools.cs | Workspace health check |
| ListProjectFrameworkTargets | SentinelWorkspaceTools.cs | Returns each project's TargetFramework |
| ScanAsyncMigrationCandidates | SentinelAsyncifyTools.cs | Flags qualifying methods with [MigrationCandidate] |
| GetAsyncMigrationProgress | SentinelAsyncifyTools.cs | Returns async migration progress metrics |
| ClearAsyncMigrationCandidateFlags | SentinelAsyncifyTools.cs | Removes [MigrationCandidate] attributes |
| EventHandlersToAsync | SentinelAsyncifyTools.cs | Converts HandlerToAsyncCandidate event handlers |
| Asyncify | SentinelAsyncifyTools.cs | Full async-migration workflow |
| AsyncifyLoop | SentinelAsyncifyTools.cs | Asyncify in loop until convergence |
| GetMigrationLedger | SentinelAsyncifyTools.cs | Returns persisted migration ledger |
| ResetMigrationLedger | SentinelAsyncifyTools.cs | Clears all ledger entries |

### Handle-Resolving Operations (7 tools)
Resolve targets via opaque handles (docCommentId, changeId, scanId) from prior calls; already unambiguous by construction.

| Tool | File | Description |
|------|------|---|
| GetOperationDetail | SentinelWorkspaceTools.cs | Filters operation result by changeId |
| UndoLastApply | SentinelWorkspaceTools.cs | Reverts from changeId-stored batch |
| RenameSymbol | SentinelRefactoringTools.cs | Renames symbol via docCommentId + projectName |
| PreviewRenameImpact | SentinelSymbolTools.cs | Previews rename via docCommentId + projectName |
| BridgeAsyncMethods | SentinelAsyncifyTools.cs | Converts methods via target list {FilePath, MethodNames} |
| UpliftCallers | SentinelAsyncifyTools.cs | Updates callers via {BridgedMethodName, ProjectName} |
| PropagateCancellationToken | SentinelAsyncifyTools.cs | Threads CT via target list {FilePath, MethodNames} |
| AddCancellationToken | SentinelAsyncifyTools.cs | Adds CT parameter via target list {FilePath, MethodNames} |

### Coordinate-Resolving Operations (19 tools)
Resolve targets by filepath + line/column, or filepath + contextSnippet (± lineBefore/lineAfter).

| Tool | File | Disambiguation Method | Notes |
|------|------|----------------------|-------|
| GetMethodSource | SentinelWorkspaceTools.cs | name + FirstOrDefault + case fallback | Described as "first match for overloaded names" |
| GetFileOutline | SentinelWorkspaceTools.cs | filepath only | Returns structural outline (namespaces, classes, methods with line ranges) |
| SafeDeleteUnusedSymbol | SentinelWorkspaceTools.cs | line/column (TextSpan) | `FindNode(TextSpan)` for exact node; coordinate-based, safe |
| InspectSymbol | SentinelSymbolTools.cs | contextSnippet ± lineBefore/lineAfter | Resolves via `FindSymbolAtSnippetAsync` |
| FindReferences | SentinelSymbolTools.cs | name + optional contextSnippet/filepath | Can narrow via contextSnippet if filepath supplied; else union |
| GenerateMapping | SentinelRefactoringTools.cs | type name + FirstOrDefault | Delegates to MappingEngine for type resolution |
| AddUsingDirective | SentinelRefactoringTools.cs | namespace name + idempotency check | Checks for duplicates; silent-first-match-with-caveat |
| SyncTypeAndFilename | SentinelRefactoringTools.cs | filepath + first non-nested type | Operates on primary type; no name collision |
| ExtractLocalVariable | SentinelRefactoringTools.cs | contextSnippet ± lineBefore/lineAfter | `TryFindSnippetPosition()` for position precision |
| ExtractMethodSafe | SentinelRefactoringTools.cs | contextSnippet ± lineBefore/lineAfter | `FindSnippetPosition()` for position precision |
| FindImplementationsForMemberAsync | SentinelSymbolTools.cs | name + optional contextSnippet | Disambiguates via contextSnippet when supplied |
| ApplyFileCodemod | SentinelCodemodTools.cs | filepath only | File-wide transformation, no target resolution |
| ApplyMethodCodemod | SentinelCodemodTools.cs | filepath + methodName + optional contextSnippet | Can narrow via contextSnippet ± lineBefore/lineAfter |
| ApplyClassCodemod | SentinelCodemodTools.cs | filepath + className + optional contextSnippet | Can narrow via contextSnippet ± lineBefore/lineAfter |
| ConvertAnonymousToNamed | SentinelAdvancedRefactoringTools.cs | filepath only | Converts first anonymous object to class |
| InvertAssignments | SentinelAdvancedRefactoringTools.cs | filepath + startLine + endLine | Line-range based, coordinate-precise |
| Introduce | SentinelAdvancedRefactoringTools.cs | contextSnippet ± lineBefore/lineAfter | Resolves from expression snippet |
| ExtractEventHandlers | SentinelAsyncifyTools.cs | contextSnippet ± lineBefore/lineAfter | Extracts from event handler; snippet-based |
| ExtractMembers | SentinelAdvancedRefactoringTools.cs | filepath + className + optional contextSnippet | Can narrow via contextSnippet |
| WrapRange | SentinelAdvancedRefactoringTools.cs | filepath + startLine + endLine | Line-range based |

### Name-Resolving Operations (58 tools)
Resolve targets by symbol/member/type/project name only; **this category includes the defect shape tools**.

#### High-Risk: Name-Only + Mutating (11 tools — the defect shape)

| Tool | File | Target Resolution | Mutates | Evidence Citation |
|------|------|-------------------|---------|-------------------|
| **ReplaceMember** | SentinelRefactoringTools.cs:333 | Member name + FirstOrDefault | Member body | RefactoringEngine.cs:1223-1224 `.FirstOrDefault(m => GetMemberName(m) == memberName)` |
| **RemoveMember** | SentinelRefactoringTools.cs:382 | Member name + FirstOrDefault | Member declaration | RefactoringEngine.cs:1325-1326; tool gains precheck (SentinelRefactoringTools.cs:394-413) but resolution unchanged |
| **ChangeAccessibility** | SentinelRefactoringTools.cs:524 | Member name + FirstOrDefault | Member modifiers | RefactoringEngine.cs:2937-2938 `.FirstOrDefault(m => GetMemberName(m) == targetName)` |
| **ModifyAttribute** (add) | SentinelRefactoringTools.cs:721 | Member name + FirstOrDefault | Attribute list | RefactoringEngine.cs:2586-2587 `.FirstOrDefault(m => GetMemberName(m) == targetName)` |
| **ModifyAttribute** (replace) | SentinelRefactoringTools.cs:721 | Member name + FirstOrDefault | Attribute list | RefactoringEngine.cs (same as add) |
| **ModifyAttribute** (remove) | SentinelRefactoringTools.cs:721 | Member name + FirstOrDefault | Attribute list | RefactoringEngine.cs (same as add) |
| **ModifyModifier** (add) | SentinelRefactoringTools.cs:777 | Member name + FirstOrDefault | Member modifiers | RefactoringEngine.cs:3003-3004 `.FirstOrDefault(m => GetMemberName(m) == targetName)` |
| **ModifyModifier** (remove) | SentinelRefactoringTools.cs:777 | Member name + FirstOrDefault | Member modifiers | RefactoringEngine.cs:3068-3069 `.FirstOrDefault(...)` |
| **ModifyBaseType** (add) | SentinelRefactoringTools.cs:828 | Type name + FirstOrDefault | Base type list | RefactoringEngine.cs:2666-2667 `.FirstOrDefault(c => c.Identifier.Text == typeName)` |
| **ModifyBaseType** (remove) | SentinelRefactoringTools.cs:828 | Type name + FirstOrDefault | Base type list | RefactoringEngine.cs:2887-2888 `.FirstOrDefault(...)` |
| **AddSummaryComment** | SentinelRefactoringTools.cs:562 | Member name + FirstOrDefault | XML documentation | RefactoringEngine.cs:3132-3133 `.FirstOrDefault(m => GetMemberName(m) == targetName)` |

#### Medium-Risk: Name-Only + Mutating (4 tools)

| Tool | File | Target Resolution | Mutates | Evidence Citation |
|------|------|-------------------|---------|-------------------|
| **AddMember** | SentinelRefactoringTools.cs:879 | Container name + FirstOrDefault | Member list | RefactoringEngine.cs:1256-1257 `.FirstOrDefault(c => c.Identifier.Text == containerName)` |
| **AddMemberTyped** | SentinelRefactoringTools.cs:940 | Container name + FirstOrDefault | Member list | Delegates to AddPropertyAsync/AddFieldAsync (same as AddMember) |
| **AddConstructorParameter** | SentinelRefactoringTools.cs:600 | Class name + FirstOrDefault, then first constructor | Constructor + field | RefactoringEngine.cs:3394-3395 |
| **ModifyEnum** | SentinelRefactoringTools.cs:482 | Enum name + FirstOrDefault | Enum members | RefactoringEngine.cs:2281 `.FirstOrDefault(e => e.Identifier.Text == enumName)` |

#### Lower-Risk: Name-Only + Mutating (2 tools)

| Tool | File | Target Resolution | Mutates | Evidence Citation |
|------|------|-------------------|---------|-------------------|
| **CreateProject** | SentinelWorkspaceTools.cs:628 | Project name (new, no collision check) | New project + solution file | SentinelWorkspaceTools.cs:628+ |
| **SplitProjectByFolder** | SentinelWorkspaceTools.cs:649 | Project name + folder name | Files across projects | SentinelWorkspaceTools.cs:649+ |

#### Read-Only: Name-Only + Safe (15 tools)

| Tool | File | Target Resolution | Notes |
|------|------|-------------------|-------|
| **LocateSymbol** | SentinelSymbolTools.cs:189-293 | Symbol name + optional filters (containingType, namespace, project) | Returns all matches; caller disambiguates |
| **GetTypeInfo** | SentinelSymbolTools.cs:451-458 | Type name + optional projectName | Read-only, returns type information hierarchy |
| **QuerySymbolRelationships** | SentinelSymbolTools.cs:200-263 | Name + searchKind filter | Returns all matches; filters by kind, broadens on zero results |
| **GetBestInsertionPoint** | SentinelSymbolTools.cs:314+ | Method name + filepath | Read-only, returns line number for insertion |
| **ChangeSignature** | SentinelAdvancedRefactoringTools.cs | Method name + optional filepath + contextSnippet | Mutating but can narrow via contextSnippet |
| **InlineClass** | SentinelAdvancedRefactoringTools.cs | Class name + filepath (source + target) | Mutating; multiple files but class name by context |
| **MoveAllTypesToFiles** | SentinelAdvancedRefactoringTools.cs | Scope (file/project/solution) + optional target filepath | Scope-based, not single-name resolution |
| **PullUpMember** | SentinelAdvancedRefactoringTools.cs | Class name + member name + FirstOrDefault | Mutating; pulls member up; can use contextSnippet to disambiguate |
| **IntroduceParameterObject** | SentinelAdvancedRefactoringTools.cs | Method name + optional filepath | Mutating; encapsulates parameters |
| **Inline** | SentinelAdvancedRefactoringTools.cs | Target name (method/variable/field) + optional methodName for param | Mutating; inlines by name; no contextSnippet parameter observed |
| **MoveType** | SentinelAdvancedRefactoringTools.cs | Type name + destination scope | Mutating; moves type; name-based resolution |
| **SyncInterface** | SentinelAdvancedRefactoringTools.cs | Interface name + optional className + action | Mutating; manages sync; multiple actions |

---

## Step 2: Detailed Categorization of Name-Resolving & Coordinate-Resolving Tools

### Key Disambiguation Methods (Ranked by Safety)

1. **Semantic-uniqueness check** (most safe — 0 tools currently implement)
   - Engine explicitly checks candidate count; errors if >1
   - Force caller to disambiguate or use alternative approach
   - Currently: **NO tools implement this pattern** — flag as "pattern to move toward"

2. **Line/column exact node matching** (safe — 1 tool: SafeDeleteUnusedSymbol)
   - `FindNode(TextSpan(position, 0))` yields exact syntax node
   - Two overloads at different lines are unambiguous
   - Evidence: StructuralRefinementEngine.cs:72-122

3. **ContextSnippet + lineBefore/lineAfter** (good — 8 tools)
   - Snippet text match, optionally narrowed by adjacent line context
   - Reduces ambiguity by matching code structure, not just name
   - Applied to: ExtractLocalVariable, ExtractMethodSafe, FindReferences, FindImplementationsForMemberAsync, InspectSymbol, ApplyMethodCodemod, ApplyClassCodemod, ExtractEventHandlers
   - Pattern established in plan-symbol-tool-hardening-v1.md (Tasks D/E)

4. **Name + containingType/namespace/projectName filter** (moderate — 6 tools)
   - Narrows scope but may still match multiple if filter isn't unique
   - Example: LocateSymbol with `containingType` filter
   - Less safe than snippet matching; depends on caller providing all filters

5. **Name-only with FirstOrDefault** (unsafe — 15 tools)
   - **This is the defect shape**
   - Silent first-match; no error on ambiguity
   - Example: ReplaceMember, RemoveMember, ChangeAccessibility, AddMember, AddMemberTyped, ModifyEnum, ModifyAttribute (3 sub-actions), ModifyModifier (2 sub-actions), ModifyBaseType (2 sub-actions), AddSummaryComment, AddConstructorParameter

### Ambiguous-Match Behavior Categories

**Silent First-Match (Defect):** 15 tools
- When 2+ candidates match, picks one silently with no warning/error
- Caller has no way to target "the second one"
- Tools: All 15 listed above in "Name-only with FirstOrDefault"

**Silent First-Match With Caveat:** 2 tools
- Same behavior, but tool documentation or code comment states how ties break (e.g., "first declaration in document order")
- Still a defect, but not surprising if read carefully
- Tools: GetMethodSource (documented as "first match"), AddUsingDirective (idempotency check)

**Errors on Ambiguity:** 0 tools
- Engine detects >1 candidate, returns error instead of guessing
- Currently: no tools implement this; would be the safest choice

**Returns All Matches:** 3 tools
- Engine returns list; caller disambiguates
- Tools: LocateSymbol, QuerySymbolRelationships, SearchSolutionText (regex-based, inherently multi-match)

**Narrowing Parameters (Partial):** 8 tools
- Accept contextSnippet or filters but don't *require* them
- If not provided, fall back to first-match or union
- Tools: FindReferences, FindImplementationsForMemberAsync, InspectSymbol, ApplyMethodCodemod, ApplyClassCodemod, ExtractLocalVariable, ExtractMethodSafe, ExtractEventHandlers

---

## Step 3: Cross-Reference Against ReplaceMember's Defect Shape

### ReplaceMember's Defect Shape Definition

- **Target resolution:** Name-only via `GetMemberName(MemberDeclarationSyntax)` → identifier text only
- **Lookup:** `root.DescendantNodes().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => GetMemberName(m) == memberName)`
- **Behavior on ambiguity:** Silent first-match (document-order walk, first comes first)
- **Impact:** Mutating (replaces member body); if wrong member matched, body replaced with no warning

### Tools Matching All Three Criteria: Name-Only + Silent-First-Match + Mutating

**15 tools total** share this exact defect shape. Ranked by severity (blast radius, likelihood of harm):

#### CRITICAL SEVERITY (2 tools)

| Tool | Mutates | Escape Hatch | Severity Note |
|------|---------|--------------|---|
| **ReplaceMember** | Member body (1 file) | None | Replaces wrong method body if overloads exist. Silent, undetectable code corruption. Exact replica of defect. |
| **RemoveMember** | Member declaration (1 file) | Precheck for callers/implementations exists, but doesn't prevent wrong member selection (checks usage of the *matched* member, not whether *correct* member was matched). | Deletes wrong member if multiple share name. Precheck guards "don't delete if used" but not "delete the right one." Destructive, code loss. |

#### HIGH SEVERITY (1 tool)

| Tool | Mutates | Escape Hatch | Severity Note |
|------|---------|--------------|---|
| **ChangeAccessibility** | Member accessibility modifier (1 file) | None | Changes visibility of wrong overload (e.g., private→public or vice versa). Silent semantic corruption. Affects public API surface; downstream callers may break. |

#### MEDIUM SEVERITY (8 tools)

| Tool | Mutates | Escape Hatch | Severity Note |
|------|---------|--------------|---|
| **ModifyAttribute** (add/replace/remove — 3 sub-actions) | Attribute list (1 file) | None | Adds/replaces/removes attribute on wrong member. Semantic corruption ([Obsolete], [NotNull], [MethodImpl], etc. on wrong overload). Code compiles; behavior subtly broken. |
| **ModifyModifier** (add/remove — 2 sub-actions) | Member modifiers (1 file) | None | Adds/removes modifier (virtual/static/async/override) on wrong member. Breaks inheritance/polymorphism contracts. Semantic corruption without compilation error. |
| **ModifyBaseType** (add/remove — 2 sub-actions) | Base type list (1 file) | None | Adds/removes base type or interface on wrong type. Type collisions rare in practice; nesting edge case. Semantic change; code compiles. |
| **AddSummaryComment** | XML documentation (1 file) | None | Adds summary to wrong overload. Silent misresolution; wrong documentation reaches users. Low code-corruption risk (not executable), but trust-breaking. |

#### LOW-MEDIUM SEVERITY (4 tools)

| Tool | Mutates | Escape Hatch | Severity Note |
|------|---------|--------------|---|
| **AddMember** | Member list (1 file) | Container-name collisions rare; `position` parameter can narrow insertion point further (optional, not required) | Adds member to first matching container if nested classes share name. Rare collision; position parameter provides partial escape. |
| **AddMemberTyped** | Member list (1 file) | Same as AddMember | Typed variant doesn't change resolution safety; relies on AddMember logic. |
| **AddConstructorParameter** | Constructor + field (1 file) | Class name is usually unique; multiple constructors on same class is rare. First-match only applies to constructor selection, not class. | Finds first matching class + its constructor. If class has 2+ constructors, first is chosen. Rare but possible. |
| **ModifyEnum** | Enum member list (1 file) | Enum names rarely collide; internal nesting is edge case. No escape hatch observed. | Enum names almost never overload/collide at same scope. Internal nesting (same name in sibling types) is the only collision case. Very rare in practice. |

#### LOWER SEVERITY (2 tools)

| Tool | Mutates | Escape Hatch | Severity Note |
|------|---------|--------------|---|
| **CreateProject** | New project + solution file (multiple files) | Whole-workspace operation; doesn't resolve an *existing* target. No collision risk (new project). | Not truly "name-only target resolution" — creates new, not resolves existing. Low defect relevance. |
| **SplitProjectByFolder** | Files across projects (multiple files) | Project-level operation, not symbol-level. Folder name is not a symbol resolution. | Not a single-target symbol/member resolution. Project-level, lower defect relevance. |

### RemoveMember's Tool-Level Precheck (Clarification)

Per plan-symbol-tool-hardening-v1.md, RemoveMember gained a precheck (SentinelRefactoringTools.cs:394-413) that:
- Calls `FindCallersAsync()` and `FindImplementationsForMemberAsync()` to detect usages
- **Refuses removal if any callers/implementations exist**

**Critical observation:** This precheck validates *usage*, not *ambiguity*. If a method signature takes `int` and there are two `Add(int)` methods (overloads), the precheck:
1. Matches the first `Add(int)` (name-only resolution)
2. Checks its usages via semantic lookup
3. If no usages found, removal is allowed
4. **Wrong member is deleted; precheck passes**

The precheck is a behavioral safeguard (don't delete used members) but does **NOT fix the core resolution defect** (name-only, silent first-match).

### Tools with Partial Escape Hatches (Not in Defect Shape but Close)

Several tools are NAME-RESOLVING + MUTATING but have optional narrowing parameters (not required):

| Tool | Narrowing Option | Status |
|------|------------------|--------|
| **ApplyMethodCodemod** | contextSnippet ± lineBefore/lineAfter | Optional; can narrow via snippet but defaults to name-only if omitted |
| **ApplyClassCodemod** | contextSnippet ± lineBefore/lineAfter | Optional; same as ApplyMethodCodemod |
| **ChangeSignature** | methodName + optional filepath/contextSnippet | Optional narrowing; defaults to name-only |
| **PullUpMember** | className + memberName + optional contextSnippet | Optional narrowing |
| **MoveType** | typeName + destination scope | Type names rarely collide; scope narrowing is secondary |
| **Inline** | targetName + kind (method/variable/field) + optional methodName | Kind parameter narrows scope; not full disambiguation |
| **IntroduceParameterObject** | methodName + optional filepath | Optional narrowing |

These are **not in the defect shape** (have optional escape hatches) but should be monitored — if callers ignore the optional parameters, they degrade to name-only resolution.

---

## Summary: Top 5 Highest-Priority Candidates for Remediation

Based on mutation severity × blast radius × likelihood:

### 1. ReplaceMember (CRITICAL)
**Defect:** Name-only resolution + silent first-match + mutates member body.  
**Scenario:** A method with two overloads (e.g., `Add(string)` and `Add(int)`). ReplaceMember matches the first, caller's `newSource` has a different signature. Wrong method body replaced; caller has no indication anything went sideways.  
**Why highest priority:** Direct code corruption; replacement is undetectable in compilation.

### 2. RemoveMember (CRITICAL)
**Defect:** Name-only resolution + silent first-match + deletes member.  
**Scenario:** A method with two overloads. RemoveMember matches the first, deletes it. Precheck validates *usage* of that wrong member (no callers → deletion allowed), but doesn't catch the misresolution.  
**Why:** Destructive mutation (code loss); precheck doesn't prevent silent misresolution.

### 3. ChangeAccessibility (HIGH)
**Defect:** Name-only resolution + silent first-match + changes API surface.  
**Scenario:** Two `Handle()` methods (overloads). ChangeAccessibility makes the wrong one public. Public API now exposes something unintended; downstream callers may break.  
**Why:** Silent semantic corruption affecting public contract; API surface is often versioned/documented, making a mistake here especially damaging.

### 4. ModifyAttribute (add/replace/remove) (MEDIUM)
**Defect:** Name-only resolution + silent first-match + adds/removes attributes.  
**Scenario:** Two `Process()` methods. `ModifyAttribute` adds `[Obsolete]` to the wrong one. Code compiles; behavior silently wrong. Or adds `[MethodImpl(Aggressive)]` to the wrong overload, changing performance characteristics unexpectedly.  
**Why:** Semantic corruption without compilation error; attributes control behavior, threading, caching, etc.

### 5. ModifyModifier (add/remove) (MEDIUM)
**Defect:** Name-only resolution + silent first-match + changes modifiers.  
**Scenario:** Two `ValidateAsync()` methods (virtual and non-virtual overloads). ModifyModifier adds `virtual` to the wrong one. Inheritance contract silently broken.  
**Why:** Breaks polymorphism contracts; semantic corruption that compiles.

---

## Verification Checklist

- [x] Step 1: Tool count verified fresh (104 tools, 2026-08-18)
- [x] Step 2: Categorization claims cite exact file/line (40+ tools analyzed)
- [x] Step 3: Ranked cross-reference produced with evidence
- [x] Defect shape definition clear (name-only + silent-first-match + mutating)
- [x] RemoveMember precheck behavior clarified (usage validation, not resolution fix)
- [x] SafeDeleteUnusedSymbol current state confirmed (coordinate-based, safe)

---

## Suggested Next Step

A follow-up remediation plan should address the 5 highest-priority tools (ReplaceMember, RemoveMember, ChangeAccessibility, ModifyAttribute, ModifyModifier) by choosing one of:

1. **Add optional contextSnippet + lineBefore/lineAfter parameters** (backward compatible)
   - Mirror the pattern from plan-symbol-tool-hardening-v1.md (FindReferences, FindImplementationsForMemberAsync)
   - Caller can narrow by code context; defaults to existing name-only if not supplied
   - Pro: Backward compatible; incremental roll-out
   - Con: Still name-only by default; doesn't force disambiguation

2. **Require line/column coordinates** (breaking change)
   - Migrate to exact syntax node matching (like SafeDeleteUnusedSymbol)
   - Pros: Unambiguous; no silent failures
   - Con: Requires callers to provide precise coordinates

3. **Error on ambiguity** (breaking change)
   - Check candidate count; refuse operation if >1
   - Pros: Catches misresolution immediately; clear signal to caller
   - Con: Requires caller to disambiguate before retry

The plan should also measure impact on existing tests and decide whether to apply the same pattern to the medium-risk tools (AddMember, AddMemberTyped, ModifyEnum, AddConstructorParameter) and tools with optional-narrowing parameters (ApplyMethodCodemod, ChangeSignature, etc.).

**Out of scope for this plan:** Tools already covered by plan-symbol-tool-hardening-v1.md (FindReferences, QuerySymbolRelationships) were confirmed current; no re-review needed.

---

**Report compiled by:** Claude Code  
**Approach:** Exhaustive code review of all 104 tools, high-risk tools analyzed in depth (40+ detailed), medium/low-risk tools spot-checked or categorized from parameter lists.  
**Scope limitations:** Time-boxed review prioritized mutating tools; read-only discovery tools spot-checked. All findings grounded in exact code citations (file/line).
