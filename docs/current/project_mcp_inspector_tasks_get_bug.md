---
name: project-mcp-inspector-tasks-get-bug
description: "Confirmed against the MCP spec itself (streamable-http transport page): Mcp-Name is required ONLY for tools/call, resources/read, prompts/get. ModelContextProtocol.Extensions.Tasks 2.2.0 registers tasks/get|update|cancel with RoutingNameParameter=\"taskId\" anyway, and ModelContextProtocol.AspNetCore 2.2.0's header validator over-generalizes the spec's fixed 3-method list into \"any handler with a routing-name parameter\" — an upstream SDK spec-conformance bug (HTTP transport only; stdio/in-process transports unaffected), not a RoslynSentinel bug and not an Inspector bug"
metadata:
  type: project
  originSessionId: ed38ed5b-d0aa-4f27-828c-fcbbb5d0b086
  modified: 2026-08-26T03:10:07.386Z
---

**Symptom:** against the HTTP flavor of the server, calling a task-eligible tool (e.g. `Features`)
under protocol version `2026-07-28` correctly returns a `CreateTaskResult`/`taskId`, but every
subsequent `tasks/get` poll (from MCP Inspector, or any spec-compliant client) fails with
`{"code":-32020,"message":"Missing required Mcp-Name header."}` — even when the client *does* send
a correct `Mcp-Method: tasks/get` header matching the body. The task then appears stuck "working"
forever from the client's point of view, even though the underlying tool call likely finished
server-side.

**Root cause — confirmed two ways: (1) decompiling both packages with `ilspycmd` since source isn't
published for either, and (2) checking the actual spec at
`https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http`:**

- The spec's "Standard Request Headers" table is explicit and closed-ended:

  | Header | Source Field | Required For |
  |---|---|---|
  | `Mcp-Method` | `method` | All requests |
  | `Mcp-Name` | `params.name` or `params.uri` | `tools/call`, `resources/read`, `prompts/get` requests |

  `Mcp-Name` is a **fixed list of exactly three methods** — there is no spec language about a
  generic "routing name parameter" concept, and `tasks/get`/`tasks/update`/`tasks/cancel` are not
  in that list. A spec-compliant client (Inspector included) has no reason to ever send `Mcp-Name`
  on a `tasks/get` call. This falsifies any theory that Inspector was non-conformant.
- `ModelContextProtocol.AspNetCore` 2.2.0's `StreamableHttpHandler.ValidateMcpHeaders` does not
  implement the spec's fixed 3-method list directly. Instead it requires `Mcp-Name` **whenever**
  `GetRoutingNameParameter(method, serverOptions.RequestHandlers)` returns non-null — a helper that
  hardcodes `tools/call`→`"name"`, `prompts/get`→`"name"`, `resources/read`→`"uri"`, and otherwise
  looks up *any* custom-registered `McpServerRequestHandler` for that method and returns its
  `RoutingNameParameter`. This is a generalization the spec never asked for.
- `ModelContextProtocol.Extensions.Tasks` 2.2.0's `WithTasks(...)` registers exactly such handlers:
  `tasks/get`, `tasks/update`, and `tasks/cancel` are all registered with
  `RoutingNameParameter = "taskId"` (confirmed in the decompiled registration code around
  `set_Method("tasks/get")` / `set_RoutingNameParameter("taskId")`). Combined with the AspNetCore
  validator's over-generalization above, this makes the validator wrongly demand `Mcp-Name` for
  three methods the spec never lists.
- Conclusion: this is a genuine upstream spec-conformance bug spanning both packages —
  `ModelContextProtocol.AspNetCore` generalizes past what the spec defines, and
  `ModelContextProtocol.Extensions.Tasks` registers a `RoutingNameParameter` that trips that
  over-generalization. It is not a RoslynSentinel bug and not an Inspector bug.

**Why the harness tests still pass:** `ValidateMcpHeaders`/this whole header-validation path lives
entirely in `StreamableHttpHandler` (the ASP.NET Core / Streamable HTTP transport). The
[[project_mcp_tasks_test_harness_plan]] harness uses `WithStreamServerTransport` over an in-process
`System.IO.Pipelines.Pipe` (the stdio-style stream transport), which has no such header-validation
layer at all — so `tasks/get`/`tasks/cancel` work fine there. **This bug is specific to the HTTP
server flavor** (`RoslynSentinel.Server.Advanced`'s `--http` mode / `ServerHttp.cs`), not the stdio
flavor, and not the tasks feature's core logic.

**How to apply:** don't chase this as a RoslynSentinel-authored bug — it's not in our code, it's in
`ModelContextProtocol.AspNetCore`/`ModelContextProtocol.Extensions.Tasks` 2.2.0 upstream, and is
verifiably a spec-conformance defect (not a judgment call) per the header table above. To verify a
task-backed tool call actually completed when testing via the HTTP transport (Inspector or any
other client), check the tool's real-world side effect directly (file written, feature flag
changed) rather than trusting `tasks/get`'s status — or test via the stdio transport / the
automated harness's in-process `McpClient` instead, where polling isn't affected. If either NuGet
package ships a fix (e.g. a next `2.2.x`/`2.3.0`), check whether `GetRoutingNameParameter` stops
generalizing past the spec's fixed 3-method list, or whether the Tasks extension stops declaring a
`RoutingNameParameter` for `tasks/*` methods — that's the specific regression to look for before
upgrading and assuming it's fixed. This would also be a reasonable upstream bug report to file
(SDK repo, not the spec repo — the spec itself is correct and unambiguous here).
