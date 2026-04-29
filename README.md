# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." Unlike standard CLI tools that suffer from 30-second "cold start" delays, Roslyn Sentinel stays running in the background, keeping your .NET solution "hot" in memory.

## 🚀 Key Features

*   **The "Hot" Daemon**: Maintains an `MSBuildWorkspace` in memory. It uses `FileSystemWatcher` to detect external changes (Drift) and incrementally reloads projects, ensuring the semantic model is always current.
*   **Speculative Validation Loop**: Allows AI to submit **Unified Diffs**. The server applies them to an in-memory fork, runs a full compilation check, and returns diagnostic errors (CSXXXX) before any code is written to disk.
*   **Blast Radius Reporting**: Performs deep semantic tracing. It tells the AI: "If you change this method signature, you will break 3 interfaces and 14 call-sites across 5 projects."
*   **Focused Modes**: To prevent AI context window overwhelm, the server can be launched in specific modes (e.g., `Refactor`, `Analysis`, `Quality`).

## 🛠️ Prerequisites

*   **.NET 10 SDK** (Stable version)
*   **Visual Studio 2022** or **MSBuild** installed on the host machine.
*   An MCP-compatible AI client (Claude Desktop, etc.).

## 📦 Installation

1.  Clone this repository to a stable location.
2.  Build the server:
    ```bash
    dotnet publish RoslynSentinel.Server/RoslynSentinel.Server.csproj -c Release -o ./publish
    ```

## ⚙️ Configuration

### Claude Desktop
Add the following to your `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "roslyn-sentinel": {
      "command": "dotnet",
      "args": [
        "E:/source/repos/RoslynSentinel/RoslynSentinel.Server/bin/Release/net10.0/RoslynSentinel.Server.dll",
        "--mode=all"
      ]
    }
  }
}
```

### Modes Reference
| Mode | Description |
| :--- | :--- |
| `all` | (Default) Exposes all 300+ capabilities. |
| `Workspace` | Core solution loading, diagnostics, and project management. |
| `Intelligence` | Impact analysis, semantic search, and metrics. |
| `Refactor` | Heavy-duty refactorings (Rename, Move, Interface extraction). |
| `Quality` | Performance scans, Security audits, and Test generation. |
| `Generation` | JSON-to-Class and API Client generation. |

## 🤖 AI Workflow (The Safety Loop)

Agents using this server should follow this foundational loop:

1.  **Check Drift**: Call `mcp_get_external_changes` to ensure memory matches disk.
2.  **Analyze**: Use `mcp_get_blast_radius` to see the impact of a change.
3.  **Propose**: Generate a change and call `mcp_get_proposed_diff` to show the user.
4.  **Validate**: Call `mcp_validate_proposed_diff`.
5.  **Commit**: Only if validation succeeds, use `mcp_apply_proposed_diff` and write to disk.

## 📜 Unfinished Capabilities
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex AST transformations currently in development.
