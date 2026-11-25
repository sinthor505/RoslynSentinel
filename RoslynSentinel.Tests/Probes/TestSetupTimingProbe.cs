using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Probes;

// Measurement probe, not an assertion test: it times the phases of per-test setup for in-memory tests (metadata
// references, InMemoryWorkspace.Create, first compilation, apply with/without validation, PersistentWorkspaceManager
// construction, formatting) and writes a markdown table to TestContext.Out and to <scratch root>\probes\.
// Explicit so it never runs in a normal suite; select it with filter FullyQualifiedName~TestSetupTimingProbe.
[TestFixture]
public class TestSetupTimingProbe
{
    private const int Iterations = 20;
    private const int ParallelIterations = 16;

    // One namespace, one class, five methods touching System.Linq / List<T> / async Task so binding hits the framework refs.
    private const string Source =
        "using System;\n" +
        "using System.Collections.Generic;\n" +
        "using System.Linq;\n" +
        "using System.Threading.Tasks;\n" +
        "\n" +
        "namespace Probe.Sample\n" +
        "{\n" +
        "    public class Widget\n" +
        "    {\n" +
        "        private readonly List<int> _values = new List<int>();\n" +
        "\n" +
        "        public int Sum()\n" +
        "        {\n" +
        "            return _values.Sum();\n" +
        "        }\n" +
        "\n" +
        "        public IEnumerable<int> Evens()\n" +
        "        {\n" +
        "            return _values.Where(v => v % 2 == 0).OrderBy(v => v);\n" +
        "        }\n" +
        "\n" +
        "        public async Task<int> CountAsync()\n" +
        "        {\n" +
        "            await Task.Delay(1);\n" +
        "            return _values.Count;\n" +
        "        }\n" +
        "\n" +
        "        public Dictionary<int, List<int>> Group()\n" +
        "        {\n" +
        "            return _values.GroupBy(v => v % 3).ToDictionary(g => g.Key, g => g.ToList());\n" +
        "        }\n" +
        "\n" +
        "        public void Add(int value)\n" +
        "        {\n" +
        "            _values.Add(value);\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private const string FileName = "Widget.cs";

    // Same 12 names TestSolutionBuilder hard-codes (the list is inline there, so it is replicated here).
    private static readonly string[] CandidateNames =
    {
        "System.Runtime.dll",
        "mscorlib.dll",
        "System.Collections.dll",
        "System.Linq.dll",
        "System.Console.dll",
        "System.Private.CoreLib.dll",
        "netstandard.dll",
        "System.Threading.dll",
        "System.Threading.Tasks.dll",
        "System.Net.Http.dll",
        "System.Collections.Concurrent.dll",
        "System.Text.RegularExpressions.dll",
    };

    private static readonly Lazy<IReadOnlyList<MetadataReference>> SharedReferences = new(() => BuildFreshReferences());

    private delegate Task<IDisposable?> Timed();

    private sealed record Row(string Phase, double? Cold, double? Median, double? P90, string Notes);

    private sealed class DisposeAll : IDisposable
    {
        private readonly IDisposable[] _items;

        public DisposeAll(params IDisposable[] items) => _items = items;

        public void Dispose()
        {
            foreach (var item in _items)
            {
                item.Dispose();
            }
        }
    }

    private int _lastCompileErrors = -1;

