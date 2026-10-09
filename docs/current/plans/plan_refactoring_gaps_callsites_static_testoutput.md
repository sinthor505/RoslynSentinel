# Plan: single-pass call-site edits for parameter removal, RunTest failed-test output, and a RenameSymbol refusal comment

**Status:** DRAFT 2026-10-09. Four deferred journal-digest items traced to source: (a) and (c) get implementation steps, (b) is already shipped (no steps), (d) is a one-comment slice. Nothing is built.

## Problem

Source of the items: `docs/current/proposals/proposal_journal_digest_followup_decisions.md` (decisions 12 and 13, "Still open" list) and Risks items 4, 5, 7 of `docs/current/plans/plan_agent_tooling_hooks_and_small_ergonomics.md`. Each item was re-traced to source for this plan; journal lines were not trusted.

**(a) MethodSignature / ParameterEdit `remove` refuses when a call site sits in the edited file** (journal `47b2c93d:L39`).
`MemberRefactoringEngine.RemoveMethodParameterAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:1453`) builds the shortened declaration first and stores that text in `pendingChanges[filePath]` (line ~1519). For each reference it then re-parses `pendingChanges` text and finds the call site with `inv.Span == invocation.Span` (line 1596), where `invocation` comes from the ORIGINAL tree. Any edit earlier in the file (the shortened declaration, or an earlier call site) shifts later spans, so the lookup misses and the tool refuses with "call site at ... could not be re-located after an earlier edit to the same file" (line 1603). `ParameterEditToolTests.cs:90` records this as "the original tool's behavior".
Consequences beyond the reported case (hypotheses until Step 1's tests run; the code path is the same):
- Two or more call sites in ONE other file hit the same miss after the first rewrite.
- The nearest enclosing `InvocationExpressionSyntax` is taken from the reference token without checking the token is part of that invocation's `Expression`. For `Wrap(Compute)` (method group passed as an argument) the `Wrap(...)` call is picked and its last argument is dropped: a silent wrong edit that only the compile gate can catch.
- Call-site files are formatted whole-file with `Formatter.FormatAsync(doc, null, ...)` (line ~1630) and no EOL normalisation. `RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyFourTests.cs:620` already notes that an LF fixture trips the EOL-change guard because of this ("a real tool-side defect, tracked separately").
The sibling `BasicRefactoringEngine.ChangeSignatureAsync` has the same relocation pattern (`BasicRefactoringEngine.cs:365-376`, comment "an earlier edit to this same document may have shifted spans, so this can legitimately miss") but degrades to `skippedCallSites` instead of refusing. It is out of scope here (see Risks).

**(b) MoveMember of instance members into a static class** (journal `51989d5e:L18`, "CS0708, no makeStatic option"). ALREADY HANDLED, no steps. The journal line is 2026-10-03 01:50; commit `b773820` (2026-10-03 02:47, "ModifyModifier/MoveMember: convert instance members to static and rewrite callers") fixed it the same night. Evidence in current source:
- `MemberRefactoringEngine.MoveMemberAsync` (`MemberRefactoringEngine.cs:3330-3337`) detects a static target class and calls `RequireInstanceMembersConvertibleToStatic` (line 3447), which refuses instance fields ("a static class cannot hold instance state ... Make the field(s) static first") and refuses methods/properties that use instance state, naming each use.
- `StaticConversionEdits.FindInstanceStateUses` (`StaticConversionEdits.cs:121`) treats every `this`/`base` expression, instance field/property/event, implicit-this instance call and primary-constructor capture as instance state, so `this` handling is "refuse and name it".
- Stateless members are made `static` and callers rewritten to `Target.Member` (`MemberRefactoringEngine.cs:3403-3411`).
- The `MoveMember` tool description already says so (`AdvancedRefactoringTools.cs` `targetClassName`: "an existing STATIC class receives instance members that use no instance state as STATIC").
- Tests: `RoslynSentinel.Tests.Battery.Basic/MakeStaticRewritesCallersTests.cs` `MoveInstanceMemberIntoStaticClass_MakesItStaticAndRewritesCaller` (line 190) and `..._UsingInstanceState_IsRefusedNamingTheState` (line 213).
The live server binary is current (`McpServerStatus`: built 2026-10-09, not stale). The only open piece is a stateful member (needs a `this`-as-parameter conversion); that is feature-sized and listed under Risks as needs design.

**(c) RunTest does not surface test output** (journal `47b2c93d:L31`, `L36`). `TestRunEngine.ParseTrx` (`RoslynSentinel.Engines.Basic/TestRunEngine.cs:457-492`) reads only `UnitTestResult/Output/ErrorInfo`; the sibling `Output/StdOut` element of the TRX (where VSTest adapters put captured test output) is never read. The only raw output kept is `StdoutTail` (the `dotnet test` console, last 40 lines), which `WorkspaceBuildTestImpl.WithoutTailsWhenClean` (`RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:129`) drops on clean runs and which contains no per-test output at `-v quiet` anyway. `TestCaseResult` (`TestRunEngine.cs:7`) has no field to carry it.

**(d) RenameSymbol same-signature refusal** (journal `51989d5e:L28`). Decision 13 of the proposal: the CS0111/CS0121 refusal is correct, add a comment so nobody re-investigates. Traced: there is no explicit collision check in `RefactoringSignatureImpl.RenameSymbol` (`RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs:61`) or `BasicRefactoringEngine.RenameSymbolAsync` (`BasicRefactoringEngine.cs:698`); the rename is produced by Roslyn's `Renamer` and the refusal comes from the compile gate in the `ValidateAndApplyAsync` call (line ~107), with `CompilerErrorLookupHelper` adding a CS0101/CS0111 hint (`CompilerErrorLookupHelper.cs:94`). Not reproduced live in this run (hypothesis for the exact mechanism; the decision itself is the owner's).

