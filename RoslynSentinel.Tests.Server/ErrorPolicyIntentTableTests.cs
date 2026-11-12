// plan_error_policy_pointer Step 3b: ErrorPolicyIntentTable must classify every declared tool and every value of
// every dispatch selector (operation / action / mode), so a newly added tool or operation fails here until it is
// classified as Lookup or Edit. Lives in Tests.Server because it is the project that can see every Tools.* assembly.
using System.Reflection;
using System.Text.Json;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ErrorPolicy")]
public class ErrorPolicyIntentTableTests
{
    private static readonly string[] SelectorParameterNames = ["operation", "action", "mode"];

    // Loads every Tools.* assembly by touching one type from each, then scans the declared [McpServerTool] methods.
    private static IReadOnlyList<(string ToolName, Type ToolType, MethodInfo Method)> DeclaredTools()
    {
        var assemblies = new[]
        {
            typeof(ServerStatusTools).Assembly,
            typeof(SubAgentTools).Assembly,
            typeof(CodeTransformTools).Assembly,
        };
        return McpToolSchemaPatcher.DiscoverToolMethods(assemblies);
    }

    private static List<ParameterInfo> EnumSelectors(MethodInfo method) =>
        method.GetParameters()
            .Where(p => p.Name is not null
                && SelectorParameterNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                && (Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType).IsEnum)
            .ToList();

