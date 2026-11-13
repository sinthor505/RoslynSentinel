using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Splices an error-message variant into a saved transcript's first erroring tool result, leaving every other
/// byte of the recorded ResultJson alone. A static helper with no state; it defines no data type
/// (DTO-EXEMPT: static helper class, no data).
/// </summary>
/// <remarks>
/// Escaping: the server writes tool results with <see cref="SharedJsonOptions"/>, whose encoder is
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (no < for '&lt;', no ' for an apostrophe;
/// control characters, quotes and backslashes are still escaped). The same encoder is used here, and
/// <see cref="SpliceMessage"/> proves it per call: re-encoding the decoded recorded message must reproduce the
/// recorded bytes, otherwise it fails instead of silently changing the escaping.
/// </remarks>
public static class ResultOverride
{
    /// <summary>The literal token a variant may use for the fixture solution directory.</summary>
    public const string SolutionToken = "{SOLUTION}";

    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Keeps turns 1..<paramref name="cutTurn"/> of <paramref name="transcript"/> and rewrites the single erroring call
    /// of the cut turn to carry <paramref name="variant"/>. The cut turn is a NEW <see cref="AgentTranscriptTurn"/> with
    /// every field copied (Assists included); the transcript itself is not changed.
    /// </summary>
    /// <param name="cutTurn">Number of turns to keep; null means the first turn with a call whose ResultJson has isError:true.</param>
    /// <param name="solutionDirectory">Replaces <see cref="SolutionToken"/> in the variant's message and trailing text; null leaves the token.</param>
    /// <returns>
    /// The kept turns, and the 12-hex <see cref="ErrorVariantLoader.HashOf"/> of the decoded recorded message,
    /// which identifies the text that was replaced.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No turn has an erroring call, the cut turn has zero or several, a call's IsError flag disagrees with its ResultJson,
    /// the call has no errorData.message, or its errorCode differs from the variant's.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cutTurn"/> is below 1 or above the number of turns.</exception>
    public static (IReadOnlyList<AgentTranscriptTurn> Turns, string RecordedMessageHash) Apply(
        AgentTranscript transcript,
        ErrorVariant variant,
        int? cutTurn = null,
        string? solutionDirectory = null)
    {
        var cut = cutTurn ?? FirstErrorTurn(transcript);
        var kept = SavedTranscript.KeepTurns(transcript.Turns, cut);
        var turn = kept[^1];

        var (errorIndex, recorded, recordedCode, recordedMessage) = FindErrorCall(turn, cut);

        if (!string.Equals(recordedCode, variant.ErrorCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Turn {cut}, call {errorIndex + 1} ({recorded.ToolName}): the recorded errorData.errorCode is '{recordedCode}' but variant '{variant.Id}' targets '{variant.ErrorCode}'.");
        }

        var (newResultJson, newTrailing) = ApplyToCall(recorded, variant, solutionDirectory);

        var newCalls = new List<AgentToolCallRecord>(turn.ToolCalls);
        newCalls[errorIndex] = CopyCall(recorded, newResultJson, newTrailing);

        var turns = new List<AgentTranscriptTurn>(kept);
        turns[^1] = CopyTurn(turn, newCalls);
        return (turns, ErrorVariantLoader.HashOf(recordedMessage));
    }

    /// <summary>
    /// Finds the single call of <paramref name="turn"/> whose ResultJson has isError:true and reads its errorData. Shared by
    /// <see cref="Apply"/> and the transcript tool host so both pick the same overridden call.
    /// </summary>
    /// <param name="turn">The cut turn.</param>
    /// <param name="turnNumber">1-based turn number, for the messages.</param>
    /// <exception cref="InvalidOperationException">
    /// A call's IsError flag disagrees with its ResultJson, the turn has zero or several erroring calls, or the call has no
    /// errorData.errorCode and errorData.message strings.
    /// </exception>
    public static (int CallIndex, AgentToolCallRecord Call, string ErrorCode, string Message) FindErrorCall(
        AgentTranscriptTurn turn,
        int turnNumber)
    {
        var errorIndexes = new List<int>();
        for (var i = 0; i < turn.ToolCalls.Count; i++)
        {
            var call = turn.ToolCalls[i];
            var parsedIsError = IsErrorResult(call.ResultJson);
            if (parsedIsError != call.IsError)
            {
                throw new InvalidOperationException(
                    $"Turn {turnNumber}, call {i + 1} ({call.ToolName}): the record says IsError={call.IsError} but its ResultJson has isError:{(parsedIsError ? "true" : "false or absent")}.");
            }

            if (parsedIsError)
            {
                errorIndexes.Add(i);
            }
        }

        if (errorIndexes.Count != 1)
        {
            throw new InvalidOperationException(
                $"Turn {turnNumber} has {errorIndexes.Count} call(s) whose ResultJson has isError:true (of {turn.ToolCalls.Count} call(s)); exactly one is required.");
        }

        var recorded = turn.ToolCalls[errorIndexes[0]];
        if (!TryReadError(recorded.ResultJson, out var recordedCode, out var recordedMessage))
        {
            throw new InvalidOperationException(
                $"Turn {turnNumber}, call {errorIndexes[0] + 1} ({recorded.ToolName}): the erroring ResultJson has no errorData.errorCode and errorData.message strings.");
        }

        return (errorIndexes[0], recorded, recordedCode, recordedMessage);
    }

    /// <summary>
    /// The ResultJson and TrailingText <paramref name="recorded"/> would carry under <paramref name="variant"/>: the variant's
    /// message (with <see cref="SolutionToken"/> replaced) spliced into the recorded ResultJson, and
    /// <see cref="ApplyTrailing"/> on the trailing text. The caller has already checked the call is the one to override.
    /// </summary>
    public static (string ResultJson, string? TrailingText) ApplyToCall(
        AgentToolCallRecord recorded,
        ErrorVariant variant,
        string? solutionDirectory) =>
        (SpliceMessage(recorded.ResultJson, ReplaceSolutionToken(variant.Message, solutionDirectory)),
         ApplyTrailing(recorded.TrailingText, variant, solutionDirectory));

    /// <summary>
    /// Returns <paramref name="resultJson"/> with only the bytes of the errorData.message string value replaced by
    /// <paramref name="newMessage"/>, encoded with the server's relaxed escaping; every other byte is untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The JSON has no errorData.message string, or its recorded escaping is not what the relaxed encoder produces.
    /// </exception>
    public static string SpliceMessage(string resultJson, string newMessage)
    {
        var bytes = Encoding.UTF8.GetBytes(resultJson);
        var scan = Scan(bytes);
        if (scan.Message is null)
        {
            throw new InvalidOperationException("The ResultJson has no top-level errorData.message string value to replace.");
        }

        var recordedRaw = bytes.AsSpan(scan.MessageStart, scan.MessageLength);
        var reencoded = JsonEncodedText.Encode(scan.Message, Encoder).EncodedUtf8Bytes;
        if (!reencoded.SequenceEqual(recordedRaw))
        {
            throw new InvalidOperationException(
                "The recorded errorData.message uses an escaping the relaxed encoder does not reproduce, so a spliced value would not match its neighbours. Recorded: "
                + Encoding.UTF8.GetString(recordedRaw) + " Re-encoded: " + Encoding.UTF8.GetString(reencoded));
        }

        var replacement = JsonEncodedText.Encode(newMessage, Encoder).EncodedUtf8Bytes;
        var result = new byte[bytes.Length - scan.MessageLength + replacement.Length];
        bytes.AsSpan(0, scan.MessageStart).CopyTo(result);
        replacement.CopyTo(result.AsSpan(scan.MessageStart));
        bytes.AsSpan(scan.MessageStart + scan.MessageLength).CopyTo(result.AsSpan(scan.MessageStart + replacement.Length));
        return Encoding.UTF8.GetString(result);
    }

    /// <summary>
    /// The trailing text a variant leaves on the call: null variant trailing keeps <paramref name="recorded"/> as is, an
    /// empty one removes it (null), otherwise the variant's text with <see cref="SolutionToken"/> replaced.
    /// </summary>
    public static string? ApplyTrailing(string? recorded, ErrorVariant variant, string? solutionDirectory)
    {
        if (variant.Trailing is null)
        {
            return recorded;
        }

        return variant.Trailing.Length == 0 ? null : ReplaceSolutionToken(variant.Trailing, solutionDirectory);
    }

    /// <summary>Plain ordinal replace of <see cref="SolutionToken"/> with <paramref name="solutionDirectory"/>; the text is returned unchanged when the directory is null.</summary>
    public static string ReplaceSolutionToken(string text, string? solutionDirectory) =>
        solutionDirectory is null ? text : text.Replace(SolutionToken, solutionDirectory, StringComparison.Ordinal);

    /// <summary>
    /// Reads a tool result envelope: true only when the top-level isError is true and errorData has string errorCode and
    /// message values. <paramref name="message"/> is the decoded text (escapes resolved). Both are empty when false.
    /// </summary>
    public static bool TryReadError(string resultJson, out string errorCode, out string message)
    {
        var scan = Scan(Encoding.UTF8.GetBytes(resultJson));
        if (scan.IsError && scan.ErrorCode is not null && scan.Message is not null)
        {
            errorCode = scan.ErrorCode;
            message = scan.Message;
            return true;
        }

        errorCode = string.Empty;
        message = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="resultJson"/> parses as an object whose top-level isError is true.</summary>
    public static bool IsErrorResult(string resultJson) => Scan(Encoding.UTF8.GetBytes(resultJson)).IsError;

    private static int FirstErrorTurn(AgentTranscript transcript)
    {
        for (var i = 0; i < transcript.Turns.Count; i++)
        {
            if (transcript.Turns[i].ToolCalls.Any(call => IsErrorResult(call.ResultJson)))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException(
            $"No turn of the transcript ({transcript.Turns.Count} turn(s)) has a call whose ResultJson has isError:true.");
    }

    // TODO statusMessage targets (plan Step 5): variants only target errorData.message of isError envelopes
    // Walks the top-level object only: isError, and errorData.errorCode / errorData.message. Anything else, including a
    // nested errorData, is skipped. Not an error envelope or not JSON: IsError false and no message.
    private static (bool IsError, string? ErrorCode, string? Message, int MessageStart, int MessageLength) Scan(byte[] utf8)
    {
        var isError = false;
        string? errorCode = null;
        string? message = null;
        var messageStart = -1;
        var messageLength = 0;

        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (false, null, null, -1, 0);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                if (name == "isError")
                {
                    isError = reader.TokenType == JsonTokenType.True;
                }
                else if (name == "errorData" && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var inner = reader.GetString();
                        reader.Read();
                        if (inner == "errorCode" && reader.TokenType == JsonTokenType.String)
                        {
                            errorCode = reader.GetString();
                        }
                        else if (inner == "message" && reader.TokenType == JsonTokenType.String)
                        {
                            message = reader.GetString();
                            // TokenStartIndex is the opening quote; ValueSpan is the raw (still escaped) bytes between the quotes.
                            messageStart = (int)reader.TokenStartIndex + 1;
                            messageLength = reader.ValueSpan.Length;
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return (false, null, null, -1, 0);
        }

        return (isError, errorCode, message, messageStart, messageLength);
    }

    // Every field is copied; AgentTranscriptTurn.Assists included (TranscriptResume.RewriteTurns drops it, do not copy that).
    // ModelMessage is shared by reference: nothing here mutates it.
    private static AgentTranscriptTurn CopyTurn(AgentTranscriptTurn turn, List<AgentToolCallRecord> calls) => new()
    {
        TurnNumber = turn.TurnNumber,
        ModelMessage = turn.ModelMessage,
        ModelLatency = turn.ModelLatency,
        StartedAt = turn.StartedAt,
        CompletedAt = turn.CompletedAt,
        ToolCalls = calls,
        Assists = turn.Assists is null ? null : [.. turn.Assists],
    };

    private static AgentToolCallRecord CopyCall(AgentToolCallRecord call, string resultJson, string? trailingText) => new()
    {
        ToolName = call.ToolName,
        ArgumentsJson = call.ArgumentsJson,
        ResultJson = resultJson,
        IsError = call.IsError,
        Latency = call.Latency,
        StartedAt = call.StartedAt,
        CompletedAt = call.CompletedAt,
        ToolCallId = call.ToolCallId,
        TrailingText = trailingText,
    };
}
