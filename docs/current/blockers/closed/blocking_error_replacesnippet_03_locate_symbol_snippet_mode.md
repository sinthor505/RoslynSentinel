# Repeated tool failure - `ReplaceSnippet` during plan step 03-locate-symbol-snippet-mode.md

**Written automatically by PlanStepRunner** when its repeated-failure breaker tripped.

**Run directory:** `C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\PlanStepRunner\20260916-184526-620`
**Transcript:** `C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\PlanStepRunner\20260916-184526-620\03-locate-symbol-snippet-mode\Logs`
**Step:** `03-locate-symbol-snippet-mode.md` (readOnly=False, buildOptional=False)

## What happened

`ReplaceSnippet` failed 3 consecutive times with the same
failure signature across turns 15–17. The run was
terminated rather than allowed to consume its remaining turn budget re-issuing the
same call.

**Signature:** `ReplaceSnippet|InvalidArgument|RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`

## Final failing call

Arguments:

```json
{"action":"apply","reason":"Add contextSnippet null check at start of method body","filepath":"RoslynSentinel.Server.Basic/SentinelSymbolTools.cs","newContent":"        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);\n\n        // Snippet-based mode: when contextSnippet is supplied, it requires filepath.\n        if (contextSnippet != null)\n        {\n            if (string.IsNullOrEmpty(filepath))\n            {\n                return new ToolResult<object>\n                {\n                    Success = false,\n                    Error = new ResultError(ToolErrorCode.MissingParameter, \"contextSnippet requires filepath to also be supplied.\")\n                };\n            }\n\n            try\n            {\n                var result = await _symbolNavigationEngine.LocateSymbolBySnippetAsync(filePathResolved, contextSnippet, symbolName, lineBefore, lineAfter, cancellationToken);\n                if (result.Count == 0)\n                {\n                    return new ToolResult<object>\n                    {\n                        Success = false,\n                        Error = new ResultError(ToolErrorCode.Exception, $\"No symbol found at snippet '{contextSnippet}' in file '{filepath}'.\")\n                    };\n                }\n\n                return new ToolResult<object>\n                {\n                    Success = true,\n                    Data = result,\n                    TotalRecords = result.Count,\n                    WorkspaceVersion = _workspaceManager.WorkspaceVersion\n                };\n            }\n            catch (Exception ex)\n            {\n                _logger.LogError(ex, \"LocateSymbol failed for snippet '{ContextSnippet}'\", contextSnippet);\n                return new ToolResult<object>\n                {\n                    Success = false,\n                    Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, \"LocateSymbol\")\n                };\n            }\n        }\n\n        try\n        {\n            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch, cancellationToken);","oldContent":"        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);\n\n        try\n        {\n            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch, cancellationToken);"}
```

Result:

```json
{"serverVersion":"1.0.0.0","serverBuildTimeUtc":"2026-09-16T19:11:17.8952737Z","serverBinaryPath":"C:\\Users\\Administrator\\source\\repos\\RoslynSentinel-TestRuns\\PlanStepRunner\\20260916-184526-620\\03-locate-symbol-snippet-mode\\Worktree\\bin-runner\\Advanced\\RoslynSentinel.Server.Advanced.dll","serverPid":26020,"responseId":"11939e2c-f8b6-43ea-8d0d-8998a5dfaf84","success":false,"error":{"errorCode":"InvalidArgument","message":"ReplaceSnippet: newContent is 2026 chars (limit 2000). If this is a structural change (rename, signature, extract, add/replace a member) rather than free text, use the matching Roslyn tool (RenameSymbol, ChangeSignature, ExtractMethodSafe, Member) - those have no size limit. Otherwise split the edit into several smaller ReplaceSnippet calls, one per contiguous region."},"findings":[],"directiveKind":"Proceed","hasMorePages":false}
```

## Resolution

Confirmed tool defect: `DescribeExceededSnippetSizeBounds` in
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` compared raw `string.Length` against the
2000-char cap, so per-line leading indentation counted as "content." The failing call's
`newContent` was a legitimately-sized, deeply-nested edit (an `if` block with a nested try/catch)
that only exceeded the cap because of indentation depth.

Fixed 2026-09-16 (commit `6e3503c`, amended to include this doc): `DescribeExceededSnippetSizeBounds`
now measures `oldContent`/`newContent` length after stripping each line's leading indentation via
a new `CountCharsIgnoringLeadingIndentation` helper, applied identically to both parameters. Build
succeeded with 0 errors.
