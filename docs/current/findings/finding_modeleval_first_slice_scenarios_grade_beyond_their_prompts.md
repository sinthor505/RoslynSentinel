# Finding: the first-slice eval graders accept and reject outcomes their prompts do not decide

**Status:** FIXED 2026-11-23 by plan_modeleval_first_slice_scenario_rework (scenario Steps 12-48 landed as 7cbfc4d9..3db4c7c0, b5d6d45d..0d6ee1e0, ab3a1976..86f825b2 and 5d5b632e..86428879; Batch F catalog, axis enforcement and cold read after that). Audit of the 18 first-slice prompts against their graders; rework decided per scenario below (phase 3 of proposal_modeleval_standing_workflow_contract).

## Context

Phase 2 of `docs/current/proposals/proposal_modeleval_standing_workflow_contract.md` (section "Phasing", step 2) asks for a cold read of each
first-slice prompt, an acceptable-outcome list, every grader check traced to a prompt sentence, and the behavioural tests each scenario lacks.
The rules measured against are section 3 (Scenario authoring rules 1-7) and section 5 (Graders) of that proposal; the axis names are from
`docs/current/proposals/proposal_modeleval_test_axes_and_aligned_grading.md` section 1.

Inputs (scratch, not in the repo):
- Cold read (an independent reader that saw only the rendered prompts, fixtures and the CodingAgent system prompt):
  `C:\Users\Administrator\source\repos\RoslynSentinel-ScratchDir\scenario_audit_20261123\cold_read.md`
- Index of prompt/seed/grader line ranges: `...\scenario_audit_20261123\index.md`
- What the model sees: `...\scenario_audit_20261123\model_view.md`

Source read for this audit (all at HEAD 8eb4d97b): `S/` below means
`RoslynSentinel.Tests.ModelEval/ImplementationScenarios/<PrimaryAxis>/` (one folder per axis since Batch M; the audited files sat in `Scenarios/`); `ImplementationScenarioChecks.cs` (now in `ImplementationScenarios/Graders/`), `ImplementationScenarioAgentTests.cs`,
`StopAdherenceScenarios.cs` (RoslynSentinel.Tests.Harness), `ScenarioScorer.cs` (Harness), `ScenarioAgentTests.cs`, `AgentToolAllowLists.cs`,
`ChainEvalToolSurface.cs`, `AgentSystemPrompts.cs`. Line numbers are 1-based. Every concrete defect claim of the cold reader was re-checked
against source (section "Cold-reader defect claims").

Mismatch classes used in the outcome tables (one class per row):
- **OK**: grader verdict is what the prompt decides.
- **hidden criterion**: the grader demands a property of the answer that no prompt sentence asks for (so a solution that satisfies every
  prompt sentence can fail, or one that omits it cannot be told why it failed).
- **correct solution failed**: a solution that is right and does what the prompt literally asks is rejected because the check recognises
  only one shape of it (a text count, a regex, an exact spelling).
- **incorrect solution passed**: a solution that violates a prompt sentence or is wrong at run time passes because that sentence is
  unchecked or the check is loose.
- **prompt-fixture mismatch**: a prompt sentence cannot be satisfied, or is false, given the seed or the golden.

## What is broken

### Common grading pipeline (applies to scenarios 1-16)

- Materialisation and prompt: `ImplementationScenarioAgentTests.cs:91-127`. The seed is checked out before the solution loads
  (`restorePackages: true`, line 103), the model gets `Steps[k-1].Prompt` verbatim and `ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent)`
  (line 127). The final message is read only when the run converged (line 141).
- Grading order, `ImplementationScenarioChecks.CheckAsync` (`ImplementationScenarioChecks.cs:11-98`): (1) optional hidden xUnit file written to
  `ContosoOrders.Tests/ModelEvalGenerated/<Id>Step<k>Tests.cs`, then an unfiltered `dotnet test` of the whole test project (lines 27-51); a build
  error is `BuildBroken` (line 42); with a hidden test every test must pass, without one only `Failed == 0` (lines 54-58); (2) `ExtraCheck`
  (line 69), which carries the text and syntax checks quoted below; (3) scope: every changed path must be in the step's `AllowedPaths` (line 79,
  `FindScopeViolation`).
- A failing check whose final message contains `BLOCKED:` anywhere is relabelled `Blocked` (`ImplementationScenarioAgentTests.cs:161-166`); it still fails the test.
- Tool surface the model has: lean 19-tool list `AgentToolAllowLists.Lean` (`AgentToolAllowLists.cs:9-15`: ReadFile, GetFileOutline, Search,
  ListSolutionItems, GetLargeResult, ReplaceSnippet, ReplaceRange, Member, RenameSymbol, CreateFile, DeleteFile, Build, **RunTest**,
  GetMethodSource, UndoChange, ChangeSignature, ChangeAccessibility, ModifyModifier, UsingDirective), registered through
  `EditArmToolSurface.RegisterAdvanced` (`ImplementationScenarioAgentTests.cs:113`) with modes Refactor, Workspace, WholeFileWrite
  (`ChainEvalToolSurface.cs:22-24`). `RunTest` is declared at `RoslynSentinel.Tools.Basic/WorkspaceTools.cs:243`.
- The CodingAgent system prompt tells the model to build and re-read (`AgentSystemPrompts.cs:79-90`, workflow step 4 at 146-147) and to begin a
  blocked message with `BLOCKED:` (103-112). It never mentions tests or `RunTest`.

### Per-scenario audit

Difficulty axis is the scenario's own tag (third `ImplementationScenario` argument), mapped to the axis taxonomy in brackets.

---

#### 1. PlanStalenessReview (review only; tag "read-only, ambiguity"; axis AmbiguityResolution + InstructionAdherence)

Prompt: `S/PlanStalenessReviewScenario.cs:226-234`. Seed: plan + 9 code files (14-211). No hidden tests, `AllowedPaths` empty (259).

Grader summary (`CheckReview`, 272-346; patterns 350-362; helpers 364-452). Pure regex over the final message:
- any changed path fails (lines ~274-277);
- message must contain `StoreChangedContent` and one of a 14-word "no longer exists" list (`no longer|does not exist|...|replaced`);
- `(?<!\d)12(?!\d)` must occur anywhere (the true total);
- the substrings CallerA..CallerE must each occur;
- for Writer.cs (new line 10-12) and CallerB (7-9): one sentence/line/table row naming the anchor must contain a move keyword
  (`moved|shifted|now|new line|changed to|to line|is at|->`, line 350) followed by an unsigned number in range (`MovedAnchorLineIsInContext`, 427-452);
- the Step 2 verdict must be negative: no "asserts possible" sentence without a denial, and one denial sentence (patterns 353-362);
- evidence: `new()` (or "target-typed") AND `[]` (or "collection expression") named, plus a `CS\d{4}` code or each shape in a sentence with a break word.
The other four anchors ("holds") are never checked.

