# McpServerStatus: replace the additive toolListing and settings parameters with one view enum

**Status:** PROPOSED 2026-11-25. Owner approved writing it up; design to be settled with `plan-design` in a fresh chat.

## Motivation

`McpServerStatus` has two independent listing parameters that add sections to the same response:
`toolListing` (`none | inactive | all`, `Tools.Basic/ServerStatusTools.cs:66`, enum at :306) with its
`toolNameFilter` (:67-68), and `settings` (`none | nonDefault | all`, :70, added by
`plan_startup_configuration_registry` Step 21). Every listing response also carries the full status block.

Measured on 2026-11-25 against the live server (build bb675d5e, claude-lean mode, 124 declared tools):

| Call | Payload | Result |
| --- | --- | --- |
| status only (no listing) | about 3.0 KB | inline |
| `settings: all` (76 rows, after the scope grouping in bb675d5e) | about 10 KB | inline |
| `settings: all` before bb675d5e | 14,545 bytes | offloaded as a large result, 63 of 76 rows inline |
| `toolListing: all` | 25,110 bytes | offloaded, 51 of 124 tools inline |

The large-result cap is about 15 KB. With additive parameters each list shares the cap with the status
block (and with the other list if both are requested). `toolListing: all` would still exceed the cap on its own
(about 22 KB without the status block); see Open questions.

Schema cost matters too: `ClaudeLeanSchemaBudgetTests` allows 50000 characters and measured 49677 before the
`settings` parameter was added (`plan_startup_configuration_registry`, decision Q21). Two enum parameters with
descriptions cost more schema characters than one.

## Proposal

One parameter, `view`, selects exactly one section; it defaults to `status`, so a call with no listing
argument behaves as today (both listings default to `none` now).

Decided so far (owner, 2026-11-25): a single enum, default `status`, each view returns only its own section.

Draft value set (a flat enum keeps the two levels each list has today):

| `view` | Returns | Replaces |
| --- | --- | --- |
| `status` (default) | the status block, incl. `Configuration.Problems`, `Halted`, `UndeclaredEnvCount` and its hint | `toolListing: none, settings: none` |
| `inactiveTools` | declared tools not active in this mode, each with `enabledBy`; hidden parameter groups | `toolListing: inactive` |
| `allTools` | every declared tool; hidden parameter groups | `toolListing: all` |
| `settings` | settings not at their default, plus the undeclared env names | `settings: nonDefault` |
| `allSettings` | every setting, grouped by scope, plus the undeclared env names | `settings: all` |

- `toolNameFilter` applies to the two tool views only.
- The list views carry a minimal identity header (server pid, build sha) so a caller can tell which instance
  answered; the rest of the status block is omitted.
- The configuration halt keeps working unchanged: the halt filter allows `McpServerStatus` by tool name, and
  the problems stay in the `status` view, which the halt message points at.

Migration (CLAUDE.md migration-first): add `view` beside the old parameters; hide `toolListing` and `settings`
from the schema but keep binding them (a call with a parameter missing from the client's cached schema still
reaches the server: `McpServerStatus(settings: all)` worked from a session whose schema lacked `settings`,
2026-11-25); move the hints, description, docs and CLAUDE.md to `view`; remove the old parameters last.

## Alternatives considered, not pursued

- **Three values `status | toollist | options`** (the first sketch): loses `inactive` vs `all` and
  `nonDefault` vs `all`. `toolListing: inactive` is the documented gated-tool check (CLAUDE.md "Gated tools",
  the unknown-tool hints at `Server.Advanced/CallToolFailureHandler.cs:120` and :131), so the inactive view is needed.
- **Keep two parameters and only compact the rows**: done for settings (bb675d5e, rows grouped by scope,
  14.5 KB to about 10 KB); does not shrink the schema and leaves the status block in every listing.
- **A `view` enum plus a separate `detail: changed | all` qualifier**: two parameters again, and the qualifier
  means different things per view; weaker for small models than one closed set.

## Open questions

- Value names: `inactiveTools`/`allTools`/`settings`/`allSettings`, or `tools`/`toolsAll`/`settings`/`settingsAll`, or other.
  Enum casing must follow the existing lowercase-member convention of the two current enums.
- `allTools` is about 22 KB even without the status block. Options: compact rows (most `enabledBy` strings
  repeat per class, e.g. "not in the claude-lean core toolset; restart with --mode Asyncify instead of
  claude-lean"), group tools by class or by `enabledBy`, or accept paging for the full list.
- Does any caller rely on getting status and a listing in one call? (Tests do:
  `McpServerStatusToolListingTests` asserts status fields on listing calls; the in-server hints do not.)
- `toolTelemetry` appeared in the `toolListing: all` response but not in the `settings: all` one; which view
  owns it is undecided (not traced).
- Whether the old parameters stay hidden-but-bound for a fixed period or until a named eval run shows no use.

## Cost / risk

- C#: `toolListing` appears in 8 files (41 hits; 24 in `Tests.Tools.Basic/McpServerStatusToolListingTests.cs`),
  including the two model-facing hints in `CallToolFailureHandler.cs:120,131` and the tool description
  (`ServerStatusTools.cs:55`, "call with toolListing=inactive"). `StartupSettings.UndeclaredEnvHint` names the
  `settings` values.
- Guidance and docs: `CLAUDE.md:54`, `reference_architecture_map.md:95,297`,
  `reference_changing_claude_lean_tools.md:15,89`, `reference_not_found_error_inventory.md:343`,
  `proposal_ilspy_derived_capabilities.md:165`; the tool-surface golden catalogs in Tests.Harness and
  Tests.ModelEval change with the schema.
- Model-facing change: eval prompts, cached schemas and learned habits use `toolListing`; keeping the old
  parameters bound but hidden covers them during the migration.
- A changed tool schema needs a fresh chat session to be visible to Claude Code (a `/mcp` reconnect did not
  refresh it, 2026-11-25).

## Related

- `docs/current/plans/closed/plan_startup_configuration_registry.md` (added `settings`, decisions Q6, Q21, Q24)
- `docs/current/references/reference_tool_response_pipeline.md` (large-result cap and offload)
- `docs/current/references/reference_changing_claude_lean_tools.md`
- `closed/proposal_tool_parameter_surface_options.md` (hidden parameter groups, `--hide-params`)
