# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent [Model Context Protocol (MCP)](https://spec.modelcontextprotocol.io/) server that gives AI agents **compiler-grade intelligence** over .NET solutions. Rather than asking an AI to guess what a refactored file should look like, Roslyn Sentinel uses the **official Roslyn compiler APIs** to perform every rename, move, extraction, and modernization with the same semantic accuracy as Visual Studio itself — and then validates the result with a real compilation pass before touching a single file on disk.

---

## 📦 Installation

### 1. Build from Source

```bash
git clone https://github.com/rhale78/RoslynSentinel
cd RoslynSentinel
dotnet publish RoslynSentinel.Server/RoslynSentinel.Server.csproj \
  -c Release -o ./publish
```

This produces:
- **Windows:** `./publish/RoslynSentinel.Server.exe`
- **macOS/Linux:** `dotnet ./publish/RoslynSentinel.Server.dll`

> **Requirements:** .NET 10 SDK or later  
> **Windows shortcut:** Run `.\scripts\install.ps1` — builds the server and prints ready-to-paste config snippets for Claude Desktop.

---

### 2. Configure Your AI Assistant

Replace `/path/to/RoslynSentinel` with the actual path to your cloned repository.

#### GitHub Copilot (VS Code)

Edit `~/.copilot/mcp-config.json` (create if it doesn't exist):

```json
{
  "servers": {
    "roslyn-sentinel": {
      "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe"
    }
  }
}
```

> **Windows tip:** Use `\\` as path separator: `"C:\\Dev\\RoslynSentinel\\publish\\RoslynSentinel.Server.exe"`
> **macOS/Linux tip:** Use `"command": "dotnet"` and `"args": ["/path/to/publish/RoslynSentinel.Server.dll"]`

#### Claude Desktop

Edit `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows):

```json
{
  "mcpServers": {
    "roslyn-sentinel": {
      "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe"
    }
  }
}
```

#### Cursor

Open Cursor Settings → MCP, or edit `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "roslyn-sentinel": {
      "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe"
    }
  }
}
```

#### VS Code (MCP workspace config)

Create `.vscode/mcp.json` in your project:

```json
{
  "servers": {
    "roslyn-sentinel": {
      "type": "stdio",
      "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe"
    }
  }
}
```

#### Cline (VS Code Extension)

In VS Code settings, add to `cline.mcpServers`:

```json
{
  "roslyn-sentinel": {
    "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe",
    "args": []
  }
}
```

#### Continue.dev

Edit `~/.continue/config.json`:

```json
{
  "experimental": {
    "modelContextProtocolServers": [
      {
        "transport": {
          "type": "stdio",
          "command": "/path/to/RoslynSentinel/publish/RoslynSentinel.Server.exe"
        }
      }
    ]
  }
}
```

---

### 3. Verify

After configuring, reload your AI assistant and run:

```
Load my solution: /path/to/MySolution.sln
```

You should see Roslyn Sentinel confirm the solution is loaded and list available projects.

---

### 4. Running Real-Solution Integration Tests (Optional)

The test suite includes batteries that load a real .NET solution for smoke testing.
To run them, set the `ROSLYN_SENTINEL_TEST_SLN` environment variable:

```bash
# Windows (PowerShell)
$env:ROSLYN_SENTINEL_TEST_SLN = "C:\Dev\MySolution\MySolution.sln"

# macOS/Linux
export ROSLYN_SENTINEL_TEST_SLN=/home/user/dev/MySolution/MySolution.sln

