# Task: Fix one whole-file-reformat bug in AdvancedStructuralEngine.cs

Follow these steps in exact order. Do not skip a step. Do not combine steps. If a
step's tool call fails, stop and report the exact error message — do not guess a
workaround.

## Setup

**Step 1.** Confirm the RoslynSentinel MCP tools are available and reachable by
calling `ListWorkspaceSolutions` with:
```
workspacePath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting
```
You should get back a list containing `RoslynSentinel.slnx`. If the tool call
fails or times out, stop and report this as a blocking error.

**Step 2.** Call `LoadSolution` with:
```
solutionPath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.slnx
```
Confirm the response says `success: true`.

## Read the current (buggy) code

**Step 3.** Call `GetMethodSource` with:
```
filepath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Advanced\AdvancedStructuralEngine.cs
methodName: ConvertAbstractClassToInterfaceAsync
```
Read the `source` field in the result. Find the line inside it that looks like:
```
UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
```
This is the bug: it reformats the ENTIRE file, not just the part of the file
that was changed. Your job in this task is to replace this one line's approach
with a version that only reformats the specific piece of code that changed.

**Step 4.** Call `GetMethodSource` again, this time with:
```
filepath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Basic\RefactoringEngine.cs
methodName: ReplaceNodeFormattedAsync
```
This returns an existing helper method already used elsewhere in this codebase
to fix exactly this bug pattern. Copy its full source text exactly as returned
— you will reuse it verbatim in Step 6. Do not modify it, rename it, or change
any part of it.

## Apply the fix

**Step 5.** Call `UsingDirective` with:
```
filepath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Advanced\AdvancedStructuralEngine.cs
operation: add
namespaceName: Microsoft.CodeAnalysis.Formatting
```
This adds a `using` statement the helper method needs.

**Step 6.** Call `Member` with:
```
filepath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Advanced\AdvancedStructuralEngine.cs
operation: add
containerName: AdvancedStructuralEngine
position: after:AdvancedStructuralEngine
newMemberSource: <the exact text you copied in Step 4, with "private static async Task<string>" kept exactly as-is>
```
This inserts the helper method into the class, right after its constructor.

**Step 7.** Call `Member` with:
```
filepath: C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Advanced\AdvancedStructuralEngine.cs
operation: replace
memberName: ConvertAbstractClassToInterfaceAsync
newMemberSource: <the full method, copied from Step 3's result, but with ONE change>
```
The one change: replace this line:
```
var newRoot = root!.ReplaceNode(classNode, interfaceNode);
return new DocumentEditResult
{
    Outcome = EditOutcome.Modified,
    UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
    FilePath = filePath
};
```
with this:
```
return new DocumentEditResult
{
    Outcome = EditOutcome.Modified,
    UpdatedText = await ReplaceNodeFormattedAsync(document, root!, classNode, interfaceNode, cancellationToken),
    FilePath = filePath
};
```
Note that the `var newRoot = ...` line is deleted entirely — it's no longer
needed, because `ReplaceNodeFormattedAsync` does that work internally. Keep
every other line of the method exactly as it was in Step 3's result.

## Verify

**Step 8.** Run this exact command using your terminal/bash tool (not an MCP
tool) to confirm the code compiles:
```
dotnet build "C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.Advanced\RoslynSentinel.Advanced.csproj" -c Debug
```
You should see `Build succeeded.` and `0 Error(s)`. If you see any errors,
stop and report the exact error text — do not attempt to guess a fix beyond
re-checking that Steps 5-7 were applied exactly as written.

**Step 9.** Call `GetMethodSource` one final time with the same arguments as
Step 3, and confirm the returned source no longer contains the text
`NormalizeWhitespace` anywhere, and instead contains a call to
`ReplaceNodeFormattedAsync`.

## Done

If Steps 8 and 9 both succeed, the task is complete. Report back: "Fix applied
and verified — build succeeded, NormalizeWhitespace call site replaced with
ReplaceNodeFormattedAsync."
