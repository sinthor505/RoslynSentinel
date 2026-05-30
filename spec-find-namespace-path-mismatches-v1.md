# Proposed Tool — `find_namespace_path_mismatches`
<!-- v1 -->

Spec generated 2026-05-28. Append to PROPOSED_TOOLS.md.

---

## Context

During the Avaal Express async migration, files with mismatched namespace declarations and physical paths caused unexpected write-target confusion. A file physically at `Avaal.Forms\SomeForm.cs` but declaring `namespace Avaal.Orders` can be found two ways by Roslyn — by physical path (workspace document identity) and by namespace/type name (semantic identity). When those diverge, tools that resolve types by namespace may write to a different path than the file's actual location, producing orphaned copies and potentially blanked originals.

The mismatch originated from an earlier namespace sync that updated namespace declarations without moving files to matching folder locations. The inconsistency was dormant until migration tools performed writes based on semantic (namespace) resolution rather than path resolution.

This tool detects that class of problem before it can affect batch operations.

---

## Proposal — `find_namespace_path_mismatches`

### Priority: HIGH

Pre-flight diagnostic. Low implementation cost (syntax-only scan, no compilation). High value for any codebase that has been through a refactor, monolith split, or namespace reorganization.

### Problem

Roslyn's workspace document model is **path-based**: document identity is the physical file path. Semantic analysis is **symbol-based**: type resolution follows namespace declarations. When a file's declared namespace doesn't match its folder path, the two resolution strategies disagree on where the type lives.

Consequences:
- `SymbolFinder.FindReferencesAsync` may return document paths that differ from the physical location
- `TryApplyChanges` writes to the path Roslyn believes is correct (from semantic resolution), not the path the file actually lives at
- Batch tools that apply many changes across many files may silently write to wrong locations
- Original files may be blanked when the workspace believes it has updated them via a path that resolves to a different document

### Proposed tool signature

```csharp
[McpServerTool]
[Description(
    "Scans the loaded solution for source files where the declared namespace does not match " +
    "the file's folder path relative to its project root. Mismatches indicate that Roslyn's " +
    "path-based document identity and namespace-based type identity are out of sync, which can " +
    "cause batch refactoring tools to write changes to incorrect file locations. " +
    "Syntax-only scan — no compilation required. Safe to run at any time including when the " +
    "build is broken. Returns findings grouped by severity: Error (duplicate type names exist " +
    "across mismatched paths — immediate risk), Warning (mismatch exists but no duplicate " +
    "detected — latent risk). Optionally scoped to a single project.")]
public async Task<NamespacePathMismatchReport> FindNamespacePathMismatches(
    string? projectName = null)
```

### Return type

```csharp
public class NamespacePathMismatchReport
{
    public List<NamespacePathMismatch> Errors    { get; set; }   // duplicate type names detected — immediate risk
    public List<NamespacePathMismatch> Warnings  { get; set; }   // mismatch only — latent risk
    public int    TotalFiles           { get; set; }   // total files scanned
    public int    MismatchCount        { get; set; }   // Errors.Count + Warnings.Count
    public bool   IsClean              { get; set; }   // true if MismatchCount == 0
    public string Summary              { get; set; }   // human-readable one-liner
}

public class NamespacePathMismatch
{
    public string FilePath             { get; set; }   // physical path on disk
    public string ProjectName          { get; set; }
    public string DeclaredNamespace    { get; set; }   // from the namespace declaration in source
    public string ExpectedNamespace    { get; set; }   // derived from folder path relative to project root
    public string Severity             { get; set; }   // "Error" or "Warning"
    public string Reason               { get; set; }   // see Severity rules below
    public List<string> ConflictingFiles { get; set; } // other files that declare the same type (Error only)
}
```

### Severity rules

**Error** — `Severity = "Error"`, `Reason = "DuplicateTypeAtMismatchedPath"`:
- A file at path A declares namespace N, AND another file exists at the path that *would* correspond to namespace N
- This means Roslyn's semantic resolution and path resolution disagree on where the type lives, and both locations have content
- Batch write operations against this type have undefined target behavior

**Warning** — `Severity = "Warning"`:

| Reason | Condition |
|---|---|
| `"NamespaceFolderMismatch"` | Declared namespace doesn't match folder path; no duplicate detected |
| `"MultipleNamespacesInFile"` | File contains more than one namespace declaration (uncommon but legal; creates ambiguity) |
| `"GlobalNamespace"` | File has no namespace declaration; folder path implies a non-global namespace |
| `"PartialClassAcrossNamespaces"` | `partial class` with the same name declared in different namespaces across files |

### Implementation notes

**Namespace-to-path convention:**

The expected namespace for a file is derived by:
1. Take the file's path relative to the project root (the folder containing the `.csproj`)
2. Replace directory separators with `.`
3. Prepend the project's root namespace (from `<RootNamespace>` in the `.csproj`, or the project name if not set)
4. Strip the `.cs` extension