    [Test]
    public void DeclaredTools_SanityLowerBound_AllToolsAssembliesWereLoaded()
    {
        var declared = DeclaredTools();

        // About 121 tools today; the bound only has to prove Basic, Advanced and Experimental were all scanned.
        Assert.That(declared.Count, Is.GreaterThanOrEqualTo(110));
        Assert.That(declared.Select(t => t.ToolType.Assembly.GetName().Name).Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public void EveryDeclaredTool_HasATableEntry()
    {
        var missing = DeclaredTools()
            .Select(t => t.ToolName)
            .Where(name => !ErrorPolicyIntentTable.ToolNames.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(missing, Is.Empty, "Tools declared but not classified in ErrorPolicyIntentTable (add a Lookup/Edit entry): " + string.Join(", ", missing));
    }

    [Test]
    public void TableNamesNoToolThatIsNotDeclared()
    {
        var declared = DeclaredTools().Select(t => t.ToolName).ToHashSet(StringComparer.Ordinal);
        var stale = ErrorPolicyIntentTable.ToolNames
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(stale, Is.Empty, "ErrorPolicyIntentTable names tools that no [McpServerTool] method declares (remove them): " + string.Join(", ", stale));
    }

    [Test]
    public void EveryEnumSelector_IsCoveredByADispatchEntryWithExactlyTheEnumValues()
    {
        var problems = new List<string>();

        foreach (var (toolName, _, method) in DeclaredTools().OrderBy(t => t.ToolName, StringComparer.Ordinal))
        {
            var selectors = EnumSelectors(method);
            var isDispatch = ErrorPolicyIntentTable.TryGetDispatch(toolName, out var selectorName, out var values, out var ignored);

            if (selectors.Count == 0)
            {
                if (isDispatch)
                {
                    problems.Add($"{toolName}: table has a dispatch entry on '{selectorName}' but the tool has no enum-typed operation/action/mode parameter.");
                }

                continue;
            }

            var names = string.Join(", ", selectors.Select(p => p.Name));
            if (!isDispatch)
            {
                problems.Add($"{toolName}: has enum selector(s) [{names}] but the table entry is not a dispatch entry; map every value to Lookup or Edit.");
                continue;
            }

            var primary = selectors.FirstOrDefault(p => string.Equals(p.Name, selectorName, StringComparison.OrdinalIgnoreCase));
            if (primary is null)
            {
                problems.Add($"{toolName}: dispatch selector '{selectorName}' is not one of the tool's enum selectors [{names}].");
                continue;
            }

            var enumType = Nullable.GetUnderlyingType(primary.ParameterType) ?? primary.ParameterType;
            var enumValues = Enum.GetNames(enumType).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tableValues = values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var value in enumValues.Except(tableValues, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal))
            {
                problems.Add($"{toolName}: {enumType.Name} value '{value}' (selector '{selectorName}') has no intent in the table.");
            }

            foreach (var value in tableValues.Except(enumValues, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal))
            {
                problems.Add($"{toolName}: table names value '{value}' that {enumType.Name} does not declare (selector '{selectorName}').");
            }

            // Secondary enum selectors must be acknowledged in the entry; stale acknowledgements fail too.
            var otherNames = selectors
                .Where(p => !ReferenceEquals(p, primary))
                .Select(p => p.Name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var other in otherNames.Except(ignored, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{toolName}: enum selector '{other}' is neither the dispatch selector nor listed as ignored in the table entry.");
            }

            foreach (var stale in ignored.Except(otherNames, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{toolName}: table lists '{stale}' as an ignored selector but the tool has no such enum selector.");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }
}

[TestFixture]
[Category("ErrorPolicy")]
public class ErrorPolicyClassifyTests
{
    private static IReadOnlyDictionary<string, JsonElement> Args(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);

    [Test]
    public void ReadFile_FileNotFound_IsFileLookup()
    {
        var key = ErrorPolicyClassifier.Classify("ReadFile", Args(("filePath", "a.cs")), "FileNotFound", null);

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.FileNotFoundLookup));
    }

    [Test]
    public void ReplaceSnippet_Apply_DocumentNotFound_IsFileEdit()
    {
        var key = ErrorPolicyClassifier.Classify("ReplaceSnippet", Args(("action", "apply")), ToolErrorCode.NotFound, "document");

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.FileNotFoundEdit));
    }

    [Test]
    public void ReplaceSnippet_Validate_DocumentNotFound_IsFileLookup()
    {
        // validate never writes, so it takes the Lookup key.
        var key = ErrorPolicyClassifier.Classify("ReplaceSnippet", Args(("action", "validate")), ToolErrorCode.NotFound, "document");

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.FileNotFoundLookup));
    }

    [Test]
    public void Member_View_IsSymbolLookup_AndReplace_IsSymbolEdit()
    {
        var view = ErrorPolicyClassifier.Classify("Member", Args(("operation", "view")), ToolErrorCode.NotFound, "method");
        var replace = ErrorPolicyClassifier.Classify("Member", Args(("operation", "replace")), ToolErrorCode.NotFound, "method");

        Assert.That(view, Is.EqualTo(ErrorPolicyKey.SymbolNotFoundLookup));
        Assert.That(replace, Is.EqualTo(ErrorPolicyKey.SymbolNotFoundEdit));
    }

    [Test]
    public void Search_SymbolMode_SymbolNotFound_IsSymbolLookup()
    {
        var key = ErrorPolicyClassifier.Classify("Search", Args(("mode", "symbol")), ToolErrorCode.NotFound, "symbol");

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.SymbolNotFoundLookup));
    }

    [Test]
    public void RenameSymbol_MethodNotFound_IsSymbolEdit()
    {
        var key = ErrorPolicyClassifier.Classify("RenameSymbol", Args(("symbolName", "Foo")), "MethodNotFound", null);

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.SymbolNotFoundEdit));
    }

    [Test]
    public void UnknownTool_ReturnsNull()
    {
        Assert.That(ErrorPolicyClassifier.Classify("NoSuchTool", null, "FileNotFound", null), Is.Null);
    }

    [Test]
    public void NonNotFoundErrorCode_ReturnsNull()
    {
        Assert.That(ErrorPolicyClassifier.Classify("ReadFile", null, ToolErrorCode.InvalidArgument, "document"), Is.Null);
    }

    [Test]
    public void NotFoundWithoutKind_ReturnsNull()
    {
        Assert.That(ErrorPolicyClassifier.Classify("ReadFile", null, ToolErrorCode.NotFound, null), Is.Null);
    }

    [Test]
    public void DispatchTool_MissingArguments_UsesTheToolDefault()
    {
        // Member and ReplaceSnippet default to Edit (they can mutate); Search defaults to Lookup (it cannot).
        Assert.That(ErrorPolicyClassifier.Classify("Member", null, ToolErrorCode.NotFound, "member"), Is.EqualTo(ErrorPolicyKey.SymbolNotFoundEdit));
        Assert.That(ErrorPolicyClassifier.Classify("ReplaceSnippet", new Dictionary<string, JsonElement>(), ToolErrorCode.NotFound, "document"), Is.EqualTo(ErrorPolicyKey.FileNotFoundEdit));
        Assert.That(ErrorPolicyClassifier.Classify("Search", null, ToolErrorCode.NotFound, "symbol"), Is.EqualTo(ErrorPolicyKey.SymbolNotFoundLookup));
    }

    [Test]
    public void DispatchTool_SelectorNameAndValueAreCaseInsensitive()
    {
        var key = ErrorPolicyClassifier.Classify("ReplaceSnippet", Args(("Action", "VALIDATE")), ToolErrorCode.NotFound, "document");

        Assert.That(key, Is.EqualTo(ErrorPolicyKey.FileNotFoundLookup));
    }

    [Test]
    public void DispatchTool_NullOrUnrecognisedOrNumericSelector_UsesTheToolDefault()
    {
        Assert.That(ErrorPolicyIntentTable.Resolve("Member", Args(("operation", null))), Is.EqualTo(ErrorPolicyIntent.Edit));
        Assert.That(ErrorPolicyIntentTable.Resolve("Member", Args(("operation", "nonsense"))), Is.EqualTo(ErrorPolicyIntent.Edit));
        Assert.That(ErrorPolicyIntentTable.Resolve("Member", Args(("operation", 4))), Is.EqualTo(ErrorPolicyIntent.Edit));
        Assert.That(ErrorPolicyIntentTable.Resolve("Search", Args(("mode", "nonsense"))), Is.EqualTo(ErrorPolicyIntent.Lookup));
    }

    [Test]
    public void Git_StatusIsLookup_CommitIsEdit_AndMissingOperationDefaultsToEdit()
    {
        Assert.That(ErrorPolicyIntentTable.Resolve("Git", Args(("operation", "status"))), Is.EqualTo(ErrorPolicyIntent.Lookup));
        Assert.That(ErrorPolicyIntentTable.Resolve("Git", Args(("operation", "commit"))), Is.EqualTo(ErrorPolicyIntent.Edit));
        Assert.That(ErrorPolicyIntentTable.Resolve("Git", null), Is.EqualTo(ErrorPolicyIntent.Edit));
    }
}
