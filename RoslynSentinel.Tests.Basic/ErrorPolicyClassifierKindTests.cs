using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class ErrorPolicyClassifierKindTests
{
    [TestCase(ToolErrorCode.NotFound, "document", ErrorPolicyTargetKind.File)]
    [TestCase(ToolErrorCode.NotFound, "file", ErrorPolicyTargetKind.File)]
    [TestCase(ToolErrorCode.NotFound, "class", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "interface", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "type", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "record", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "enum", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "enum member", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "method", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "member", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "declaration", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "property", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "field", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "constructor", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "parameter", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "attribute", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "namespace", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "symbol", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "container", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "project", null)]
    [TestCase(ToolErrorCode.NotFound, "statement", null)]
    [TestCase(ToolErrorCode.NotFound, "statements", null)]
    [TestCase(ToolErrorCode.NotFound, "expression", null)]
    [TestCase(ToolErrorCode.NotFound, "snippet", null)]
    [TestCase(ToolErrorCode.NotFound, "block", null)]
    [TestCase(ToolErrorCode.NotFound, "someUnlistedKind", null)]
    [TestCase(ToolErrorCode.NotFound, "Document", ErrorPolicyTargetKind.File)]
    [TestCase(ToolErrorCode.NotFound, " FILE ", ErrorPolicyTargetKind.File)]
    [TestCase(ToolErrorCode.NotFound, "ENUM MEMBER", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, "  Class  ", ErrorPolicyTargetKind.Symbol)]
    [TestCase(ToolErrorCode.NotFound, null, null)]
    [TestCase(ToolErrorCode.NotFound, "", null)]
    [TestCase(ToolErrorCode.NotFound, "   ", null)]
    [TestCase("FileNotFound", null, ErrorPolicyTargetKind.File)]
    [TestCase("FileNotFound", "method", ErrorPolicyTargetKind.File)]
    [TestCase("MethodNotFound", null, ErrorPolicyTargetKind.Symbol)]
    [TestCase("MethodNotFound", "document", ErrorPolicyTargetKind.Symbol)]
    [TestCase("Ambiguous", "document", null)]
    [TestCase("SolutionNotLoaded", "document", null)]
    [TestCase(null, "document", null)]
    [TestCase("", "document", null)]
    public void MapKind_ReturnsExpectedTargetKind(string? errorCode, string? missingTargetKind, ErrorPolicyTargetKind? expected)
    {
        var actual = ErrorPolicyClassifier.MapKind(errorCode, missingTargetKind);

        Assert.That(actual, Is.EqualTo(expected));
    }
}
