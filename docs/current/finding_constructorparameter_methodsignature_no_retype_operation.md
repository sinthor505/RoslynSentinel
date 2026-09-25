# Finding: No MCP tool can retype an existing constructor/method parameter (or its backing field)
# in place -- only add, remove, and view exist

**Status:** confirmed by source inspection (the enum itself proves the operation set), not yet
fixed. This is a "document but continue" finding -- a session-scoped policy permitted routing
around it via `ReplaceSnippet` directly on the constructor/field text rather than halting the
session.

## What was being attempted

During the interface-widening sweep described in `docs/current/design_read_chokepoint.md`
(migrating consumers from `ISolutionProvider` to `IWorkspaceManager`/`IWorkspaceReader`),
`AdvancedLogicEngine`'s constructor needed its single parameter's declared type changed from
`ISolutionProvider` to `IWorkspaceManager`, along with the matching change to its backing field's
declared type. This is a pure retype of an existing parameter/field -- the parameter's name,
position, and role in the constructor body are unchanged; only the type annotation changes.

No combination of the two MCP tools that own constructor/method parameter editing --
`ConstructorParameter` and `MethodSignature` -- could express this as a single structural
operation. Both tools' `operation` argument is typed `AddRemoveViewAction`, which only has three
values: `add`, `remove`, `view`. There is no `retype`/`update`/`replace` member, so this is not a
matter of an existing option being obscure or undocumented -- the operation is structurally
unrepresentable in the current schema.

## Where this is confirmed (traced to source, not inferred from tool descriptions)

`RoslynSentinel.Common/ToolEnums.cs:187-191`:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AddRemoveViewAction
{
    add, remove, view
}
```

This is the actual parameter type both tools declare:

- `RoslynSentinel.Server.Basic/RefactoringSignatureTools.cs:91-115` -- `ConstructorParameter`,
  `operation` parameter typed `AddRemoveViewAction` (line 98), tool `[Description]` at line 93:
  "Add, remove, or view DI constructor parameters on a class."
- `RoslynSentinel.Server.Basic/RefactoringSignatureTools.cs:47-72` -- `MethodSignature`,
  `operation` parameter typed `AddRemoveViewAction` (line 54), tool `[Description]` at line 49:
  "Add, remove, or view a method's parameters."

Because the enum itself has no fourth value, this is a compile-time-provable absence, not a gap
that only shows up by reading every branch of the implementation -- no branch handling a retype
could exist without first adding the enum member and updating both `[McpServerTool]` call sites.

Confirmed by reading the implementation bodies as well:
`RoslynSentinel.Basic/RefactoringSignatureImpl.cs:317-352` (`ConstructorParameter`) branches only
on `AddRemoveViewAction.view` (line 336) and `AddRemoveViewAction.add` (line 356), falling through
to remove for anything else -- there is no third branch to retype an existing parameter's type or
its backing field's type.

## A related, but separately-confirmed-intentional, restriction: MethodSignature(remove) only
## removes the last parameter

While tracing `MethodSignature`'s operation set, `RoslynSentinel.Basic/RefactoringEngine.cs:4894-4902`
confirms `RemoveMethodParameterAsync` explicitly rejects removing any parameter other than the
last one:

```csharp
var lastParam = parameters[^1];
if (lastParam.Identifier.Text != paramName)
{
    return new DocumentEditResult
    {
        Outcome = EditOutcome.CannotRemove,
        FilePath = filePath,
        Message = $"// Cannot remove '{paramName}': MethodSignature(remove) only supports the last parameter (currently '{lastParam.Identifier.Text}') - removing an earlier parameter would require reordering every call site's remaining positional arguments, which cannot always be done safely."
    };
}
```

This matches the tool's own `[Description]` text
(`RefactoringSignatureTools.cs:53`, "remove: only the LAST parameter can be removed ... a
deliberate restriction") -- so, unlike the missing-retype gap above, this is a documented, reasoned
design decision (avoiding unsafe positional-argument reordering at call sites), not an
undocumented defect. It is recorded here only because it was checked as part of establishing the
tools' actual operation set, and because it compounds the retype gap in practice: a caller who
wants to change an earlier (non-last) parameter's type cannot achieve it by removing and
re-adding either, since removal of a non-last parameter is refused for a different, legitimate
reason.

## How the gap was worked around this session

Fell back to `ReplaceSnippet` directly against the constructor declaration text and the backing
field declaration text in `AdvancedLogicEngine`, under an explicit session-scoped policy
relaxation permitting a documented bypass rather than a session halt. This is exactly the kind of
manual text-massaging the MCP structural-tool surface exists to avoid (CLAUDE.md's dog-fooding
mandate treats "an agent reaches for a shell command / manual edit to modify C# code" as itself
the finding, not merely a workaround detail).

## Impact

This is not a one-off. It surfaces at every consuming class touched by an interface-composition
change that widens or narrows a constructor dependency's type -- a routine, recurring shape of
change for this repo's own dog-fooding-driven refactors, and specifically the shape of
`design_read_chokepoint.md` step 3, which is expected to touch roughly 78 more files converting
constructor parameters from `ISolutionProvider` to `IWorkspaceManager`/`IWorkspaceReader`. Each of
those call sites will hit the same missing-retype gap unless the tool surface is extended before
that sweep runs.

## What unblocks it

A `retype` (or `update`) value added to a parameter-editing operation, either:

1. Extending `AddRemoveViewAction` itself (renaming or superseding it, since
   "AddRemoveViewAction" would no longer describe its full member set) with a `retype` case wired
   through both `ConstructorParameter` and `MethodSignature`'s implementations
   (`RefactoringSignatureImpl.cs`), accepting a new parameter type string and -- for
   `ConstructorParameter` specifically -- updating the backing field's declared type to match in
   the same operation, since the two are coupled by construction (`AddConstructorParameterAsync`
   already creates both together); or
2. A new dedicated tool/operation scoped to this specific case if reusing `AddRemoveViewAction`
   is judged too disruptive to the existing add/remove/view contract.

Either path needs a decision from the repo owner on whether the retype should also fix up callers
(e.g. object-initializer or positional-argument call sites passing a value of the old type) or
simply change the declared type and let the resulting compile errors surface to whatever tool runs
next -- this determines how much of `ChangeSignature`'s (Advanced-tier) existing caller-fixup
machinery, if any, should be shared rather than reimplemented.

## Related

- `docs/current/design_read_chokepoint.md` -- the migration this gap was hit during; step 3 is
  expected to hit it again at other call sites.
- `RoslynSentinel.Common/ToolEnums.cs:187-191` -- `AddRemoveViewAction` definition (the compile-time
  proof of the missing operation).
- `RoslynSentinel.Server.Basic/RefactoringSignatureTools.cs:47-72, 91-115` -- `MethodSignature` and
  `ConstructorParameter` tool declarations.
- `RoslynSentinel.Basic/RefactoringSignatureImpl.cs:317-352` -- `ConstructorParameter`
  implementation, confirming only `view`/`add`/(fallthrough)`remove` branches exist.
- `RoslynSentinel.Basic/RefactoringEngine.cs:4894-4902` -- `RemoveMethodParameterAsync`'s
  documented last-parameter-only restriction (related but separately intentional).
