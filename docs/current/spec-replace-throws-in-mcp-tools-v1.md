# Plan: Replace Throws with Return Strings in [McpServerTool] Methods

## Background

`[McpServerTool]` methods are the public boundary of the MCP server. Any unhandled exception
thrown from these methods cannot be caught by the MCP framework and converted to a meaningful
message — the agent receives a generic error instead. All `[McpServerTool]` methods must return
a string value when an error occurs, rather than throwing.

## Scope

- **In scope**: Only methods decorated with `[McpServerTool]`.
- **Out of scope**: Private helpers, engine methods, and any method without `[McpServerTool]`.
- **Change constraint**: Only change `throw`/`throw new` to `return "message"`. Change method
  return types only where strictly required to allow a string return (two cases noted below).

## Conversion Patterns

| Before | After |
|--------|-------|
| `throw new SomeException("message")` | `return "message";` |
| `?? throw new SomeException("message")` | Split: check null → `return "message";`, then use value |
| `catch (T) { throw; }` (rethrow) | Remove the rethrow catch block entirely |
| `catch (Exception ex) { throw new T($"msg: {ex.Message}", ex); }` | `catch (Exception ex) { return $"msg: {ex.Message}"; }` |
| `_ => throw new SomeException("message")` (switch expression arm) | Convert switch to if/else with `return "message";` |
| `scopeName ?? throw new ArgumentException("msg")` (switch expression arg) | Pre-validate before switch: `if (cond) return "msg";`, use `scopeName!` in switch |

---

## Files and Changes

### 1. `SentinelWorkspaceTools.cs`

#### `Features` — returns `object`

| # | Location | Before | After |
|---|----------|--------|-------|
| 1 | Switch arm `_` | `_ => throw new ArgumentException($"Unknown action '{action}'. Valid: list, get, update.", nameof(action))` | `_ => (object)$"Unknown action '{action}'. Valid: list, get, update."` |

#### `List` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 2 | kind=files guard | `throw new ArgumentException("projectName is required when kind=files.")` | `return "projectName is required when kind=files.";` |
| 3 | project null check | `throw new InvalidOperationException($"Project '{projectName}' not found.")` | `return $"Project '{projectName}' not found.";` |
| 4 | inner catch | `catch (InvalidOperationException) { throw; }` | **Remove this catch block entirely** |
| 5 | outer catch body | `throw new InvalidOperationException($"List files for project '{projectName}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"List files for project '{projectName}' failed: {ex.GetType().Name}: {ex.Message}";` |
| 6 | kind=dependencies guard | `throw new ArgumentException("projectName is required when kind=dependencies.")` | `return "projectName is required when kind=dependencies.";` |
| 7 | unknown kind | `throw new ArgumentException($"Unknown kind '{kind}'. Valid values: projects, files, dependencies.")` | `return $"Unknown kind '{kind}'. Valid values: projects, files, dependencies.";` |

#### `Diagnose` — returns `Task<HealthReport>` ⚠️ return type change required

> **Return type must change to `Task<object>`** to allow returning a string in the error path.

| # | Location | Before | After |
|---|----------|--------|-------|
| 8 | outer catch body | `throw new InvalidOperationException($"Diagnose failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"Diagnose failed: {ex.GetType().Name}: {ex.Message}";` |
| — | signature | `public async Task<HealthReport> Diagnose(...)` | `public async Task<object> Diagnose(...)` |

#### `ProposedChange` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 9  | format=files changes null | `throw new ArgumentException("changes is required when format=files.")` | `return "changes is required when format=files.";` |
| 10 | pre-apply validate catch | `throw new InvalidOperationException($"ProposedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"ProposedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}";` |
| 11 | validate catch | `throw new InvalidOperationException($"ProposedChange validate failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"ProposedChange validate failed: {ex.GetType().Name}: {ex.Message}";` |
| 12 | format=diff guard | `throw new ArgumentException("filePath and unifiedDiff are required when format=diff.")` | `return "filePath and unifiedDiff are required when format=diff.";` |
| 13 | document null check | `throw new InvalidOperationException("File not found.")` | `return "File not found.";` |
| 14 | inner catch | `catch (InvalidOperationException) { throw; }` | **Remove this catch block entirely** |
| 15 | outer diff catch | `throw new InvalidOperationException($"ProposedChange diff apply for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"ProposedChange diff apply for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";` |
| 16 | unknown format/action | `throw new ArgumentException($"Unknown format '{format}' or action '{action}'. Valid formats: files, diff. Valid actions: apply, validate.")` | `return $"Unknown format '{format}' or action '{action}'. Valid formats: files, diff. Valid actions: apply, validate.";` |

