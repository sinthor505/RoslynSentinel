using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Common;

/// <summary>
/// Always-on server-side call transcript of one server host (one session): one JSONL line per <c>tools/call</c> with the
/// start time, tool name, call id and agent id (when known), the arguments as received, <c>isError</c>, the response text
/// as sent and the duration. Written to <c>&lt;scratch root&gt;/call-transcripts/calls-&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;-&lt;short guid&gt;.jsonl</c>,
/// created on the first recorded call; files of earlier sessions are swept by age and count when this object is constructed
/// (through <see cref="RuntimeStateRetention.SweepLogs"/>, the sweeper the host logs use).
/// <para>
/// Registered as a DI singleton so every host has its own file; the writer is the call-tool filter
/// <c>AddToolCallTranscriptFilter</c> (Server.Advanced). Best effort by design: <see cref="Record"/> never throws, and a write
/// failure is reported once through <see cref="Debug"/> (never stdout, which the stdio transport owns).
/// </para>
/// </summary>
public sealed class ToolCallTranscript
{
    /// <summary>File-name prefix of a transcript file; also the stem the sweep matches.</summary>
    public const string FilePrefix = "calls-";

    /// <summary>File extension of a transcript file.</summary>
    public const string FileExtension = ".jsonl";

    /// <summary>Folder under the scratch root that holds the transcripts.</summary>
    public const string FolderName = "call-transcripts";

    // Relaxed escaping keeps quotes and '+' readable; the file is a local log, never embedded in HTML (same choice as InProcessCommitRoute's JSONL).
    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _writeLock = new();
    private bool _directoryCreated;
    private int _failureLogged;

    /// <summary>
    /// Creates the transcript for one host and sweeps old files in <paramref name="directory"/>. A null directory means the
    /// default folder under <see cref="ScratchRoot"/>; if that cannot be resolved the transcript is disabled (<see cref="FilePath"/> is null).
    /// </summary>
    /// <param name="directory">Target folder (tests pass a temp folder); null for the default.</param>
    /// <param name="maxFiles">Sweep limit on the number of transcript files; default <see cref="RuntimeStateRetention.LogMaxFiles"/>.</param>
    /// <param name="maxAge">Sweep limit on file age; default <see cref="RuntimeStateRetention.LogMaxAge"/>.</param>
    public ToolCallTranscript(string? directory = null, int maxFiles = RuntimeStateRetention.LogMaxFiles, TimeSpan? maxAge = null)
    {
        try
        {
            var folder = directory ?? Path.Combine(ScratchRoot.Resolve(), FolderName);
            var name = $"{FilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}{FileExtension}";
            DirectoryPath = folder;
            FilePath = Path.Combine(folder, name);
            RuntimeStateRetention.SweepLogs(folder, FilePrefix, FilePath, maxFiles, maxAge ?? RuntimeStateRetention.LogMaxAge, utcNow: null, fileExtension: FileExtension);
        }
        catch (Exception ex)
        {
            // e.g. ROSLYNSENTINEL_SCRATCH_ROOT is not an absolute path: the transcript is off, calls are unaffected.
            Debug.WriteLine($"Tool call transcript disabled: {ex.GetType().Name}: {ex.Message}");
            DirectoryPath = null;
            FilePath = null;
        }
    }

    /// <summary>The folder the transcript is written to, or null when the transcript is disabled.</summary>
    public string? DirectoryPath { get; }

    /// <summary>The file this host writes to (created on the first recorded call), or null when the transcript is disabled.</summary>
    public string? FilePath { get; }

    /// <summary>
    /// Copies the arguments into a detached <see cref="JsonNode"/> so later filters that rewrite the dictionary in place
    /// (the argument validator repairs parameter-name case) cannot change what is logged. Null in, null out; never throws.
    /// </summary>
    public static JsonNode? SnapshotArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToNode(arguments);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tool call transcript argument snapshot failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Appends one line. <paramref name="response"/> is the response text exactly as sent (no trimming, truncation or paging);
    /// null properties are omitted from the line. Never throws; the first failure is reported once through <see cref="Debug"/>.
    /// </summary>
    public void Record(
        DateTimeOffset startedUtc,
        string? tool,
        JsonNode? arguments,
        bool isError,
        string? response,
        long durationMs,
        string? callId = null,
        string? agentId = null)
    {
        if (FilePath is null || DirectoryPath is null)
        {
            return;
        }

        try
        {
            var line = new JsonObject { ["utc"] = startedUtc.UtcDateTime.ToString("O") };
            if (tool is not null)
            {
                line["tool"] = tool;
            }

            if (!string.IsNullOrEmpty(callId))
            {
                line["callId"] = callId;
            }

            if (!string.IsNullOrEmpty(agentId))
            {
                line["agentId"] = agentId;
            }

            if (arguments is not null)
            {
                line["arguments"] = arguments;
            }

            line["isError"] = isError;
            if (response is not null)
            {
                line["response"] = response;
            }

            line["durationMs"] = durationMs;
            var text = line.ToJsonString(LineOptions) + "\n";
            lock (_writeLock)
            {
                if (!_directoryCreated)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    _directoryCreated = true;
                }

                File.AppendAllText(FilePath, text, Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            {
                Debug.WriteLine($"Tool call transcript write failed (further failures are not reported): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