Example:
- Project root: `C:\repos\Avaal3\Main\Avaal.Forms\`
- File path: `C:\repos\Avaal3\Main\Avaal.Forms\Dispatch\DispatchBoardForm.cs`
- Relative path: `Dispatch\DispatchBoardForm.cs`
- Root namespace: `Avaal.Forms`
- Expected namespace: `Avaal.Forms.Dispatch`
- If declared namespace is `Avaal.Orders.Dispatch` → **mismatch**

**Syntax-only scan — no SemanticModel needed:**

Use `SyntaxTree.GetRoot().DescendantNodes().OfType<NamespaceDeclarationSyntax>()` (and `FileScopedNamespaceDeclarationSyntax` for C# 10+ file-scoped namespaces). Extract the namespace name from the syntax node directly. No compilation, no semantic analysis.

This is intentional: the scan must work even when the build is broken (which is common mid-migration).

**Duplicate detection:**

For Error severity, cross-reference: does any other document in the solution declare the same top-level type names under a namespace that *would* resolve to this file's path? Use syntax-level type name extraction (`ClassDeclarationSyntax`, `InterfaceDeclarationSyntax`, `EnumDeclarationSyntax`, `StructDeclarationSyntax`) — no semantic model needed.

**Root namespace extraction:**

Read the project's `<RootNamespace>` MSBuild property from the `.csproj` XML. Fall back to the project name (the `.csproj` filename without extension) if `<RootNamespace>` is not set. Handle both SDK-style and legacy `.csproj` formats.

**Scope:**

When `projectName` is provided, scan only documents in that project. When null, scan all projects in the solution. Skip generated files (`.g.cs`, `.generated.cs`, `*.Designer.cs`) — these are expected to have non-standard namespace patterns.

### Files to modify

- `RoslynSentinel.Server/AnalysisEngine.cs` — add `FindNamespacePathMismatchesAsync(Solution, string? projectName)` returning `NamespacePathMismatchReport`
- `RoslynSentinel.Server/SentinelQualityTools.cs` — add `[McpServerTool]` registration

### Usage in agent workflows

**Pre-flight check before any batch operation on an unfamiliar codebase:**

```
find_namespace_path_mismatches()
```

If `IsClean = false`:
- For `Errors`: do not proceed with batch operations until resolved. Files with duplicate type names at mismatched paths must be reconciled (move the file, fix the namespace, or delete the orphan) before Roslyn's semantic resolution is trustworthy.
- For `Warnings`: proceed with caution. Add the mismatched files to a manual-review list. Prefer path-based tool calls over namespace-based resolution for affected files.

**Integration into `get_comprehensive_health_report`:**

Include `find_namespace_path_mismatches` results in the health report output. A clean solution should report `IsClean = true`. Namespace/path mismatches are a code hygiene issue independently of migration work.

**Recommended addition to agent instructions:**

```
At the start of any session that will perform batch file writes, call 
find_namespace_path_mismatches() and confirm IsClean = true before proceeding. 
If Errors are present, STOP and report. If Warnings are present, note the 
affected files and avoid batch operations targeting them until resolved.
```

### Tests required

1. **Clean solution** — all namespaces match folder paths → `IsClean = true`, empty lists
2. **Single warning** — one file with namespace/folder mismatch, no duplicate → appears in `Warnings` with `NamespaceFolderMismatch`
3. **Error case** — file at `Avaal.Forms\SomeForm.cs` declares `namespace Avaal.Orders` AND `Avaal.Orders\SomeForm.cs` exists with same type name → appears in `Errors` with `DuplicateTypeAtMismatchedPath`, `ConflictingFiles` populated
4. **Generated files skipped** — `*.Designer.cs` and `*.g.cs` excluded from results
5. **Project scope filter** — `projectName` provided → only that project's files scanned
6. **Multiple namespaces in one file** → `MultipleNamespacesInFile` warning
7. **Global namespace** — file with no namespace declaration in a project with root namespace → `GlobalNamespace` warning
8. **File-scoped namespace syntax** — C# 10 `namespace Avaal.Forms;` syntax parsed correctly
9. **Root namespace from csproj** — `<RootNamespace>` read correctly from both SDK-style and legacy project formats
10. **Idempotency** — running twice returns identical results

### Implementation order relative to other proposals

Run before implementing any new batch tools. This is a diagnostic, not a transformation — it is always safe to add and never modifies files. Implement before the next batch operation phase on any codebase that has had namespace reorganization.

---

## Key files in RoslynSentinel

- `RoslynSentinel.Server/SentinelQualityTools.cs` — all `[McpServerTool]` registrations
- `RoslynSentinel.Server/AnalysisEngine.cs` — home for new analysis method; see existing detection patterns for reference
- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` — `GetWorkspaceHealth` and `GetComprehensiveHealthReport` for integration point

<!-- v1 -->