#### `StagedChange` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 17 | pre-apply validate catch | `throw new InvalidOperationException($"StagedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"StagedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}";` |
| 18 | unknown action | `throw new ArgumentException($"Unknown action '{action}'. Valid values: apply, get, validate, discard.")` | `return $"Unknown action '{action}'. Valid values: apply, get, validate, discard.";` |

#### `GetDiagnostics` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 19 | scope=file guard | `throw new ArgumentException("scopeName (filePath) is required when scope=file.")` | `return "scopeName (filePath) is required when scope=file.";` |
| 20 | scope=project guard | `throw new ArgumentException("scopeName (projectName) is required when scope=project.")` | `return "scopeName (projectName) is required when scope=project.";` |
| 21 | unknown scope | `throw new ArgumentException($"Unknown scope '{scope}'. Valid values: file, project, solution.")` | `return $"Unknown scope '{scope}'. Valid values: file, project, solution.";` |

#### `GetMethodSource` — returns `Task<string>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 22 | document null | `throw new FileNotFoundException($"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).")` | `return $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).";` |
| 23 | syntax root null | `var root = await document.GetSyntaxRootAsync() ?? throw new InvalidOperationException("Syntax root not found.")` | Split: `var root = await document.GetSyntaxRootAsync(); if (root == null) return "Syntax root not found.";` |
| 24 | method null | `throw new InvalidOperationException($"Method '{methodName}' not found in '{filePath}'.")` | `return $"Method '{methodName}' not found in '{filePath}'.";` |

#### `GetFileOutline` — returns `Task<List<OutlineItem>>` ⚠️ return type change required

> **Return type must change to `Task<object>`** to allow returning a string in the error path.

| # | Location | Before | After |
|---|----------|--------|-------|
| 25 | document null | `throw new FileNotFoundException($"File not found in solution: {normalizedPath} ...")` | `return $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).";` |
| 26 | syntax root null | `var root = await document.GetSyntaxRootAsync() ?? throw new InvalidOperationException("Syntax root not found.")` | Split: `var root = await document.GetSyntaxRootAsync(); if (root == null) return "Syntax root not found.";` |
| — | signature | `public async Task<List<OutlineItem>> GetFileOutline(...)` | `public async Task<object> GetFileOutline(...)` |

---

### 2. `SentinelRefactoringTools.cs`

#### `SyncTypeAndFilename` — returns `Task<string>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 27 | catch body | `throw new InvalidOperationException($"SyncTypeAndFilename for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"SyncTypeAndFilename for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";` |

#### `MoveAllTypesToFiles` — returns `Task<object>`

> Switch expression uses `scopeName ?? throw` args and `_ => throw` arm.
> Convert switch expression to if/else statements with pre-validation.

| # | Location | Before | After |
|---|----------|--------|-------|
| 28 | scope=file guard | `scopeName ?? throw new ArgumentException("scopeName (file path) is required for scope=file.", nameof(scopeName))` | Add before switch: `if (scope == "file" && scopeName is null) return "scopeName (file path) is required for scope=file.";` |
| 29 | scope=project guard | `scopeName ?? throw new ArgumentException("scopeName (project name) is required for scope=project.", nameof(scopeName))` | Add before switch: `if (scope == "project" && scopeName is null) return "scopeName (project name) is required for scope=project.";` |
| 30 | unknown scope | `_ => throw new ArgumentException($"Unknown scope '{scope}'. Valid: file, project, solution.", nameof(scope))` | Convert to final `else` branch: `return $"Unknown scope '{scope}'. Valid: file, project, solution.";` |

