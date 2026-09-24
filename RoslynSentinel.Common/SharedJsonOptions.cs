using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Single shared <see cref="JsonSerializerOptions"/> for all MCP tool request/response
/// serialization. UnsafeRelaxedJsonEscaping stops System.Text.Json's default HTML-safe encoder
/// from escaping printable ASCII (notably &lt;/&gt; as </>) in MCP tool output -
/// escaped angle brackets forced a model to mentally decode C# generic syntax like
/// List&lt;(int, string)&gt; back into literal characters, and adjacent literal parentheses were
/// silently dropped during that reconstruction (see plan_replacesnippet_whitespace_tolerant_match.md's
/// companion investigation for the confirmed repro). None of this output crosses into HTML, so the
/// default encoder's escaping serves no purpose here and only adds a transcription hazard.
/// </summary>
public static class SharedJsonOptions
{
    public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
}