dotnet test RoslynSentinel.Tests/RoslynSentinel.Tests.csproj
```

Without the env var, real-solution tests are automatically skipped (`Assert.Ignore`) — unit tests run normally.

---

## 📚 Documentation

| Document | Purpose |
|----------|---------|
| **[README.md](./README.md)** | High-level overview (this file) |
| **[TOOL_DOCUMENTATION.md](./TOOL_DOCUMENTATION.md)** | Before/after code examples for every tool |
| [UNFINISHED.md](./UNFINISHED.md) | Full development history and session notes |
| [UNFINISHED_FEATURES.md](./UNFINISHED_FEATURES.md) | Backlog, deferred features, known limitations |
| [DOCUMENTATION_INDEX.md](./DOCUMENTATION_INDEX.md) | Navigation guide for all docs |

---

## 🧠 Why Roslyn Sentinel Instead of Raw AI Edits?

When an AI agent edits code manually it is doing **text manipulation**, not semantic manipulation. That creates a class of problems that Roslyn Sentinel eliminates entirely:

| Problem with raw AI edits | How Roslyn Sentinel fixes it |
|---|---|
| **Hallucinated renames** — AI renames a symbol in some files but misses call sites hidden in lambdas, generic constraints, or XML docs | `safe_delete_symbol` / `rename` uses `SymbolFinder.FindReferencesAsync` — every reference in the entire solution, including dynamically-typed code, is found and updated atomically |
| **Broken diffs** — off-by-one line numbers, wrong context lines, lost indentation | All changes are produced by Roslyn's `SyntaxFactory` and `Formatter` — output is always syntactically valid and correctly indented |
| **Silent compilation failures** — AI edits often compile fine in isolation but break cross-project contracts | `validate_proposed_changes` runs a full in-memory compilation and returns the actual `CS1234` diagnostics before any file is touched |
| **Slow cold starts** — loading a 300k-LOC solution for every tool call takes 60+ seconds | Solution stays loaded in RAM; subsequent calls resolve in milliseconds |
| **No blast-radius awareness** — AI doesn't know how many files a change touches before making it | `preview_rename_impact` + `get_blast_radius` show exact file/reference counts before committing |
| **Destructive edits** — AI rewrites an entire file to change one method, wiping formatting, region blocks, and unrelated comments | Every tool targets a specific AST node — unchanged code is bit-for-bit identical in the output |
| **No safe-preview mode** — you only find out the edit was wrong after the file is already saved | All writes are **staged in memory** first; call `validate_staged_changes`, review previews, then `apply_staged_changes` to commit — or discard |
| **Inconsistent formatting** — different AI runs produce different whitespace | Roslyn's `Formatter.Format` normalises every touched file to the workspace's EditorConfig rules |

---

## 🚀 260 MCP Tools Across 44 Engines + 7 Tool Classes

Roslyn Sentinel exposes **260 named MCP tools** through 7 façade classes that wrap 44 specialized analysis and transformation engines. All tools are always live; individual **feature toggles** let you silence noisy analysis rules without touching code.

> **Rating Key:** ⭐⭐⭐⭐⭐ Production-ready &nbsp;·&nbsp; ⭐⭐⭐⭐ Stable, minor edge cases &nbsp;·&nbsp; ⭐⭐⭐ Functional, documented limitations &nbsp;·&nbsp; ⭐⭐ Partial implementation

---

## 🏗️ Infrastructure & Workspace (22 tools)

These tools manage the solution lifecycle, staged-change workflow, and feature toggles. They are the foundation everything else builds on.

### `SentinelWorkspaceTools`

| Tool | What it does | ★ |
|------|--------------|---|
| `load_solution` | Load a `.sln` into the persistent workspace (hot-reloads automatically on file changes) | ⭐⭐⭐⭐⭐ |
| `list_projects` | List all projects with their file counts and load status | ⭐⭐⭐⭐⭐ |
| `list_files` | List all documents in a project | ⭐⭐⭐⭐⭐ |
| `list_dependencies` | Show project-to-project references and NuGet package graph | ⭐⭐⭐⭐⭐ |
| `create_project` | Scaffold a new class-library project and add it to the solution | ⭐⭐⭐⭐⭐ |
| `get_workspace_health` | True workspace state report — projects loaded, documents, errors | ⭐⭐⭐⭐⭐ |
| `diagnose` | Report Roslyn workspace diagnostics | ⭐⭐⭐⭐⭐ |
| `get_project_diagnostics` | Compiler diagnostics (`CS` codes) for one project | ⭐⭐⭐⭐⭐ |
| `get_solution_diagnostics` | Compiler diagnostics across the entire solution | ⭐⭐⭐⭐⭐ |
| `get_file_diagnostics` | Compiler diagnostics for a single file | ⭐⭐⭐⭐⭐ |
| `get_diagnostics_summary` | Diagnostics grouped by code (e.g. `CS0168 × 43`) sorted by frequency | ⭐⭐⭐⭐⭐ |
| `safe_delete` | Delete a file after verifying no remaining references | ⭐⭐⭐⭐⭐ |
| `split_project_by_folder` | Split an oversized project into sub-projects by folder | ⭐⭐⭐⭐⭐ |
| `validate_proposed_diff` | Compile a unified diff in memory; return `CS` errors before touching disk | ⭐⭐⭐⭐⭐ |
| `validate_proposed_changes` | Compile a set of proposed file contents; return errors | ⭐⭐⭐⭐⭐ |
| `validate_staged_changes` | Compile currently staged changes; return errors | ⭐⭐⭐⭐⭐ |
| `apply_proposed_diff` | Apply a unified diff to disk (validates first) | ⭐⭐⭐⭐⭐ |
| `apply_proposed_changes` | Apply proposed file contents to disk (validates first) | ⭐⭐⭐⭐⭐ |
| `apply_staged_changes` | Commit all staged changes to disk | ⭐⭐⭐⭐⭐ |
| `retry_failed_changes` | Re-attempt a previously failed staged change batch | ⭐⭐⭐⭐⭐ |
| `list_features` | Show all ~65 analysis rule names and their `ENABLED/DISABLED` state | ⭐⭐⭐⭐⭐ |
| `update_features` | Batch-enable or batch-disable rules by name | ⭐⭐⭐⭐⭐ |
| `get_feature_status` | Query the status of specific rules by name | ⭐⭐⭐⭐⭐ |

> **Staged-change workflow:** Every write tool has an `autoStage` flag (default `true`). When staged, changes live in memory. Call `validate_staged_changes` to compile them, then `apply_staged_changes` to flush to disk — or discard without consequence.

---

## 🛠️ Refactoring — 63 tools ("The Surgical Suite")

Powered by `RefactoringEngine`, `GranularRefactoringEngine`, `RefinementEngine`, `AdvancedRefactoringEngine`, `MappingEngine`, `CodeFlowEngine`, and `AdvancedStructuralEngine`.

### Structural Refactoring

| Tool | What it does | ★ |
|------|--------------|---|
| `extract_method` | Extract selected lines to a new named method; updates call site | ⭐⭐⭐⭐⭐ |
| `extract_method_safe` | `extract_method` using `contextSnippet` instead of line/col offsets | ⭐⭐⭐⭐⭐ |
| `extract_interface` | Extract public non-static methods **and properties** to a new `I{ClassName}` interface; adds the interface to the class base list | ⭐⭐⭐⭐⭐ |
| `extract_superclass` | Extract common members to a new abstract base class | ⭐⭐⭐⭐⭐ |
| `extract_class` | Move named members to a new class; removes them from source and adds a `private readonly {NewClass}` composition field; returns both files | ⭐⭐⭐⭐ |
| `extract_members_to_partial` | Move specified members to a `partial` companion file | ⭐⭐⭐⭐⭐ |
| `inline_method` | Inline an expression-body or single-return method at **all call sites solution-wide** and remove the method declaration; returns a file→content map for every modified file | ⭐⭐⭐⭐⭐ |
| `inline_field` | Inline a field's value and remove the field | ⭐⭐⭐⭐⭐ |
| `inline_parameter` | Remove a parameter whose value is always a constant | ⭐⭐⭐⭐⭐ |
| `inline_variable` | Inline a local variable's value and remove it | ⭐⭐⭐⭐⭐ |
| `inline_class` | Move all members of a class into a target class and remove the source declaration — works same-file or cross-file (call sites referencing the old class name in other files must be updated manually) | ⭐⭐⭐⭐ |
| `change_signature` | Reorder/remove method parameters + update all call sites | ⭐⭐⭐⭐⭐ |
| `sync_type_and_filename` | Rename a file to match its primary type (or vice-versa) | ⭐⭐⭐⭐⭐ |
| `safe_delete_symbol` | Delete a symbol after verifying zero references solution-wide | ⭐⭐⭐⭐⭐ |
| `rename_symbol` | Rename a symbol and all its references via `SymbolFinder` | ⭐⭐⭐⭐⭐ |
| `preview_rename_impact` | Show reference count + affected files before committing a rename | ⭐⭐⭐⭐⭐ |
| `move_type_to_file` | Move a type to its own `{TypeName}.cs` file | ⭐⭐⭐⭐⭐ |
| `move_type_to_outer_scope` | Unnest a nested type into its enclosing namespace | ⭐⭐⭐⭐⭐ |
| `move_all_types_to_files` | Move every non-primary type in a file to its own file | ⭐⭐⭐⭐⭐ |
| `move_all_types_to_files_in_project` | Same, applied across a whole project | ⭐⭐⭐⭐⭐ |
| `move_all_types_to_files_in_solution` | Same, applied across the whole solution | ⭐⭐⭐⭐⭐ |
| `convert_abstract_to_interface` | Convert an abstract class to an interface | ⭐⭐⭐⭐⭐ |
| `convert_anonymous_to_named` | Replace anonymous types with a named `record` | ⭐⭐⭐⭐⭐ |
| `make_method_static` | Make a method `static` (validates it uses no instance members) | ⭐⭐⭐⭐⭐ |
| `extension_to_static` | Convert an extension method back to a plain static method | ⭐⭐⭐⭐⭐ |
| `convert_method_to_indexer` | Replace a `GetX(key)` method with an indexer | ⭐⭐⭐⭐⭐ |
| `convert_property_to_methods` | Replace a property with `GetX()` / `SetX()` methods | ⭐⭐⭐⭐⭐ |
| `reduce_block_depth` | Flatten nested `if` / `foreach` by early-return guard clauses | ⭐⭐⭐⭐⭐ |
| `replace_constructor_with_factory` | Extract constructor logic to a static `Create(...)` factory method | ⭐⭐⭐⭐⭐ |
| `introduce_parameter_object` | Group method parameters into a new `record` parameter object | ⭐⭐⭐⭐⭐ |
| `sync_interface_to_implementation` | Update an interface to match added/changed methods in its implementation class | ⭐⭐⭐⭐⭐ |
| `update_xml_docs_from_signature` | Re-generate `<param>` XML doc tags when a method signature has changed | ⭐⭐⭐⭐⭐ |
| `generate_mapping` | Generate a mapping method between two types (DTO ↔ entity) | ⭐⭐⭐⭐⭐ |
| `invert_assignments` | Swap source and target in a block of assignment statements | ⭐⭐⭐⭐⭐ |
| `optimize_task_wait` | Replace `.Result` / `.Wait()` with `await` | ⭐⭐⭐⭐⭐ |
| `wrap_in_using` | Wrap selected lines in a `using (resource) { ... }` block | ⭐⭐⭐⭐⭐ |
| `pull_up_member` | Move a method from a derived class to its base class | ⭐⭐⭐⭐⭐ |
| `replace_string_concat_with_interpolation` | Replace `"a" + b + "c"` with `$"a{b}c"` | ⭐⭐⭐⭐⭐ |

### Surgical Member Editing

All tools below accept `contextSnippet` for position-free targeting. All are `autoStage=true` by default.

| Tool | What it does | ★ |
|------|--------------|---|
| `add_member_to_class` | Append any member (field, property, method, etc.) to a class body | ⭐⭐⭐⭐⭐ |
| `insert_member_after` | Insert a member immediately after a named sibling | ⭐⭐⭐⭐⭐ |
| `insert_member_before` | Insert a member immediately before a named sibling | ⭐⭐⭐⭐⭐ |
| `replace_member` | Replace a named member wholesale with new source | ⭐⭐⭐⭐⭐ |
| `remove_member` | Remove a named member from a class | ⭐⭐⭐⭐⭐ |
| `add_using_directive` | Add `using X.Y.Z;` idempotently (supports `static` and `global` usings) | ⭐⭐⭐⭐⭐ |
| `add_enum_value` | Append a named value (+ optional explicit integer) to an enum | ⭐⭐⭐⭐⭐ |
| `add_attribute` | Attach `[Attribute]` to any member or type (with/without `Attribute` suffix) | ⭐⭐⭐⭐⭐ |
| `remove_attribute` | Remove a specific attribute from a member or type | ⭐⭐⭐⭐⭐ |
| `add_base_type` | Add a base class or interface to a type (idempotent) | ⭐⭐⭐⭐⭐ |
| `remove_base_type` | Remove a base class or interface from a type | ⭐⭐⭐⭐⭐ |
| `change_accessibility` | Change `public`/`private`/`internal`/`protected`/etc. on any member | ⭐⭐⭐⭐⭐ |
| `add_modifier` | Add `virtual`, `abstract`, `sealed`, `static`, `readonly`, `async`, etc. | ⭐⭐⭐⭐⭐ |
| `remove_modifier` | Remove any modifier (idempotent) | ⭐⭐⭐⭐⭐ |
| `add_summary_comment` | Add or replace `/// <summary>` doc comment on any member or type | ⭐⭐⭐⭐⭐ |
| `add_property` | Generate an auto-property from name/type/accessibility flags | ⭐⭐⭐⭐⭐ |
| `add_field` | Generate a field from name/type/accessibility/readonly/static/initializer | ⭐⭐⭐⭐⭐ |
| `sort_members` | Reorder class members by convention: fields → ctors → properties → methods → nested | ⭐⭐⭐⭐⭐ |
| `wrap_in_try_catch` | Wrap a line range in `try { } catch (ExceptionType ex) { }` | ⭐⭐⭐⭐⭐ |
| `add_constructor_parameter` | Add a DI dependency: new `readonly` field + ctor param + body assignment in one shot | ⭐⭐⭐⭐⭐ |
| `wrap_in_region` | Surround a line range with `#region name` / `#endregion` | ⭐⭐⭐⭐⭐ |
| `convert_expression_body` | Toggle a member between block body and expression body by name | ⭐⭐⭐⭐⭐ |
| `extract_constant` | Extract a literal to a named `const` (uses `contextSnippet`) | ⭐⭐⭐⭐⭐ |
| `extract_local_variable` | Extract an expression to a named local variable | ⭐⭐⭐⭐⭐ |
| `analyze_control_flow` | Report always/sometimes/never-returns for a method body | ⭐⭐⭐⭐⭐ |
| `analyze_data_flow` | Report read/written/captured variables in a method's code region | ⭐⭐⭐⭐⭐ |

