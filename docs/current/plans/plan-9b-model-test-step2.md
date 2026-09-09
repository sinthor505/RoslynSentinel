# Task: Fix one whole-file-reformat bug in AdvancedStructuralEngine.cs (level 2)

You have access to RoslynSentinel MCP tools only — no terminal/bash access. Use only those
tools for every step, including verifying your fix compiles.

This plan gives you less detail than a fully-scripted one. You are told *what* to do at each
stage and *which tool* to use, but not the exact parameters or exact code to write — you need to
work those out yourself from what the tools return. If a tool call fails, stop and report the
exact error message rather than guessing around it.

## Background

`RoslynSentinel.Advanced/AdvancedStructuralEngine.cs` has a known bug pattern: several methods
build an edited syntax tree and then call `.NormalizeWhitespace().ToFullString()` on the whole
file root before returning it. This reformats the ENTIRE file — not just the part that changed —
which silently reindents/reflows unrelated code and shifts line numbers.

This exact bug was already fixed, using the same fix pattern, across every file in the sibling
`RoslynSentinel.Basic` project. Your job is to apply that same fix pattern to ONE specific method
in `AdvancedStructuralEngine.cs`: `ConvertAbstractClassToInterfaceAsync`.

## Steps

1. Load the solution at `C:\Users\Administrator\source\repos\RoslynSentinel-AgentTesting\RoslynSentinel.slnx`.

2. Read `ConvertAbstractClassToInterfaceAsync` in
   `RoslynSentinel.Advanced\AdvancedStructuralEngine.cs`. Identify the exact line responsible for
   the whole-file-reformat bug described above.

3. Find the existing fix pattern already used elsewhere in this codebase for this same bug.
   Look at `RoslynSentinel.Basic\RefactoringEngine.cs` — there is a private helper method there,
   already used by several methods in that file, that solves exactly this problem by formatting
   only the changed node instead of the whole file. Locate it and read its full source.

4. Apply the same fix to `ConvertAbstractClassToInterfaceAsync`:
   - Bring the helper method into `AdvancedStructuralEngine.cs` (this class doesn't have it yet).
   - Add whatever `using` directive the helper needs to compile.
   - Update `ConvertAbstractClassToInterfaceAsync` so it uses the helper instead of the
     whole-file `.NormalizeWhitespace()` call, producing the same edit (still replacing the
     abstract class node with the generated interface node) but formatting only that change.

5. Verify your change compiles, using an MCP tool (you have no terminal). Scope the build to
   just the `RoslynSentinel.Advanced` project rather than the whole solution.

6. Confirm the fix: re-read `ConvertAbstractClassToInterfaceAsync` and check that the
   whole-file-reformat call is gone and the helper is being used instead.

7. Report what you changed and the verification result.

## Constraints

- Don't touch any other method in the file — there are other `.NormalizeWhitespace()` call sites
  in `AdvancedStructuralEngine.cs` that are explicitly out of scope for this task.
- Don't invent a new helper method or a different fix approach — reuse the existing pattern from
  `RefactoringEngine.cs` as closely as possible; don't rename it or change its behavior.
- Preserve the original method's behavior (same inputs/outputs/branches) — only the formatting
  mechanism should change.