The resulting structure is:
```csharp
if (scope == "file" && scopeName is null) return "scopeName (file path) is required for scope=file.";
if (scope == "project" && scopeName is null) return "scopeName (project name) is required for scope=project.";
if (scope == "file")
    return await MoveAllTypesToFilesCore(
        await _refactoringEngine.MoveAllTypesToFilesAsync(scopeName!), autoStage,
        $"Move all types to files in '{Path.GetFileName(scopeName)}'", previewFiles: true);
if (scope == "project")
    return await MoveAllTypesToFilesCore(
        await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(scopeName!), autoStage,
        $"Move all types to files in project '{scopeName}'", previewFiles: false);
if (scope == "solution")
    return await MoveAllTypesToFilesCore(
        await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync(), autoStage,
        "Move all types to files in solution", previewFiles: false);
return $"Unknown scope '{scope}'. Valid: file, project, solution.";
```

#### `ReplaceMember` — returns `Task<string>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 31 | catch body | `throw new InvalidOperationException($"ReplaceMember ...")` | `return $"ReplaceMember ... {ex.GetType().Name}: {ex.Message}";` |

#### `IntroduceParameterObject` — returns `Task<string>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 32 | result null check | `throw new InvalidOperationException($"IntroduceParameterObject failed for '{methodName}' in '{filePath}': file not found in workspace or method not found. Ensure the solution is loaded.")` | `return $"IntroduceParameterObject failed for '{methodName}' in '{filePath}': file not found in workspace or method not found. Ensure the solution is loaded.";` |

#### `ExtractLocalVariable` — returns `Task<string>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 33 | result null check | `throw new InvalidOperationException($"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.")` | `return $"ExtractLocalVariable failed for variable '{variableName}' in '{filePath}': file not found in workspace or context snippet did not match any expression. Ensure the solution is loaded.";` |

#### `ModifyAttribute` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 34 | unknown action | `throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.")` | `return $"Unknown action '{action}'. Valid values: add, remove.";` |

#### `ModifyModifier` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 35 | unknown action | `throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.")` | `return $"Unknown action '{action}'. Valid values: add, remove.";` |

#### `ModifyBaseType` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 36 | unknown action | `throw new ArgumentException($"Unknown action '{action}'. Valid values: add, remove.")` | `return $"Unknown action '{action}'. Valid values: add, remove.";` |

#### `Introduce` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 37 | unknown as | `throw new ArgumentException($"Unknown as '{@as}'. Valid values: localVariable, field, parameter, constant.")` | `return $"Unknown as '{@as}'. Valid values: localVariable, field, parameter, constant.";` |

#### `ExtractMembers` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 38 | as=interface newTypeName guard | `throw new ArgumentException("newTypeName (interface name) is required when as=interface.")` | `return "newTypeName (interface name) is required when as=interface.";` |
| 39 | as=interface catch | `throw new InvalidOperationException($"ExtractMembers as=interface for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"ExtractMembers as=interface for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}";` |
| 40 | as=class memberNames guard | `throw new ArgumentException("memberNames is required when as=class.")` | `return "memberNames is required when as=class.";` |
| 41 | as=class newTypeName guard | `throw new ArgumentException("newTypeName (new class name) is required when as=class.")` | `return "newTypeName (new class name) is required when as=class.";` |
| 42 | as=partial memberNames guard | `throw new ArgumentException("memberNames is required when as=partial.")` | `return "memberNames is required when as=partial.";` |
| 43 | as=superclass newTypeName guard | `throw new ArgumentException("newTypeName (new base class name) is required when as=superclass.")` | `return "newTypeName (new base class name) is required when as=superclass.";` |
| 44 | as=superclass catch | `throw new InvalidOperationException($"ExtractMembers as=superclass for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"ExtractMembers as=superclass for '{newTypeName}' failed: {ex.GetType().Name}: {ex.Message}";` |
| 45 | unknown as | `throw new ArgumentException($"Unknown as '{@as}'. Valid values: interface, class, partial, superclass.")` | `return $"Unknown as '{@as}'. Valid values: interface, class, partial, superclass.";` |

