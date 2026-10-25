using System.Text.Json;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Pure coverage (no server, no schema) for the benign-ignore table of <see cref="ToolArgumentRewrites.Apply"/>:
/// a key is removed with one note only when ignoring it cannot change the results; otherwise it stays so
/// <c>ToolArgumentValidator.Validate</c> still rejects it.
/// </summary>
[TestFixture]
public class ToolArgumentRewritesTests
{
    private static readonly Dictionary<string, JsonElement> NoProperties = new(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Args(params (string Key, object Value)[] pairs)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            arguments[key] = JsonSerializer.SerializeToElement(value);
        return arguments;
    }

    private static IReadOnlyList<string>? Apply(string tool, Dictionary<string, JsonElement> arguments, params string[] declared) =>
        ToolArgumentRewrites.Apply(tool, arguments, new HashSet<string>(declared, StringComparer.Ordinal), NoProperties, null);

    [Test]
    public void OutputMode_Content_IsRemovedWithNote()
    {
        var arguments = Args(("mode", "text"), ("query", "Foo"), ("output_mode", "content"));

        var notes = Apply("Search", arguments, "mode", "query");

        Assert.That(arguments.ContainsKey("output_mode"), Is.False);
        Assert.That(arguments.ContainsKey("query"), Is.True);
        Assert.That(notes, Is.Not.Null);
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'output_mode' is not a parameter of Search and was ignored (Search always returns every match with file, line and preview). Omit it in future calls."));
    }