---

## ⚡ Modernization — 26 tools (.NET 8/9/10 · C# 12/13/14)

Powered by `CodeStyleEngine`, `SyntaxUpgradeEngine`, `ModernizationEngine`, `IDEStyleEngine`, `ImmutabilityEngine`, `AsyncOptimizationEngine`, `ModernLoggingEngine`, `LogicOptimizationEngine`, and `AdvancedLogicEngine`.

| Tool | What it does | ★ |
|------|--------------|---|
| `upgrade_to_primary_constructor` | Convert pure-assignment constructors to C# 12 primary constructors | ⭐⭐⭐⭐⭐ |
| `upgrade_to_modern_guards` | Replace `if (x == null) throw` with `ArgumentNullException.ThrowIfNull(x)` | ⭐⭐⭐⭐⭐ |
| `use_exception_expressions` | Modernize all throw patterns to expression form | ⭐⭐⭐⭐⭐ |
| `convert_switch_to_expression` | Convert `switch` statements to `switch` expressions | ⭐⭐⭐⭐⭐ |
| `add_braces` | Add braces to all brace-less `if`/`foreach`/`while` bodies (IDE0011) | ⭐⭐⭐⭐⭐ |
| `use_field_backed_properties` | Apply C# 14 field-backed property syntax (`field` keyword) | ⭐⭐⭐⭐⭐ |
| `find_use_frozen_collections` | Detect `private static readonly Dictionary/HashSet` that should be `FrozenDictionary`/`FrozenSet` | ⭐⭐⭐⭐⭐ |
| `cleanup_implicit_spans` | Remove redundant `.AsSpan()` and `.ToCharArray()` calls | ⭐⭐⭐⭐⭐ |
| `upgrade_pattern_matching` | Convert `is`-casts and null checks to C# 9 pattern matching | ⭐⭐⭐⭐⭐ |
| `upgrade_unbound_nameof` | Upgrade `typeof(T).Name` and string literals to `nameof(T)` | ⭐⭐⭐⭐⭐ |
| `use_index_from_end` | Replace `array[array.Length - 1]` with `array[^1]` | ⭐⭐⭐⭐⭐ |
| `fix_thread_sleep` | Replace `Thread.Sleep(n)` in async methods with `await Task.Delay(n)` (EPC33) | ⭐⭐⭐⭐⭐ |
| `upgrade_thread_safety` | Replace `lock` with `System.Threading.Lock` (.NET 9+) | ⭐⭐⭐⭐⭐ |
| `use_time_provider` | Inject `TimeProvider` instead of `DateTime.UtcNow` direct calls | ⭐⭐⭐⭐⭐ |
| `modernize_exceptions` | Apply modern exception patterns | ⭐⭐⭐⭐⭐ |
| `class_to_record` | Convert a POCO class to an immutable `record` | ⭐⭐⭐⭐⭐ |
| `record_to_class` | Convert a `record` back to a mutable class | ⭐⭐⭐⭐⭐ |
| `make_class_immutable` | Add `init`-only setters and `readonly` fields throughout a class | ⭐⭐⭐⭐⭐ |
| `simplify_verbosity` | Remove redundant type specifiers, explicit `this.`, etc. | ⭐⭐⭐⭐⭐ |
| `simplify_member_access` | Simplify overly-qualified member access chains | ⭐⭐⭐⭐⭐ |
| `convert_to_source_generated_logging` | Convert `ILogger.Log(...)` calls to `[LoggerMessage]` source-generated stubs | ⭐⭐⭐⭐⭐ |
| `simplify_boolean_expressions` | Simplify `if (x == true)` → `if (x)`, `!x == false` → `x`, etc. | ⭐⭐⭐⭐⭐ |
| `optimize_to_value_task` | Convert `Task<T>` returns to `ValueTask<T>` where safe | ⭐⭐⭐⭐⭐ |
| `optimize_independent_awaits` | Parallelise sequential `var x = await a; var y = await b;` to `Task.WhenAll` | ⭐⭐⭐⭐⭐ |
| `convert_static_to_extension` | Convert a plain `static` method to an extension method | ⭐⭐⭐⭐⭐ |
| `invert_boolean_logic` | Invert conditions and negate return values throughout a method | ⭐⭐⭐⭐⭐ |

