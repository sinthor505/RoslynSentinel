using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Loads a saved transcript.json as an <see cref="AgentTranscript"/> and picks the turns a resume keeps.
/// A static helper with no state; it defines no data type.
/// </summary>
public static class SavedTranscript
{
    /// <summary>
    /// AgentTranscript's list properties (Turns, Tools) are get-only, so the default deserializer skips them and the turns are lost.
    /// Populate fills the existing lists instead.
    /// </summary>
    private static readonly JsonSerializerOptions PopulateOptions = new()
    {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    /// <summary>Reads and deserializes the transcript.json at <paramref name="transcriptJsonPath"/>, Turns and Tools included.</summary>
    /// <exception cref="InvalidOperationException">The file deserializes to null, or it has turns in the file but none were read.</exception>
    public static AgentTranscript Load(string transcriptJsonPath)
    {
        var json = File.ReadAllText(transcriptJsonPath);
        var transcript = JsonSerializer.Deserialize<AgentTranscript>(json, PopulateOptions)
            ?? throw new InvalidOperationException($"Transcript at '{transcriptJsonPath}' deserialized to null.");
        if (json.Contains("\"ToolCalls\"") && transcript.Turns.Count == 0)
        {
            throw new InvalidOperationException($"Transcript '{transcriptJsonPath}' has turns in the file but none were read.");
        }

        return transcript;
    }

    /// <summary>Turns 1..<paramref name="keepTurns"/> of <paramref name="turns"/>, in order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keepTurns"/> is below 1 or above the number of turns.</exception>
    public static IReadOnlyList<AgentTranscriptTurn> KeepTurns(IReadOnlyList<AgentTranscriptTurn> turns, int keepTurns)
    {
        if (keepTurns < 1 || keepTurns > turns.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keepTurns), keepTurns,
                $"The transcript has {turns.Count} turn(s); keep between 1 and {turns.Count}.");
        }

        return turns.Take(keepTurns).ToList();
    }
}
