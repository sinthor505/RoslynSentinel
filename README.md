# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, eliminating cold-start delays and providing deep semantic analysis.

## 🚀 Key Features

*   **High-Performance Orchestration**: Parallelized engine that scans 80+ projects concurrently.
*   **One-Shot Health Diagnostics**: Get a bird's-eye view of your solution's health with `get_comprehensive_health_report`.
*   **Speculative Validation**: AI can validate Unified Diffs in-memory before writing to disk.
*   **Modernization Suite**: Automated .NET 9 upgrades (TimeProvider, Records, modern Guard Clauses).
*   **Precision Scoping**: Analysis respects project and file boundaries to prevent "solution bleed."

---

## 🛠️ Installation (Local-Per-Solution Pattern)

Roslyn Sentinel is optimized when installed **locally per solution** or per logical group of solutions. This ensures the AI agent always has a dedicated "expert" daemon for that specific codebase.

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

### **Windsurf / Cursor**
Most agents support `stdio`. If you are running multiple solutions simultaneously, give each instance a **unique name** in the config to avoid context collision.

---

## 🌐 Advanced: Running as a Service (SSE)

If you prefer to run Roslyn Sentinel as a background service accessible via HTTP (SSE transport), use the `--port` argument. This is ideal for shared development environments or when multiple agents need access to the same "hot" solution.

**Start the Service:**
```bash
./RoslynSentinel.Server.exe --port=5001 --solution="C:/repos/ProjectA/ProjectA.sln"
```

**Connect (Claude Config Example):**
```json
{
  "mcpServers": {
    "roslyn-project-a": {
      "url": "http://localhost:5001/mcp"
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
| `--port=[number]` | Switches transport to SSE on the specified port. | Stdio |
| `--host=[address]` | The host address for SSE transport. | `localhost` |

---

## 🤖 AI Workflow (The Safety Loop)

1.  **Diagnostic**: `get_comprehensive_health_report(engines: ["Modernization", "Safety"])`
2.  **Analysis**: `get_blast_radius(filePath: "...", line: 10, column: 5)`
3.  **Propose**: `validate_proposed_diff(filePath: "...", diff: "...")`
4.  **Sync**: `acknowledge_sync()` after manual edits.

## 🧪 Verification

Roslyn Sentinel is backed by 150+ functional tests.
```bash
dotnet test
```

## 📜 Unfinished Capabilities
See [UNFINISHED.md](./UNFINISHED.md) for the upcoming roadmap.
