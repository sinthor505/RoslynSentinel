# Plan: add SnippetSymbolLocator encode/decode helper

## Background

`RoslynSentinel.Common/` holds small standalone helper types, one per file (see
`ContextHelper.cs`, `PersistentWorkspaceManager.cs` for the existing pattern). We need a new one:
a type that packs `(filePath, contextSnippet, symbolName, lineBefore, lineAfter)` into a single
opaque string, and can unpack that string back into its fields.

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
- Set `locator = null` and return `false` immediately if `input` is null, empty, or does not start
  with `Marker` (use `input.StartsWith(Marker, StringComparison.Ordinal)`). Do this check first,
  before attempting any decoding.
- Otherwise, strip the `Marker` prefix, base64-decode the remainder, UTF-8-decode the bytes to a
  JSON string, and deserialize it back to a `SnippetSymbolLocator` using
  `System.Text.Json.JsonSerializer.Deserialize<SnippetSymbolLocator>`.
- Wrap the decode/deserialize step in a try/catch around `FormatException` (bad base64) and
  `System.Text.Json.JsonException` (bad JSON). On either exception, set `locator = null` and
  return `false`. Do not let either exception escape `TryDecode`.
- On success, set `locator` to the deserialized value and return `true`.

## Tests

Add a new test file `RoslynSentinel.Tests/SnippetSymbolLocatorTests.cs` (same project and
attribute style as the existing `RoslynSentinel.Tests/ContextHelperTests.cs` -- open that file
first and match its test attributes, namespace, and class layout exactly). Add these test cases:

1. `Encode` then `TryDecode` round-trips a simple locator (short snippet, no special characters) --
   assert every field of the decoded record equals the original.
2. `Encode` then `TryDecode` round-trips a locator whose `ContextSnippet` contains a colon (`:`), a
   double-quote (`"`), and a newline character -- assert every field still matches exactly.
3. `TryDecode` on a plain doc-comment-ID-shaped string (e.g. `"M:Foo.Bar.Baz"`) returns `false` and
   sets `locator` to `null`.
4. `TryDecode` on an empty string returns `false` and sets `locator` to `null`.
5. `TryDecode` on a string that starts with the `"SSL1:"` marker but has garbage (non-base64, or
   base64 that isn't valid JSON) after it returns `false` and sets `locator` to `null` -- it must
   not throw.

## Verification

- Build the solution: 0 errors, 0 new warnings.
- Run the new test file: all 5+ test cases pass.
- Do not modify any other file in this step.
