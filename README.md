# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, eliminating cold-start delays and providing deep semantic analysis.

## 🚀 Key Features

*   **High-Performance Orchestration**: Parallelized engine that scans 80+ projects concurrently, utilizing multi-core processing to eliminate timeouts on massive solutions.
*   **One-Shot Health Diagnostics**: Bird's-eye view of solution health with `get_comprehensive_health_report`, featuring incremental paging and configurable timeouts.
*   **Global Feature Toggle System**: Granular control over every analysis and refactoring rule. Enable or disable specific diagnostics solution-wide at runtime.
*   **Speculative Validation**: AI can validate Unified Diffs in-memory before writing to disk, preventing compilation errors.
*   **Modernization Suite**: Automated **.NET 10 / C# 14** upgrades:
    *   **Lock Modernization**: Upgrades legacy `lock(this)` to high-performance `.NET 10 System.Threading.Lock`.
    *   **C# 14 Features**: Support for **Field-Backed Properties** (`field` keyword) and **Implicit Span Cleanup**.
    *   **TimeProvider Injection**: Replaces static `DateTime` calls with testable abstractions.
    *   **Record Conversion**: Surgical transformation of POCOs into positional records and back.
*   **Precision Scoping**: Analysis respects project and file boundaries to prevent "solution bleed."

---

## 🛠️ Installation (Local-Per-Solution Pattern)

Roslyn Sentinel is optimized when installed **locally per solution**. This ensures the AI agent always has a dedicated "expert" daemon for that specific codebase.

1.  **Clone & Publish**:
    ```bash
    dotnet publish RoslynSentinel.Server/RoslynSentinel.Server.csproj -c Release -o ./publish
    ```

2.  **Verify Setup**:
    ```bash
    ./publish/RoslynSentinel.Server.exe --mode=all --solution="C:/path/to/YourSolution.sln"
    ```

---

## ⚙️ Configuration for AI Agents

### **Feature Toggles**
Use these tools to customize the server's analytical footprint:
- `list_features()`: See all 40+ rules and their current status.
- `update_features(updates)`: Batch enable or disable rules (e.g., `[{"Key": "BoxingAllocation", "Value": false}]`).

### **Claude Desktop**
Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "roslyn-sentinel-project-a": {
      "command": "dotnet",
      "args": [
        "C:/path/to/RoslynSentinel/publish/RoslynSentinel.Server.dll",
        "--solution=C:/repos/ProjectA/ProjectA.sln",
        "--mode=all"
      ]
    }
  }
}
```

---

## ⌨️ Command Line Arguments

| Argument | Description | Default |
| :--- | :--- | :--- |
| `--solution=[path]` | Automatically loads the specified .sln or .csproj on startup. | None |
| `--mode=[modes]` | Comma-separated list of toolsets (Workspace, Intelligence, Refactor, Modernize, Quality, Generation). | `all` |
| `--port=[number]` | Switches transport to SSE (HTTP) on the specified port. | Stdio |

---

## 🤖 AI Workflow (The Safety Loop)

1.  **Diagnostic**: `get_comprehensive_health_report(limit: 10, offset: 0)`
2.  **Toggle**: `update_features([{"Key": "MultiTypeFile", "Value": false}])` if noise is too high.
3.  **Refactor**: `replace_member(filePath: "...", memberName: "MyMethod", newSource: "...")`
4.  **Sync**: `acknowledge_sync()` after any manual file system changes.

## 🧪 Verification

Roslyn Sentinel is backed by a robust suite of 142+ functional and integration tests.
```bash
dotnet test
```

## 📜 Unfinished Capabilities
See [UNFINISHED.md](./UNFINISHED.md) for the upcoming roadmap.
