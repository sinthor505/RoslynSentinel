# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." Unlike standard CLI tools that suffer from 30-second "cold start" delays, Roslyn Sentinel stays running in the background, keeping your .NET solution "hot" in memory.

## 🚀 Key Features

*   **High-Performance Orchestration**: Features a parallelized `HealthOrchestrationEngine` that scans 80+ projects concurrently, utilizing multi-core processing to eliminate timeouts on massive (300k+ LOC) solutions.
*   **The "Hot" Daemon**: Maintains an `MSBuildWorkspace` in memory. It uses proactive synchronization to ignore internal writes while detecting external "drift" automatically.
*   **One-Shot Solution Health Diagnostics**: A single command (`get_comprehensive_health_report`) provides a bird's-eye view of structural, architectural, safety, and performance issues across the entire solution.
*   **Speculative Validation Loop**: Allows AI to submit **Unified Diffs**. The server applies them to an in-memory fork, runs a full compilation check, and returns diagnostic errors (CSXXXX) before any code is written to disk.
*   **Modernization Suite**: Automates .NET 8/9 upgrades:
    *   **TimeProvider Injection**: Replaces static `DateTime` calls with testable abstractions.
    *   **Custom Exception Generation**: Automatically replaces generic `throw new Exception` with strongly-typed, generated exception classes.
    *   **Record Conversion**: Surgical transformation of POCOs into positional records and back.
    *   **Guard Clauses**: Batch-upgrades legacy null-checks to modern `ArgumentNullException.ThrowIfNull` helpers.
*   **Blast Radius Reporting**: Performs deep semantic tracing. It tells the AI: "If you change this method signature, you will break 3 interfaces and 14 call-sites across 5 projects."

## 🛠️ Prerequisites

*   **.NET 10 SDK** (Stable version)
*   **Visual Studio 2022** or **MSBuild** installed on the host machine.
*   An MCP-compatible AI client (Claude Desktop, etc.).

## 📦 Installation

1.  Clone this repository.
2.  Build and Publish:
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
        "C:/path/to/RoslynSentinel/publish/RoslynSentinel.Server.dll",
        "--mode=all"
      ]
    }
  }
}
```

### Modes Reference
| Mode | Description |
| :--- | :--- |
| `all` | (Default) Exposes all 350+ capabilities. |
| `Workspace` | Core solution loading, diagnostics, and project management. |
| `Intelligence` | Impact analysis, health reports, and solution metrics. |
| `Refactor` | Heavy-duty refactorings (Rename, Move, Interface extraction). |
| `Modernize` | .NET 9 upgrades, TimeProvider, and syntax simplification. |
| `Quality` | Performance scans, Thread-safety audits, and Security. |
| `Generation` | JSON-to-Class and API Client generation. |

## 🧪 Verification

The server includes a robust test suite (150+ tests) covering:
*   **DeepFunctionalVerification**: Exact syntax transformations.
*   **SolutionWideFunctional**: Correct scoping and project aggregation.
*   **ExhaustiveRefactoring**: Edge-case handling for complex C# patterns.

Run tests via CLI:
```bash
dotnet test
```

## 🤖 AI Workflow (The Safety Loop)

Agents using this server follow this foundational loop:
1.  **Diagnostic**: Call `get_comprehensive_health_report` to map solution-wide issues.
2.  **Surgical Analysis**: Focus on a project with `find_structural_smells(projectName: "...")`.
3.  **Propose**: Generate a change and call `validate_proposed_diff`.
4.  **Sync**: Call `acknowledge_sync` to align memory after manual modifications.

## 📜 Unfinished Capabilities
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex AST transformations currently in development.