## Decision

1. **(a)** Replace the "edit declaration, then re-locate each call site in edited text" loop with a single pass per file: collect all edit nodes (the declaration and every call-site invocation) from ONE canonical syntax root per file path, then apply them with one `root.ReplaceNodes(...)` and one annotation-scoped format, normalising EOL to the file's dominant EOL. No re-parse, no span re-lookup, so no edit can invalidate another. The method-group mis-pick is closed by requiring the reference to lie inside `invocation.Expression`. Existing refusals (named arguments, non-last parameter, non-invocation) and their message text stay.
2. **Ledger for (a): do not extend `IScopedOperationLedger` to `remove`.** After Step 1 the operation is all-or-nothing: it either refuses before any write or emits one complete changeset that the compile gate validates, so there is no residual state to track. The ledger (`RoslynSentinel.Common/IScopedOperationLedger.cs`; opened only at `AdvancedRefactoringTools.cs:533`) earns its keep where a tool deliberately applies a partial, intentionally non-compiling state (`MoveMember` with unresolved receivers). The honest ledger candidate is `ChangeSignatureAsync` (it returns `skippedCallSites` and applies the rest); that is a separate design (Risks 2).
3. **(b)** No work. Record the evidence above; close the journal item.
4. **(c)** Add `string? Output = null` as a trailing optional parameter of `TestCaseResult`, filled from TRX `<StdOut>` for FAILED tests only, whitespace-only treated as absent, capped at 2000 characters keeping the END of the text (closest to the failure) with a leading `[truncated] ` marker. No new `RunTest` parameter (no schema-diet cost, no public parameter change). Passed and skipped tests never carry `Output`. Both `RunTest` tool descriptions get one sentence saying so.
5. **(d)** Comment only.

## Execution rules

- Each step is one compile-green slice for an `implementer` (Haiku, pinned): at most 3 files, edit nothing outside the named symbols, reply `RESCOPE:` if a test seems to need another change.
- No Write/Edit/shell writes on `.cs` files: use `Member(replace)`, `ReplaceSnippet`, `Member(addMember)` / `addTopLevelType` (new type), `CreateFile` via `WriteFile(operation: CreateFile, content: ...)`. Copy `oldContent` snippets from `ReadFile` output (files are CRLF).
- NUnit 5.0: `await` every `Assert.ThrowsAsync`/`CatchAsync`; test method names end in `Async` where they await.
- After Step 1 and Step 2, a plain `Build` is not enough; each step names one test fixture to run with `RunTest`.
- Steps 1, 2, 3, 4, 5 are independent of each other and may land in any order, but dispatch them sequentially (shared server process; never parallel `.cs` edits). Step 3 and Step 4 must follow Step 2 (the `Output` property must exist).

