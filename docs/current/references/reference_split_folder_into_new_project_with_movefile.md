# Reference: split a folder into a new project with CreateProject, MoveFile and ChangeNamespace

**Status:** CURRENT 2026-11-25. Verified against MoveFileTool and ChangeNamespaceTool at commit d62e2e89; supersedes the interim link-stand-in recipe.

## Purpose

The procedure for moving a folder of .cs files out of one project into a new project. Consult it before splitting a folder into its own project.

**Superseded:** the interim recipe is no longer the way to do this. It wrote a hand-written csproj with a temporary `<Compile Include="..\Old\Folder\**\*.cs" LinkBase="Folder" />` link stand-in, then ran one MoveFile per file, then removed the stand-in (about 121 calls for the Git split). Use the steps below instead.

## How it works

**Toolsets.** ChangeNamespace is in the `moveExtract` toolset and CreateProject is in `projectAdmin`; both are gated off at startup. Enable them with:

- `McpToolsetControl(toolSet: moveExtract, enabled: true)`
- `McpToolsetControl(toolSet: projectAdmin, enabled: true)`

MoveFile is on the default surface.

**Step 1 - create the project.** `CreateProject(projectName, projectType)` creates a new project and adds it to the loaded solution. Put the destination folder inside that project's directory.

**Step 2 - move the files.** `MoveFile(moves: [{sourcePath, destinationPath}], namespaceHandling: keep, addProjectReferences, addInternalsVisibleTo, dryRun)`

- `moves` is a required list of pairs; a single move is a list of one.
- A pair may name a folder. The folder's contents land at the destination path, subfolders kept, merging into an existing folder. A same-name file refuses the call. File and folder pairs may mix.
- Up to 200 files per call; a larger set is refused with a message that names splitting into smaller calls.
- One build check and one undo (`UndoChange`) cover the whole set.
- `dryRun: true` returns the expanded file-by-file pairs and the facts, and writes nothing.
- `addProjectReferences` and `addInternalsVisibleTo` default to false. With false, each needed edge is only reported as a fact. With true, the edge is added to the project file in the same undoable change. A ProjectReference cycle is refused.
- `namespaceHandling: keep` moves the text unchanged, so a .cs namespace stays as written.

Recommended order: call with `dryRun: true`, read the facts, then make the real call.

**Facts returned as findings, and what to do with each:**

| Fact | Meaning | Action |
| --- | --- | --- |
| MissingProjectReference | "project 'A' needs a ProjectReference to 'B': <reason>" | Repeat with `addProjectReferences: true`, or add the reference by hand. |
| MissingInternalsVisibleTo | "project 'A' needs to grant InternalsVisibleTo 'B': <reason>" | Repeat with `addInternalsVisibleTo: true`. |
| MissingPackageReference | "the moved file '<path>' uses '<symbol>' from '<assembly>', which project '<dest>' does not reference; package references are not modelled" | Add the PackageReference to the destination project file by hand, run `LoadSolution(forceReload: true)` (restores before loading), then re-run the call. |
| SourceCsprojItemNamesFolder | "not updated: the project file '<csproj>' has an item that names the moved folder: <line>" | The tool does not edit source csproj items for a moved folder. Fix that item by hand after the move. |
| EmptySourceFolderLeft | "the source folder '<folder>' is left in place; moving files does not remove folders" | Informational. Delete the empty folder if wanted. |
| KeptNamespace | The file keeps the namespace it declares, which differs from the folder-derived one | Fix with ChangeNamespace (step 3). |
| CsprojEdgeAdded | "added a ProjectReference to 'B' to the project file of 'A'" (dryRun: "would add") | None. |
| ReloadPending | "the project file was written; a background reload of the workspace follows, and the next call waits for it." | None. A very long reload makes the next call fail with a load-timeout error that names the background reload; a retry works. |
| EdgeWouldCloseCycle (refusal) | "a ProjectReference from 'A' to 'B' would close a project cycle, because 'B' already depends on 'A'" | Move the files that use each other into one project, or move a different set. |
| Destination in no loaded project (refusal, InvalidArgument) | "the destination '<path>' is inside no loaded project" with the option "LoadSolution with forceReload after adding a project that covers the destination folder" | Run `LoadSolution(forceReload: true)` (for example after CreateProject, or after a hand-written csproj plus `dotnet sln add`), then repeat the MoveFile call. |
| WorkspaceChangedDuringGate | "the workspace changed during the call; the check ran without the added references; a retry recomputes them." | The call refused safely. Repeat it. |

Toggle-off edge facts: with `addProjectReferences` or `addInternalsVisibleTo` false, the MissingProjectReference and MissingInternalsVisibleTo facts name the parameter that adds the edge.

**Step 3 - fix the namespaces.** `ChangeNamespace(filePath, newNamespace | matchFolder, dryRun)` (`moveExtract` toolset).

- `filePath` is a .cs file or a folder. A folder covers every .cs under it, subfolders included. Globs are not accepted.
- Give exactly one of `newNamespace` (one namespace for every file) or `matchFolder: true` (each file gets the namespace its folder path gives: project root namespace plus folders). With `matchFolder`, every file must expect the same namespace.
- The tool moves the namespace declaration and rewrites every reference to the types, in one build check. It is undoable with `UndoChange`.
- A partial type needs all of its parts in the set.
- Call with `dryRun: true` first. Facts come back as findings with source "ChangeNamespace".
- If every file already declares the target namespace and nothing references the types, the result is "No file changed".

Example order for a split: MoveFile folder pair with `namespaceHandling: keep`, then ChangeNamespace on the new folder with `matchFolder: true` (or `newNamespace`).

## Usage

1. `McpToolsetControl(toolSet: projectAdmin, enabled: true)`, then `CreateProject(projectName, projectType)` with the destination folder inside the new project's directory.
2. `MoveFile(moves: [...], namespaceHandling: keep, dryRun: true)`. Read the facts, act on each one, then repeat with `dryRun` omitted.
3. `McpToolsetControl(toolSet: moveExtract, enabled: true)`, then `ChangeNamespace(filePath: <new folder>, matchFolder: true, dryRun: true)`, then the real call.
4. Fix any SourceCsprojItemNamesFolder item by hand, and delete the EmptySourceFolderLeft folder when wanted.

## Gotchas

- Run one `dryRun` first; it is the only way to see the facts before any write.
- The cap is 200 files per MoveFile call. Split larger sets into several calls.
- After a MoveFile that writes a project file, the next call may wait for the background reload.
- UndoChange reverts the csproj edits made with the move (same changeId).
- MoveFile only reports "not updated" for source csproj items; it does not rewrite them.
- Package references are reported as facts only; the tool does not add them.
- A move whose destination folder is inside no loaded project is refused for .cs files. A non-.cs file under no project still moves.
