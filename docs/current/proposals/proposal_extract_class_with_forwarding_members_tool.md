# One tool call that extracts members into a new class and leaves attributed forwarding members behind

**Status:** PROPOSED 2026-11-27. Sketch from the layering design session; not designed, nothing built. Design happens in its own session.

## Motivation

Splitting a tool class into an MCP surface (`*Tools`) and an implementation (`*Impl`) is a known, repeated job here, and no tool does it as
one semantic operation.

- **It was done by hand once.** `plans/closed/plan_split_workspace_refactoring_tools_for_di.md` (Decision 1 amendment) split 51 Basic tools
  into `*Tools`/`*Impl` pairs: bodies verbatim into `*Impl`, one-line delegates, every MCP attribute on the wrapper only. The plan names no tool
  for the move.
- **18 Basic tools are still unsplit** (survey 2026-11-27): `WholeFileWriteTools` 5, `CoreRefactoringTools` 4, `AdminTools` 2, and one each in
  `DocumentationTools`, `GitTools`, `GitRemoteTools`, `NamedArgumentsTools`, `SemanticFindReplaceTools`, `ServerStatusTools`,
  `ToolsetControlTools`. Extracting them is a roadmap item of
  [plan_roadmap_layering_and_project_splits.md](../plans/plan_roadmap_layering_and_project_splits.md) (question Q85 of
  [proposal_engines_report_facts_tool_layer_writes_text.md](proposal_engines_report_facts_tool_layer_writes_text.md)).
- **The same operation is needed again later:** three `*Impl` classes exceed 1,500 lines (`WorkspaceFileEditImpl` 2,707,
  `WorkspaceReadNavigationImpl` 2,266, `RefactoringStructuralImpl` 1,519), and `PersistentWorkspaceManager` (3,958 lines) is to be taken apart.

Three dry runs on `RoslynSentinel.Tools.Basic/NamedArgumentsTools.cs` (one tool method, four private fields, one private helper, one private
constant; 2026-11-27, nothing written) show what the existing tools refuse:

1. `MoveMember`, members `NamedArgumentsTool` and `InvalidArgument`, new target `NamedArgumentsImpl`, `oldMember: keepDelegator`. Refused,
   `TargetIneligible`: "oldMember 'keepDelegator' cannot forward 'InvalidArgument': it is Private, so the forwarder in 'NamedArgumentsTools'
   could not call it on 'NamedArgumentsImpl' (CS0122)". The keep mode applies to every listed member.
2. `MoveMember`, `NamedArgumentsTool` alone, same options. Refused, `InvalidArgument`: "their bodies reference private/internal member(s)
   [DefaultMinParameters, InvalidArgument, _engine, _logger, _validationEngine, _workspaceManager] of 'NamedArgumentsTools' that are not included
   in this move". The refusal text already separates companions used only by the moved members from those shared with staying members.
3. `ExtractMethod` on the whole body (121 lines pasted as `exactSourceBlock`), `newMethodName: NamedArgumentsCore`. Refused by the compile gate,
   `ValidationFailed`: "CS4032 (x2): The 'await' operator can only be used within an async method". The extracted method is not made async.

`MoveMember`'s keep modes also point the other way by decided design
(`proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md`, section 1): the member's attributes travel with the moved member
and the forwarder has none. That served moving a tool out of the `WorkspaceTools` facade; here the attributes must stay on the source.

A route with today's tools exists once the `ExtractMethod` async defect is fixed, but it takes several calls per class: create the target
class, add its constructor parameters (`ConstructorParameter` or `ParameterEdit`), add the source's receiver field, `ExtractMethod` each body,
then one `MoveMember` with `oldMember: remove`, `fieldMap` and `forwarderReceiver`. Not run end to end.

## Proposal

A dedicated tool (working name open) that does the whole extraction in one call. Sketch, nothing decided:

- **Inputs, all mandatory, no modes:** source file and class, the members to extract, the new class name, the receiver field name in the source
  (for example `_impl`), plus the usual `dryRun` and `returnDiff`.
- **What one call does:**
  1. Creates the target class in a new file beside the source.
  2. Moves each named member's body to the target. The moved member carries no attributes and no parameter attributes.
  3. Leaves a forwarding member in the source with the original signature, member attributes, parameter attributes and default values; its body
     is one call through the receiver field.
  4. Moves private companions (helpers, constants) used only by the moved members along, with no forwarder.
  5. Generates in the target the fields and constructor the moved bodies need, and rewrites the moved bodies to them.
  6. Adds the receiver field to the source and initialises it in the source's constructor from the source's own dependencies, so the source's
     constructor signature does not change (tests construct tool classes directly; the September split kept signatures stable for that reason).
  7. Runs the compile gate over the whole change; all or nothing.