    // Explicit so normal suites never run it. RunTest's fast suite reports it skipped (its added category exclusion
    // stops the NUnit adapter treating the name filter as an explicit selection); run it with RunTest(suite: full,
    // filter: FullyQualifiedName~TestSetupTimingProbe) or a plain `dotnet test --filter`.
    [Test, Explicit, Category("Probe")]
    public async Task MeasureSetupPhases()
    {
        var rows = new List<Row>();

        rows.Add(await RunPhaseAsync(
            "A. 12 MetadataReference.CreateFromFile (fresh)",
            () => "replicates TestSolutionBuilder list incl. File.Exists and object-assembly check",
            StepSync(() => BuildFreshReferences())));

        rows.Add(await RunPhaseAsync(
            "B. InMemoryWorkspace.Create (no compilation)",
            () => "includes Dispose; 1 file + GlobalUsings.InMemory.cs",
            StepSync(() => InMemoryWorkspace.Create((FileName, Source)).Dispose())));

        rows.Add(await RunPhaseAsync(
            "C1. build solution (fresh refs) + GetCompilationAsync + GetDiagnostics",
            () => $"solution build included in timing; compile errors in last run: {_lastCompileErrors}",
            Step(async () => _lastCompileErrors = await CompileAsync(BuildNormal()))));

        _ = SharedReferences.Value;
        rows.Add(await RunPhaseAsync(
            "C2. build solution (ONE shared static ref list) + GetCompilationAsync + GetDiagnostics",
            () => $"shared list built before this phase (not in cold); solution build included in timing; compile errors in last run: {_lastCompileErrors}",
            Step(async () => _lastCompileErrors = await CompileAsync(BuildShared()))));

        rows.Add(await RunPhaseAsync(
            "D. GetSemanticModelAsync (compilation already built)",
            () => "single class document; compilation forced in untimed prepare",
            Prepared<Document>(
                async () =>
                {
                    var solution = BuildNormal();
                    await CompileAsync(solution);
                    return solution.Projects.Single().Documents.Single(d => d.Name == FileName);
                },
                async document =>
                {
                    await document.GetSemanticModelAsync();
                    return null;
                })));

        rows.Add(await RunPhaseAsync(
            "E0. FakeWorkspaceManager.ReadWrite() + SetTestSolution (construction only)",
            () => "solution pre-built, not timed",
            Prepared<Solution>(
                () => Task.FromResult(BuildNormal()),
                solution =>
                {
                    var manager = FakeWorkspaceManager.ReadWrite();
                    manager.SetTestSolution(solution);
                    return Task.FromResult<IDisposable?>(manager);
                })));

        rows.Add(await RunPhaseAsync(
            "E1. FakeWorkspaceManager.ApplyProposedChangesAsync, validateChanges: true",
            () => "one-line change in Add(); only the apply call is timed (baseline + candidate compile inside)",
            ApplyPhase(validateChanges: true)));

        rows.Add(await RunPhaseAsync(
            "E2. FakeWorkspaceManager.ApplyProposedChangesAsync, validateChanges: false",
            () => "one-line change in Add(); only the apply call is timed",
            ApplyPhase(validateChanges: false)));

        rows.Add(await RunPhaseAsync(
            "F. TestEngines.PersistentWorkspaceManager(...) + SetTestSolution",
            () => "fresh in-memory solution built untimed; manager disposed after timing",
            Prepared<Solution>(
                () =>
                {
                    using var workspace = InMemoryWorkspace.Create((FileName, Source));
                    return Task.FromResult(workspace.Manager.CurrentSolution!);
                },
                solution =>
                {
                    var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
                    manager.SetTestSolution(solution);
                    return Task.FromResult<IDisposable?>(manager);
                })));

        rows.Add(await RunPhaseAsync(
            "G1. RoslynFormattingHelper.FormatAnnotatedTextAsync(SourceText ...) (throwaway AdhocWorkspace)",
            () => "adds one annotated statement to Sum(); eol \\n",
            Prepared<(Document Doc, SourceText Text, SyntaxNode Root)>(
                PrepareFormatAsync,
                async state =>
                {
                    await RoslynFormattingHelper.FormatAnnotatedTextAsync(state.Text, state.Root, "\n");
                    return null;
                })));

        rows.Add(await RunPhaseAsync(
            "G2. RoslynFormattingHelper.FormatAnnotatedTextAsync(Document ...) (existing workspace)",
            () => "same rewrite as G1, document already in a workspace",
            Prepared<(Document Doc, SourceText Text, SyntaxNode Root)>(
                PrepareFormatAsync,
                async state =>
                {
                    await RoslynFormattingHelper.FormatAnnotatedTextAsync(state.Doc, state.Root, "\n");
                    return null;
                })));

        var contention = new List<string>();
        for (var round = 1; round <= 2; round++)
        {
            contention.Add(await RunContentionBatchAsync($"C1 round {round} (fresh refs)", BuildNormal));
            contention.Add(await RunContentionBatchAsync($"C2 round {round} (shared refs)", BuildShared));
        }

        var markdown = BuildMarkdown(rows, contention);
        TestContext.Out.WriteLine(markdown);

        var directory = Path.Combine(ScratchRoot.Resolve(), "probes");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, $"test-setup-timing-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(outputPath, markdown);
        TestContext.Out.WriteLine($"Output file: {outputPath}");
    }

