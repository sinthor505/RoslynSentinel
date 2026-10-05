using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Converts positional call arguments to named arguments (<c>M(1, 2)</c> -> <c>M(a: 1, b: 2)</c>)
/// across a solution, project or file, or for the calls of one method/constructor. Semantic, so
/// params methods, expression trees, delegate invokes and generated code are skipped. Edits go
/// through the compile gate before applying.
/// </summary>
[McpServerToolType]
public class NamedArgumentsTools
{
    private const int DefaultMinParameters = 2;

    private readonly NamedArgumentsEngine _engine;
    private readonly ValidationEngine _validationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;

    public NamedArgumentsTools(
        NamedArgumentsEngine engine,
        ValidationEngine validationEngine,
        IWorkspaceManager workspaceManager,
        ILogger<NamedArgumentsTools> logger)
    {
        _engine = engine;
        _validationEngine = validationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "NamedArguments")]
    [Produces(DataTag.ChangeId)]
    [Description("Convert positional call arguments to named arguments (M(1, 2) -> M(a: 1, b: 2)) using the compiler's binding, " +
        "for method calls, constructor calls (new T(..), new(..), : base(..)/: this(..)) and attribute constructor arguments. " +
        "scope: solution (every project), project (scopeName = project name) or file (scopeName = file path). " +
        "targetSymbol: optional docCommentId of one method/constructor (from Search or LocateSymbol); only calls to it are converted. " +
        "minParameters: only convert calls whose method has at least this many parameters (default 2, or 1 when targetSymbol is given). " +
        "literalArgumentsOnly: only name arguments that are literals (numbers, strings, true/false/null/default). " +
        "Skipped by design: params methods, expression trees (CS0853), delegate invokes, generated code, already-named arguments. " +
        "mode: preview reports per-file counts without writing; apply writes through the compile gate. Mode is mandatory - use preview first. " +
        "Returns per-file call-site and argument counts, plus changeId on apply, or a structured error naming the parameter and a correct value.")]
    public async Task<SentinelCallToolResult<object>> NamedArguments(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("solution: all projects. project: the project named by scopeName. file: the file named by scopeName.")] NamedArgumentsScope scope,
        [Description("Project name (scope: project) or file path (scope: file). Must be omitted for scope: solution.")] string? scopeName,
        [Description("Optional docCommentId of one method or constructor; only calls to it are converted.")] string? targetSymbol,
        [Description("Minimum parameter count of the called method. Omit for the default (2, or 1 when targetSymbol is given).")] int? minParameters,
        [Description("true: only name literal arguments. false: name every positional argument.")] bool literalArgumentsOnly,
        [Description("preview: report counts without writing. apply: perform the changes.")] SemanticReplaceMode mode,
        CancellationToken cancellationToken = default)
    {
        if (scope == NamedArgumentsScope.solution && !string.IsNullOrWhiteSpace(scopeName))
        {
            return InvalidArgument("scopeName must be omitted when scope is solution. Pass scope: project or file to use a scopeName.");
        }

        if (scope != NamedArgumentsScope.solution && string.IsNullOrWhiteSpace(scopeName))
        {
            return InvalidArgument($"scopeName is required when scope is {scope}. " +
                (scope == NamedArgumentsScope.project ? "Pass the project name, e.g. RoslynSentinel.Common." : "Pass the file path."));
        }

        if (minParameters is < 1)
        {
            return InvalidArgument("minParameters must be 1 or greater.");
        }

        string? resolvedScopeName = scopeName;
        if (scope == NamedArgumentsScope.file)
        {
            resolvedScopeName = _workspaceManager.ResolveFromWire(scopeName!);
        }

        var options = new NamedArgumentsOptions(
            MinParameters: minParameters ?? (string.IsNullOrWhiteSpace(targetSymbol) ? DefaultMinParameters : 1),
            LiteralArgumentsOnly: literalArgumentsOnly);

        var outcome = await _engine.ConvertAsync(
            scope,
            resolvedScopeName,
            string.IsNullOrWhiteSpace(targetSymbol) ? null : targetSymbol,
            options,
            cancellationToken);

        if (outcome.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = outcome.Error
            };
        }

        var files = outcome.Files
            .Select(f => new { file = f.FilePath, callSites = f.CallSites, arguments = f.Arguments })
            .ToList();
        var totalCallSites = outcome.Files.Sum(f => f.CallSites);
        var totalArguments = outcome.Files.Sum(f => f.Arguments);

        if (mode == SemanticReplaceMode.preview)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new
                {
                    mode = "preview",
                    filesAffected = files.Count,
                    totalCallSites,
                    totalArguments,
                    files
                }
            };
        }

        if (outcome.Changes.Count == 0)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new
                {
                    mode = "apply",
                    message = "Nothing to convert: no positional call arguments matched the scope and filters.",
                    filesChanged = 0
                }
            };
        }

        var applyOutcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine,
            _workspaceManager,
            _logger,
            outcome.Changes,
            "NamedArguments",
            dryRun: false,
            returnDiff: false,
            cancellationToken: cancellationToken);

        if (applyOutcome.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = applyOutcome.Error
            };
        }

        return new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new
            {
                mode = "apply",
                changeId = applyOutcome.ChangeId,
                filesChanged = outcome.Changes.Count,
                totalCallSites,
                totalArguments,
                files
            }
        };
    }

    private static SentinelCallToolResult<object> InvalidArgument(string message) =>
        new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"NamedArguments: {message}")
        };
}
