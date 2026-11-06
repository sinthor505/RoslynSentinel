using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RejectedContentClassifier")] // sentinel:auto-category
public class RejectedContentClassifierTests
{
    [Test]
    public void Classify_UsingsWithAMethod_IsFileLevel()
    {
        const string content = "using System;\npublic void Run() { }";

        Assert.That(RejectedContentClassifier.Classify(content), Is.EqualTo(RejectedContentShape.FileLevel));
    }

    [Test]
    public void Classify_WholeClassDeclaration_IsFileLevel()
    {
        const string content = "public class Widget\n{\n    public int Id { get; set; }\n}";

        Assert.That(RejectedContentClassifier.Classify(content), Is.EqualTo(RejectedContentShape.FileLevel));
    }

    [Test]
    public void Classify_SingleMethod_IsSingleMember()
    {
        const string content = "public int Add(int a, int b)\n{\n    return a + b;\n}";

        Assert.That(RejectedContentClassifier.Classify(content), Is.EqualTo(RejectedContentShape.SingleMember));
    }

    [Test]
    public void Classify_FieldPlusMethod_IsMultipleMembers()
    {
        const string content = "private int _count;\npublic void Increment()\n{\n    _count++;\n}";

        Assert.That(RejectedContentClassifier.Classify(content), Is.EqualTo(RejectedContentShape.MultipleMembers));
    }

    [Test]
    public void Classify_Null_IsUnspecified()
    {
        Assert.That(RejectedContentClassifier.Classify(null), Is.EqualTo(RejectedContentShape.Unspecified));
    }

    [Test]
    public void Classify_Whitespace_IsUnspecified()
    {
        Assert.That(RejectedContentClassifier.Classify(" \r\n\t  "), Is.EqualTo(RejectedContentShape.Unspecified));
    }

    [Test]
    public void Classify_StatementFragment_IsUnspecified()
    {
        Assert.That(RejectedContentClassifier.Classify("return 1;"), Is.EqualTo(RejectedContentShape.Unspecified));
    }
}
