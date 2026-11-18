namespace RoslynSentinel.Common;

/// <summary>
/// One near-miss event of a tool call, recorded through <see cref="NearMissRecorder.Record"/> and written by the call-tool filter into the result's
/// <c>_meta.nearMissSubstitutions</c> array. EVALUATION data for the harness: it carries the wrong name, so it must never reach the model-facing text.
/// </summary>
/// <param name="Tool">Name of the tool that was called.</param>
/// <param name="FilePath">File of the unresolved use.</param>
/// <param name="Line">1-based line of the unresolved use.</param>
/// <param name="From">The wrong name as written.</param>
/// <param name="To">The closest existing name.</param>
/// <param name="DiagnosticId">The compiler diagnostic id, for example CS0117.</param>
/// <param name="CandidateCount">How many candidates were within the closeness limit.</param>
/// <param name="DisplayMode">"show" or "hide": the <see cref="NearMissOptions.NameDisplay"/> in force for the call.</param>
/// <param name="Applied">True when the substitution was written; false when only the ambiguous near-miss hint was given.</param>
public sealed record NearMissSubstitution(
    string Tool,
    string FilePath,
    int Line,
    string From,
    string To,
    string DiagnosticId,
    int CandidateCount,
    string DisplayMode,
    bool Applied);
