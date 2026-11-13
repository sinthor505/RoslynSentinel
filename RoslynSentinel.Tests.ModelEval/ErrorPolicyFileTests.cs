using System.IO;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tests.ModelEval.LiveModel;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Checks the error-policy.md file copied to the test output directory: it parses, every key has a
/// verbatim body, it is pure ASCII, and the prompt heading and pointer text agree. Never calls a model.
/// Uses explicit-argument APIs only, so the static <see cref="ErrorPolicyOptions"/> state is never mutated.
/// </summary>
[TestFixture]
public sealed class ErrorPolicyFileTests
{
    private static readonly IReadOnlyDictionary<ErrorPolicyKey, string> ExpectedBodies =
        new Dictionary<ErrorPolicyKey, string>
        {
            [ErrorPolicyKey.FileNotFoundEdit] =
                "If you stop because of this error, start your final message with BLOCKED: and quote the error. The file does not exist. If the task named this file, stop: do not edit or create a different file.",
            [ErrorPolicyKey.FileNotFoundLookup] =
                "If you stop because of this error, start your final message with BLOCKED:. If the task named this file and the error lists no matching file, stop. If you chose the path yourself, make one call from the error's options; if that finds nothing, stop.",
            [ErrorPolicyKey.SymbolNotFoundEdit] =
                "If you stop because of this error, start your final message with BLOCKED: and quote the error. The symbol does not exist. If the task named it, stop: do not edit a different symbol or create it.",
            [ErrorPolicyKey.SymbolNotFoundLookup] =
                "If you stop because of this error, start your final message with BLOCKED:. If the task named this symbol and the error lists no similar name, stop. If you chose the name yourself, make one call from the error's options; if that finds nothing, stop.",
        };

    [Test]
    public void File_ExistsAtOutputPath()
    {
        Assert.That(File.Exists(ErrorPolicyFile.OutputPath), Is.True, ErrorPolicyFile.OutputPath);
    }

    [Test]
    public void Load_ParsesWithNoMissingKeysAndNonEmptyEntryForEveryKey()
    {
        ErrorPolicyDocument document = ErrorPolicyDocument.Load(ErrorPolicyFile.OutputPath);

        Assert.That(document.MissingKeys, Is.Empty);
        foreach (ErrorPolicyKey key in ErrorPolicyKeys.All)
        {
            bool present = document.Entries.TryGetValue(key, out string? body);
            Assert.That(present, Is.True, $"no entry for {ErrorPolicyKeys.ToWireName(key)}");
            Assert.That(body ?? string.Empty, Is.Not.Empty, ErrorPolicyKeys.ToWireName(key));
        }
    }

    [Test]
    public void Entries_MatchVerbatimBodiesForEachKey()
    {
        ErrorPolicyDocument document = ErrorPolicyDocument.Load(ErrorPolicyFile.OutputPath);

        foreach (KeyValuePair<ErrorPolicyKey, string> expected in ExpectedBodies)
        {
            string wireName = ErrorPolicyKeys.ToWireName(expected.Key);
            Assert.That(document.Entries.TryGetValue(expected.Key, out string? actual), Is.True, wireName);
            Assert.That(actual, Is.EqualTo(expected.Value), wireName);
        }
    }

    [Test]
    public void File_IsPureAscii()
    {
        byte[] bytes = File.ReadAllBytes(ErrorPolicyFile.OutputPath);

        Assert.That(bytes.All(b => b < 128), Is.True, "error-policy.md contains a non-ASCII byte");
    }

    [Test]
    public void WithErrorHandling_ContainsRenderedHeadingAndPointerTextForEveryKey()
    {
        ErrorPolicyDocument document = ErrorPolicyDocument.Load(ErrorPolicyFile.OutputPath);

        string prompt = AgentSystemPrompts.WithErrorHandling(AgentSystemPrompts.CodingAgent, document);

        foreach (ErrorPolicyKey key in ErrorPolicyKeys.All)
        {
            string heading = ErrorPolicyDocument.RenderedHeading(key);
            Assert.That(prompt, Does.Contain(heading), ErrorPolicyKeys.ToWireName(key));
            Assert.That(ErrorPolicyFilter.PointerText(key), Does.Contain(heading), ErrorPolicyKeys.ToWireName(key));
        }
    }

    [TestCase("pointer", null)]
    [TestCase("inline", null)]
    [TestCase("inline", "   ")]
    public void ResolveConfigureArgs_PointerOrInlineWithoutFile_DefaultsToBundledPolicyFile(string mode, string? file)
    {
        string[] args = ErrorPolicyFile.ResolveConfigureArgs(mode, file);

        Assert.That(args, Is.EqualTo(new[] { "--error-policy-file=" + ErrorPolicyFile.OutputPath }));
    }

    [TestCase(null, null)]
    [TestCase("off", null)]
    [TestCase("pointer", "x.md")]
    public void ResolveConfigureArgs_OffOrFileGiven_ReturnsNoArgs(string? mode, string? file)
    {
        string[] args = ErrorPolicyFile.ResolveConfigureArgs(mode, file);

        Assert.That(args, Is.Empty);
    }

    [Test]
    public void ResolveConfigureArgs_UnknownMode_Throws()
    {
        Assert.Throws<ArgumentException>(() => ErrorPolicyFile.ResolveConfigureArgs("bogus", null));
    }

    [Test]
    public void ResolveConfigureArgs_ResolvedPath_StampsPointerModeAndPolicyHash()
    {
        string[] args = ErrorPolicyFile.ResolveConfigureArgs("pointer", null);
        Assert.That(args, Has.Length.EqualTo(1));

        const string prefix = "--error-policy-file=";
        string resolvedPath = args[0][prefix.Length..];
        Assert.That(resolvedPath, Is.EqualTo(ErrorPolicyFile.OutputPath));
        Assert.That(File.Exists(resolvedPath), Is.True, resolvedPath);

        var stamp = ModelEvalAssemblySetUp.BuildProcessStamp(("abc123", false), ErrorPolicyMode.Pointer, resolvedPath);

        Assert.That(stamp, Is.Not.Null);
        Assert.That(stamp!.Value.ErrorPolicyMode, Is.EqualTo("pointer"));
        string expectedHash = ErrorPolicyDocument.Load(ErrorPolicyFile.OutputPath).ContentHash;
        Assert.That(expectedHash, Is.Not.Empty);
        Assert.That(stamp.Value.ErrorPolicyHash, Is.EqualTo(expectedHash));
    }
}