## Steps

### Step 1 - single-pass edits in `RemoveMethodParameterAsync`, with tests

- Files:
  - `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
  - `RoslynSentinel.Tests.Tools.Basic/RemoveMethodParameterSinglePassTests.cs` (new)
  - `RoslynSentinel.Tests.Tools.Basic/ParameterEditToolTests.cs` (comment only)
- Symbols: `MemberRefactoringEngine.RemoveMethodParameterAsync` (signature unchanged); new test class `RemoveMethodParameterSinglePassTests`; the comment inside `ParameterEdit_MethodRemove_MatchesMethodSignatureAsync` (lines 89-90).
- Call sites: `RemoveMethodParameterAsync` has exactly one caller, `RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs:204`; the signature and the `DocumentEditResult` shape (`Outcome`, `UpdatedText`, `Changes`, `Message`) do not change, so no call-site edits. Existing tests that exercise it through the tool: `RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyFourTests.cs:622` (`MethodSignature_Remove_LastParameter_UpdatesCallSite`), `:631` (`..._NonLastParameter_Refused`, message must still contain "last parameter"), `:643` (`..._NamedArgumentCallSite_Refused`, message must still contain "named arguments"), and `ParameterEditToolTests.cs:92` (parity).
- Change (engine). Keep everything in the method up to and including the `lastParam.Identifier.Text != paramName` refusal verbatim. Replace everything from `var targetParamCount = parameters.Count;` to the end of the method with the design below. Use `Member(operation: replace, ...)` on the whole method, or one `ReplaceSnippet` per contiguous block; it is one logical edit.
  ```csharp
  var targetParamCount = parameters.Count;

  // Single pass: every edit in a file (the declaration and all call sites) is collected from ONE root per file
  // path and applied with one ReplaceNodes, so no edit can shift the span of another (the old code re-parsed the
  // half-edited text and looked call sites up by their ORIGINAL span, which missed after the first edit).
  var rootByPath = new Dictionary<FilePathWrapper, SyntaxNode> { [filePath] = root };
  var documentByPath = new Dictionary<FilePathWrapper, Document> { [filePath] = document };
  var editNodesByPath = new Dictionary<FilePathWrapper, List<SyntaxNode>> { [filePath] = [methodDecl] };

  var symbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
  if (symbol != null)
  {
      var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
      foreach (var reference in references)
      {
          foreach (var location in reference.Locations)
          {
              if (location.IsImplicit || location.Document?.FilePath == null) { continue; }

              var refDoc = location.Document;
              FilePathWrapper refPath = refDoc.FilePath!;   // same implicit conversion the old code used
              if (!rootByPath.TryGetValue(refPath, out var refRoot))
              {
                  var loaded = await refDoc.GetSyntaxRootAsync(cancellationToken);
                  if (loaded == null) { continue; }
                  refRoot = loaded;
                  rootByPath[refPath] = refRoot;
                  documentByPath[refPath] = refDoc;
                  editNodesByPath[refPath] = [];
              }

              var span = location.Location.SourceSpan;
              var refLineNumber = refRoot.SyntaxTree.GetLineSpan(span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
              var token = refRoot.FindToken(span.Start);
              // The reference must lie inside the invocation's callee expression; otherwise `Wrap(Compute)` (a method
              // group argument) would match the enclosing Wrap(...) call and silently drop ITS last argument.
              var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => i.Expression.Span.Contains(span));
              if (invocation == null) { /* existing "not a simple invocation expression" refusal, unchanged text */ }
              // existing named-argument refusal (unchanged text)
              // existing `args.Count != targetParamCount` -> continue (unchanged)
              if (!editNodesByPath[refPath].Contains(invocation)) { editNodesByPath[refPath].Add(invocation); }
          }
      }
  }

  var result = new Dictionary<FilePathWrapper, string>();
  foreach (var (path, nodes) in editNodesByPath)
  {
      var fileDocument = documentByPath[path];
      var annotation = new SyntaxAnnotation();
      var newRoot = rootByPath[path].ReplaceNodes(nodes, (original, rewritten) => (rewritten switch
      {
          MethodDeclarationSyntax m => (SyntaxNode)m.WithParameterList(m.ParameterList.WithParameters(SyntaxFactory.SeparatedList(m.ParameterList.Parameters.Take(targetParamCount - 1)))),
          InvocationExpressionSyntax i => i.WithArgumentList(i.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(i.ArgumentList.Arguments.Take(targetParamCount - 1)))),
          _ => rewritten
      }).WithAdditionalAnnotations(annotation));
      var formatted = await Formatter.FormatAsync(fileDocument.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
      var originalText = await fileDocument.GetTextAsync(cancellationToken);
      result[path] = EolUtilities.NormalizeEol((await formatted.GetTextAsync(cancellationToken)).ToString(), EolUtilities.DetectDominantEol(originalText));
  }

  return new DocumentEditResult { Outcome = EditOutcome.Modified, FilePath = filePath, UpdatedText = result[filePath], Changes = result,
      Message = $"// paramName='{paramName}', callSitesUpdated='{result.Count - 1}'" };
  ```
  Notes for the implementer: the three "existing ... refusal" bullets keep their current message strings exactly (the Advanced tests assert "named arguments"); the old `pendingChanges`, `currentContent`, `currentRoot`, `targetInv` locals and the trailing "Format every touched file" loop are deleted. `ReplaceNodes` handles nested targets (a recursive call inside the declaration) because the callback receives the already-rewritten node. If `EolUtilities` is not in scope, add `using RoslynSentinel.Common;` (Common is visible to Engines.Basic; `RoslynFormattingHelper` already uses it). If `result[filePath]` throws `KeyNotFoundException` because `FilePathWrapper` equality differs between `filePath` and `document.FilePath`, key `editNodesByPath` etc. by the same wrapper the old code used for `Changes` (`filePath`) and reply `RESCOPE:` with the observed values.
- Change (tests, new file). Class `RemoveMethodParameterSinglePassTests`, `[TestFixture]`, `[Category("RefactoringSignatureTools")]`. Build the engine directly, no tool layer: `new MemberRefactoringEngine(workspace.Manager, new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance), new ValidationEngine(workspace.Manager, new DiffEngine(), NullLogger<ValidationEngine>.Instance), new SentinelConfiguration())` (same construction as `ParameterEditToolTests.BuildTools`, lines 45-50); workspace via `InMemoryWorkspace.Create((relativePath, content), ...)` (`RoslynSentinel.Tests/Fakes/InMemoryWorkspace.cs`), file path via `workspace.Manager.ResolveFromWire(workspace.PathOf(relativePath))`, text via `workspace.ReadText` is NOT needed because the engine only returns text (assert on `result.Changes`). Cases (each calls `RemoveMethodParameterAsync(path, "Compute", "second")` on `public void Compute(int first, string second)`):
  1. `SameFileCallSiteAfterDeclaration_IsUpdated`: caller in the same class after the declaration. Expect `Outcome == Modified`, `Changes.Count == 1`, text contains `Compute(int first)` and a call `Compute(1)`, and no `"x"`.
  2. `TwoCallSitesInSameFile_BeforeAndAfterDeclaration_BothUpdated`.
  3. `TwoCallSitesInAnotherFile_BothUpdated`: `Changes.Count == 2`, both calls shortened. (Probes the multi-site hypothesis; it is a regression guard either way.)
  4. `RecursiveCallInsideDeclaration_IsUpdated`: `Compute` calls itself from its own body.
  5. `MethodGroupArgument_IsRefusedNotMisedited`: `Wrap(Compute)` where `Wrap(Action<int,string>)`; expect `Outcome == CannotRemove` and message containing "not a simple invocation expression".
  6. `LfCallerFile_StaysLf`: caller file content uses only `\n`; the returned text for it contains no `\r`. If this fails, do not weaken it: reply `RESCOPE:` with the observed text (it would mean `Formatter.FormatAsync` rewrote EOL despite `NormalizeEol`).
  Each fixture source uses `namespace N;` file-scoped types; keep sources small and compile-valid (the engine resolves symbols against the in-memory compilation).
- Change (comment). In `ParameterEditToolTests.cs` lines 89-90 replace the two-line comment with: `// The call site lives in a second file so this parity case does not depend on same-file handling; same-file and multi-site call sites are covered by RemoveMethodParameterSinglePassTests.`
- Done when: `RunTest(scope: project, scopeName: "RoslynSentinel.Tests.Tools.Basic", filter: "FullyQualifiedName~RemoveMethodParameterSinglePassTests|FullyQualifiedName~ParameterEditToolTests")` passes (all new cases plus the untouched parity cases) and a `Build` has 0 errors.