---

## 🔍 Intelligence & Analysis — 57 tools

Powered by `AnalysisEngine`, `MetricsEngine`, `SymbolNavigationEngine`, `ArchitecturalEngine`, `DeadCodeEngine`, `AsyncSafetyEngine`, `AsyncOptimizationEngine`, `ThreadSafetyEngine`, `DependencyInjectionEngine`, `DiscoveryEngine`, and `SemanticSearchEngine`.

### Call Graph & Navigation

| Tool | What it does | ★ |
|------|--------------|---|
| `get_call_graph` | Build a forward call graph from a method (calls what) | ⭐⭐⭐⭐⭐ |
| `get_reverse_call_graph` | Build a reverse call graph (who calls this) | ⭐⭐⭐⭐⭐ |
| `generate_call_tree` | Produce a human-readable call tree string for a method | ⭐⭐⭐⭐⭐ |
| `find_callers_safe` | Find all callers of a symbol by name (no line/col required) | ⭐⭐⭐⭐⭐ |
| `find_implementations_safe` | Find all implementations of an interface member by name | ⭐⭐⭐⭐⭐ |
| `find_all_implementations` | Find all concrete implementations of an interface across the solution | ⭐⭐⭐⭐⭐ |
| `get_symbol_info` | Full semantic information for a named symbol | ⭐⭐⭐⭐⭐ |
| `get_public_api_surface` | List all public members of a type | ⭐⭐⭐⭐⭐ |
| `get_type_members_detail` | Detailed member list (modifiers, return types, parameter lists) | ⭐⭐⭐⭐⭐ |
| `find_extension_methods` | Discover all extension methods targeting a type | ⭐⭐⭐⭐⭐ |

### Architecture & Dependencies

