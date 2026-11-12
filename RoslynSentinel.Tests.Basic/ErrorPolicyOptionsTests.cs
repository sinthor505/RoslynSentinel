using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// ErrorPolicyOptions is static (process-wide state) and reads process environment variables, so this fixture must not run in parallel with other tests.
[NonParallelizable]
public class ErrorPolicyOptionsTests
{
    private const string ModeEnv = "ROSLYNSENTINEL_ERROR_POLICY_MODE";
    private const string FileEnv = "ROSLYNSENTINEL_ERROR_POLICY_FILE";

    private string? _savedMode;
    private string? _savedFile;
    private string? _tempFile;

    [SetUp]
    public void SaveAndClearEnv()
    {
        _savedMode = Environment.GetEnvironmentVariable(ModeEnv);
        _savedFile = Environment.GetEnvironmentVariable(FileEnv);
        Environment.SetEnvironmentVariable(ModeEnv, null);
        Environment.SetEnvironmentVariable(FileEnv, null);
    }

    [TearDown]
    public void RestoreEnvAndResetOptions()
    {
        // Reset while the env vars are still cleared, so the developer's own settings cannot make this teardown throw.
        ErrorPolicyOptions.Configure(Array.Empty<string>());
        Environment.SetEnvironmentVariable(ModeEnv, _savedMode);
        Environment.SetEnvironmentVariable(FileEnv, _savedFile);
        if (_tempFile is not null && File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }

        _tempFile = null;
    }

    [Test]
    public void Configure_NoArgsOrEnv_DefaultsToOffWithNullFile()
    {
        ErrorPolicyOptions.Configure(Array.Empty<string>());

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Off));
        Assert.That(ErrorPolicyOptions.PolicyFilePath, Is.Null);
    }

    [Test]
    public void Configure_EnvVarPointer_SelectsPointer()
    {
        Environment.SetEnvironmentVariable(ModeEnv, "pointer");

        ErrorPolicyOptions.Configure(Array.Empty<string>());

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Pointer));
    }

    [Test]
    public void Configure_FlagOverridesEnvVar()
    {
        Environment.SetEnvironmentVariable(ModeEnv, "pointer");

        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Off));
    }

    [Test]
    public void Configure_InlineEqualsForm_ParsesWithFile()
    {
        var path = CreateTempPolicyFile();

        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=inline", "--error-policy-file=" + path });

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Inline));
        Assert.That(ErrorPolicyOptions.PolicyFilePath, Is.EqualTo(path));
    }

    [Test]
    public void Configure_InlineSpaceForm_ParsesWithFile()
    {
        var path = CreateTempPolicyFile();

        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode", "inline", "--error-policy-file", path });

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Inline));
        Assert.That(ErrorPolicyOptions.PolicyFilePath, Is.EqualTo(path));
    }

    [Test]
    public void Configure_InvalidFlagValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=bogus" }));
    }

    [Test]
    public void Configure_InvalidEnvValue_Throws()
    {
        Environment.SetEnvironmentVariable(ModeEnv, "bogus");

        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(Array.Empty<string>()));
    }

    [Test]
    public void Configure_BareModeFlag_Throws()
    {
        // A bare flag reads as the value "true", which is not a valid mode.
        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(new[] { "--error-policy-mode" }));
    }

    [TestCase("POINTER")]
    [TestCase("  Pointer  ")]
    public void ParseMode_IsCaseInsensitiveAndTrimmed(string raw)
    {
        Assert.That(ErrorPolicyOptions.ParseMode(raw), Is.EqualTo(ErrorPolicyMode.Pointer));
    }

    [Test]
    public void Configure_InlineWithNoFile_Throws()
    {
        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=inline" }));
    }

    [Test]
    public void Configure_InlineWithMissingPath_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rs-error-policy-missing-" + Guid.NewGuid().ToString("N") + ".md");

        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=inline", "--error-policy-file=" + missing }));
    }

    [Test]
    public void Configure_PointerWithoutFile_Succeeds()
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=pointer" });

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Pointer));
        Assert.That(ErrorPolicyOptions.PolicyFilePath, Is.Null);
    }

    [Test]
    public void Configure_FailedCall_LeavesPreviousStateUnchanged()
    {
        var path = CreateTempPolicyFile();
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=inline", "--error-policy-file=" + path });

        Assert.Throws<ArgumentException>(() => ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=bogus" }));

        Assert.That(ErrorPolicyOptions.Mode, Is.EqualTo(ErrorPolicyMode.Inline));
        Assert.That(ErrorPolicyOptions.PolicyFilePath, Is.EqualTo(path));
    }

    [TestCase(ErrorPolicyKey.FileNotFoundLookup, "FileNotFound.Lookup")]
    [TestCase(ErrorPolicyKey.FileNotFoundEdit, "FileNotFound.Edit")]
    [TestCase(ErrorPolicyKey.SymbolNotFoundLookup, "SymbolNotFound.Lookup")]
    [TestCase(ErrorPolicyKey.SymbolNotFoundEdit, "SymbolNotFound.Edit")]
    public void WireName_RoundTrips(ErrorPolicyKey key, string wireName)
    {
        Assert.That(ErrorPolicyKeys.ToWireName(key), Is.EqualTo(wireName));
        Assert.That(ErrorPolicyKeys.TryParseWireName(wireName, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(key));
    }

    [Test]
    public void WireName_AllListsEveryKey()
    {
        Assert.That(ErrorPolicyKeys.All, Is.EquivalentTo(Enum.GetValues<ErrorPolicyKey>()));
    }

    [TestCase("filenotfound.lookup")]
    [TestCase("FileNotFound")]
    [TestCase("FileNotFound.Lookup ")]
    [TestCase(null)]
    public void TryParseWireName_RejectsNonExactNames(string? name)
    {
        Assert.That(ErrorPolicyKeys.TryParseWireName(name, out _), Is.False);
    }

    private string CreateTempPolicyFile()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "rs-error-policy-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(_tempFile, "policy");
        return _tempFile;
    }
}