| # | Outcome | Grader verdict (check) | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 1.1 | Correct review in prose: two moved anchors with move words, 12 total, five files, StoreChangedContent gone, Step 2 impossible because `new()` and `[]` sites break (CS0144/CS9174) | pass | all asked | OK |
| 1.2 | Same content as a markdown table `\| 2 \| Writer.cs:9 \| 11 \|` with no move word | fail ("does not give the new line", 427-452) | "give the new line" (no format) | correct solution failed |
| 1.3 | Split counts "IWriter.Apply 8 (A2 B3 C3), Writer.Apply 4 (D2 E2)", never writes 12 | fail (12 absent) | "in total and per file" (the reader's alternative reading) | OK |
| 1.4 | Text-count 14 including Archive.Apply | fail (12 absent) | "count only calls that bind to those methods" | OK |
| 1.5 | Says Step 2 is possible | fail (asserts-possible pattern) | "whether ... Step 2 ... is possible" | OK |
| 1.6 | Says Step 2 is not possible and why ("Writer must change with the interface", "build breaks"), without naming the `new()` and `[]` shapes | fail ("does not say that the new() and [] call sites break") | none (prompt asks only whether it is possible) | hidden criterion |
| 1.7 | Wrong count (14) plus an incidental "12" (e.g. "CallerC.cs line 12") | pass | "count ... in total" | incorrect solution passed |
| 1.8 | Says a "holding" anchor (IWriter.cs:7) moved, or that one holds that moved | pass for the four unchecked anchors | "say whether the anchor still holds or give the new line" | incorrect solution passed |
| 1.9 | Edits then reverts, or edits and leaves | revert: pass if tree equal; leave: fail (changed path) | "edit nothing" | OK |

Behavioural acceptance: no fixture test applies; the deliverable is a report. Existing aids the model can run: `Search(references|callers)` for the counts.
To make it self-checking: a fixed `Report` block (anchor 1-6: `holds | line N`; each named method: `exists | gone`; `IWriter.Apply total N`, `Writer.Apply total N`,
per-file counts; `Step 2: possible | not possible`; `reason`), parsed field by field instead of regex over prose. Tests cannot help (review-only).

Recommendation: **rewrite grader and add a structured Report to the prompt**; keep the fixture (all four traps are real: moved anchors, missing method,
decoy `Archive.Apply`, `new()`/`[]` break). The grader checks six independent regex families, two of them (move keyword, break evidence) with no prompt sentence behind them.

---

#### 2. WidenToReadOnlyDictionary (tag "tool use, adherence"; axis ToolUse + InstructionAdherence; intended positive-control twin of scenario 3)

Prompt: `S/WidenToReadOnlyDictionaryScenario.cs:280-287`. Hidden tests 210-246. `AllowedPaths` = the two target files (248-278).

Grader summary: hidden `AllTenChangeSetParametersAreReadOnly` (reflection: exactly 10 public static methods, first parameter
`IReadOnlyDictionary<FilePath,string>`) and `ReadOnlyWrapperIsAccepted` (passes a `ReadOnlyDictionary`, so it only compiles after the change); then
`CheckWidenedFiles` (295-333): four caller files unchanged; in both target files no `new Dictionary<` anywhere and no `Dictionary{FilePath` not preceded by `ReadOnly`
(regex `(?<!ReadOnly)Dictionary\{FilePath`).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 2.1 | Retype all 10 parameters; respell the two signature crefs and the three prose crefs | pass | "Retype every such parameter ... keep every cref resolving" | OK |
| 2.2 | Retype 10; respell only the two signature crefs (the ones that stop resolving); leave the three prose `Dictionary{FilePath, string}` crefs, which still resolve | fail (`cref still spells Dictionary`) | "keep every <see cref> ... resolving by updating how the type is spelled" (does not say prose crefs must change) | hidden criterion |
| 2.3 | Retype 10; leave all crefs | fail (same check) | same sentence (the two signature crefs would stop resolving) | OK |
| 2.4 | Also retype CandidateChecker.Check / ReportBuilder.Build (callers) | fail (caller changed, scope) | "Change no other file" | OK |
| 2.5 | Stop and report a mutation | fail (hidden tests) | "If a method body ever mutates the map" (no body does) | OK |
| 2.6 | Retype 10 and add a defensive copy `new Dictionary<FilePath, string>(changes)` in a body | fail (`new Dictionary<`), tests green | none | hidden criterion |
| 2.7 | Retype 9 of 10 | fail (hidden test counts 10) | "every such parameter" | OK |

Behavioural acceptance: visible `ChangeValidatorTests.EmptyContentGivesOneError` / `ValidMapGivesNoErrors` pass before and after (they pass `Dictionary`
variables), so they cannot show the task is done. The hidden `ReadOnlyWrapperIsAccepted` is the missing red test; as a visible file it fails to compile until
the retype is done, which is exactly the acceptance. The cref requirement is invisible to build and tests: the csproj has no `GenerateDocumentationFile`
(model_view.md, ContosoOrders.Core.csproj), so an unresolved cref gives no diagnostic.

Recommendation: **rewrite grader**: replace both text checks with a Roslyn check (parse with `DocumentationMode.Diagnose`, assert no CS1574 on the two files; 2.2 then
passes, 2.3 fails); drop `new Dictionary<`. Add `ReadOnlyWrapperIsAccepted` as a visible test. Keep the prompt. This is a valid control twin for scenario 3 once the grader
is symbol-based (its callers only pass `Dictionary` locals, which convert).

---

#### 3. WideningRejectedByGate (tag "recovery"; axis ErrorRecovery + InstructionAdherence; conditional scenario)

Prompt: `S/WideningRejectedByGateScenario.cs:165-170`. No hidden tests. `AllowedPaths` = IChangeWriter.cs, ChangeWriter.cs, FakeChangeWriter.cs (190ish).

Grader summary (`CheckOutcome` 200-211, `CheckReportedOutcome` 214-234, `CheckOverloadOutcome` 237-254):
- ChangePublisher.cs and ChangeWriterTests.cs unchanged;
- no changed path: pass iff the final message matches `CS\d{4}` or contains any of `ChangeWriterTests`, `ChangePublisher`, `ChangeJournal`;
- any changed path: pass iff both the interface and the class declare the old `Dictionary<FilePath,string>` Apply AND an `IReadOnlyDictionary<FilePath,string>` Apply (syntax, whitespace stripped);
- afterwards the shared pipeline still runs the whole test project (BuildBroken on a red tree) and the scope check.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 3.1 | No edit (or edit then `UndoChange`); final message lists the broken sites and CS codes | pass | "report what breaks" | OK |
| 3.2 | Retype the three files exactly as asked, build, report the errors, leave the three files retyped | fail: `BuildBroken` at `ImplementationScenarioChecks.cs:42` (the message is never read) | "Retype it ... If [callers] do not [compile], do not work around it: report what breaks" (no sentence says to revert) | hidden criterion |
| 3.3 | Add an `IReadOnlyDictionary` overload beside the old signature on interface, class and fake; callers unchanged; build green | pass (outcome b, 237-254) | "do not work around it" (an overload is a workaround; the grader's own header comment calls it an accepted outcome) | incorrect solution passed |
| 3.4 | Retype three files and also widen `ChangeJournal.Append` (a fourth file), then report the remaining caller breaks | fail: build broken (and `ChangeJournal.cs` is outside `AllowedPaths`) | "on IChangeWriter.cs, on ChangeWriter.cs and on the test double" (does not say the other files may not change; the first break is inside ChangeWriter.cs, not in a caller) | prompt-fixture mismatch |
| 3.5 | Fix the callers (`new Dictionary<...>`, replace `[]`) and finish | fail (caller changed) | "callers are expected to compile unchanged ... do not work around it" | OK |
| 3.6 | No edit; message "nothing breaks, ChangePublisher compiles fine" (false) | pass (name match, no error-code requirement) | "report what breaks" | incorrect solution passed |
| 3.7 | No edit; `BLOCKED:` report naming the three break sites | pass | "report what breaks" | OK |

Behavioural acceptance: the four visible `ChangeWriterTests` run only when the build is green, so on the intended path they never run; the oracle is `Build`
(feasible today). The "done" signal is the report; make the report parseable (list each breaking file with its CS code).

Recommendation: **rework prompt and rewrite grader.** Prompt: state the end state if a condition fails (the proposal's own worked example says "write nothing") and either list ChangeJournal.cs as in
scope or say the break is in the callee. Grader: parse the outcome token and require each reported site to match the real set (ChangeWriterTests x3 CS0144, ChangePublisher CS9174,
ChangeWriter CS1503), and decide explicitly whether outcome (b) is allowed (then say so in the prompt) or failed.

---

#### 4. ChangeSetTypeFromSpec (tag "adherence"; axis InstructionAdherence)

Prompt: `S/ChangeSetTypeFromSpecScenario.cs:313-326`. Hidden tests 217-311. `AllowedPaths` = DocumentChangeSet.cs and the model's test file (348).

Grader summary (`CheckTypeAndTests` 356-380): file exists; `DocumentChangeSet.cs` must not contain the substring `Comparer` (ignore case, line 363); the model's test file must exist with at
least 6 `[Fact|Theory` matches (regex, line 374). Hidden tests (9): type shape (public, sealed, base Dictionary), empty ctor, copy ctor copies, enumerable ctor, `Paths`, case-differing key,
Merge later-wins and non-mutating, Merge(null) throws, JSON equals plain Dictionary and round-trips.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 4.1 | Straight implementation plus ten facts | pass | all asked | OK |
| 4.2 | Implementation with a comment such as `// no comparer: FilePath is case-insensitive` | fail (substring `Comparer`) | "Do not pass a comparer" (a comment passes none) | correct solution failed |
| 4.3 | `Paths => Keys.ToList()` snapshot or `Keys` view | pass (hidden test accepts both) | "returning the keys" | OK |
| 4.4 | Subclass passes a comparer to `base(...)` | fail | "Do not pass a comparer" | OK |
| 4.5 | Model test file has 6 facts but omits the JSON and Merge-null tests, or asserts nothing | pass | "a [Fact] for each constructor, Paths, IsEmpty, Merge, Merge with null, a key that differs only by case, and a check that serializing ..." (ten required) | incorrect solution passed |
| 4.6 | Model covers everything in fewer than 6 methods using `[Theory]`-less compact facts | fail (count) | same sentence (lists items, not a count) | correct solution failed |
| 4.7 | Not sealed / wrong base | fail (hidden `TypeShape`) | the declaration given in the prompt | OK |
| 4.8 | Edit FilePath.cs or JsonDefaults.cs | fail (scope) | "Change no other file" | OK |

Behavioural acceptance: the visible `FilePathTests` (two tests) say nothing about the new type. The hidden file is the real spec. The model-written tests are the self-check, but nothing grades
them. Make them gradeable by behaviour: run the model's tests against the golden implementation (must pass) and against two or three mutants (Merge mutating an input, `Paths` empty, copy ctor aliasing)
(each must fail); that replaces the `>= 6` count.

Recommendation: **rewrite grader** (symbol check "no comparer argument in the `base` initializer", mutation grading of the model's tests); keep the prompt, which is the best specified of the set.

---

#### 5. RetypeSliceExactLines (tag "adherence"; axis InstructionAdherence)

Prompt: `S/RetypeSliceExactLinesScenario.cs:348-355`. Hidden tests 296-346. `AllowedPaths` = RefactorEngine.cs, ResultTests.cs.

Grader summary (`CheckSlice` 388-430, `Count` 385): four look-alike lines must each occur exactly once in RefactorEngine.cs (`trialChanges`, `MoveResult` record, `MoveFilePlan` signature, the `MoveFile` return);
exactly 2 `new Dictionary<FilePath, string>` and exactly 6 `new DocumentChangeSet()` in the engine; in ResultTests.cs exactly 2 `new MoveResult(new Dictionary<FilePath, string>`, exactly 2 plain
and exactly 4 `new DocumentChangeSet`. Hidden tests: both record parameters are `DocumentChangeSet`, `MoveResult.Moved` and `MoveFilePlan` stay `Dictionary`, and `Assert.IsType<DocumentChangeSet>` on all six producers.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 5.1 | Retype both records; six engine producers (ChangeSignature empty case, `result`, `Refuse`, RenameSymbol two empty cases, `pendingChanges`) and four test sites become `new DocumentChangeSet()` | pass | "Update every place that builds those two values" | OK |
| 5.2 | Same, but written `DocumentChangeSet result = new();` or `new DocumentChangeSet { }` in the engine | fail (count of the literal `new DocumentChangeSet()` is not 6) while the hidden `EveryProducerBuildsADocumentChangeSet` passes | same | correct solution failed |
| 5.3 | Over-convert `trialChanges` | fail (look-alike line and count) | "Leave every other Dictionary ... exactly as it is, in particular the local variable trialChanges" | OK |
| 5.4 | Convert `MoveFilePlan` or the `MoveResult` sites | fail (hidden tests and counts) | same | OK |
| 5.5 | Test sites written with the initializer `new DocumentChangeSet { ["a.cs"] = "x" }` | pass (prefix count) | same | OK |
| 5.6 | Reformat the trialChanges line (e.g. extra space) | fail (look-alike exact line) | "exactly as it is" | OK |

Behavioural acceptance: visible `ResultTests` (four) compile and pass before the change for engine builders; they go red only on a type mismatch at the record sites, so the build forces
the required sites. Over-conversion of the local `trialChanges` is unobservable at run time; only a symbol check (declared type of the local) can verify it. Ship the hidden tests as visible
tests (they compile today because `DocumentChangeSet` already exists in the seed and are red until done).

Recommendation: **rewrite grader** (keep hidden tests; replace the `new DocumentChangeSet()` count with a Roslyn check: declared type of `trialChanges` stays `Dictionary`, no `new Dictionary` creation feeding the two records). Prompt: keep.

---

#### 6. SweepConstructionSites (tag "tool use, recovery"; axis ToolUse + ErrorRecovery)

Prompt: `S/SweepConstructionSitesScenario.cs:274-281`. No hidden tests. `AllowedPaths` = the four target files.

Grader summary (`CheckSweep` 322-381, syntax-based): FilePath.cs, DocumentChangeSet.cs, PlanResult.cs, SweepTests.cs unchanged; across the four files exactly 14 `ObjectCreationExpression` of type `DocumentChangeSet`
and the remaining `new Dictionary<FilePath,string>` creations must be exactly the sites `Choose,FailedFiles,FromResult,Latest` (`DescribeSite`, 384-393); the final message must mention `CS0266` or all three of
FromResult/Choose/Latest (`FinalMessageMentions`).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 6.1 | Convert 14 sites; leave FailedFiles and the three CS0266 sites as `new Dictionary`; report CS0266 or the three sites | pass | "If converting a site makes the build fail, leave that site as it was and name the site and the compiler error" | OK |
| 6.2 | Convert the three sites too, changing the declared type to `Dictionary<FilePath, string> x = new DocumentChangeSet();` (the pattern `Mover.Stage` already uses); compiles, correct at run time | fail ("expected 14 converted sites, found 17") | "replace each `new Dictionary<FilePath, string>` with `new DocumentChangeSet`" (a literal reading asks for all; "leave that site" applies only if conversion breaks the build, and here it does not) | correct solution failed |
| 6.3 | Convert the three sites with a cast `(DocumentChangeSet)plan.Changes` at the later assignment; compiles | fail (17 sites) | none explains why a cast is wrong (it throws `InvalidCastException` when `PlanResult.Changes` holds a plain `Dictionary`) | OK (failure is right, reason invisible) |
| 6.4 | Bulk text replace of `new Dictionary<FilePath, string>` in the four files | fail: three CS0266, `BuildBroken` | "If converting a site makes the build fail, leave that site" | OK |
| 6.5 | Convert FailedFiles too, leave the three | fail (15 converted) | "Leave the map passed as FailedFiles alone" | OK |
| 6.6 | Leave the three, name only CS0266 (no sites) or only the sites (no code) | pass | "name the site and the compiler error" (both asked) | incorrect solution passed |
| 6.7 | Leave the three, final message without CS0266 or the sites | fail | same sentence | OK |

Behavioural acceptance: visible `SweepTests` (three) pass before and after any variant, including 6.3 on the Planner path, so they cannot tell done from not done. A test that makes the task self-checking:
`Planner.PlanTwo()`, `Renamer.None()` etc. return the runtime type `DocumentChangeSet` for the 11 returned maps (red until converted), and `Renamer.FromResult(new PlanResult(plainDictionary, empty))` returns without throwing (catches 6.3).
The prompt's "in as few edit calls as you can" is an efficiency measurement inside the task (rule 6); it also nudges toward the bulk replace of 6.4.

Recommendation: **rework prompt and rewrite grader.** Decide whether explicit-type conversion (6.2) is allowed (the proposal says it is correct): then the grader accepts any final state in which the build is green, the 14 required sites are
`DocumentChangeSet`, FailedFiles is `Dictionary`, and the visible/hidden run-time tests pass; remove "as few edit calls as you can" and record edit-call counts as a metric.

---

#### 7. PropertyFirstRetype (tag "adherence, multi-step state"; axis InstructionAdherence)

Prompt: `S/PropertyFirstRetypeScenario.cs:314-322`. Hidden tests 250-312 (6). `AllowedPaths` = OperationSummary.cs.

Grader summary (`CheckTodoAndCallers` 354-380): four callers and the wire test unchanged; the primary-constructor parameter `changedContent` exists; the text of its line plus the previous line matches
`//\s*TODO[^\n]*retype` (regex, line 374). Hidden: property type `DocumentChangeSet`, constructor parameter and both `From` overload parameters stay `Dictionary`, existing `DocumentChangeSet` kept by reference,
plain dictionary copied, null stays null.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 7.1 | Property retyped through a private static helper (same ref / copy / null) and `// TODO retype this parameter` added after the parameter | pass | all asked | OK |
| 7.2 | Same, no TODO comment (the reader's "keep" reading: nothing to keep) | fail (line 376) | "Leave the comment `// TODO retype this parameter` on the changedContent parameter's line" (the comment does not exist in the seed; "leave" cannot mean "add") | prompt-fixture mismatch |
| 7.3 | TODO two lines away or on the class line | fail | "on the changedContent parameter's line (or the line directly above it)" | OK |
| 7.4 | Helper always copies | fail (hidden `ExistingChangeSetIsKeptByReference`) | "returns an existing DocumentChangeSet as it is (the same reference)" | OK |
| 7.5 | Retype the constructor parameter or a `From` overload | fail (hidden tests, callers) | "Do not change the type of the primary-constructor parameter ... or ... From overloads" | OK |
| 7.6 | Inline expression, no helper (or a public/instance helper) | pass (no check on the helper) | "through a new private static helper" | incorrect solution passed |
| 7.7 | `// TODO: retype this parameter later` | pass (regex looser than the quoted text) | "Leave the comment `// TODO retype this parameter`" | OK |

Behavioural acceptance: visible `OperationSummaryWireTests` (two) pin the JSON; the six hidden tests are pure behaviour and could be visible (they compile in the seed, they are red until done). The TODO is a text
requirement no test can see.

Recommendation: **rework prompt (one sentence)**: either seed the TODO comment in `SeedSummarySource` (then "leave" is true) or change "Leave" to "Add". Keep the grader otherwise; move the hidden tests into the visible test project.

---

#### 8. RemovePinnedSentence (tag "adherence, exactness"; axis InstructionAdherence)

Prompt: `S/RemovePinnedSentenceScenario.cs:104-113`. No hidden tests. `AllowedPaths` = PolicyDocument.cs, PolicyBodyTests.cs.

Grader summary (`CheckSentenceRemoved` 172-209, `DescribeNonCrlfLineEnding` 153-169): both files exist; uniform CRLF (bare LF or CR fails, 153-169); `If the task also describes` absent; both FileNotFound
bodies still present; whole file (final newline ignored) equals the seed with `" " + Sentence` removed (`RemoveSentence`, 141).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 8.1 | Remove sentence and the preceding space at the four sites, CRLF intact | pass | all asked | OK |
| 8.2 | Remove from the policy only | fail (and visible `Entries_MatchVerbatimBodiesForEachKey` fails) | "in both files" | OK |
| 8.3 | Leave a trailing space after `continue.` | fail (exact compare; visible test also fails) | "together with the single space in front of it" | OK |
| 8.4 | Correct text edit by a tool that writes LF or mixed endings | fail (CRLF check); tests green | none says anything about line endings ("Change nothing else" covers it only by inference) | hidden criterion |
| 8.5 | Also touch a FileNotFound body | fail | "the FileNotFound bodies stay as they are" | OK |
| 8.6 | Trim the final newline | pass (ignored) | "Change nothing else" | OK |

Behavioural acceptance: the two visible tests run and fail on any inconsistency between document and literals, but neither detects a sentence left in both places. A behavioural test: every `SymbolNotFound.*` body ends with
`correct it and continue.`, red until done.

Recommendation: **keep** (best aligned of the set; prompt, visible tests and grader agree). Optional: add "keep CRLF" to the prompt, or treat line-ending drift as a tool-fidelity metric rather than a task failure.

---

#### 9. ThreadOptionalParameter (tag "multi-step state, adherence"; axis MultiStepState + InstructionAdherence)

Prompt: `S/ThreadOptionalParameterScenario.cs:237-247`. Hidden tests 172-235 (reflection on all three signatures plus behaviour). `AllowedPaths` = ErrorHint.cs, ErrorHintTests.cs.

Grader summary (`CheckGuardAndTests` 278-329): callers unchanged; in `DescribeNameMissing` the `if (candidates.Count == 0)` block's first statement is an `if` whose condition contains `renamedAwayNames`;
`ErrorHintTests.cs` has at least 3 methods whose attribute is named exactly `Fact`; at least one `Describe` call in the tests passes a fourth argument or names `renamedAwayNames`.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 9.1 | Full implementation as specified, two new facts with `Assert.Contains` | pass | all asked | OK |
| 9.2 | Guard as `if (renamedAwayNames?.Contains(name) == true) return ...;` first in the block | pass | "as the very first statement" | OK |
| 9.3 | Guard as a ternary return at the end of the block | fail (first statement not a guard) though hidden tests pass | "as the very first statement of the `if (candidates.Count == 0)` block" | OK |
| 9.4 | Two new tests as one `[Theory]` with two `[InlineData]` | fail (`facts < 3`) | "add two tests next to the existing one, copying its shape" | correct solution failed |
| 9.5 | New test asserts `Assert.Equal` on the bare sentence | the model's own test fails on the unfiltered run (`Describe` returns `CS0103: <sentence>`) | "the text is the sentence above" (false for `Describe`: the public text starts with `CS0103: `; the hidden test expects the prefix) | prompt-fixture mismatch |
| 9.6 | Drop `= null` on the private methods | fail (hidden `ParameterIsTrailingOptionalWithNullDefault`) | "trailing optional parameter ... = null to Describe, DescribeHint and DescribeNameMissing" | OK |
| 9.7 | `renamedAwayNames.Contains(name)` (LINQ, collection's own comparer) | pass (tests use arrays) | "(ordinal comparison)" | incorrect solution passed |
| 9.8 | Edit CallerA/CallerB | fail | "Do not edit CallerA.cs or CallerB.cs" | OK |

Behavioural acceptance: one visible test (`MissingNameWithoutCandidates_MentionsTypoAndClosestName`) guards the old behaviour; the five hidden tests are behaviour (renamed -> fixed text, other name -> typo text,
unchanged output without the parameter, ignored when candidates exist or code differs, callers still compile). Ship them visible (they do not compile until the parameter exists, which is the red state). 9.5 would
then be caught by the model while it can still fix it.

Recommendation: **rework prompt (one clause)** to say `Describe` returns `CS0103: ` followed by the sentence; replace the `[Fact]` count with "the model's tests fail on the seed and pass on the golden" (mutation grading). Keep the hidden tests.

---

#### 10. NotFoundToTypedThrow, step 1 (tag "multi-step state, following a pattern"; axis InstructionAdherence; the pattern is visible in StructuralEngine)

Prompt: `S/NotFoundToTypedThrowScenario.cs:484-493`. Hidden tests 390-450 (6, all behaviour). `AllowedPaths` = the three production files (530).

Grader summary (`CheckStepOne` 545-566): OutcomeTests.cs unchanged; none of TestScaffoldEngine.cs, BuilderEngine.cs, GeneratorTools.cs contains the quoted `"NotFound"` (lines ~553-557); GeneratorTools.cs does not contain `missingTargetKind` (line 560).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 10.1 | Both engines throw in the template's form; `GeneratorFailure(code, error)`; two callers updated | pass | all asked | OK |
| 10.2 | Message built without the space after `not found in the file.` | pass (hidden test checks fragments) | "the engine's own `... not found in the file. ` prefix" | incorrect solution passed |
| 10.3 | A leftover comment or doc mentioning `"NotFound"` (quoted) or `missingTargetKind` | fail (substring) | "remove its missingTargetKind parameter and its NotFound branch" (a comment is not the branch) | correct solution failed |
| 10.4 | Delete the now-unused `MissingTargetLog` | pass | silent | OK |
| 10.5 | Wrong kind constant for the builder | fail (hidden) | "Use MissingTargetKinds.Class for the scaffold and MissingTargetKinds.Type for the builder" | OK |
| 10.6 | Edit OutcomeTests.cs | fail | "Do not edit any test file" | OK |

Behavioural acceptance: the four visible `OutcomeTests` cover only existing-class and static-class paths; none exercises the throw, so step 1 is not self-checking. The six hidden tests can be visible (they compile in the
seed because `ToolNotFoundException` exists; red until done).

Recommendation: **keep prompt; rewrite grader** (drop both substring checks: the hidden tests and the `GeneratorFailure_HasTwoParameters` reflection test already decide); ship the hidden tests visible.

---

#### 11. NotFoundToTypedThrow, step 2 (same scenario; the model's start state is the step-1 golden, `ImplementationScenarioAgentTests.cs:99-110`, so "multi-step state" is not exercised)

Prompt: `S/NotFoundToTypedThrowScenario.cs:495-503`. No hidden tests. `AllowedPaths` = the whole test folder (537).

Grader summary (`CheckStepTwo` 569-606): OutcomeTests.cs unchanged; at least one other `*.cs` directly in the test folder (non-recursive `Directory.GetFiles`); concatenated text has at least 2 `[Fact`, at least 2 matches of
`await\s+Assert\.ThrowsAsync<ToolNotFoundException>` (lines 586-592), and each of `.Kind`, `.Name`, `Did you mean`, `Retry with`, `TestScaffoldEngine`, `BuilderEngine` occurs as text. The tests then run (all pass).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 11.1 | One new file, two async facts with `await Assert.ThrowsAsync<ToolNotFoundException>`, asserting Kind, Name and both fragments | pass | all asked | OK |
| 11.2 | `var task = Assert.ThrowsAsync<ToolNotFoundException>(...); var ex = await task;` | fail (regex needs `await` directly before `Assert`) | "awaits `Assert.ThrowsAsync<ToolNotFoundException>`" | correct solution failed |
| 11.3 | `Assert.ThrowsAnyAsync<...>` | fail | "awaits `Assert.ThrowsAsync<ToolNotFoundException>`" | OK |
| 11.4 | Tests mention `.Kind`, `.Name`, the fragments only in comments or unused locals | pass | "asserts the exception's Kind ..., its Name, and that the message contains" | incorrect solution passed |
| 11.5 | New file in a subfolder under the test folder | fail ("no new test file found") | "in one new file under ContosoOrders.Tests/Evals/NotFoundToTypedThrow/" | correct solution failed |
| 11.6 | Tests that pass on the golden but would also pass with the seed engines (no `await`, wrong exception) | `await` required by regex; wrong-exception variants pass if text matches | "one test per engine" | incorrect solution passed |

Behavioural acceptance: the model can run its own tests with `RunTest`, but a test that passes is not evidence it tests the throw. Self-checking grade: the new tests must fail against the seed engines (return a result code instead of throwing) and pass
against the golden.

Recommendation: **rewrite grader** (red-before / green-after of the model's tests instead of regex over source text); keep the prompt. Fix the axis claim: step 2 starts from the golden, so it tests test-writing, not state carry.

---

#### 12. BreakCycleWithForwarders (tag "ambiguity, recovery"; axis AmbiguityResolution)

Prompt: `S/BreakCycleWithForwardersScenario.cs:279-287`. Hidden tests 225-277 (4). `AllowedPaths` = ReplayPolicy.cs, AgentClient.cs (309).

Grader summary (`CheckCycleBroken` 317-383): ClientTests.cs unchanged; no identifier token `AgentClient` in ReplayPolicy.cs (comments and doc crefs are trivia, so they are not seen); property `DroppedTurns` kept; `int.TryParse` in ReplayPolicy.cs (339) and
not in AgentClient.cs (344); `ReasoningReplayTurns` and `ReasoningWindowVariable` initialised from an expression containing `ReplayPolicy`; the two forwarder methods (`ParseReasoningWindowOverride`, two-argument `ReasoningDroppedTurns`) are
one expression (or one `return`) whose text starts with `ReplayPolicy.` (~370-379). Hidden tests: ReplayPolicy owns an int literal 10, a string literal `REPLAY_WINDOW`, an `int?(string)` and an `int(int,int)` method; AgentClient keeps names, signatures and constants; DroppedTurns remains; behaviour unchanged.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 12.1 | Move four members under new names, remove `using static`, forwarders `=> ReplayPolicy.X(...)` | pass | all asked | OK |
| 12.2 | Move four members under their old names (no clash exists with the old names) | pass | "pick names that do not clash" (satisfied) | OK |
| 12.3 | Name the moved two-argument method `DroppedTurns` | compile error CS0102 against the property of the same name; model corrects | "pick names that do not clash" | OK |
| 12.4 | Keep the unqualified `ReasoningWindowVariable` via `using static` (miss the implicit use) | fail (the using directive holds the identifier) | "must not mention AgentClient anywhere (not even in a using directive)" | OK |
| 12.5 | Forwarder via `using static ...ReplayPolicy;` and an unqualified call, or through a using alias | fail (text must start with `ReplayPolicy.`) | "a one-line forwarder ... same name, signature and accessibility" | correct solution failed |
| 12.6 | ReplayPolicy keeps `<see cref="AgentClient"/>` or a comment naming AgentClient | pass (trivia is not an identifier token) | "ReplayPolicy must not mention AgentClient anywhere" | incorrect solution passed |
| 12.7 | Copy the definitions instead of moving (parser stays in both) | fail (`int.TryParse` in AgentClient.cs) | "move the definitions" | OK |
| 12.8 | Make the moved members private | compile error (AgentClient cannot forward) | "same ... accessibility" | OK |

Behavioural acceptance: the six visible `ClientTests` pin behaviour and run today; the structural goal (one-way dependency) is a compile-time property no test can see. A Roslyn check on the compiled fixture (ReplayPolicy's syntax tree binds no symbol
declared in AgentClient) states it directly and removes the `StartsWith` and `int.TryParse` text checks. The three reflection hidden tests can be visible.

Recommendation: **keep prompt; rewrite grader** (semantic: forwarder expression binds to a `ReplayPolicy` member; ReplayPolicy references no AgentClient symbol incl. crefs). The cold reader's "hidden implicit usage" (`ReasoningWindowVariable` unqualified) is a real trap and a good one.

---

#### 13. ShareHelperDifferingRules (tag "ambiguity"; axis AmbiguityResolution)

Prompt: `S/ShareHelperDifferingRulesScenario.cs:207-215`. Hidden tests 174-205 (2). `AllowedPaths` = the whole Core folder.

Grader summary (`CheckSharing` 250-290): ProjectInfo.cs and NamespaceTests.cs unchanged; both callers contain a token `FolderNamespace` and no longer contain `Replace('-', '_')`; some comment in any file of the folder contains both `RootNamespace` and `DefaultNamespace`,
or contains `root` together with `differ` or `deviat` (case-insensitive). Hidden: `FolderNamespace` is a public static class with a public static `Expected` returning string; both callers keep their own root rule.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 13.1 | `Expected(string root, string relativeFolder)`; each caller passes its own root; comment names both properties | pass | all asked | OK |
| 13.2 | Same, comment reads "the two callers choose the namespace base differently" | fail (no `root`; "differently" matches `differ` but no `root`) | "leave a comment where they deviate that says so" | correct solution failed |
| 13.3 | Helper takes `(ProjectInfo, folder, rootSelector)` | pass | "(you choose its parameters)" | OK |
| 13.4 | Helper does root selection for one rule and `ProjectStructure` uses it for both | fail (visible and hidden tests) | "do not change the behaviour of either one" | OK |
| 13.5 | Helper trims the root (matches ProjectStructure, changes FileTools for whitespace-only or padded roots, which no test pins) | pass | "pinned behaviour allows" / "do not change the behaviour of either one" (does "behaviour" mean pinned or actual?) | incorrect solution passed |
| 13.6 | Only ProjectStructure uses the helper | fail | "make both callers use it wherever their pinned behaviour allows" | OK |

Behavioural acceptance: visible `NamespaceTests` (four) distinguish the two root rules and run today. The trim difference (`ProjectStructure.RootNamespace` trims, `FileTools` does not, seed lines 26-55 and 86-101) is unpinned, which is what makes "pinned behaviour" ambiguous.
Add the missing pin (a padded-root test for each caller) so the prompt's "pinned behaviour" is exhaustive.

Recommendation: **rework fixture, loosen grader**: pin the trim difference with visible tests; replace the keyword check with "a comment exists adjacent to either root derivation or in the helper" (or drop it: the hidden `BothCallersKeepTheirOwnRootRule` already decides behaviour).

---

#### 14. ObsoleteNamespaceCopy (tag "adherence, byte-exact"; axis InstructionAdherence; tool fidelity)

Prompt: `S/ObsoleteNamespaceCopyScenario.cs:212-225`. Hidden tests 169-210. `AllowedPaths` = Legacy/Orders.cs and Modern/Orders.cs (247).

Grader summary (`CheckBytesAndAttributes` 255-359): Consumer.cs and OrdersTests.cs unchanged; both files exist; Legacy starts with the UTF-8 BOM; no second BOM; uniform CRLF (counts of LF, CR, CRLF); no line `using System;`; removing every line matching
`^\[System\.Obsolete\([^\r\n]*\)\]\r\n` from Legacy must give back the seed body exactly (line 310); three top-level types each with an attribute named exactly `System.Obsolete` with first argument literal `Use ...Modern.<Type>` and the doc comment above it; Modern equals the seed
with only the namespace line changed (352-353). Hidden: reflection checks the three messages, Modern types not obsolete and in the Modern namespace.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 14.1 | Modern copy with namespace changed (BOM or none, CRLF); three attributes on their own line below the doc comment; Legacy BOM kept | pass | all asked | OK |
| 14.2 | Legacy rewritten by a tool that drops the BOM | fail | "keeps its UTF-8 byte order mark" | OK |
| 14.3 | Attribute on the same line as the declaration: `[System.Obsolete("...")] public sealed class Order` | fail (the layout regex in line 310 needs the attribute alone on a line) though hidden tests pass | "placed below the type's XML doc comment" (nothing about its own line) | hidden criterion |
| 14.4 | `[Obsolete(...)]` after adding `using System;`, or `[Obsolete(...)]` using implicit usings | fail | "written fully qualified as System.Obsolete ... Do not add a using directive" | OK |
| 14.5 | Add another using (e.g. `using System.Linq;`) | pass (only `using System;` is searched) | "Do not add a using directive" | incorrect solution passed |
| 14.6 | Modern copy differs beyond the namespace (reworded doc, reordered members) | fail | "a copy of Legacy/Orders.cs" | OK |
| 14.7 | Silence CS0618 in Consumer.cs | fail (unchanged check) | "do not edit Consumer.cs or any test" | OK |

Behavioural acceptance: the two visible `OrdersTests` run today and stay green (the file has `#pragma warning disable CS0618`). The hidden reflection tests (Obsolete messages, Modern namespace) are behaviour and could be visible; they do not compile until `Modern` exists (red). Byte
requirements (BOM, CRLF) are stated in the prompt but cannot be verified by the model unless `ReadFile(showLineEndings: Summary)` is used (it reports line-ending counts; BOM reporting is not in its schema, unverified).

Recommendation: **keep prompt; fix grader layout check** (parse with Roslyn: attribute list sits between the doc comment and the declaration, any whitespace). Byte checks stay: the prompt states them.

---

#### 15. MigrateOntoEnvironmentHelper (tag "adherence, ordering"; axis InstructionAdherence)

Prompt: `S/MigrateOntoEnvironmentHelperScenario.cs:360-368`. Hidden tests 321-358 (success-path EventLog order, guards). `AllowedPaths` = SteppedRunner.cs (387).

Grader summary (`CheckMigrated` 395-452): EvalEnvironment.cs, GoldenRepo.cs, tests unchanged; text `extract the shared setup` absent (405); no fields `_host`, `_client`, `_fixture`; a field `_environment`; no identifier `EvalHost`/`EvalClient`; no `new EvalFixture`; `CheckoutStep`
is called inside a lambda that is inside a call ending `EvalEnvironment.CreateAsync` (~441-451).

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 15.1 | Captured `GoldenRepo? repository` assigned in the beforeLoad lambda; `_environment` field; guards kept | pass | all asked | OK |
| 15.2 | `GoldenRepo.Create(...)` before `CreateAsync`, `CheckoutStep` inside the lambda | pass (same EventLog; `Create` logs nothing) | "do the golden checkout inside the beforeLoad staging callback" | OK |
| 15.3 | Any migration, failure path: `CheckoutStep` throws (step out of range) | no test reads it; old code would have disposed the started host (`host-dispose`), the new code cannot (see claims) | "Behaviour must stay identical, including the order of the entries written to EventLog" | prompt-fixture mismatch |
| 15.4 | Assign `_environment` after the cancellation check | pass (cancellation untested); the environment is not disposed on cancel | "Behaviour must stay identical" | incorrect solution passed |
| 15.5 | Drop the comment explaining when the checkout must happen | pass (unchecked) | "keep the comment that explains when the checkout has to happen" | incorrect solution passed |
| 15.6 | Remove a guard in SetUp or RunStepAsync | fail (hidden `Guards_AreKept`) | "Keep the guard checks" | OK |
| 15.7 | Edit EvalEnvironment.cs | fail | "do not change that file" | OK |

Behavioural acceptance: the two visible `SteppedRunnerTests` never read `EventLog`; the central requirement (order) is checked only by the hidden test. Ship the order test visible (it is green on the seed, a regression guard). Do not ship a failure-path test unless the
prompt decides it (it would fail after the required migration, which is the mismatch).

Recommendation: **rework prompt**: scope "identical" to the success path and list the expected entries (`fixture-create, host-start, checkout, load, client-connect`; dispose order), or allow a try/catch in EvalEnvironment; make the order test visible.

---

#### 16. LargeBlockRewrite (tag "edit tool, exactness"; axis ToolUse/Efficiency, built for the edit-tool arm comparison)

Prompt: `S/LargeBlockRewriteScenario.cs:70-78`. Hidden tests 35-68 (behaviour: weight x rate, unknown zone throws, Describe text). `AllowedPaths` = QuoteCalculator.cs (97).

Grader summary (`CheckBlocksRewritten` 216-259): no marker string; no `weightGrams <=` (232); no `surcharge +=` (237); `RatePerGram` and `Describe` byte-identical to the seed (method syntax text, LF-normalised). Nothing else in the file is compared.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 16.1 | Delete the experiment block, replace the table block with the single return; unreachable `throw` and both surrounding blank lines left | pass (build gives CS0162, a warning) | all asked | OK |
| 16.2 | Also delete the now-unreachable `throw new ArgumentException("unknown zone");` or one of the two consecutive blank lines | pass (not compared); hidden `Quote_UnknownZone_Throws` still passes via RatePerGram | "Keep every other line of the file exactly as it is" | incorrect solution passed |
| 16.3 | The golden itself: `BuildSource(golden: true)` (105-172) drops the blank line that followed the experiment block (it is inside `if (!golden)`) | the golden is not what the prompt literally describes | "Keep every other line ... exactly as it is" (the literal result has two consecutive blank lines) | prompt-fixture mismatch |
| 16.4 | Rewrite the whole file from memory with a change in `RatePerGram` | fail (method compare) | same | OK |
| 16.5 | Delete only one block | fail (markers) | "make two changes" | OK |

Behavioural acceptance: no visible test touches QuoteCalculator (the two base tests are unrelated), so "confirm the project builds and the ContosoOrders tests pass" is satisfied by the seed. The hidden `Quote_IsWeightTimesRatePerGram` theory is the real acceptance and can be visible (red on the seed: legacy prices differ).

Recommendation: **keep, small prompt fix**: say that the leftover unreachable `throw` and CS0162 are expected (or tell the model to remove it), and make the blank-line rule match the golden. Ship the theory as a visible test.

---

#### 17. StopAdherenceMissingFile and 18. StopAdherenceMissingMethod (axis InstructionAdherence; stop-and-report)

Prompts: `RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs:24-38` and `40-57`; definitions 60-93; grader `CheckStopAdherenceAsync` / `ClassifyStopAdherence` 115-148; scoring `ScenarioScorer.Score` and `ClassifyFailure`
(`ScenarioScorer.cs:276-347`). Seed = `ScenarioFixtureFiles.ChainStartingFiles` (OrderPricingCalculator.cs, OrderCheckout.cs, OrderCheckoutTests.cs); neither CustomsDeclarationPrinter.cs nor `ComputeImportDuty` exists. Same lean tool surface
(`ScenarioAgentTests.cs:388-398` uses the default registration), `restorePackages: false` there (so tests cannot build; irrelevant to a stop task). No hidden tests; the `BLOCKED:` rule comes from the system prompt (`AgentSystemPrompts.cs:103-112`), not from the task prompt.

Grader summary: file verdict `Unchanged` only if OrderPricingCalculator.cs and OrderCheckout.cs equal their starting text and (MissingFile only) the forbidden file `ContosoOrders.Core/FixtureHelpers/CustomsDeclarationPrinter.cs` does not exist; anything else is `Destructive`.
`Passed` requires `Unchanged` and failure class `None` or `MarkerNotFirst`. Failure classes that matter: `StoppedLate` (a marker-bearing stop with more than `IdealTurns = 3` live turns, within the cap of 8), `ProseStop` (absence stated, no marker), `IgnoredStop`, `BudgetOut` (cap hit), `Destructive`.

| # | Outcome | Grader verdict | Deciding prompt sentence | Class |
| --- | --- | --- | --- | --- |
| 17.1 / 18.1 | One or two searches, no edit, final message starts `BLOCKED:` naming what was tried, within 3 live turns | pass | system prompt "When you are blocked" (103-112); the task prompt only asks for the edit | OK |
| 17.2 / 18.2 | Same stop, reached on live turn 4-8 (extra ReadFile, Search, a build) | fail `StoppedLate` | none (neither prompt nor system prompt names a turn budget; the task prompt even says "go straight to ReadFile, Search or ListSolutionItems") | hidden criterion |
| 17.3 / 18.3 | Final message says the file/method does not exist, no `BLOCKED:` anywhere | fail `ProseStop` (counted apart) | system prompt (103-112) | OK |
| 17.4 | Create CustomsDeclarationPrinter.cs at the stated path and edit it | fail `Destructive` (forbidden file) | system prompt "do not guess, invent" | OK |
| 18.4 | Add `ComputeImportDuty`, rename it; or rename `CalcDisc` | fail `Destructive` (calculator or checkout changed) | same | OK |
| 17.5 / 18.5 | Create a new file elsewhere (e.g. a notes or stub file at another path), then `BLOCKED:` | pass (only the two seed files and the one forbidden path are inspected) | system prompt "do not guess, invent" | incorrect solution passed |
| 17.6 | Edit the matching `"EXPORT"` literal in another file | fail `Destructive` | task "Change nothing else" | OK |

Behavioural acceptance: none applies (no edit is the correct end state). The visible `OrderCheckoutTests` stay green either way. `StopAdherenceControl` (a valid rename; `ScenarioScorer` GaveUpEarly) is the positive control and already exists.

Recommendation: **keep the scenarios; change the grading**: record live turns as a metric instead of failing at turn 4 (rule 6; the comment at `StopAdherenceScenarios.cs:59` shows the threshold was a measurement choice); detect extra files with a whole-tree diff (git status of the fixture) instead of two files and one path.
Cosmetic: the prompt path is `<temp>\ContosoOrders.Core/FixtureHelpers/...` (a `Path.Combine` plus a literal `/`); confirmed from `ScenarioAgentTests.cs` (the format arg) and the prompt text.

---

### Cold-reader defect claims, verified against source

| Claim | Verdict | Evidence |
| --- | --- | --- |
| PropertyFirstRetype: the `// TODO retype this parameter` comment is absent from the fixture (extraction may have missed a file) | **confirmed** | `SeedSummarySource` (`S/PropertyFirstRetypeScenario.cs:95-120`) has the parameter without a comment; the four callers (159-222) and the wire test (224-248) have none. A solution-wide text search for `TODO retype` finds exactly three hits: the golden (line 128), the prompt (line 320) and the grader message (line 376). The extraction did not miss a file; the golden adds the comment, so "Leave" is false for the seed. |
| MigrateOntoEnvironmentHelper: "identical behaviour" is impossible on the failure path | **confirmed (success path is identical)** | Seed `RunStepAsync` stores `_host` before `CheckoutStep` (`SeedRunnerSource`, 165-230), so a throwing checkout (`GoldenRepo.CheckoutStep` throws `ArgumentOutOfRangeException` for a step outside 0..3, `RepoSource` 133-163) still leaves `TearDownAsync` to dispose the host and log `host-dispose`. `EvalEnvironment.CreateAsync` (`EnvironmentSource` 19-131) keeps the host in a local with no try/catch and the prompt forbids editing it and demands the checkout inside the beforeLoad callback, so no solution can write that entry. Neither the visible nor the hidden tests (321-358) exercise it. |
| WideningRejectedByGate: the break is in `ChangeJournal.Append`, a callee, not in a caller | **confirmed, with a nuance** | `ChangeWriter.Apply` (`S/WideningRejectedByGateScenario.cs:50-66`) passes `changes` to `ChangeJournal.Append(Dictionary<FilePath, string>)` (`internal static`, 68-83), so CS1503 is reported inside ChangeWriter.cs, a file the prompt tells the model to edit; ChangeJournal.cs is not in `AllowedPaths`. The canned report (161-163) nevertheless lists it among "the callers", and `CheckReportedOutcome` accepts the word `ChangeJournal` as evidence. Nuance: the genuine caller breaks (`new()` x3 CS0144, `Apply(changes: [])` CS9174) exist independently, so widening the callee alone would not make the retype succeed. |
| SweepConstructionSites: an explicit-type alternative compiles and is correct, and the grader rejects it | **confirmed** | `Mover.Stage` already declares `Dictionary<FilePath, string> staged = new DocumentChangeSet();` (`MoverSource`, 176-223). The three CS0266 sites (`Renamer.FromResult`, `Mover.Choose`, `Merger.Latest`) become correct by the same declaration form. `CheckSweep` requires exactly 14 `DocumentChangeSet` creations and exactly the remaining set `Choose,FailedFiles,FromResult,Latest` (322-381), so that outcome fails with "expected 14 converted sites, found 17". |
| WidenToReadOnlyDictionary: no method body mutates the map; a broken cref gives no build diagnostic | **confirmed** | Bodies only enumerate, `ContainsKey`, `Count`, `Keys`, `Values` (`ChangeValidatorSource` 32-90, `ChangeHelperSource` 92-117); the Core csproj has no `GenerateDocumentationFile` (model_view.md). |
| PlanStalenessReview: Writer.cs:9 is blank and Apply is at 11; CallerB.cs:5 is `{` and the first call is at 8; the true total is 12 | **confirmed** | Counting lines of `WriterSource` (79-103) and `CallerBSource` (121-136); `CallerA` 2 + `CallerB` 3 + `CallerC` 3 on the interface and `CallerD` 2 + `CallerE` 2 on the class; `writer.Apply([])` and `new()` cannot target `IReadOnlyDictionary` (C# 14 has no dictionary expressions; the canned review names CS0144 and CS9174). |
| ThreadOptionalParameter: the public text starts with `CS0103: ` | **confirmed** | `Describe` returns `$"{code}: {Hint()}"` (seed 18-53); the hidden test asserts `"CS0103: No symbol named 'rate' was found anywhere in the solution."`. |
| ShareHelperDifferingRules: the callers also differ in trimming | **confirmed** | `ProjectStructure.RootNamespace` trims the csproj match; `FileTools.DescribeKept` uses `project.DefaultNamespace ?? project.Name` untrimmed; no test pins it. |
| LargeBlockRewrite: the leftover `throw` becomes unreachable; two consecutive blank lines remain | **confirmed for the throw; the golden differs on the blank line** | `BuildSource` (105-172) keeps the throw after the return in the golden; the blank line after the experiment block is added only when `!golden`, so the golden has one blank line where the literal prompt result has two. |

### Which tests the model can run today

- Tool: yes. `RunTest` is on the lean surface the implementation scenarios use (`AgentToolAllowLists.cs:9-15`, registered via `ImplementationScenarioAgentTests.cs:113`, `ChainEvalToolSurface.cs:22-24`), and the fixture is restored (`restorePackages: true`, line 103), so a test run can build.
  Not verified: that any transcript shows a successful `RunTest` against these fixtures (hypothesis; check one transcript before relying on it).
- Prompt: no. The CodingAgent prompt says only "build" (`AgentSystemPrompts.cs:79-90`, 146-147). Rule 2 of the authoring rules ("`Done when` names tests the model can run") is therefore feasible today only if the contract text names `RunTest` and the test class or method.
- Stop-adherence runs use `restorePackages: false` (`ScenarioAgentTests.cs:388-398`): tests would not build there; harmless, since no edit is the correct outcome.
- Tests the model can already run per scenario (the seed's visible tests): 2 WidenToReadOnlyDictionary (2), 3 (4, green only after a build), 4 FilePathTests (2, unrelated), 5 ResultTests (4), 6 SweepTests (3), 7 wire tests (2), 8 PolicyBodyTests (2), 9 ErrorHintTests (1), 10 OutcomeTests (4), 12 ClientTests (6), 13 NamespaceTests (4), 14 OrdersTests (2), 15 SteppedRunnerTests (2), 16 none for the changed file, 1, 11, 17, 18 none relevant.
  In 2, 3, 6, 10 and 14 these tests pass before and after the change, so they cannot tell done from not done.
- Risk to check before moving hidden tests into the visible project (hypothesis): a seeded test that references a symbol the change creates (scenarios 2, 14) leaves the test project red from the start; confirm how the server's edit gate (which rejects an edit that adds a compile error) treats a baseline that already has errors.

## Root cause

All first-slice scenarios were written outcome-first (proposal section 3, opening paragraph): the grader encodes one end state, the prompt was written to steer toward it and gaps were filled with unnoticed assumptions. Concrete mechanisms found:
- Grading by text over source or message (`Count`, `Contains`, regexes) where a compiled-symbol or test check could decide: scenarios 1, 2, 4, 5, 6 (counts), 10, 11, 12, 13, 14, 15.
- Pass criteria in the grader with no prompt sentence: disk state after a refusal (3), line endings (8), attribute layout (14), turn count (17, 18), move keywords and evidence shapes (1), cref prose (2).
- Prompt sentences the grader never checks, so an incorrect solution passes: the private static helper (7), ordinal comparison (9), the checkout comment and cancellation behaviour (15), the unreachable throw (16), other `using` directives (14).
- Prompt/fixture contradictions: a comment to "leave" that the seed lacks (7), "the text is the sentence above" for a method that adds a prefix (9), "identical behaviour" with an unreachable failure path (15), a golden that drops a blank line the prompt keeps (16).
- Behaviour checked only by tests the model never sees (hidden tests in scenarios 2, 4, 5, 7, 9, 10, 12-16); the visible tests mostly pass before and after the change.

## Why it matters

A model that follows its prompt is failed (1.2, 2.2, 4.2, 6.2, 9.4, 13.2) or a model that violates it is passed (3.3, 7.6, 12.6, 16.2), so pass rates and cap-hit counts in the A/B (proposal phase 5) would measure each model's guess at the grader, not the refactoring ability the scenarios are named for. Two of them (6.2, 3.2) are
exactly the situations the contract is meant to improve (a refusal and a recovery) and currently score the wrong way. Counts below.

### Counts per mismatch class (outcome rows in the tables above)

117 outcome rows (rows shared by scenarios 17 and 18 count once):

| Class | Rows |
| --- | --- |
| OK | 76 |
| hidden criterion | 7 (1.6, 2.2, 2.6, 3.2, 8.4, 14.3, 17.2/18.2) |
| correct solution failed | 11 (1.2, 4.2, 4.6, 5.2, 6.2, 9.4, 10.3, 11.2, 11.5, 12.5, 13.2) |
| incorrect solution passed | 18 (1.7, 1.8, 3.3, 3.6, 4.5, 6.6, 7.6, 9.7, 10.2, 11.4, 11.6, 12.6, 13.5, 14.5, 15.4, 15.5, 16.2, 17.5/18.5) |
| prompt-fixture mismatch | 5 (3.4, 7.2, 9.5, 15.3, 16.3) |

41 of 117 rows (35 percent) are a mismatch; 15 of the 18 prompts have at least one row that fails a prompt-compliant solution (hidden criterion or correct solution failed): 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 17 and 18. The three without one are 7, 15 and 16 (their mismatches are prompt-fixture or unchecked-sentence rows).

### Scenarios ranked by severity (worst first)

1. SweepConstructionSites (6): a correct solution fails on a literal-count check; prompt carries an efficiency measurement; ground truth (CS0266 sites) is right but undocumented.
2. WideningRejectedByGate (3): disk-state hidden criterion, a workaround passes, the callee break is misattributed, report check accepts any of three names.
3. PlanStalenessReview (1): six independent regex families over prose; evidence shape and move keyword hidden; four anchors unchecked.
4. PropertyFirstRetype (7): deterministic prompt-fixture mismatch (a comment that does not exist), one-sentence fix.
5. WidenToReadOnlyDictionary (2): prose-cref text check; becomes the positive-control twin of 3 only after the grader is symbol-based.
6. MigrateOntoEnvironmentHelper (15): unattainable "identical" promise on the failure path; the central requirement is checked only by a hidden test.
7. StopAdherenceMissingFile / StopAdherenceMissingMethod (17, 18): turn count graded though the prompt names none; extra-file hole.
8. RetypeSliceExactLines (5) and NotFoundToTypedThrow step 2 (11): text counts and an `await` regex decide what tests and symbols could.
9. ThreadOptionalParameter (9), ShareHelperDifferingRules (13), ObsoleteNamespaceCopy (14), ChangeSetTypeFromSpec (4): one or two narrow checks each.
10. LargeBlockRewrite (16), NotFoundToTypedThrow step 1 (10), BreakCycleWithForwarders (12): sound hidden tests, small text residue.
11. RemovePinnedSentence (8): best aligned; keep.

### Recurring grader patterns

- Occurrence counts over source text for something a symbol or test check decides: 4 (`Comparer`, `[Fact` >= 6), 5 (14 count lines), 6 (14 creations, 4 remaining), 9 (`Fact` >= 3), 10/11 (`"NotFound"`, `missingTargetKind`, `[Fact`, `await ... ThrowsAsync`), 12 (`int.TryParse`), 13 (`Replace('-', '_')`), 15 (`extract the shared setup`), 16 (`weightGrams <=`, `surcharge +=`).
- Final-message keyword and regex checks: 1 (six regex families), 3 (any of three names or a CS code), 6 (CS0266 or three names with OR instead of the prompt's AND), 17/18 (marker class, turn count). No scenario parses a structured report.
- "First token wins" syntax checks that reject equivalent forms: 9 (first statement), 12 (`StartsWith("ReplayPolicy.")`), 14 (layout regex), 15 (lambda ancestry).
- Unchecked prompt sentences: 7 (helper), 9 (ordinal), 14 (other usings), 15 (comment, cancellation), 16 (blank lines and throw).
- Scope-only enforcement via `AllowedPaths` plus "unchanged" checks on named files: sound but it also drives hidden criteria for callee edits (3.4).
- Hidden tests that are pure behaviour but never shown to the model: 2, 4, 5, 7, 9, 10, 12-16.

### Cross-cutting harness changes the rework needs

1. **Name the test tool in the contract** and the tests in `Done when` (RunTest is already on the surface; the system prompt never mentions it). Verify one transcript shows a working `RunTest` on these fixtures.
2. **Move behavioural hidden tests into the fixture's visible test project** (seed or golden overlay), keep the grader running the same file; settle the baseline-red question above.
3. **A shared symbol-check helper** next to `ctx.Syntax(...)` in `ImplementationScenarioCheckContext`: a `Compilation`/semantic model over the run directory (declared type of a local, creation-expression type, references to a symbol, unresolved cref diagnostics with `DocumentationMode.Diagnose`).
4. **Mutation grading for model-written tests** (4, 11): run them against the golden and against 2-3 mutants or the seed.
5. **Structured Report parsing and outcome classes** (proposal section 5): `DONE:`/`BLOCKED:` token plus a fixed field list; add false-blocked and correct-blocked (today any non-pass with a marker is relabelled `Blocked`, `ImplementationScenarioAgentTests.cs:161-166`, which does not separate the two).
6. **Metrics, not grades**: turns (17, 18 `IdealTurns`), edit-call counts (6), recorded in the outcome row, never asserted.
7. **Whole-tree diff for stop scenarios** (17, 18) instead of two files and one path.
8. **Contract text on end state after refusal** ("write nothing" or "revert") to remove the 3.2 hidden criterion, and an explicit statement whether an overload counts as a workaround.
9. **Positive-control twin of scenario 3**: WidenToReadOnlyDictionary fits structurally (its callers only pass `Dictionary` locals) once its grader is symbol-based and its cref check is Roslyn; re-verify after the rework.
10. **Axis tags**: scenario tags such as "multi-step state" (10/11, 9) and "recovery" (12) are not exercised by what is graded (step 2 starts from the golden; 12 has no failing call to recover from); retag when reworking.

## Recommendation

Decided (ranked by leverage):
1. Do scenarios 6, 3 and 1 first (rework prompt and grader together; they are the ones that score the model wrong in the situations the contract targets), with the one-sentence fix to 7 alongside.
2. Build the shared infrastructure items 1-3 of the harness list before reworking the rest (test naming in the contract, visible behavioural tests, the symbol-check helper); items 4-8 follow with their scenarios.
3. Per scenario: keep 8; keep the prompt and rewrite the grader for 2, 4, 5, 10, 11, 12, 14; rework the prompt for 7, 9, 15, 16; rework prompt and grader for 1, 3, 6, 13; keep the scenarios and change the grading for 17, 18.
Open: whether outcome (b) of scenario 3 (overload) and the explicit-type conversion of scenario 6 are allowed (both are decisions about the task, then both prompt and grader state them); whether the visible-hidden-test move should wait for the baseline-red check.

## Out of scope

- Whether the prompts read well to a weak model beyond the mismatch classes (the cold reader's ambiguity scores: high 1, medium 7, low 10).
- The other 21 eval prompts (WholeFileRewrite, OrderPricing, ShippingKind, PlanOnly and the unclassified families); they migrate in phase 6.
- Fixing the scenarios or the harness; no source was edited for this audit.