- **Reuse, not composition through `ExtractMethod`.** "Extract the body, then move it" equals "move the member and leave a forwarder that keeps
  the attributes", so the statement-level tool is not needed. The engine pieces to reuse are `MoveMember`'s: the companion analysis behind
  refusal 2, the `fieldMap` body rewrite, `MoveMemberTextEdits.BuildForwarder` (with the attribute side switched), new-class creation and the
  same-class call rewrite. Hypothesis: these are reusable as they stand; they were not read in current source for this sketch.
- **Rule for shared state.** Only a `readonly` field assigned from a constructor parameter may be duplicated into the target. A mutable field
  that staying members also use is refused with the list of users, because two copies would drift apart.
- **Refusals** follow `MoveMember`'s keep-mode table where the reason is the same (virtual, abstract, override, interface implementation,
  partial source class, properties and nested types in a first version).

## Alternatives considered, not pursued

- **A fourth `oldMember` value on `MoveMember`.** One enum value would switch three behaviours at once (attribute side, silent companions,
  generated target state) on a tool that already has eleven parameters and a refusal table. Every added mode also costs schema tokens in each
  session where the tool is active.
- **A knob on `ExtractMethod`.** That tool works on statements inside one method; this operation is about members and classes. It would also
  inherit the paste-the-whole-body parameter.
- **Restoring the class mode of `ExtractMembers`.** Its `newType: class` was removed in favour of `MoveMember`
  (`RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs`, comment above `:416`). Possible home; it sits in `Tools.Advanced`, which the
  layering roadmap converts last. Left as an open question, not rejected.
- **The multi-call route with today's tools.** Works in principle after the `ExtractMethod` fix, but an agent has to carry the dependency list
  across five kinds of call per class; that is the sequencing burden a semantic tool exists to remove.
- **Manual copy.** Ruled out by the tool-gap rule in CLAUDE.md for a job of this size.

## Open questions

- The tool's name. "Delegate" is avoided in tool names here (`proposal_parallel_change_deprecated_wrappers.md`, Terminology); "extract class"
  is the established term.
- Home: a new tool class, a method on `CoreRefactoringTools`, or a mode of `ExtractMembers`; and which toolset exposes it.
- Does the source construct the target in its constructor (sketch), or receive it as a constructor parameter? The second changes the source's
  signature and its callers; it may be a later option.
- Several constructors, a primary constructor, or a static source class.
- The moved member's name and parameter list: same name as the tool method or a chosen one; whether parameter defaults are kept on it; whether
  an unused parameter such as `reason` stays (the September split kept `reason` on both layers).
- XML doc comments: on both members, as `MoveMember` does, or on one.
- Whether the forwarder drops `async` and returns the target's task directly, as `BuildForwarder` does today.
- A target in another project. The layering roadmap wants the new `*Impl` classes in a layer project below the tool projects; a first version
  can create the class beside the source and leave the cross-project step to `MoveFile`.
- Whether DI registration of the new class is in scope (the 12 existing `*Impl` classes are registered in
  `Tools.Basic/ToolServiceCollectionExtensionsBasic.cs:35-46`).
- Test fixtures: the closed `MoveMember` proposal notes that documents with bare names make the compile gate skip a new target file.

## Cost / risk

- A new mutation kind that generates a class, a constructor and a field initialisation; the compile gate limits a wrong result to a refusal.
- One more tool schema; it can sit in an on-demand toolset.
- A new `[McpServerToolType]` class needs its `ToolClassRegistry` entries, a server restart and a fresh chat session.
- `MemberRefactoringEngine` is over 6,000 lines; the orchestration belongs in its own class, not in that file.
- Independent of this proposal, the `ExtractMethod` async defect stays a defect to fix (INBOX, 2026-11-27).

## Related

- [proposal_engines_report_facts_tool_layer_writes_text.md](proposal_engines_report_facts_tool_layer_writes_text.md) (Q85 and its probe entry).
- [proposal_movemember_keep_delegator_and_same_class_calls.md](closed/proposal_movemember_keep_delegator_and_same_class_calls.md) (forwarder
  shape, refusal table, `fieldMap`, `forwarderReceiver`, same-class call rewrite).
- [proposal_parallel_change_deprecated_wrappers.md](proposal_parallel_change_deprecated_wrappers.md) (terminology, wrapper conventions).
- [plan_split_workspace_refactoring_tools_for_di.md](../plans/closed/plan_split_workspace_refactoring_tools_for_di.md) (the manual split and
  its rules).
- `docs/current/INBOX.md`, two lines of 2026-11-27 (`MoveMember` extraction gaps; `ExtractMethod` async defect).
