using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Wraps a tool call's mandatory <c>reason</c> string so it can't be silently swapped with an
/// adjacent string parameter (e.g. filePath) at a call site — the compiler rejects a plain string
/// passed where a <see cref="ToolCallReason"/> is expected. Only string -> ToolCallReason is
/// implicit; there is no reverse conversion, so a ToolCallReason can't slide back into a
/// string-typed parameter by accident.
/// </summary>
[JsonConverter(typeof(ToolCallReasonJsonConverter))]
public readonly record struct ToolCallReason
{
    public string Value { get; }

    public ToolCallReason(string value) => Value = value;

    public static implicit operator ToolCallReason(string value) => new(value);

    public override string ToString() => Value ?? string.Empty;
}

public sealed class ToolCallReasonJsonConverter : JsonConverter<ToolCallReason>
{
    public override ToolCallReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, ToolCallReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