| Tool | What it does | ★ |
|------|--------------|---|
| `find_circular_dependencies` | Detect circular project references (Tarjan's SCC algorithm) | ⭐⭐⭐⭐⭐ |
| `check_package_inconsistency` | Flag NuGet packages referenced at different versions across projects | ⭐⭐⭐⭐⭐ |
| `convert_to_background_service` | Scaffold a class into an `IHostedService` background worker | ⭐⭐⭐⭐⭐ |
| `fix_mismatched_namespaces` | Fix all files whose namespace doesn't match their folder path | ⭐⭐⭐⭐⭐ |
| `move_file_to_namespace_folder` | Move a file to the folder that matches its namespace | ⭐⭐⭐⭐⭐ |
| `find_services_not_registered` | Detect interfaces used via DI that have no `AddXxx` registration | ⭐⭐⭐⭐⭐ |
| `find_di_registrations` | List all `AddSingleton`/`AddScoped`/`AddTransient` calls in the solution | ⭐⭐⭐⭐⭐ |
| `verify_interface_completeness` | Check that every interface member has an implementation in each implementor | ⭐⭐⭐⭐⭐ |

### Dead Code & Metrics

| Tool | What it does | ★ |
|------|--------------|---|
| `find_unused_private_members` | Detect private members never referenced (DI-aware false-positive avoidance) | ⭐⭐⭐⭐⭐ |
| `find_unused_constructors` | Detect constructors that are never called | ⭐⭐⭐⭐⭐ |
| `detect_unused_private_fields` | Detect private fields assigned but never read | ⭐⭐⭐⭐⭐ |
| `detect_unused_local_variables` | Detect local variables never used after assignment | ⭐⭐⭐⭐⭐ |
| `check_for_unused_event_subscriptions` | Find `+=` without a matching `-=` | ⭐⭐⭐⭐⭐ |
| `find_unused_references` | Find project/assembly references that are never used | ⭐⭐⭐⭐⭐ |
| `find_unused_interfaces` | Find interfaces with no implementors | ⭐⭐⭐⭐⭐ |
| `find_uninstantiated_types` | Find non-abstract classes that are never instantiated | ⭐⭐⭐⭐⭐ |
| `find_internal_classes_that_could_be_private` | Suggest `private` for `internal` nested types | ⭐⭐⭐⭐⭐ |
| `get_solution_metrics` | Cyclomatic complexity, maintainability index, and LCOM cohesion for every type | ⭐⭐⭐⭐⭐ |
| `get_code_inventory` | Project/file/type/method counts across the solution | ⭐⭐⭐⭐⭐ |

### Pattern & Structure Search

| Tool | What it does | ★ |
|------|--------------|---|
| `find_all_throw_sites` | List every `throw` statement/expression in a file/project | ⭐⭐⭐⭐⭐ |
| `find_object_creation_sites` | List every `new T(...)` for a given type | ⭐⭐⭐⭐⭐ |
| `find_best_insertion_point` | Return the optimal line to insert a field/ctor/property/method by convention | ⭐⭐⭐⭐⭐ |
| `find_todo_fixme_comments` | Scan `TODO`/`FIXME`/`HACK`/`BUG`/`REVIEW` comments, severity-ranked | ⭐⭐⭐⭐⭐ |
| `find_large_switch_statements` | Flag `switch` statements with many cases | ⭐⭐⭐⭐⭐ |
| `find_structural_smells` | Detect classes that are too large, too coupled, or have too many responsibilities | ⭐⭐⭐⭐⭐ |
| `find_readonly_field_candidates` | Find fields that are never written after construction (should be `readonly`) | ⭐⭐⭐⭐⭐ |
| `analyze_type_cohesion` | LCOM cohesion score — how tightly related a class's methods are | ⭐⭐⭐⭐⭐ |
| `find_methods_by_return_type` | Find all methods returning a specific type | ⭐⭐⭐⭐⭐ |

### Memory & Equality

| Tool | What it does | ★ |
|------|--------------|---|
| `detect_memory_leaks` | Detect event subscriptions without `IDisposable` unsubscribe | ⭐⭐⭐⭐⭐ |
| `find_possible_infinite_loops` | Flag loops with no exit condition | ⭐⭐⭐⭐⭐ |
| `generate_equality_overrides` | Generate `Equals` + `GetHashCode` using `HashCode.Combine` | ⭐⭐⭐⭐⭐ |
| `document_poco_fields` | Add `/// <summary>` to every undocumented POCO field | ⭐⭐⭐⭐⭐ |

### Async Safety

| Tool | What it does | ★ |
|------|--------------|---|
| `find_task_void_usage` | Flag `async void` methods (EPC27) | ⭐⭐⭐⭐⭐ |
| `find_task_yield_usage` | Flag unnecessary `await Task.Yield()` | ⭐⭐⭐⭐⭐ |
| `find_task_delay_usage` | Flag `await Task.Delay(0)` patterns | ⭐⭐⭐⭐⭐ |
| `find_task_delay_zero_usage` | Flag `Task.Delay(0)` used as a yield-back hack | ⭐⭐⭐⭐⭐ |
| `find_task_when_all_usage` | Detect `Task.WhenAll` patterns that could be simplified | ⭐⭐⭐⭐⭐ |
| `find_configure_await_missing` | Flag every `await` missing `.ConfigureAwait(false)` (EPC15) | ⭐⭐⭐⭐⭐ |
| `find_blocking_calls_in_async` | Flag `.Result`/`.Wait()` inside async methods (EPC35) | ⭐⭐⭐⭐⭐ |
| `find_async_in_constructor` | Flag `async` work fired from constructors | ⭐⭐⭐⭐⭐ |
| `find_task_run_in_async` | Flag `Task.Run(...)` inside already-async methods | ⭐⭐⭐⭐⭐ |
| `find_concurrent_collection_opportunities` | Detect non-thread-safe collections used across threads | ⭐⭐⭐⭐⭐ |
| `find_unsafe_lazy_init` | Flag unsafe `Lazy<T>` initialization patterns | ⭐⭐⭐⭐⭐ |
| `detect_value_task_misuse` | Flag double-await, `.Result` on `ValueTask`, deferred `ValueTask` | ⭐⭐⭐⭐⭐ |
| `find_async_over_sync` | Flag async methods with no real awaits | ⭐⭐⭐⭐⭐ |
| `find_unawaited_fire_and_forget` | Flag unawaited task invocations | ⭐⭐⭐⭐⭐ |
| `add_configure_await_false` | Bulk-add `.ConfigureAwait(false)` to all awaits in a file | ⭐⭐⭐⭐⭐ |
| `remove_configure_await_false` | Bulk-remove `.ConfigureAwait(false)` from all awaits | ⭐⭐⭐⭐⭐ |
| `add_cancellation_token_to_method` | Add a `CancellationToken` parameter and propagate it to callees | ⭐⭐⭐⭐⭐ |
| `convert_lock_to_semaphore_slim` | Replace `lock (x) { }` with `await _semaphore.WaitAsync()` | ⭐⭐⭐⭐⭐ |
| `convert_to_async_enumerable` | Convert a `List<T>` return to `IAsyncEnumerable<T>` | ⭐⭐⭐⭐⭐ |
| `make_method_thread_safe` | Wrap a method with a `SemaphoreSlim` guard | ⭐⭐⭐⭐⭐ |
| `find_missing_cancellation_tokens` | Find async methods missing a `CancellationToken` parameter | ⭐⭐⭐⭐⭐ |

---

## 🔎 Quality & Anti-Patterns — 72 tools

Powered by `AntiPatternEngine`, `PerformanceEngine`, `SecurityEngine`, `TestingEngine`, `CodeStyleEngine`, `CodeStyleAnalysisEngine`, `ThreadSafetyEngine`, `AsyncSafetyEngine`, and `DiagnosticEngine`.

### Performance

| Tool | What it does | ★ |
|------|--------------|---|
| `analyze_performance` | Full performance audit: boxing, LINQ materialization, string concat | ⭐⭐⭐⭐⭐ |
| `find_boxing_allocations` | Flag value types boxed to `object` | ⭐⭐⭐⭐⭐ |
| `detect_inefficient_string_comparisons` | Flag `ToLower()`/`ToUpper()` string comparisons | ⭐⭐⭐⭐⭐ |
| `optimize_resource_disposal` | Find `IDisposable` objects not wrapped in `using` | ⭐⭐⭐⭐⭐ |
| `find_use_frozen_collections` | Detect static dictionaries/sets that should be `FrozenDictionary`/`FrozenSet` | ⭐⭐⭐⭐⭐ |

### Security

| Tool | What it does | ★ |
|------|--------------|---|
| `analyze_security` | Full security audit: SQL injection, hardcoded secrets, weak crypto | ⭐⭐⭐⭐⭐ |
| `check_for_sql_injection` | Specifically scan for concatenated SQL strings | ⭐⭐⭐⭐⭐ |
| `find_hardcoded_paths` | Flag hardcoded file system paths | ⭐⭐⭐⭐⭐ |
| `find_string_magic_values` | Flag unexplained string literals that should be constants | ⭐⭐⭐⭐⭐ |

### Code Health

| Tool | What it does | ★ |
|------|--------------|---|
| `detect_anti_patterns` | Full anti-pattern report (long methods, god classes, feature envy) | ⭐⭐⭐⭐⭐ |
| `find_mutable_public_properties` | Flag mutable public setters that should be `init`-only | ⭐⭐⭐⭐⭐ |
| `find_naming_violations` | Flag names that violate .NET naming conventions | ⭐⭐⭐⭐⭐ |
| `find_long_parameter_list` | Flag methods with ≥ N parameters (skips DI-injection ctors) | ⭐⭐⭐⭐⭐ |
| `find_primitive_obsession` | Flag methods using the same primitive type 3+ times as distinct params | ⭐⭐⭐⭐⭐ |
| `find_inconsistent_async_suffix` | Flag async methods missing "Async" / non-async with "Async" | ⭐⭐⭐⭐⭐ |
| `analyze_exception_handling` | Audit catch blocks: empty catches, swallowed exceptions, Pokemon catches | ⭐⭐⭐⭐⭐ |
| `check_for_empty_catch_blocks` | Flag `catch { }` and `catch (Exception) { }` with no body | ⭐⭐⭐⭐⭐ |
| `check_for_redundant_cast` | Flag unnecessary explicit casts | ⭐⭐⭐⭐⭐ |
| `detect_mismatched_await` | Flag `await` used where the expression is not a `Task` | ⭐⭐⭐⭐⭐ |
| `detect_reflection_usage` | Flag `typeof`, `GetType()`, and `Activator.CreateInstance` | ⭐⭐⭐⭐⭐ |
| `find_possible_deadlocks` | Detect nested lock acquisition patterns prone to deadlock | ⭐⭐⭐⭐⭐ |
| `analyze_semaphore_usage` | Detect `SemaphoreSlim` misuse (never released, released without acquire) | ⭐⭐⭐⭐⭐ |
| `get_diagnostics_summary` | Compiler diagnostics grouped by `CS` code, sorted by frequency | ⭐⭐⭐⭐⭐ |

### Precision Detectors (v2 additions)

#### Performance

| Tool | What it does | ★ |
|------|--------------|---|
| `find_linq_n1_patterns` | Detect LINQ terminal calls inside loops where the loop variable appears in the chain — each iteration triggers a separate query | ⭐⭐⭐⭐⭐ |
| `find_string_format_in_loops` | Detect `$"..."` / `string.Format()` inside loop bodies — each iteration allocates; use `StringBuilder` instead | ⭐⭐⭐⭐⭐ |
| `find_multiple_enumeration` | Detect `IEnumerable<T>` / `IQueryable<T>` locals and parameters iterated more than once without materializing | ⭐⭐⭐⭐⭐ |
| `find_linq_redundant_where` | Detect `.Where(pred).First()` / `.Any()` / `.Count()` chains — collapse to `First(pred)` for allocation-free single pass | ⭐⭐⭐⭐⭐ |
| `find_implicit_nullable_boxing` | Detect explicit casts of `Nullable<T>` to `object` — surprising null-equality behavior and boxing overhead | ⭐⭐⭐⭐⭐ |

#### Stability / Resource Safety

| Tool | What it does | ★ |
|------|--------------|---|
| `find_finalizer_on_disposable` | Detect `IDisposable` classes with a finalizer but no disposed-flag guard — may double-free unmanaged resources | ⭐⭐⭐⭐⭐ |
| `find_unbounded_static_collections` | Detect static `Dictionary`/`List`/`HashSet` fields populated with `.Add()` but never `.Clear()`ed — memory exhaustion DoS | ⭐⭐⭐⭐⭐ |
| `find_unbounded_recursion` | Detect recursive methods with no depth parameter or base-case guard before the recursive call — `StackOverflowException` on deep input | ⭐⭐⭐⭐⭐ |

#### Thread Safety

| Tool | What it does | ★ |
|------|--------------|---|
| `find_unsafe_lazy_init_thread` | Detect `if (_field == null) _field = new X()` outside a lock without `volatile` or `Lazy<T>` — partially constructed objects on other threads | ⭐⭐⭐⭐⭐ |
| `find_cas_loop_without_backoff` | Detect `while (Interlocked.CompareExchange(...))` with no `SpinWait`, `Thread.Sleep`, or `Task.Delay` — 100% CPU live-lock under contention | ⭐⭐⭐⭐⭐ |
| `find_double_checked_locking` | Detect the DCL pattern without `volatile` — CPU/JIT may reorder the store and expose a partially-constructed object | ⭐⭐⭐⭐⭐ |
| `find_check_then_act_on_dictionary` | Detect `ContainsKey()` + `Add()` on the same dictionary outside a lock — another thread may insert between the check and the add | ⭐⭐⭐⭐⭐ |

#### Security

| Tool | What it does | ★ |
|------|--------------|---|
| `find_re_dos_patterns` | Detect Regex literals with nested quantifiers (`(a+)+`) — catastrophic backtracking on non-matching input | ⭐⭐⭐⭐⭐ |
| `find_unvalidated_regex_source` | Detect `new Regex(variable)` where the pattern argument is not a compile-time literal — Regex injection and ReDoS amplification | ⭐⭐⭐⭐⭐ |
| `find_regex_new_in_loop` | Detect `new Regex()` inside loop bodies — recompiles the pattern on every iteration; hoist to a `static readonly` field | ⭐⭐⭐⭐⭐ |

#### Async Safety

| Tool | What it does | ★ |
|------|--------------|---|
| `find_sequential_independent_awaits` | Detect consecutive `await` calls where neither result depends on the other — missed `Task.WhenAll()` parallelism | ⭐⭐⭐⭐⭐ |
| `find_async_void_without_try_catch` | Detect `async void` methods whose body is not wrapped in `try/catch` — unhandled exceptions crash the process with no caller recovery | ⭐⭐⭐⭐⭐ |
| `find_unawaked_dispose_async` | Detect `DisposeAsync()` calls not wrapped in `await` — async cleanup finishes after the method returns, leaving resources dangling | ⭐⭐⭐⭐⭐ |
| `find_unobserved_task_in_field` | Detect `Task`/`ValueTask` assigned to a field and never subsequently awaited in the class — exceptions silently swallowed | ⭐⭐⭐⭐⭐ |

#### Code Style

| Tool | What it does | ★ |
|------|--------------|---|
| `find_mutable_public_collection_properties` | Detect public properties exposing `List<T>`, `Dictionary<K,V>`, `HashSet<T>` etc. with a public setter — callers can replace the entire collection | ⭐⭐⭐⭐⭐ |

### Testing

| Tool | What it does | ★ |
|------|--------------|---|
| `generate_test_skeleton` | Scaffold an NUnit/xUnit test class with one test per public method | ⭐⭐⭐⭐⭐ |
| `generate_test_scaffold` | Generate a richer test file with Arrange/Act/Assert stubs | ⭐⭐⭐⭐⭐ |
| `analyze_path_coverage` | Report which code paths lack test coverage for a file | ⭐⭐⭐⭐⭐ |
| `add_guard_clauses` | Add null-guard clauses to all method parameters | ⭐⭐⭐⭐⭐ |
| `add_benchmark_stub` | Add a BenchmarkDotNet stub for every public method | ⭐⭐⭐⭐⭐ |
| `detect_long_parameter_lists` | (Intelligence context) Flag long parameter lists project-wide | ⭐⭐⭐⭐⭐ |

---

## 🏭 Code Generation — 11 tools

Powered by `CodeGenerationEngine`, `ApiIntegrationEngine`, and `AsyncOptimizationEngine`.

| Tool | What it does | ★ |
|------|--------------|---|
| `generate_constructor` | Generate a constructor from all `readonly` fields | ⭐⭐⭐⭐⭐ |
| `generate_to_string` | Generate a `ToString()` override listing all properties | ⭐⭐⭐⭐⭐ |
| `generate_to_string_safe` | `generate_to_string` using `contextSnippet` for precise targeting | ⭐⭐⭐⭐⭐ |
| `generate_equality_overrides` | Generate `Equals` + `GetHashCode` using `HashCode.Combine` | ⭐⭐⭐⭐⭐ |
| `generate_repository_interface` | Generate an `IRepository<T>` interface from a concrete repository class | ⭐⭐⭐⭐⭐ |
| `generate_http_client` | Scaffold a typed `HttpClient` wrapper from a controller's public methods | ⭐⭐⭐⭐⭐ |
| `generate_fluent_builder` | Generate a fluent builder class for a type | ⭐⭐⭐⭐⭐ |
| `generate_default_config_json` | Generate a `appsettings.json` stub from a configuration class | ⭐⭐⭐⭐⭐ |
| `generate_decorator_class` | Scaffold a decorator pattern wrapper for a given interface | ⭐⭐⭐⭐⭐ |
| `generate_async_overload` | Scaffold an async overload for a synchronous method | ⭐⭐⭐⭐⭐ |
| `add_validation_to_poco` | Add `[Required]`/`[Range]`/`[StringLength]` annotations to a POCO | ⭐⭐⭐⭐⭐ |
| `implement_interface_safe` | Generate correct `public` method stubs with `throw new NotImplementedException()` — never incorrectly adds `override` | ⭐⭐⭐⭐⭐ |

---

## 🔧 Built-in Tool Augmentations — 12 tools

These are drop-in replacements for broken or limited VS/Roslyn built-in tools. Always prefer these over their built-in equivalents.

| Roslyn Sentinel Tool | Replaces | Why | ★ |
|---|---|---|---|
| `encapsulate_field_safe` | `encapsulate_field` | Built-in generates self-referential `Field { get { return Field; } }` with the same name | ⭐⭐⭐⭐⭐ |
| `analyze_switch_for_pattern_conversion` | *(pre-flight for below)* | Detects multi-assignment cases the MS tool silently mishandles | ⭐⭐⭐⭐⭐ |
| `convert_switch_to_pattern_safe` | `convert_switch_to_expression` | Rejects multi-assignment cases rather than generating broken code | ⭐⭐⭐⭐⭐ |
| `convert_string_format_to_interpolated_smart` | `convert_to_interpolated_string` | Resolves **const string format arguments** via semantic model; built-in fails on named consts | ⭐⭐⭐⭐⭐ |
| `sort_and_deduplicate_usings` | `sort_usings` | Built-in sorts but does **not** remove duplicates | ⭐⭐⭐⭐⭐ |
| `format_document_safe` | `format_document` | Adds true **preview support** (`preview=true`); built-in has no preview at all | ⭐⭐⭐⭐⭐ |
| `analyze_foreach_for_linq_conversion` | *(pre-flight for below)* | Detects when collection has prior `.Add()` calls (built-in silently destroys them) | ⭐⭐⭐⭐⭐ |
| `convert_foreach_to_linq` | `convert_foreach_linq` | Only proceeds when `analyze_foreach_for_linq_conversion` confirms `IsSafeToConvert=true` | ⭐⭐⭐⭐⭐ |
| `get_workspace_health` | `diagnose` | Built-in reports `healthy:false` even when workspace is fine (tests MSBuild path, not actual load state) | ⭐⭐⭐⭐⭐ |
| `preview_add_missing_usings` | `add_missing_usings` | Built-in's `preview:true` flag is completely ignored — it always writes to disk | ⭐⭐⭐⭐⭐ |
| `extract_constant_safe` | `extract_constant` | Uses `contextSnippet`; built-in requires exact 1-based char offsets and throws cryptic column errors | ⭐⭐⭐⭐⭐ |
| `extract_method_safe` | `extract_method` | Uses `contextSnippet`; built-in requires line/column coordinates | ⭐⭐⭐⭐⭐ |

---

## 🤖 AI-First Tool Design: `contextSnippet`

Every tool that targets a specific code location uses a **`contextSnippet`** parameter — a verbatim substring of the source — rather than `(line, column)` coordinates. This eliminates the most common failure mode in AI-driven code edits: stale line numbers.

```
// ❌ Fragile (requires coordinate math):
introduce_field(filePath, line: 47, column: 23, "newFieldName")

// ✅ Stable (AI pastes nearby text):
introduce_field(filePath, contextSnippet: "var result = _service.Get(", "newFieldName")
```

**Rules:**
- Snippet appears exactly once → position resolved, operation proceeds
- Snippet not found → descriptive error returned
- Snippet matches multiple locations → add `lineBefore` / `lineAfter` to disambiguate

```
// When "var x = 0;" appears in multiple methods:
introduce_field(filePath,
    contextSnippet: "var x = 0;",
    lineBefore: "public void MethodA()",
    lineAfter:  "{")
```

`lineBefore`/`lineAfter` are trimmed and matched with `Contains` — indentation differences are ignored.

---

## ⚙️ Feature Toggles

Roslyn Sentinel has a runtime **Feature Toggle System** with ~65 named rules. Disable noisy rules to reduce analysis chatter without changing any code.

### Toggle Management Tools

```
list_features()                          → all rule names + ENABLED/DISABLED
get_feature_status(["BoxingAllocation"]) → targeted query
update_features([{"BoxingAllocation": false}, {"MultiTypeFile": false}]) → batch update
```

Changes take effect immediately for all subsequent tool calls, including `get_comprehensive_health_report`.

### Available Feature Toggle Names

**Refactoring rules** (all `true` by default):
`ChangeSignature`, `ConvertAbstractClassToInterface`, `ConvertAnonymousToNamedType`, `ConvertExtensionMethodToPlainStatic`, `ConvertIndexerToMethod`, `ConvertInterfaceToAbstractClass`, `ConvertMethodToIndexer`, `ConvertMethodToProperty`, `ConvertPropertyToAutoProperty`, `ConvertPropertyToMethod`, `ConvertStaticToExtensionMethod`, `CopyToGlobalUsing`, `CopyType`, `EncapsulateField`, `ExtractClass`, `ExtractInterface`, `ExtractMethod`, `ExtractSuperclass`, `InlineClass`, `InlineField`, `InlineMethod`, `InlineParameter`, `InlineVariable`, `IntroduceField`, `IntroduceParameter`, `IntroduceVariable`, `InvertBoolean`, `MakeMethodNonStatic`, `MakeMethodStatic`, `ExtractMembersToPartial`, `MoveInstanceMethod`, `MoveTypeToFile`, `MoveTypeToNamespace`, `MoveTypeToOuterScope`, `PullMembersUp`, `PushMembersDown`, `Rename`, `SafeDelete`, `AddRemoveParams`, `TransformParameters`, `UseBaseTypeWherePossible`, `ConvertExpressionBody`, `ExtractConstant`, `ExtractLocalVariable`, `AnalyzeControlFlow`, `AnalyzeDataFlow`

**Modernization rules**:
`TimeProviderInjection`, `ModernGuardClauses`, `PrimaryConstructors`, `CollectionExpressions`, `LockModernization`, `FieldBackedProperties`, `ImplicitSpanCleanup`, `UnboundNameof`, `NullConditionalAssignment`, `PatternMatching`, `ThrowExpressions`, `ClassToRecord`, `RecordToClass`, `IfToSwitch`, `SimplifyVerbosity`, `LengthMinusOneToIndex`

**Quality/Analysis rules**:
`BoxingAllocation`, `ReflectionUsage`, `InefficientStringComparison`, `AsyncVoidUsage`, `EmptyCatchBlocks`, `Deadlocks`, `DuplicateMethods`, `LargeTypes`, `LargeMethods`, `LargeSwitchStatements`, `MemoryLeaks`, `ResourceDisposal`, `UnusedReferences`, `InconsistentSql`, `MultiTypeFile`, `NameMismatch`, `ThreadSafety`, `TimeAbstraction`, `RedundantTypeSpecification`, `LongParameterLists`, `RedundantCasts`, `MismatchedAwait`, `SemaphoreLeaks`, `UninstantiatedTypes`, `UnusedInterfaces`

**IDE diagnostic rules**:
`IDE0001` (simplify name), `IDE0005` (remove unused import), `IDE0011` (add braces), `IDE0016` (throw expression), `IDE0028` (collection initializers), `IDE0032` (auto property), `IDE0035` (remove unreachable code), `IDE0044` (add readonly), `IDE0051` (remove unused private member)

**EPC async rules**:
`EPC14` (ConfigureAwait redundant), `EPC15` (ConfigureAwait required), `EPC27` (avoid async void), `EPC33` (Thread.Sleep in async), `EPC35` (blocking in async)

---

## 🧪 Verification

Roslyn Sentinel is backed by **1,896 tests across 76 test files** (1,809 passing, 87 skipped for real-solution integration tests), including:
- Unit tests for every engine method
- Accuracy tests verifying each detector fires on positive cases and stays silent on negative cases
- Real-solution smoke tests against a live .NET codebase (requires `ROSLYN_SENTINEL_TEST_SLN`)
- Regression tests for every fixed bug (Batteries targeting specific CS codes and tool edge cases)

```bash
dotnet test RoslynSentinel.Tests/RoslynSentinel.Tests.csproj
# → 1,809 passed, 87 skipped
```

---

## 📜 Roadmap & Backlog

See [UNFINISHED_FEATURES.md](./UNFINISHED_FEATURES.md) for the backlog of planned additions:
- `ConvertInterfaceToAbstractClass`, `AutoParallelize`
- Full `InlineClass` (requires cross-file symbol discovery)
- IDE0042/IDE0050/IDE0210/IDE0250/IDE0340 modernization passes
- EPC16/EPC18/EPC31/EPC32 async audit rules
- Intent-Based AST Command Model (high-level "recipe" refactorings)

---