#### `SyncInterface` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 46 | action=implement className guard | `throw new ArgumentException("className is required when action=implement.")` | `return "className is required when action=implement.";` |
| 47 | action=implement result null | `throw new InvalidOperationException($"SyncInterface implement failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.")` | `return $"SyncInterface implement failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.";` |
| 48 | action=sync className guard | `throw new ArgumentException("className is required when action=sync.")` | `return "className is required when action=sync.";` |
| 49 | action=sync result null | `throw new InvalidOperationException($"SyncInterface sync failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.")` | `return $"SyncInterface sync failed for '{className}' implementing '{interfaceName}' in '{filePath}': file not found in workspace, class not found, or interface not found. Ensure the solution is loaded.";` |
| 50 | unknown action | `throw new ArgumentException($"Unknown action '{action}'. Valid values: implement, sync, verify.")` | `return $"Unknown action '{action}'. Valid values: implement, sync, verify.";` |

#### `Inline` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 51 | kind=method catch | `throw new InvalidOperationException($"Inline method '{targetName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex)` | `return $"Inline method '{targetName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}";` |
| 52 | kind=parameter methodName guard | `throw new ArgumentException("methodName is required when kind=parameter.")` | `return "methodName is required when kind=parameter.";` |
| 53 | unknown kind | `throw new ArgumentException($"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.")` | `return $"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.";` |

#### `AddMember` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 54 | unknown position | `throw new ArgumentException($"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.")` | `return $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.";` |

#### `AddMemberTyped` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 55 | unknown kind | `throw new ArgumentException($"Unknown kind '{kind}'. Valid values: property, field.")` | `return $"Unknown kind '{kind}'. Valid values: property, field.";` |

#### `WrapRange` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 56 | wrapper=using name guard | `throw new ArgumentException("name (disposalName) is required when wrapper=using.")` | `return "name (disposalName) is required when wrapper=using.";` |
| 57 | wrapper=region name guard | `throw new ArgumentException("name (regionName) is required when wrapper=region.")` | `return "name (regionName) is required when wrapper=region.";` |
| 58 | unknown wrapper | `throw new ArgumentException($"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.")` | `return $"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.";` |

#### `MoveType` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 59 | unknown destination | `throw new ArgumentException($"Unknown destination '{destination}'. Valid values: ownFile, outerScope.")` | `return $"Unknown destination '{destination}'. Valid values: ownFile, outerScope.";` |

---

### 3. `SentinelIntelligenceTools.cs`

#### `InspectSymbol` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 60 | symbol null | `return await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter) ?? throw new InvalidOperationException("Symbol info not found.")` | Split: `var info = await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter); if (info == null) return "Symbol info not found."; return info;` |

#### `GetCallGraph` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 61 | forward fwd null | `throw new InvalidOperationException($"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). Use get_document_outline to list available methods in the file.")` | `return $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). Use get_document_outline to list available methods in the file.";` |
| 62 | reverse rev null | Same throw (different variable) | Same replacement |

---

### 4. `SentinelScanTools.cs`

#### `Scan` — returns `Task<object>`

| # | Location | Before | After |
|---|----------|--------|-------|
| 63 | duplicate_blocks_in_hierarchy scopeName guard | `throw new ArgumentException("duplicate_blocks_in_hierarchy requires scopeName to be the root type name.")` | `return "duplicate_blocks_in_hierarchy requires scopeName to be the root type name.";` |

---

### 5. `DocumentationTools.cs`

#### `ProjectDoc` — returns `object`

> The `?? throw` patterns appear inside a switch expression arm argument — they cannot be replaced
> inline. Pre-validate `content` before the switch expression, then use `content!` in the arm.