### Step 2 - capture failed-test `<StdOut>` in `TestRunEngine`

- Files:
  - `RoslynSentinel.Engines.Basic/TestRunEngine.cs`
  - `RoslynSentinel.Tests.Basic/TestRunEngineTrxOutputTests.cs` (new)
- Symbols: record `TestCaseResult` (line 7), `TestRunEngine.ParseTrx` (line 457, `private static` -> `public static`, same precedent as `DetectFileLock` at line 257, which the existing `TestRunEngineLockDetectionTests` calls directly), new `private const int MaxTestOutputChars = 2000`.
- Call sites: `TestCaseResult` is constructed in exactly one place, `TestRunEngine.cs:482` (inside `ParseTrx`); other mentions are type references (`TestRunEngine.cs:33, 182, 457, 462` and the lambda `RoslynSentinel.Tests.Tools.Basic/RunTestTests.cs:236`), none positional. Adding a trailing optional parameter breaks none. `ParseTrx` has one caller (`TestRunEngine.cs:~395`, `var allResults = ParseTrx(trxPath);`).
- Change:
  1. `TestCaseResult(..., string? ErrorStackTrace, string? Output = null)`.
  2. In `ParseTrx`, next to the `ErrorInfo` read: `var stdOut = unitTestResult.Element(ns + "Output")?.Element(ns + "StdOut")?.Value;` then `string? output = outcome == TestOutcome.Failed && !string.IsNullOrWhiteSpace(stdOut) ? CapOutput(stdOut) : null;` and pass `Output: output`.
  3. New `private static string CapOutput(string text)`: trim trailing whitespace; if length <= 2000 return it; otherwise return `"[truncated] " + text[^2000..]`.
  4. Change `private static List<TestCaseResult> ParseTrx` to `public static`.
