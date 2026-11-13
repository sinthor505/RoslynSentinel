namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class ErrorVariantLoaderTests
{
    private const string Code = "FileNotFound";

    [Test]
    public void Parse_TrailingAbsent_IsNull()
    {
        var variant = ErrorVariantLoader.Parse(Code, "t", "## message\nhello\n");

        Assert.That(variant.Message, Is.EqualTo("hello"));
        Assert.That(variant.Trailing, Is.Null);
    }

    [Test]
    public void Parse_TrailingPresentButEmpty_IsEmptyString()
    {
        var variant = ErrorVariantLoader.Parse(Code, "t", "## message\nhello\n\n## trailing\n\n");

        Assert.That(variant.Trailing, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Parse_TrailingPresent_HasText()
    {
        var variant = ErrorVariantLoader.Parse(Code, "t", "## message\nhello\n## trailing\nremove it\n");

        Assert.That(variant.Trailing, Is.EqualTo("remove it"));
    }

    [Test]
    public void Parse_MessageAndTrailing_TrimmedAndLfNormalized()
    {
        var markdown = "\r\n## message\r\n\r\nfirst line\r\nsecond line  \r\n\r\n## trailing\r\n\r\ntail line\r\n\r\n";

        var variant = ErrorVariantLoader.Parse(Code, "t", markdown);

        Assert.That(variant.Message, Is.EqualTo("first line\nsecond line"));
        Assert.That(variant.Trailing, Is.EqualTo("tail line"));
        Assert.That(variant.Message, Does.Not.Contain("\r"));
        Assert.That(variant.Trailing, Does.Not.Contain("\r"));
    }

    [Test]
    public void Parse_SetsIdErrorCodeAndHashOfMarkdown()
    {
        const string markdown = "## message\nhello\n";

        var variant = ErrorVariantLoader.Parse(Code, "armX", markdown);

        Assert.That(variant.Id, Is.EqualTo("armX"));
        Assert.That(variant.ErrorCode, Is.EqualTo(Code));
        Assert.That(variant.Hash, Is.EqualTo(ErrorVariantLoader.HashOf(markdown)));
    }

    [Test]
    public void HashOf_SameForCrlfAndLf()
    {
        Assert.That(
            ErrorVariantLoader.HashOf("a\r\nb\r\n"),
            Is.EqualTo(ErrorVariantLoader.HashOf("a\nb\n")));
    }

    [Test]
    public void HashOf_DiffersWhenTextDiffers()
    {
        Assert.That(ErrorVariantLoader.HashOf("a"), Is.Not.EqualTo(ErrorVariantLoader.HashOf("b")));
    }

    [Test]
    public void HashOf_Is12LowercaseHex()
    {
        Assert.That(ErrorVariantLoader.HashOf("anything"), Does.Match("^[0-9a-f]{12}$"));
    }

    [Test]
    public void Parse_RejectsMissingMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ErrorVariantLoader.Parse(Code, "t", "## trailing\nx\n"));

        Assert.That(ex!.Message, Does.Contain("'t'"));
        Assert.That(ex.Message, Does.Contain("missing"));
    }

    [Test]
    public void Parse_RejectsUnknownSection()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ErrorVariantLoader.Parse(Code, "t", "## message\nx\n## extra\ny\n"));

        Assert.That(ex!.Message, Does.Contain("'t'"));
        Assert.That(ex.Message, Does.Contain("extra"));
    }

    [Test]
    public void Parse_RejectsDuplicateSection()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ErrorVariantLoader.Parse(Code, "t", "## message\nx\n## message\ny\n"));

        Assert.That(ex!.Message, Does.Contain("'t'"));
        Assert.That(ex.Message, Does.Contain("duplicate"));
    }

    [Test]
    public void Parse_RejectsTextBeforeFirstHeading()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ErrorVariantLoader.Parse(Code, "t", "stray\n## message\nx\n"));

        Assert.That(ex!.Message, Does.Contain("'t'"));
        Assert.That(ex.Message, Does.Contain("before the first"));
    }

    [Test]
    public void Parse_RejectsEmptyMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ErrorVariantLoader.Parse(Code, "t", "## message\n\n## trailing\nx\n"));

        Assert.That(ex!.Message, Does.Contain("'t'"));
        Assert.That(ex.Message, Does.Contain("empty"));
    }

    [Test]
    public void HarvestedFileNotFoundVariants_Load()
    {
        foreach (var id in new[] { "armE", "armF", "armG" })
        {
            var variant = ErrorVariantLoader.Load(Code, id);

            Assert.That(variant.ErrorCode, Is.EqualTo(Code));
            Assert.That(variant.Id, Is.EqualTo(id));
            Assert.That(variant.Message, Is.Not.Empty);
            Assert.That(variant.Trailing, Is.Not.Null.And.Not.Empty);
            Assert.That(variant.Hash, Has.Length.EqualTo(12));
        }

        Assert.That(ErrorVariantLoader.ListIds(Code), Does.Contain("armE").And.Contain("armF").And.Contain("armG"));
    }
}
