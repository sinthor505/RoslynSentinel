# `ChangeSignature` real-Roslyn-engine migration blocked — the entire change-signature pipeline in Microsoft.CodeAnalysis(.CSharp).Features 5.9.0 is `internal`

**Status:** OPEN — found 2026-09-13, while implementing
`docs/current/proposal_changesignature_full_roslyn_service.md`. Stopped per CLAUDE.md's "a tool/API
failure is a blocking finding" doctrine; no workaround attempted.

## What was being attempted

Implementing `docs/current/proposal_changesignature_full_roslyn_service.md`: replacing
RoslynSentinel's hand-rolled `ChangeSignatureAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs:90-223`)
with one backed by Roslyn's real change-signature service, so call sites using named arguments or
optional/`params` arguments — currently skipped with a warning rather than rewritten — would be
handled correctly by the real VS engine instead. The proposal doc itself flagged the risk that
motivates this writeup as an explicit open item:

> Which extension point actually resolves `IChangeSignatureService` outside a full VS host.

The task instructions for this session said explicitly that if the service "cannot be
resolved/constructed outside a full VS Workspace/MEF host... that is exactly the kind of blocking
finding CLAUDE.md describes: stop, write the blocker doc... Do not fall back to hand-rolled syntax
manipulation as a workaround."

## Work completed before hitting the blocker (kept, not reverted)

1. Added `<PackageReference Include="Microsoft.CodeAnalysis.Features" Version="5.9.0" />` to
   `RoslynSentinel.Basic/RoslynSentinel.Basic.csproj`, alongside the existing
   `Microsoft.CodeAnalysis.CSharp.Workspaces`/`Workspaces.MSBuild` 5.9.0 references. Harmless,
   legitimate addition; kept because the research below required the package restored locally to
   inspect its actual public surface.
2. Restored the package via `dotnet restore` and located the assemblies in the NuGet cache:
   `C:\Users\Administrator\.nuget\packages\microsoft.codeanalysis.features\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.Features.dll`
   and the sibling
   `C:\Users\Administrator\.nuget\packages\microsoft.codeanalysis.csharp.features\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.CSharp.Features.dll`.
   The second package is **not** added to any `.csproj` — it was restored only into a throwaway
   probe project for inspection, since deleted.
3. Decompiled both DLLs with `ilspycmd` (already installed as a dotnet global tool) to verify the
   real 5.9.0 API surface directly, rather than trusting the proposal doc's assumed type names
   (`IChangeSignatureService`, `SignatureChange`, `AddedParameterOrExistingIndex`), which were drawn
   from older documentation/blog posts, not this pinned version.

## The exact finding — decompiled declarations, not a hypothesis

Every type in the real change-signature pipeline is declared `internal` in
Microsoft.CodeAnalysis.Features 5.9.0 / Microsoft.CodeAnalysis.CSharp.Features 5.9.0:

- `Microsoft.CodeAnalysis.ChangeSignature.AbstractChangeSignatureService`
  (`Microsoft.CodeAnalysis.Features.dll`) —
  `internal abstract class AbstractChangeSignatureService : ILanguageService`. Its core method,
  `public abstract SyntaxNode ChangeSignature(SemanticDocument document, ISymbol declarationSymbol, SyntaxNode potentiallyUpdatedNode, SyntaxNode originalNode, SignatureChange signaturePermutation, LineFormattingOptions lineFormattingOptions, CancellationToken cancellationToken)`,
  is itself `public`, but the containing class is `internal`, so it is unreachable from outside the
  assembly regardless of the member's own accessibility.
- `Microsoft.CodeAnalysis.CSharp.ChangeSignature.CSharpChangeSignatureService`
  (`Microsoft.CodeAnalysis.CSharp.Features.dll`) —
  `internal sealed class CSharpChangeSignatureService : AbstractChangeSignatureService`, exported via
  `[ExportLanguageService]`/`[Shared]` for MEF composition only.
- `Microsoft.CodeAnalysis.ChangeSignature.SignatureChange` — `internal sealed class`. This is the
  request/result DTO the proposal doc assumed would be the public entry shape; it does not exist as
  a public type anywhere in 5.9.0.
- `Microsoft.CodeAnalysis.ChangeSignature.ParameterConfiguration`, `Parameter`, `ExistingParameter`,
  `AddedParameter` — all `internal` (sealed or abstract).
- `Microsoft.CodeAnalysis.ChangeSignature.ChangeSignatureResult` — `internal sealed class`. Note:
  this is a *different* type from RoslynSentinel's own `ChangeSignatureResult` record already
  defined in `RefactoringEngine.cs` — same name, unrelated, both exist simultaneously once the
  package is referenced.
- `Microsoft.CodeAnalysis.ChangeSignature.IChangeSignatureOptionsService` —
  `internal interface IChangeSignatureOptionsService : IWorkspaceService` with method
  `ChangeSignatureOptionsResult? GetChangeSignatureOptions(SemanticDocument document, int positionForTypeBinding, ISymbol symbol, ParameterConfiguration parameters)`
  — an internal interface whose method takes an internal DTO (`ParameterConfiguration`) as a
  parameter, so even reflection-based invocation would require constructing that internal type
  (whose factory, `Create`, is itself internal-only) just to call it.