- Change (tests, new file, class `TestRunEngineTrxOutputTests`, mirrors `TestRunEngineLockDetectionTests` style): write a TRX literal to `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".trx")`, call `TestRunEngine.ParseTrx(path)`, delete the file in `finally`. Literal shape (namespace `http://microsoft.com/schemas/VisualStudio/TeamTest/2010`, `TestRun/Results/UnitTestResult` with `testName`, `outcome`, `duration`; child `Output` holding `StdOut` then `ErrorInfo/Message` and `StackTrace`). Cases: failed test with `StdOut` -> `Output` equals the text; passed test with `StdOut` -> `Output` is null; failed test without `StdOut` -> null; failed test with whitespace-only `StdOut` -> null; failed test with 5000-char `StdOut` -> `Output` starts with `[truncated] `, ends with the last characters of the input, and `Output.Length <= 2000 + "[truncated] ".Length`; `ErrorMessage`/`ErrorStackTrace` still populated alongside `Output`.
- Done when: `RunTest(scope: project, scopeName: "RoslynSentinel.Tests.Basic", filter: "FullyQualifiedName~TestRunEngineTrxOutputTests|FullyQualifiedName~TestRunEngineLockDetectionTests")` passes.

### Step 3 - say it in both `RunTest` descriptions

- Files:
  - `RoslynSentinel.Tools.Basic/WorkspaceBuildTestTools.cs` (`RunTest`, `[Description]` at line ~50)
  - `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` (`RunTest`, `[Description]` at line ~500)
- Symbols: the method-level `[Description("Runs `dotnet test` ...")]` string on both `RunTest` methods (two copies of the same text; both are registered under `Name = "RunTest"`, so both must change).
- Call sites: none (attribute text only).
- Change: in both strings, directly after the sentence ending `...then capped by maxDetails).`, insert: `Each failed test entry also carries Output: the test's captured output (for example NUnit TestContext.Out / Console.Out or xUnit ITestOutputHelper), the last 2000 characters, present only for failed tests.`
- Done when: `Build` 0 errors, then `RunTest(scope: project, scopeName: "RoslynSentinel.Tests.Server", filter: "FullyQualifiedName~ArchitectureDocFreshnessTests")` passes (generated architecture docs must not go stale; if it fails, regenerate with `scripts/Generate-ArchitectureMap.ps1` and report it, do not hand-edit `docs/generated`).

