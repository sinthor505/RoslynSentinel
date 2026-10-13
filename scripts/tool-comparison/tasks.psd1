# Task manifest for the with/without-MCP editing comparison. See
# docs/current/references/reference_tool_comparison_harness.md.
#
# Kind 'answer': the agent writes ANSWER.md; scored by file-level precision/recall against a gold
#   set computed from the pristine copy (GoldRegex) or pinned (GoldFiles).
# Kind 'edit': the agent changes the tree; scored by build, Checks, scope and diff size.
#
# Checks: Target 'Content' (regex matches across files whose path matches PathRegex) or 'Path'
#   (file paths matching Pattern). Op 'eq' | 'ge'. Baseline = expected count on the pristine copy,
#   verified at setup (omit when unknown; setup prints the measured value so it can be pinned).
@{
    Tasks = @(
        @{
            Id       = 'D1'
            Category = 'Discovery'
            Kind     = 'answer'
            Prompt   = @'
List every place in non-test C# code that constructs a `JsonSerializerOptions` directly (either `new JsonSerializerOptions` or a target-typed `new()`), other than inside the `SharedJsonOptions` type itself. Skip comments and strings.
'@
            GoldRegex       = 'new\s+(System\.Text\.Json\.)?JsonSerializerOptions\b|JsonSerializerOptions\??\s+\w+\s*=\s*new\s*\('
            GoldExcludePath = '(^|/)(RoslynSentinel\.Tests[^/]*|[^/]*\.Tests|Samples|TempCheck|DummyConsole|_scratchbuild[^/]*)/|SharedJsonOptions\.cs$'
            IgnoreLeaves    = @()
        }
        @{
            Id       = 'R2'
            Category = 'Reference finding'
            Kind     = 'answer'
            Prompt   = @'
List every direct caller of the static method `ValidateAndApplyHelper.ValidateAndApplyAsync` (in the Common project), across the whole solution including tests. Several classes declare their own private method that is also named `ValidateAndApplyAsync`; calls to those are not calls to the helper and must not be listed. Report each caller's file and line.
'@
            GoldRegex       = 'ValidateAndApplyHelper\.ValidateAndApplyAsync\s*\('
            GoldExcludePath = '(^|/)ValidateAndApplyHelper\.cs$'
            # Explorer survey (2026-10-13) found these 9 files; setup warns if the regex gold differs.
            GoldHint        = @('TestCategoryApplyEngine.cs', 'WriteChokepointGuardrailTests.cs', 'AdvancedRefactoringTools.cs', 'GenerationTools.cs', 'NamedArgumentsTools.cs', 'RefactoringExtractionDocsImpl.cs', 'RefactoringSignatureImpl.cs', 'RefactoringStructuralImpl.cs', 'SemanticFindReplaceTools.cs')
            IgnoreLeaves    = @('ValidateAndApplyHelper.cs')
        }
        @{
            Id       = 'M2'
            Category = 'Modify members'
            Kind     = 'edit'
            Prompt   = @'
In `GitImpl.cs` (Tools.Basic), the catch blocks return error results whose text interpolates the raw exception message (`{ex.Message}`). Remove the exception text from every such error: keep the existing `Git <operation> failed` wording, end it with a period (for example `Git status failed.`), and leave every other property of the result unchanged. Do not change any other file.
'@
            AllowedChangedPath = 'GitImpl\.cs$'
            Checks = @(
                @{ Name = 'no {ex.Message} left in GitImpl';  Target = 'Content'; PathRegex = 'GitImpl\.cs$'; Pattern = '\{ex\.Message\}'; Op = 'eq'; Expected = 0;  Baseline = 15 }
                @{ Name = 'Git <op> failed. wording present'; Target = 'Content'; PathRegex = 'GitImpl\.cs$'; Pattern = 'Git \w+ failed\.'; Op = 'ge'; Expected = 15; Baseline = 0 }
            )
        }
        @{
            Id       = 'N1'
            Category = 'Rename'
            Kind     = 'edit'
            Prompt   = @'
Rename the class `SyntaxTargetResolver` (Engines.Basic) to `SnippetTargetResolver`, including its file name, and update every reference across the solution, including tests, XML doc crefs and comments.
'@
            TestFilter = 'GenericContainerNameTests'
            Checks = @(
                @{ Name = 'old name gone from code';  Target = 'Content'; PathRegex = '\.cs$'; Pattern = '\bSyntaxTargetResolver\b'; Op = 'eq'; Expected = 0; Baseline = 188 }
                @{ Name = 'new name used';            Target = 'Content'; PathRegex = '\.cs$'; Pattern = '\bSnippetTargetResolver\b'; Op = 'ge'; Expected = 188; Baseline = 0 }
                @{ Name = 'old file gone';            Target = 'Path'; Pattern = '(^|/)SyntaxTargetResolver\.cs$'; Op = 'eq'; Expected = 0; Baseline = 1 }
                @{ Name = 'new file present';         Target = 'Path'; Pattern = '(^|/)SnippetTargetResolver\.cs$'; Op = 'eq'; Expected = 1; Baseline = 0 }
            )
        }
        @{
            Id       = 'N2'
            Category = 'Rename'
            Kind     = 'edit'
            Prompt   = @'
Rename the static method `ValidateAndApplyHelper.ValidateAndApplyAsync` (in the Common project) to `ApplyValidatedChangesAsync` and update all of its callers across the solution, including tests. Several other classes declare their own private methods that are also named `ValidateAndApplyAsync`; those are unrelated and must keep their names and their callers.
'@
            TestFilter = 'WriteChokepointGuardrailTests'
            Checks = @(
                @{ Name = 'no qualified call to old name'; Target = 'Content'; PathRegex = '\.cs$'; Pattern = 'ValidateAndApplyHelper\.ValidateAndApplyAsync'; Op = 'eq'; Expected = 0; Baseline = 9 }
                @{ Name = 'new name used (decl + callers)'; Target = 'Content'; PathRegex = '\.cs$'; Pattern = '\bApplyValidatedChangesAsync\b'; Op = 'ge'; Expected = 10; Baseline = 0 }
                # 67 at baseline, 9 of which are the qualified helper calls that must move.
                @{ Name = 'same-named wrappers survive';    Target = 'Content'; PathRegex = '\.cs$'; Pattern = '\bValidateAndApplyAsync\s*\('; Op = 'ge'; Expected = 58; Baseline = 67 }
            )
        }
        @{
            Id       = 'S1'
            Category = 'Change signature'
            Kind     = 'edit'
            Prompt   = @'
Add an optional parameter `bool logChanges = false` to the static method `ValidateAndApplyHelper.ValidateAndApplyAsync` (in the Common project), placed immediately before its `CancellationToken` parameter, and update every direct caller of that helper to pass `logChanges: true`. Callers of same-named private methods in other classes are not callers of the helper and must not change. Do not use the new parameter inside the helper body.
'@
            TestFilter = 'WriteChokepointGuardrailTests'
            Checks = @(
                @{ Name = 'parameter declared once';   Target = 'Content'; PathRegex = 'ValidateAndApplyHelper\.cs$'; Pattern = 'bool\s+logChanges\s*=\s*false'; Op = 'eq'; Expected = 1; Baseline = 0 }
                @{ Name = 'callers pass logChanges: true'; Target = 'Content'; PathRegex = '\.cs$'; Pattern = 'logChanges:\s*true'; Op = 'ge'; Expected = 9; Baseline = 0 }
            )
        }
    )
}