    [Test]
    public void OutputMode_UnknownValue_IsLeftForValidate()
    {
        var arguments = Args(("mode", "text"), ("output_mode", "json"));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("output_mode"), Is.True);
    }

    [Test]
    public void UseRegex_True_TextMode_IsRemoved()
    {
        var arguments = Args(("mode", "Text"), ("useRegex", true));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(arguments.ContainsKey("useRegex"), Is.False);
        Assert.That(notes, Has.Count.EqualTo(1));
    }

    [Test]
    public void UseRegex_True_SymbolMode_IsLeftForValidate()
    {
        var arguments = Args(("mode", "symbol"), ("useRegex", true));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("useRegex"), Is.True);
    }

    [Test]
    public void UseRegex_False_IsLeftForValidate()
    {
        var arguments = Args(("mode", "text"), ("useRegex", false));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("useRegex"), Is.True);
    }

    [Test]
    public void Type_Cs_IsRemoved()
    {
        var arguments = Args(("mode", "text"), ("type", "cs"));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(arguments.ContainsKey("type"), Is.False);
        Assert.That(notes, Has.Count.EqualTo(1));
    }

    [Test]
    public void Type_Py_IsLeftForValidate()
    {
        var arguments = Args(("mode", "text"), ("type", "py"));

        var notes = Apply("Search", arguments, "mode");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("type"), Is.True);
    }

    [Test]
    public void DeclaredNameWins_IsNotRemoved()
    {
        var arguments = Args(("mode", "text"), ("type", "cs"));

        var notes = Apply("Search", arguments, "mode", "type");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("type"), Is.True);
    }

    [Test]
    public void IncludeDocCommentsTrue_OnGetMethodSource_IsRemoved()
    {
        var arguments = Args(("filePath", "A.cs"), ("methodName", "M"), ("includeDocComments", "true"));

        var notes = Apply("GetMethodSource", arguments, "filePath", "methodName");

        Assert.That(arguments.ContainsKey("includeDocComments"), Is.False);
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Does.StartWith("Note: 'includeDocComments' is not a parameter of GetMethodSource"));
    }

    [Test]
    public void OtherTool_IsUntouched()
    {
        var arguments = Args(("filePath", "A.cs"), ("output_mode", "content"), ("includeDocComments", true));

        var notes = Apply("ReadFile", arguments, "filePath");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.Count, Is.EqualTo(3));
    }

    private static readonly string[] ScopeMembers = ["solution", "project", "file"];
    private static readonly string[] LevelMembers = ["noBuild", "quickBuild", "fullBuild", "fullBuildThrowaway"];

    // Minimal emitted-schema stand-in: each enum parameter carries an "enum" array like the real tool schemas.
    private static readonly Dictionary<string, JsonElement> EnumProperties = new(StringComparer.Ordinal)
    {
        ["scope"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = ScopeMembers }),
        ["level"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = LevelMembers }),
    };

    private static IReadOnlyList<string>? ApplyWithSchema(string tool, Dictionary<string, JsonElement> arguments, params string[] declared) =>
        ToolArgumentRewrites.Apply(tool, arguments, new HashSet<string>(declared, StringComparer.Ordinal), EnumProperties, null);

    [Test]
    public void Search_HeadLimit5_BecomesMaxResults5()
    {
        var arguments = Args(("query", "Foo"), ("head_limit", 5));

        var notes = ApplyWithSchema("Search", arguments, "query", "maxResults");

        Assert.That(arguments.ContainsKey("head_limit"), Is.False);
        Assert.That(arguments["maxResults"].GetInt32(), Is.EqualTo(5));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'head_limit' is not a parameter of Search; it was treated as maxResults: 5. Use Search(maxResults: 5) in future calls."));
    }

    [Test]
    public void Search_HeadLimitZero_IsLeftForValidate()
    {
        var arguments = Args(("query", "Foo"), ("head_limit", 0));

        var notes = ApplyWithSchema("Search", arguments, "query", "maxResults");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("head_limit"), Is.True);
        Assert.That(arguments.ContainsKey("maxResults"), Is.False);
    }

    [Test]
    public void ReadFile_OffsetLimit_BecomesStartEndLine()
    {
        var arguments = Args(("filePath", "A.cs"), ("offset", "1659"), ("limit", "20"));

        var notes = ApplyWithSchema("ReadFile", arguments, "filePath", "startLine", "endLine");

        Assert.That(arguments.ContainsKey("offset"), Is.False);
        Assert.That(arguments.ContainsKey("limit"), Is.False);
        Assert.That(arguments["startLine"].GetInt32(), Is.EqualTo(1659));
        Assert.That(arguments["endLine"].GetInt32(), Is.EqualTo(1678));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'offset' and 'limit' are not parameters of ReadFile; they were treated as startLine: 1659, endLine: 1678. Use ReadFile(startLine: 1659, endLine: 1678) in future calls."));
    }

    [Test]
    public void ReadFile_LimitOnly_BecomesEndLine()
    {
        var arguments = Args(("filePath", "A.cs"), ("limit", 30));

        var notes = ApplyWithSchema("ReadFile", arguments, "filePath", "startLine", "endLine");

        Assert.That(arguments.ContainsKey("limit"), Is.False);
        Assert.That(arguments.ContainsKey("startLine"), Is.False);
        Assert.That(arguments["endLine"].GetInt32(), Is.EqualTo(30));
        Assert.That(notes, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReadFile_OffsetLimitWithStartLine_IsLeftForValidate()
    {
        var arguments = Args(("filePath", "A.cs"), ("startLine", 10), ("offset", 1659), ("limit", 20));

        var notes = ApplyWithSchema("ReadFile", arguments, "filePath", "startLine", "endLine");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("offset"), Is.True);
        Assert.That(arguments.ContainsKey("limit"), Is.True);
        Assert.That(arguments.ContainsKey("endLine"), Is.False);
    }

    [Test]
    public void RunTest_ProjectName_BecomesScopeProject()
    {
        var arguments = Args(("projectName", "RoslynSentinel.Tests.Server"), ("filter", "X"));

        var notes = ApplyWithSchema("RunTest", arguments, "scope", "scopeName", "filter");

        Assert.That(arguments.ContainsKey("projectName"), Is.False);
        Assert.That(arguments["scope"].GetString(), Is.EqualTo("project"));
        Assert.That(arguments["scopeName"].GetString(), Is.EqualTo("RoslynSentinel.Tests.Server"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'projectName' is not a parameter of RunTest; it was treated as scope: \"project\", scopeName: \"RoslynSentinel.Tests.Server\". Use RunTest(scope: \"project\", scopeName: \"RoslynSentinel.Tests.Server\") in future calls."));
    }

    [Test]
    public void RunTest_ScopeAlreadySupplied_IsLeftForValidate()
    {
        var arguments = Args(("scope", "solution"), ("projectName", "RoslynSentinel.Tests.Server"));

        var notes = ApplyWithSchema("RunTest", arguments, "scope", "scopeName");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("projectName"), Is.True);
        Assert.That(arguments["scope"].GetString(), Is.EqualTo("solution"));
    }

    [Test]
    public void GetDiagnostics_FilePath_BecomesScopeFile()
    {
        var arguments = Args(("filePath", "RoslynSentinel.Common/ToolException.cs"));

        var notes = ApplyWithSchema("GetDiagnostics", arguments, "scope", "scopeName");

        Assert.That(arguments.ContainsKey("filePath"), Is.False);
        Assert.That(arguments["scope"].GetString(), Is.EqualTo("file"));
        Assert.That(arguments["scopeName"].GetString(), Is.EqualTo("RoslynSentinel.Common/ToolException.cs"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Does.StartWith("Note: 'filePath' is not a parameter of GetDiagnostics; it was treated as scope: \"file\""));
    }

    [Test]
    public void Build_FullBuildTrue_BecomesLevelFullBuild()
    {
        var arguments = Args(("fullBuild", true));

        var notes = ApplyWithSchema("Build", arguments, "level", "scope");

        Assert.That(arguments.ContainsKey("fullBuild"), Is.False);
        Assert.That(arguments["level"].GetString(), Is.EqualTo("fullBuild"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'fullBuild' is not a parameter of Build; it was treated as level: \"fullBuild\". Use Build(level: \"fullBuild\") in future calls."));
    }

    [Test]
    public void Build_FullBuildFalse_IsLeftForValidate()
    {
        var arguments = Args(("fullBuild", false));

        var notes = ApplyWithSchema("Build", arguments, "level", "scope");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("fullBuild"), Is.True);
        Assert.That(arguments.ContainsKey("level"), Is.False);
    }

    [Test]
    public void Build_ModeFullBuild_BecomesLevel()
    {
        // Lower-case input proves the schema's own spelling is written.
        var arguments = Args(("mode", "fullbuild"));

        var notes = ApplyWithSchema("Build", arguments, "level", "scope");

        Assert.That(arguments.ContainsKey("mode"), Is.False);
        Assert.That(arguments["level"].GetString(), Is.EqualTo("fullBuild"));
        Assert.That(notes, Has.Count.EqualTo(1));
    }

    private const string SolutionRoot = @"C:\repo\Sol";

    [TestCase(@"C:\repo\Sol\Proj.Tools\Impl", "Proj.Tools/Impl/**")]
    [TestCase(@"C:\repo\Sol\Proj.Tools\Foo.cs", "Proj.Tools/Foo.cs")]
    [TestCase(@"c:\REPO\sol\Proj", "Proj/**")]
    [TestCase(@"Proj.Tools\Impl\", "Proj.Tools/Impl/**")]
    [TestCase("./Proj/Foo.cs", "Proj/Foo.cs")]
    [TestCase("Foo.cs", "Foo.cs")]
    [TestCase("**/Proj/**/*.cs", "**/Proj/**/*.cs")]
    [TestCase(@"C:\repo\Sol\Proj\*.cs", "Proj/*.cs")]
    [TestCase(@"C:\elsewhere\X", null)]
    [TestCase(@"C:\repo\Sol", null)]
    [TestCase(@"..\X", null)]
    [TestCase("/etc/x", null)]
    [TestCase(@"\\server\share\x", null)]
    [TestCase("", null)]
    public void PathToFileGlob_Rules(string value, string? expected)
    {
        Assert.That(ToolArgumentRewrites.PathToFileGlob(value, SolutionRoot), Is.EqualTo(expected));
    }

    [Test]
    public void PathToFileGlob_AbsolutePathWithoutSolutionRoot_IsNull()
    {
        Assert.That(ToolArgumentRewrites.PathToFileGlob(@"C:\repo\Sol\Proj", null), Is.Null);
        Assert.That(ToolArgumentRewrites.PathToFileGlob(@"C:\repo\Sol\Proj", ""), Is.Null);
    }

    private static IReadOnlyList<string>? ApplySearchPath(Dictionary<string, JsonElement> arguments, params string[] declared) =>
        ToolArgumentRewrites.Apply("Search", arguments, new HashSet<string>(declared, StringComparer.Ordinal), NoProperties, SolutionRoot);

    [Test]
    public void SearchText_AbsoluteDirectoryPath_BecomesFileGlobWithNote()
    {
        var arguments = Args(("mode", "text"), ("query", "Foo"), ("path", @"C:\repo\Sol\RoslynSentinel.Tools.Basic"));

        var notes = ApplySearchPath(arguments, "mode", "query", "fileGlob");

        Assert.That(arguments.ContainsKey("path"), Is.False);
        Assert.That(arguments["fileGlob"].GetString(), Is.EqualTo("RoslynSentinel.Tools.Basic/**"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'path' is not a parameter of Search; it was treated as fileGlob: \"RoslynSentinel.Tools.Basic/**\" (text mode restricts files with fileGlob). Use fileGlob in future calls."));
    }

    [Test]
    public void SearchText_PathAndFileGlobBothSupplied_LeavesPathForValidate()
    {
        var arguments = Args(("mode", "text"), ("fileGlob", "**/*.cs"), ("path", @"C:\repo\Sol\Proj"));

        var notes = ApplySearchPath(arguments, "mode", "fileGlob");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("path"), Is.True);
        Assert.That(arguments["fileGlob"].GetString(), Is.EqualTo("**/*.cs"));
    }

    [Test]
    public void SearchSymbolMode_Path_IsLeftForValidate()
    {
        var arguments = Args(("mode", "symbol"), ("path", @"C:\repo\Sol\Proj"));

        var notes = ApplySearchPath(arguments, "mode", "fileGlob");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("path"), Is.True);
        Assert.That(arguments.ContainsKey("fileGlob"), Is.False);
    }

    [Test]
    public void SearchText_PathOutsideRoot_IsLeftForValidate()
    {
        var arguments = Args(("mode", "text"), ("path", @"C:\elsewhere\X"));

        var notes = ApplySearchPath(arguments, "mode", "fileGlob");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("path"), Is.True);
        Assert.That(arguments.ContainsKey("fileGlob"), Is.False);
    }

    [Test]
    public void SearchText_BareFileName_NoteWarnsItMatchesAnyFolder()
    {
        var arguments = Args(("mode", "text"), ("path", "Foo.cs"));

        var notes = ApplySearchPath(arguments, "mode", "fileGlob");

        Assert.That(arguments["fileGlob"].GetString(), Is.EqualTo("Foo.cs"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Does.EndWith("This matches every file named Foo.cs in the solution."));
    }

    // filePath is a declared Search parameter (it pins symbol resolution), so these tests declare it.
    [Test]
    public void SearchText_FilePath_BecomesFileGlobWithNote()
    {
        var arguments = Args(("mode", "text"), ("query", "Foo"), ("filePath", @"C:\repo\Sol\RoslynSentinel.Tools.Basic\Impl.cs"));

        var notes = ApplySearchPath(arguments, "mode", "query", "filePath", "fileGlob");

        Assert.That(arguments.ContainsKey("filePath"), Is.False);
        Assert.That(arguments["fileGlob"].GetString(), Is.EqualTo("RoslynSentinel.Tools.Basic/Impl.cs"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Is.EqualTo(
            "Note: 'filePath' only pins symbol resolution and is ignored by Search in text mode; it was treated as fileGlob: \"RoslynSentinel.Tools.Basic/Impl.cs\". Use fileGlob to restrict a text search."));
    }

    [Test]
    public void SearchText_FilePathUnmappable_IsLeftUnchanged()
    {
        var arguments = Args(("mode", "text"), ("filePath", @"C:\elsewhere\X.cs"));

        var notes = ApplySearchPath(arguments, "mode", "filePath", "fileGlob");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("filePath"), Is.True);
        Assert.That(arguments.ContainsKey("fileGlob"), Is.False);
    }

    [Test]
    public void SearchSymbolMode_FilePath_IsUntouched()
    {
        var arguments = Args(("mode", "symbol"), ("filePath", @"C:\repo\Sol\Proj\Foo.cs"));

        var notes = ApplySearchPath(arguments, "mode", "filePath", "fileGlob");

        Assert.That(notes, Is.Null);
        Assert.That(arguments.ContainsKey("filePath"), Is.True);
        Assert.That(arguments.ContainsKey("fileGlob"), Is.False);
    }

    [Test]
    public void SearchText_PathAndFilePath_PathWinsAndFilePathIsLeft()
    {
        var arguments = Args(("mode", "text"), ("path", @"C:\repo\Sol\ProjA"), ("filePath", @"C:\repo\Sol\ProjB\Foo.cs"));

        var notes = ApplySearchPath(arguments, "mode", "filePath", "fileGlob");

        Assert.That(arguments.ContainsKey("path"), Is.False);
        Assert.That(arguments["fileGlob"].GetString(), Is.EqualTo("ProjA/**"));
        Assert.That(arguments["filePath"].GetString(), Is.EqualTo(@"C:\repo\Sol\ProjB\Foo.cs"));
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0], Does.StartWith("Note: 'path' is not a parameter of Search"));
    }
}