### Step 4 - end-to-end RunTest test for `Output`

- Files: `RoslynSentinel.Tests.Tools.Basic/RunTestTests.cs`
- Symbols: new test `RunTest_FailedTestWithCapturedOutput_SurfacesOutputAndNothingForPassedTestsAsync`, plus a new const `FailingTestWithOutputSource` beside `FailingTestSource` (line 40).
- Call sites: none (additive). Model it on `RunTest_MixedPassAndFail_ReportsCountsAndFailureMessageAsync` (line 57): `TestSolutionFixture`, `AddFileToSolution(workspaceManager, Path.Combine("ContosoOrders.Tests", "FailingOutputTests.cs"), source)`, `BuildTools`, `RunTest(reason: "test message", ToolScope.solution, timeoutSeconds: 120)`.
- Change: the fixture project is xUnit, so the source is a class with a constructor taking `ITestOutputHelper`, one `[Fact]` that calls `_output.WriteLine("marker-4f2a")` then `Assert.Fail("boom")`. Assert: the single failed entry in `data.Results` has `Output` containing `marker-4f2a`; with `resultsType: TestResultsFilter.all` every entry whose `Outcome != Failed` has `Output == null`.
- Done when: `RunTest(scope: project, scopeName: "RoslynSentinel.Tests.Tools.Basic", filter: "FullyQualifiedName~RunTest_FailedTestWithCapturedOutput")` passes. If the xUnit adapter does not emit `<StdOut>` for this case, reply `RESCOPE:` with the TRX text you observed (write it to the scratchpad); do not weaken the assertion. That would mean the TRX logger omits adapter output and the design needs a different source (Risks 6).

### Step 5 - comment at the RenameSymbol same-signature refusal

- Files: `RoslynSentinel.Tools.Basic/RefactoringSignatureImpl.cs`
- Symbols: `RefactoringSignatureImpl.RenameSymbol` (line 61), the statement `var apply = await ValidateAndApplyAsync(result.PendingChanges, $"Rename '{result.OldName}' to '{result.NewName}'.", "RenameSymbol", ...)` (line ~107).
- Call sites: none (comment only). The single definition is `RefactoringSignatureImpl.cs:61`; its wrapper is `RefactoringSignatureTools.cs:27`.
- Change: one `ReplaceSnippet` inserting these ASCII comment lines immediately above `var apply = await ValidateAndApplyAsync(` inside `RenameSymbol`, anchored on that statement's first two lines copied from `ReadFile`:
  ```
  // KNOWN, INTENTIONALLY CORRECT REFUSAL: renaming a member to a name the type already has with the same signature
  // makes the compile gate below reject the change with CS0111 (duplicate member) or CS0121 (ambiguous call). There
  // is no explicit collision check here or in BasicRefactoringEngine.RenameSymbolAsync; the gate produces the
  // refusal and CompilerErrorLookupHelper adds the CS0101/CS0111 hint. RenameSymbol is a rename, not a merge, so no
  // body-reconciliation policy exists to apply. Decision 13 in
  // docs/current/proposals/proposal_journal_digest_followup_decisions.md: do not re-investigate or "fix" this.
  ```
- Done when: `Build` 0 errors.

### Step 6 - verification (orchestrator, not an implementer)

- `Build` (`level: fullBuild`): 0 errors.
- `RunTest` at solution scope; compare against the known-failure baseline (memory `reference_known_failing_tests`); only NEW failures matter. Specifically confirm `BatteryTwentyFourTests` `MethodSignature_Remove_*` (Tests.Tools.Advanced), `ParameterEditToolTests`, `RunTestTests`, `MakeStaticRewritesCallersTests` and `ArchitectureDocFreshnessTests` are green.
- Stop the server (`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`), reconnect, `LoadSolution`, then run one live `ParameterEdit(operation: method, action: remove)` on a method whose caller is in the same file and one live `RunTest` on a project with a deliberately failing test that writes output, to confirm the new binary behaves as the tests say.
- Move the two journal items (`47b2c93d:L39`, `47b2c93d:L31/L36`) to done; record (b) as already handled with the evidence above.

## Out of scope