| # | Location | Before | After |
|---|----------|--------|-------|
| 64 | "write" content null | `WriteFile(subdir, name, content ?? throw new ArgumentException("content is required for action=write.", nameof(content)))` | Add before switch: `if (action == "write" && content is null) return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=write." };` — then change arm to `"write" => WriteFile(subdir, name, content!),` |
| 65 | "append" content null | `WriteFile(subdir, name, content ?? throw new ArgumentException("content is required for action=append.", nameof(content)), append: true)` | Add before switch: `if (action == "append" && content is null) return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=append." };` — then change arm to `"append" => docType == "completed_work" ? WriteFile(subdir, name, content!, append: true) : (object)new DocWriteResult { ... },` |

---

## Summary

| File | Method | Throws | Notes |
|------|--------|--------|-------|
| SentinelWorkspaceTools.cs | `Features` | 1 | switch arm |
| SentinelWorkspaceTools.cs | `List` | 6 | includes rethrow removal |
| SentinelWorkspaceTools.cs | `Diagnose` | 1 | **return type: `Task<HealthReport>` → `Task<object>`** |
| SentinelWorkspaceTools.cs | `ProposedChange` | 8 | includes rethrow removal |
| SentinelWorkspaceTools.cs | `StagedChange` | 2 | |
| SentinelWorkspaceTools.cs | `GetDiagnostics` | 3 | |
| SentinelWorkspaceTools.cs | `GetMethodSource` | 3 | includes `?? throw` split |
| SentinelWorkspaceTools.cs | `GetFileOutline` | 2 | **return type: `Task<List<OutlineItem>>` → `Task<object>`**; includes `?? throw` split |
| SentinelRefactoringTools.cs | `SyncTypeAndFilename` | 1 | |
| SentinelRefactoringTools.cs | `MoveAllTypesToFiles` | 3 | switch expression → if/else |
| SentinelRefactoringTools.cs | `ReplaceMember` | 1 | |
| SentinelRefactoringTools.cs | `IntroduceParameterObject` | 1 | |
| SentinelRefactoringTools.cs | `ExtractLocalVariable` | 1 | |
| SentinelRefactoringTools.cs | `ModifyAttribute` | 1 | |
| SentinelRefactoringTools.cs | `ModifyModifier` | 1 | |
| SentinelRefactoringTools.cs | `ModifyBaseType` | 1 | |
| SentinelRefactoringTools.cs | `Introduce` | 1 | |
| SentinelRefactoringTools.cs | `ExtractMembers` | 8 | |
| SentinelRefactoringTools.cs | `SyncInterface` | 5 | |
| SentinelRefactoringTools.cs | `Inline` | 3 | |
| SentinelRefactoringTools.cs | `AddMember` | 1 | |
| SentinelRefactoringTools.cs | `AddMemberTyped` | 1 | |
| SentinelRefactoringTools.cs | `WrapRange` | 3 | |
| SentinelRefactoringTools.cs | `MoveType` | 1 | |
| SentinelIntelligenceTools.cs | `InspectSymbol` | 1 | `?? throw` split |
| SentinelIntelligenceTools.cs | `GetCallGraph` | 2 | |
| SentinelScanTools.cs | `Scan` | 1 | |
| DocumentationTools.cs | `ProjectDoc` | 2 | `?? throw` in switch expression → pre-validate |
| **TOTAL** | **28 methods** | **65 throws** | |

## Out of Scope (throws NOT in [McpServerTool] methods)

The following throws exist in the same files but are in **private** helper methods that are
called by `AsyncMigrate`. They must **not** be changed:

- `SentinelQualityTools.cs` line ~689: `PropagateCancellationToken` (private)
- `SentinelQualityTools.cs` line ~1075: `RunUplift` (private)
- `SentinelQualityTools.cs` line ~1325: `FlagMigrationCandidates` (private)
- `SentinelQualityTools.cs` line ~1716: `Asyncify` (private)

The throw at line ~1845 in `AsyncMigrate` (`[McpServerTool]`) is inside a `try` block that is
caught by the method's own `catch (ArgumentException)` handler, which returns a `MigrationResult`
error — it never propagates to the caller. **No change needed.**
