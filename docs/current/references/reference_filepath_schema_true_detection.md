# Reference: detecting bare-`true` JSON schema nodes (the `FilePath` "Unrecognized schema: true" bug)

**Status:** CURRENT 2026-10-17. Bug fixed in 2026-09 (wire types are `string`; `RoslynSentinel.Common/McpToolSchemaPatcher.cs` exists as a safety net). The detection method below is still the way to prove a `tools/list` schema is clean.

## Purpose
LM Studio's grammar converter rejects any tool schema containing a bare JSON Schema boolean node
(`true`) with a 400: `JSON schema conversion failed: Unrecognized schema: true` (repeated once per
occurrence). It appears the instant MCP tools are enabled in a chat, before any prediction, and breaks
`applyPromptTemplate` as well as `predict`. Consult this when LM Studio refuses a tool set, or after
adding any `[McpServerTool]` parameter whose type is a custom struct.

## How it works
Any `[McpServerTool]` parameter, or POCO property inside a tool parameter, typed `FilePath` (a custom
struct whose `JsonConverter` only implements `ReadAsPropertyName`/`WriteAsPropertyName`) makes
`System.Text.Json.Schema.JsonSchemaExporter` emit the literal boolean `true` for that node instead of
`{"type":"string"}`. Legal in JSON Schema 2020-12, rejected by LM Studio. Two shapes triggered it:

1. Dictionary key type: `Dictionary<FilePath, string>` as a parameter (for example `ApplyDiff.changes`);
   the exporter cannot build a `propertyNames` schema for a non-trivial key.
2. A plain `public FilePath FilePath { get; set; }` property on a class used as `List<T>` items
   (for example `HandlerExtractTarget`, `FlagCandidateTarget`).

Fix pattern: make the wire-facing type plain `string` and resolve to `FilePath` right after the schema
boundary. Check sibling types first (`BatchTarget`, `FailureDetail` were already `string`).

## Usage
Do not guess from source or from error-occurrence counting (a first pass wrongly flagged a
block-commented dead method). Dump the live schema and scan it:

```powershell
# HTTP transport only; <port> is the server's port
$r = Invoke-WebRequest -Method Post -Uri http://localhost:<port>/mcp `
  -ContentType 'application/json' -Headers @{ Accept = 'application/json, text/event-stream' } `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
# the response is SSE: take the line starting with "data: " and save its JSON as toolslist.json
```

Walk the parsed JSON recursively and flag nodes that ARE the literal boolean (`properties.filePath ==
true` instead of `{"type":["string","null"]}`). A dictionary with a `"default": true` key is a normal
boolean default and is NOT the bug. The hit count must equal the number of "Unrecognized schema: true"
repeats in the LM Studio error: use it as a checksum. Re-run after the fix and a server restart to
confirm zero hits across all tools. `tools/list` did not paginate (102 tools in one page,
`nextCursor: null`); check `result.nextCursor` if the tool count grows.

## Gotchas
- Treat any new `FilePath`-typed tool parameter or nested property as suspect until the scan is clean.
- Only the stale-server case lies: scan the restarted server, not the one running the old build.
