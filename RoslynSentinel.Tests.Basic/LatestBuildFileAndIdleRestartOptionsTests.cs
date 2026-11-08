using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class LatestBuildFileTests
{
    private const string Sha40 = "0123456789abcdef0123456789abcdef01234567";
    private const string LauncherJson = """{"headSha":"0123456789abcdef0123456789abcdef01234567","buildSha8":"86874736","selection":"reuse","buildFailed":false,"utc":"2026-11-08T03:07:42Z"}""";

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "RoslynSentinel-LatestBuildTests-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LatestBuildRecord? ReadWhenFileContains(string content)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, LatestBuildFile.FileName), content);
        return LatestBuildFile.Read(_root);
    }

    [Test]
    public void Read_LauncherShapedFile_MapsEveryField()
    {
        var record = ReadWhenFileContains(LauncherJson);

        Assert.That(record, Is.Not.Null);
        Assert.That(record!.HeadSha, Is.EqualTo(Sha40));
        Assert.That(record.BuildSha8, Is.EqualTo("86874736"));
        Assert.That(record.Selection, Is.EqualTo("reuse"));
        Assert.That(record.BuildFailed, Is.False);
        Assert.That(record.Utc, Is.EqualTo("2026-11-08T03:07:42Z"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("[]")]
    public void Read_EmptyOrMalformedFile_ReturnsNull(string content)
    {
        Assert.That(ReadWhenFileContains(content), Is.Null);
    }

    [Test]
    public void Read_NullRoot_ReturnsNull()
    {
        Assert.That(LatestBuildFile.Read(null), Is.Null);
    }

    [Test]
    public void Read_MissingDirectory_ReturnsNull()
    {
        Assert.That(LatestBuildFile.Read(Path.Combine(_root, "does-not-exist")), Is.Null);
    }

    [Test]
    public void Read_DirectoryWithoutLatestJson_ReturnsNull()
    {
        Directory.CreateDirectory(_root);

        Assert.That(LatestBuildFile.Read(_root), Is.Null);
    }

    [Test]
    public void Read_DirectoryWithValidLatestJson_ReturnsRecord()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, LatestBuildFile.FileName), LauncherJson);

        var record = LatestBuildFile.Read(_root);

        Assert.That(record, Is.Not.Null);
        Assert.That(record!.HeadSha, Is.EqualTo(Sha40));
        Assert.That(record.BuildSha8, Is.EqualTo("86874736"));
    }
}

[TestFixture]
[NonParallelizable]
public class IdleRestartOptionsTests
{
    private const string MinutesEnv = "ROSLYNSENTINEL_IDLE_RESTART_MINUTES";
    private const string BuildsRootEnv = "ROSLYNSENTINEL_BUILDS_ROOT";

    private string? _savedMinutes;
    private string? _savedBuildsRoot;

    [SetUp]
    public void SetUp()
    {
        _savedMinutes = Environment.GetEnvironmentVariable(MinutesEnv);
        _savedBuildsRoot = Environment.GetEnvironmentVariable(BuildsRootEnv);
        Environment.SetEnvironmentVariable(MinutesEnv, null);
        Environment.SetEnvironmentVariable(BuildsRootEnv, null);
        ResetToDefaults();
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(MinutesEnv, null);
        Environment.SetEnvironmentVariable(BuildsRootEnv, null);
        ResetToDefaults();
        Environment.SetEnvironmentVariable(MinutesEnv, _savedMinutes);
        Environment.SetEnvironmentVariable(BuildsRootEnv, _savedBuildsRoot);
    }

    // ResetForTests is internal to Common and not visible to this assembly. Configure with no arguments and with the
    // environment variables cleared restores the built-in defaults, so it serves as the reset here.
    private static void ResetToDefaults()
    {
        IdleRestartOptions.Configure(Array.Empty<string>());
    }

    [Test]
    public void Defaults_AreFifteenMinutesNoBuildsRootAndDisabled()
    {
        IdleRestartOptions.Configure(Array.Empty<string>());

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(15));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.Null);
        Assert.That(IdleRestartOptions.Enabled, Is.False);
    }

    [Test]
    public void EqualsFormArgs_SetMinutesAndBuildsRoot_AndEnable()
    {
        IdleRestartOptions.Configure(new[] { "--idle-restart-minutes=5", "--builds-root=C:\\x" });

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(5));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.EqualTo("C:\\x"));
        Assert.That(IdleRestartOptions.Enabled, Is.True);
    }

    [Test]
    public void SpaceSeparatedArgs_SetMinutesAndBuildsRoot_AndEnable()
    {
        IdleRestartOptions.Configure(new[] { "--idle-restart-minutes", "5", "--builds-root", "C:\\x" });

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(5));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.EqualTo("C:\\x"));
        Assert.That(IdleRestartOptions.Enabled, Is.True);
    }

    [Test]
    public void MinutesZero_DisablesEvenWhenBuildsRootIsSet()
    {
        IdleRestartOptions.Configure(new[] { "--idle-restart-minutes=0", "--builds-root=C:\\x" });

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(0));
        Assert.That(IdleRestartOptions.Enabled, Is.False);
    }

    [Test]
    public void EnvironmentMinutes_UsedWhenNoMinutesArg()
    {
        Environment.SetEnvironmentVariable(MinutesEnv, "7");

        IdleRestartOptions.Configure(Array.Empty<string>());

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(7));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.Null);
        Assert.That(IdleRestartOptions.Enabled, Is.False);
    }

    [Test]
    public void EnvironmentBuildsRoot_UsedWhenNoBuildsRootArg()
    {
        Environment.SetEnvironmentVariable(BuildsRootEnv, "C:\\env");

        IdleRestartOptions.Configure(Array.Empty<string>());

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(15));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.EqualTo("C:\\env"));
        Assert.That(IdleRestartOptions.Enabled, Is.True);
    }

    [Test]
    public void Arguments_WinOverEnvironment()
    {
        Environment.SetEnvironmentVariable(MinutesEnv, "9");
        Environment.SetEnvironmentVariable(BuildsRootEnv, "C:\\env");

        IdleRestartOptions.Configure(new[] { "--idle-restart-minutes=5" });

        Assert.That(IdleRestartOptions.Minutes, Is.EqualTo(5));
        Assert.That(IdleRestartOptions.BuildsRoot, Is.EqualTo("C:\\env"));
        Assert.That(IdleRestartOptions.Enabled, Is.True);
    }
}
