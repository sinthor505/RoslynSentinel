# Step 1 — Add SnippetSymbolLocator encode/decode helper

**This step implements only this file.** Do not read, open, or act on any other plan step file
(e.g. via `ProjectDoc`) — another process runs each step as its own isolated task.

## Prior state

Nothing has changed yet except test baseline counts recorded in step 0. No production code
changed.

## Files this step touches

- `RoslynSentinel.Common/SnippetSymbolLocator.cs` (new file)
- A new test file under `RoslynSentinel.Tests/` (e.g. `SnippetSymbolLocatorTests.cs`)

## Context

`RoslynSentinel.Common/` holds small standalone helper types, one per file (see
`ContextHelper.cs`, `PersistentWorkspaceManager.cs` for the existing pattern). This step adds a
new one: a type that packs `(filePath, contextSnippet, symbolName, lineBefore, lineAfter)` into a
single opaque string, and can unpack that string back into its fields. This will be used by a
later step to let tools identify a local variable or parameter (which has no doc-comment ID) via
a token instead.

## Task

Create a new file `RoslynSentinel.Common/SnippetSymbolLocator.cs` containing:

1. A record:

```csharp
public record SnippetSymbolLocator(
    string FilePath,
    string ContextSnippet,
    string SymbolName,
    string? LineBefore,
    string? LineAfter);
```

2. A constant marker string: `private const string Marker = "SSL1:";`

3. A static method `Encode`:

```csharp
public static string Encode(SnippetSymbolLocator locator)
```

It must:
- Serialize `locator` to JSON using `System.Text.Json.JsonSerializer.Serialize`.
- Base64-encode the resulting JSON string's UTF-8 bytes using `Convert.ToBase64String`.
- Return `Marker + <base64 string>`.

4. A static method `TryDecode`:

```csharp
public static bool TryDecode(string input, out SnippetSymbolLocator? locator)
```

It must:
- Set `locator = null` and return `false` immediately if `input` is null, empty, or does not
  start with `Marker` (use `input.StartsWith(Marker, StringComparison.Ordinal)`). Do this check
  first, before attempting any decoding.
- Otherwise, strip the `Marker` prefix, base64-decode the remainder, UTF-8-decode the bytes to a
  JSON string, and deserialize it back to a `SnippetSymbolLocator` using
  `System.Text.Json.JsonSerializer.Deserialize<SnippetSymbolLocator>`.
- Wrap the decode/deserialize step in a try/catch around `FormatException` (bad base64) and
  `System.Text.Json.JsonException` (bad JSON). On either exception, set `locator = null` and
  return `false`. Do not let either exception escape `TryDecode`.
- On success, set `locator` to the deserialized value and return `true`.

## Tests

Add a new test file `RoslynSentinel.Tests/SnippetSymbolLocatorTests.cs` (open
`RoslynSentinel.Tests/ContextHelperTests.cs` first and match its test attributes, namespace, and
class layout exactly). Add these test cases:

1. `Encode` then `TryDecode` round-trips a simple locator (short snippet, no special characters)
   — assert every field of the decoded record equals the original.
2. `Encode` then `TryDecode` round-trips a locator whose `ContextSnippet` contains a colon (`:`),
   a double-quote (`"`), and a newline character — assert every field still matches exactly.
3. `TryDecode` on a plain doc-comment-ID-shaped string (e.g. `"M:Foo.Bar.Baz"`) returns `false`
   and sets `locator` to `null`.
4. `TryDecode` on an empty string returns `false` and sets `locator` to `null`.
5. `TryDecode` on a string that starts with the `"SSL1:"` marker but has garbage (non-base64, or
   base64 that isn't valid JSON) after it returns `false` and sets `locator` to `null` — it must
   not throw.

## Out of scope for this step

- Do not touch `LocateSymbol`, `RenameSymbol`, `SymbolNavigationEngine`, or any MCP tool. This
  step only adds the standalone encode/decode type and its tests.

## Gate

Full solution build clean (via the `Build` MCP tool, fullBuild scope), and the new test file's
cases all pass. Report the build result and test pass count, and stop — do not proceed to any
other step.