    private Func<Task<Timed>> ApplyPhase(bool validateChanges) =>
        Prepared<(InMemoryWorkspace Workspace, FakeWorkspaceManager Manager)>(
            () =>
            {
                var workspace = InMemoryWorkspace.Create((FileName, Source));
                var manager = FakeWorkspaceManager.ReadWrite();
                manager.SetTestSolution(workspace.Manager.CurrentSolution!);
                return Task.FromResult((workspace, manager));
            },
            async state =>
            {
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [state.Workspace.PathOf(FileName)] = Source.Replace("_values.Add(value);", "_values.Add(value + 1);"),
                };
                var result = await state.Manager.ApplyProposedChangesAsync(changes, validateChanges: validateChanges);
                if (result.IsError)
                {
                    throw new InvalidOperationException("Apply returned IsError: " + result);
                }

                return new DisposeAll(state.Manager, state.Workspace);
            });

    private static async Task<(Document Doc, SourceText Text, SyntaxNode Root)> PrepareFormatAsync()
    {
        var solution = BuildNormal();
        var document = solution.Projects.Single().Documents.Single(d => d.Name == FileName);
        var text = await document.GetTextAsync();
        var root = (await document.GetSyntaxRootAsync())!;
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Body is not null);
        var changed = method
            .AddBodyStatements(SyntaxFactory.ParseStatement("int   extra=42;"))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return (document, text, root.ReplaceNode(method, changed));
    }

    private static List<MetadataReference> BuildFreshReferences()
    {
        var references = new List<MetadataReference>();
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var name in CandidateNames)
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var objectAssembly = typeof(object).Assembly.Location;
        if (!references.Any(r => r.Display != null && r.Display.Equals(objectAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        return references;
    }

    private static Solution BuildNormal() =>
        TestSolutionBuilder.CreateSolutionWithProject("ProbeProject", [(FileName, Source)]);

    // Mirrors TestSolutionBuilder.CreateSolutionWithProject(name, documents) but reuses the shared reference list.
    private static Solution BuildShared()
    {
        const string projectName = "ProbeProject";
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectPath = Path.Combine(Path.GetTempPath(), "TestProj", $"{projectName}.csproj");

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithMetadataReferences(SharedReferences.Value)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(projectPath)
            .WithDefaultNamespace(projectName);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        var documentId = DocumentId.CreateNewId(projectId, FileName);
        return solution.AddDocument(documentId, FileName, SourceText.From(Source), filePath: FileName);
    }

    private static async Task<int> CompileAsync(Solution solution)
    {
        var compilation = await solution.Projects.Single().GetCompilationAsync();
        return compilation!.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static Func<Task<Timed>> Prepared<TState>(Func<Task<TState>> prepare, Func<TState, Task<IDisposable?>> run) =>
        async () =>
        {
            var state = await prepare();
            return () => run(state);
        };

    private static Func<Task<Timed>> Step(Func<Task> action) =>
        Prepared<int>(
            () => Task.FromResult(0),
            async _ =>
            {
                await action();
                return null;
            });

    private static Func<Task<Timed>> StepSync(Action action) =>
        Step(() =>
        {
            action();
            return Task.CompletedTask;
        });

    private static async Task<double> TimeOnceAsync(Func<Task<Timed>> prepare)
    {
        var timed = await prepare();
        var stopwatch = Stopwatch.StartNew();
        var toDispose = await timed();
        stopwatch.Stop();
        toDispose?.Dispose();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    // Cold = the first call of this phase in the process (phases run in table order, so earlier phases have warmed shared
    // JIT/MEF); then one discarded warm-up; then Iterations measured runs.
    private static async Task<Row> RunPhaseAsync(string phase, Func<string> notes, Func<Task<Timed>> prepare)
    {
        try
        {
            var cold = await TimeOnceAsync(prepare);
            await TimeOnceAsync(prepare);
            var samples = new List<double>();
            for (var i = 0; i < Iterations; i++)
            {
                samples.Add(await TimeOnceAsync(prepare));
            }

            samples.Sort();
            return new Row(phase, cold, Median(samples), Percentile(samples, 0.9), notes());
        }
        catch (Exception ex)
        {
            var firstLine = ex.Message.Split('\n')[0].Trim();
            return new Row(phase, null, null, null, $"SKIPPED: {ex.GetType().Name}: {firstLine}");
        }
    }

    private static async Task<string> RunContentionBatchAsync(string label, Func<Solution> build)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var times = new ConcurrentBag<double>();

        var wall = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ParallelIterations),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelIterations },
            async (_, _) =>
            {
                var stopwatch = Stopwatch.StartNew();
                await CompileAsync(build());
                stopwatch.Stop();
                times.Add(stopwatch.Elapsed.TotalMilliseconds);
            });
        wall.Stop();

        var sorted = times.OrderBy(t => t).ToList();
        var allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / (1024.0 * 1024.0);
        return $"| {label} | {wall.Elapsed.TotalMilliseconds:F1} | {Median(sorted):F1} | {Percentile(sorted, 0.9):F1} | {GC.CollectionCount(0) - gen0} | {GC.CollectionCount(1) - gen1} | {GC.CollectionCount(2) - gen2} | {allocatedMb:F1} |";
    }

    private static double Median(List<double> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string BuildMarkdown(List<Row> rows, List<string> contention)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Test setup timing probe");
        builder.AppendLine();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        builder.AppendLine($"- Build configuration: {configuration}; ProcessorCount: {Environment.ProcessorCount}; {RuntimeInformation.FrameworkDescription}; ServerGC: {GCSettings.IsServerGC}; OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"- Per phase: cold = first call in this process (phases run in table order, earlier phases warm shared JIT/MEF), 1 discarded warm-up, then {Iterations} iterations; ms.");
        builder.AppendLine();
        builder.AppendLine("| Phase | Cold ms | Median ms | P90 ms | Notes |");
        builder.AppendLine("| --- | ---: | ---: | ---: | --- |");
        foreach (var row in rows)
        {
            builder.AppendLine($"| {row.Phase} | {Format(row.Cold)} | {Format(row.Median)} | {Format(row.P90)} | {row.Notes} |");
        }

        builder.AppendLine();
        builder.AppendLine($"## H. Contention: {ParallelIterations} parallel iterations (Parallel.ForEachAsync, MaxDegreeOfParallelism {ParallelIterations}); each iteration = build solution + compile + diagnostics");
        builder.AppendLine();
        builder.AppendLine("| Batch | Wall ms | Per-iteration median ms | Per-iteration p90 ms | GC gen0 | GC gen1 | GC gen2 | Allocated MB |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var line in contention)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string Format(double? value) => value is null ? "n/a" : value.Value.ToString("F1");
}