- `BasicRefactoringEngine.ChangeSignatureAsync` and the ConstructorParameter remove path (same relocation pattern; separate slice, Risks 2).
- Supporting named arguments or non-last parameters in `remove`.
- Opening a scoped-operation ledger from any tool other than `MoveMember`.
- Any change to `MoveMember`, `StaticConversionEdits`, `ModifyModifier` (item b is already shipped).
- A new `includeOutput` parameter on `RunTest`; output for passed or skipped tests; `<StdErr>`; TRX `<TextMessages>`.
- The inaccurate wording "updating N call site(s)" in `RefactoringSignatureImpl.MethodSignature` (lines ~223-232): N is `changes.Count - 1`, i.e. files, not sites.
- Making `RenameSymbol` merge into an existing name (decision 13: not building).

## Risks and open decisions

1. **Needs design (b): stateful members into a static class.** Today they are refused with a message that names each use of instance state and says "pass the state in as parameters". Offering a `this`-as-parameter conversion (add a leading parameter of the source type, rewrite `this.X`/implicit-this uses to it, rewrite callers to pass the receiver) is feature-sized and changes `MoveMember`'s public shape. Human decision: is the refusal good enough, or is the conversion wanted? No `makeStatic` flag is needed (conversion is automatic when it is safe).
2. **Needs design / human decision: ledger coverage.** Recommended: no ledger for `MethodSignature`/`ParameterEdit` remove (see Decision 2). The real candidate is `ChangeSignatureAsync`: it applies a partial result and reports `skippedCallSites` (`BasicRefactoringEngine.cs:365-376`), so it can leave a tree that does not compile. Options: (i) reuse Step 1's single-pass helper there first, which removes most "re-locate" skips and leaves only genuine ambiguity (named-argument/params gaps), then (ii) decide whether the remaining skips refuse (as `remove` does) or open a ledger entry (as `MoveMember` does). Tradeoff: refusing is simpler and safe but blocks the whole change on one odd call site; the ledger lets the change land but adds a resolve-by-fixup protocol and a second tool that blocks unrelated writes. This also feeds the open TODO "Review and extend the scoped operation ledger" in `docs/current/TODO.md`.
3. **Hypothesis, untraced:** that two call sites in one OTHER file already fail today. Step 1 case 3 settles it (red-before/green-after is the proof; if it is already green, the code path I read must differ in ordering, and the test stays as a guard).
4. **Hypothesis, untraced:** that `Formatter.FormatAsync` on the whole call-site document is what flips LF to platform newlines (comment at `BatteryTwentyFourTests.cs:620`). Step 1 case 6 tests the fix; the cause was not isolated.
5. **Hypothesis:** `FilePathWrapper` equality between `filePath` (as passed) and `document.FilePath` (absolute) is what the old code relied on for `pendingChanges[filePath]`; Step 1 keys the declaration file by `filePath` and call-site files by `refDoc.FilePath` conversion, exactly as the old code did. If equality is not normalised, same-file call sites would land in a second entry; case 1's `Changes.Count == 1` assertion detects this.
6. **Hypothesis, untraced:** that the NUnit adapter writes `TestContext.Out` into TRX `<StdOut>` (the journal complaint was NUnit). Step 2 tests an NUnit-shaped TRX literal; Step 4 proves the real path only for xUnit (the fixture project). A live NUnit check happens in Step 6's manual `RunTest`. If NUnit does not populate `<StdOut>`, a different capture (for example the `dotnet test --logger "console;verbosity=detailed"` stream) would be needed, which is a design change.
7. **Human decision: output cap.** 2000 characters per failed test, tail kept. With `maxDetails` 50 that is up to about 100 KB, which crosses `LargeResultHelper.OffloadThresholdBytes` and would offload the payload (the generic filter keeps `StatusMessage`, so totals stay inline). Smaller cap (1000) keeps more runs inline but cuts long output; larger cap helps diagnosis but offloads sooner. Also: head versus tail (tail chosen because failure context is last).
8. **Human decision: no `includeOutput` parameter.** Always-on `Output` for failed tests only costs nothing on clean runs, but makes noisy failing suites larger. A parameter would be an explicit public schema change (schema-diet policy); default-on without a switch is the plan's choice.
9. The journal entries behind (a) and (c) came from sessions on possibly older binaries; both were re-traced to current source above. Entry (b) was exactly that case (stale; fixed the same night).