- `Microsoft.CodeAnalysis.ChangeSignature.ChangeSignatureCodeRefactoringProvider` —
  `internal sealed class`, `[ExportCodeRefactoringProvider("C#", ...)] [Shared]` — the actual VS
  Quick Actions entry point, MEF-composed only inside a full VS/IDE host.
- **There is no `IChangeSignatureService` type at all** in 5.9.0. The proposal doc's assumed
  interface name does not exist in this version; the closest analog is the internal
  `AbstractChangeSignatureService`/`CSharpChangeSignatureService` pair above.

Decisive evidence beyond the `internal` modifiers themselves: decompiling the assembly-level
attributes shows `InternalsVisibleTo`/`RestrictedInternalsVisibleTo` grants access to a fixed
allowlist of Microsoft's own strong-named assemblies only — e.g. `AnalyzerRunner`,
`Microsoft.CodeAnalysis.LiveUnitTesting.Orchestrator`,
`Microsoft.CodeAnalysis.UnitTesting.SourceBasedTestDiscovery(.Core/.UnitTests)`,
`Microsoft.VisualStudio.IntelliCode(.CSharp/.CSharp.Extraction)`,
`Microsoft.VisualStudio.TestWindow.Core` — gated by strong-name public key, not merely assembly
name. RoslynSentinel's assemblies are not on this list and cannot be added to it without Microsoft's
own private signing key, which is not available here.

## Where it happened

- File under migration: `RoslynSentinel.Basic/RefactoringEngine.cs:90-223` (`ChangeSignatureAsync`) —
  no change made to this file; the blocker was found during research before touching it.
- Package reference added: `RoslynSentinel.Basic/RoslynSentinel.Basic.csproj`.
- Evidence source: decompiled output of
  `microsoft.codeanalysis.features\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.Features.dll` and
  `microsoft.codeanalysis.csharp.features\5.9.0\lib\net10.0\Microsoft.CodeAnalysis.CSharp.Features.dll`
  via `ilspycmd`, both restored from the NuGet cache at
  `C:\Users\Administrator\.nuget\packages\`.
- Proposal doc: `docs/current/proposal_changesignature_full_roslyn_service.md`, open item
  "Which extension point actually resolves `IChangeSignatureService` outside a full VS host."

## Root cause

Confirmed, not a surface-level guess: the entire change-signature implementation surface in
Microsoft.CodeAnalysis.Features/CSharp.Features 5.9.0 is `internal`, and the assemblies'
`RestrictedInternalsVisibleTo` allowlist does not include RoslynSentinel or any general-purpose
consumer — only a fixed set of Microsoft's own strong-named tooling assemblies. This is a hard
access boundary in the vendored NuGet package itself, not a RoslynSentinel code defect, not a
tool-schema problem, and not something fixable by changing how RoslynSentinel calls anything.

There is no reflection workaround that would not itself violate CLAUDE.md's dog-fooding/blocking-
finding doctrine: reflecting into another vendor's internal, version-unstable implementation types
is precisely the kind of fragile hand-rolled workaround the migration exists to get away from. Doing
so would defeat the point of the migration (stability/correctness from a real, supported API) even
if it happened to compile and run today.

**Assumption, not independently verified:** the internal/MEF-only design of Roslyn's Features layer
is long-standing across versions, not a 5.9.0-specific regression — but this session did not check
other 5.x versions to confirm the surface is the same there. Flagged as an open question below, not
asserted as fact.

## What unblocks it

The proposal as written is **not implementable** against Microsoft.CodeAnalysis.Features/CSharp.Features
5.9.0 — the described public entry points (`IChangeSignatureService`, `SignatureChange`) do not
exist as public/accessible types in this version. This needs a human decision on direction before
any further implementation work proceeds. Options for whoever picks this up next (listed only —
none implemented, none recommended, per task scope):

1. Abandon driving the real VS engine directly; keep the hand-rolled engine, but incrementally fix
   its two specific known gaps (named-argument call sites, optional/`params` call sites) without
   adopting Roslyn's internal service.
2. Host a real MEF composition inside an assembly with a strong name matching one of the
   `RestrictedInternalsVisibleTo` allowlist entries — not realistic without Microsoft's cooperation
   or signing key.
3. Shell out to a separate process that already has legitimate access to the real engine (e.g. a
   VS/OmniSharp/Roslyn LSP instance's own Change Signature command via its external-facing
   protocol, if one exists) — a materially different integration shape, would need its own design
   doc.
4. Check whether a different (older or newer) Roslyn/Features version exposes any of this publicly
   — unverified in this session, worth a quick check before ruling it out entirely.

## Related

- `docs/current/proposal_changesignature_full_roslyn_service.md` — the design this blocks; its own
  "Which extension point actually resolves `IChangeSignatureService`" open item is the exact risk
  this doc confirms.
- `RoslynSentinel.Basic/RefactoringEngine.cs:90-223` — the existing hand-rolled `ChangeSignatureAsync`
  this proposal intended to replace; unchanged by this session.
- `RoslynSentinel.Basic/RoslynSentinel.Basic.csproj` — now references
  `Microsoft.CodeAnalysis.Features` 5.9.0 (kept; harmless, needed for the research above).
