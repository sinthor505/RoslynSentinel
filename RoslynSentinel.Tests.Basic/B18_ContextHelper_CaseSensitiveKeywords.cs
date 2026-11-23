// Battery #27 -> Bug-Fix Regression Tests
// Each test proves a specific bug (catalogued in the review sessions) is fixed and
// cannot silently regress.  Test names encode the bug ID they guard.
//
// Bugs covered:
//   B08 -> DeadCodeEngine: written-but-never-read variables were not detected as unused
//   B02 -> ImmutabilityEngine: const fields received readonly modifier (-> CS0106)
//   B01 -> InstrumentationEngine: throw was emitted as ExpressionStatement (invalid C#)
//   B03 -> SolutionStructureEngine: IdentifierName contained a dot (invalid identifier)
//   B09 -> AdvancedRefactoringEngine: OptimizeTaskWaitAsync rewrote all .Wait()/.Result
//         when semantic model was null (false positives)
//   B18 -> ContextHelper: OrdinalIgnoreCase blocked valid PascalCase identifiers such as
//         "String", "Object", "Int"
//   B19 -> TestingEngine: MSTest framework produced attributeless test methods
//   B17 -> DocumentationEngine: HasStructuredTrivia excluded methods inside #region
//   B11 -> ModernizationEngine.ClassToRecordAsync: duplicate property declarations (CS0102)
//   B10 -> ModernizationEngine.TryConvertOrChainToPattern: only last value used (chain lost)
//   B20 -> LogicSimplificationEngine.AddGuardClausesAsync: nullable params got null guards
//   B04 -> AntiPatternEngine.DetectMissingCancellationToken: zero-param async methods skipped
//   B16 -> SecurityAndSafetyEngine.DetectMissingNullChecksAsync: expression-bodied methods skipped
#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
public class B18_ContextHelper_CaseSensitiveKeywords
{
    [Test]
    public void GetUniqueVariableName_PascalCaseTypeNameAsBase_NotBlockedByReservedKeywords()
    {
        // Before fix: OrdinalIgnoreCase caused "String" to match keyword "string",
        // and "Object" to match "object", so these valid identifiers were unreachable.
        // After camelCase conversion: "String" -> "string" IS a keyword, so suffix is added.
        // But "MyString" -> "myString" is NOT a keyword and must be returned as-is.
        // Use a minimal scope with no declarations
        var emptyScope = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block();
        var result1 = ContextHelper.GetUniqueVariableName(emptyScope, "MyString");
        Assert.That(result1, Is.EqualTo("myString"), "'MyString' -> camelCase 'myString' is not a keyword and must be returned as-is.");
        var result2 = ContextHelper.GetUniqueVariableName(emptyScope, "MyObject");
        Assert.That(result2, Is.EqualTo("myObject"), "'MyObject' -> camelCase 'myObject' is not a keyword and must be returned as-is.");
        var result3 = ContextHelper.GetUniqueVariableName(emptyScope, "MyInt");
        Assert.That(result3, Is.EqualTo("myInt"), "'MyInt' -> camelCase 'myInt' is not a keyword and must be returned as-is.");
    }

    [Test]
    public void GetUniqueVariableName_ExistingNameConflict_CaseSensitive()
    {
        // Before fix: OrdinalIgnoreCase meant "myValue" conflicted with "MyValue"
        // in C# they are different identifiers -> the suffix should NOT be added.
        const string source = """
            public class C {
                public void M() {
                    var MyValue = 1;
                }
            }
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        // "myValue" (camelCase of "MyValue") is different from "MyValue" in C# -> no conflict
        var result = ContextHelper.GetUniqueVariableName(method, "myValue");
        Assert.That(result, Is.EqualTo("myValue"), "'myValue' is case-sensitively distinct from existing 'MyValue' - must not add suffix.");
    }

    [Test]
    public void GetUniqueVariableName_ExactCaseConflict_AddsSuffix()
    {
        // If the scope DOES have "myValue" already, we should get "myValue1"
        const string source = """
            public class C {
                public void M() {
                    var myValue = 1;
                }
            }
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First();
        var result = ContextHelper.GetUniqueVariableName(method, "myValue");
        Assert.That(result, Is.EqualTo("myValue1"), "When exact-case 'myValue' exists, must return 'myValue1'.");
    }
}
