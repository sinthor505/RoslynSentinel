namespace RoslynSentinel.Tests;

/// <summary>
/// <c>serverInfo.binaryPath</c> is stamped on every response, so it is emitted relative to the
/// loaded solution root (with / separators) when the binary is under it. Outside the root, or with
/// no solution loaded, it stays absolute. McpServerStatus keeps reporting the absolute path.
/// <para>
/// <see cref="NonParallelizableAttribute"/>: the provider-based tests swap the process-wide
/// <see cref="ServerBuildInfo.SolutionRootProvider"/> (which every
/// <see cref="PersistentWorkspaceManager"/> construction also sets) and restore it afterwards.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class ServerBuildInfoEnvelopePathTests
{
    private const string Binary = @"C:\Users\Administrator\source\repos\RoslynSentinel\bin-vscode\d87a019a\Advanced\RoslynSentinel.Server.Advanced.dll";

    [Test]
    public void BinaryUnderRoot_IsRelative_WithForwardSlashes()
    {
        var path = ServerBuildInfo.ToEnvelopeBinaryPath(Binary, @"C:\Users\Administrator\source\repos\RoslynSentinel");

        Assert.That(path, Is.EqualTo("bin-vscode/d87a019a/Advanced/RoslynSentinel.Server.Advanced.dll"));
    }

    [Test]
    public void BinaryUnderRoot_MatchesCaseInsensitively_AndToleratesTrailingSeparator()
    {
        // The loaded root is as the caller typed it (lowercase drive); Assembly.Location is uppercase.
        var path = ServerBuildInfo.ToEnvelopeBinaryPath(Binary, @"c:\users\administrator\source\repos\roslynsentinel\");

        Assert.That(path, Is.EqualTo("bin-vscode/d87a019a/Advanced/RoslynSentinel.Server.Advanced.dll"));
    }

    [Test]
    public void BinaryOutsideRoot_StaysAbsolute_NeverADotDotChain()
    {
        var path = ServerBuildInfo.ToEnvelopeBinaryPath(Binary, @"C:\tmp\fixture-solution");

        Assert.That(path, Is.EqualTo(Binary));
        Assert.That(path, Does.Not.Contain(".."));
    }

    [Test]
    public void RootThatIsOnlyANamePrefixOfTheBinaryFolder_IsNotTreatedAsUnderRoot()
    {
        // Root "...\bin" must not match "...\bin-vscode\..." by raw string prefix.
        var path = ServerBuildInfo.ToEnvelopeBinaryPath(
            Binary, @"C:\Users\Administrator\source\repos\RoslynSentinel\bin");

        Assert.That(path, Is.EqualTo(Binary));
    }

    [Test]
    public void NoSolutionRoot_StaysAbsolute()
    {
        Assert.That(ServerBuildInfo.ToEnvelopeBinaryPath(Binary, null), Is.EqualTo(Binary));
        Assert.That(ServerBuildInfo.ToEnvelopeBinaryPath(Binary, ""), Is.EqualTo(Binary));
    }

    [Test]
    public void EmptyBinaryPath_IsReturnedUnchanged()
    {
        Assert.That(ServerBuildInfo.ToEnvelopeBinaryPath("", @"C:\repo"), Is.EqualTo(""));
    }

    [Test]
    public void ServerInfo_UsesProvider_RelativeUnderRoot_AbsoluteOtherwise()
    {
        var original = ServerBuildInfo.SolutionRootProvider;
        try
        {
            var binaryDir = Path.GetDirectoryName(ServerBuildInfo.BinaryPath);
            Assume.That(binaryDir, Is.Not.Null.And.Not.Empty, "The test host has no on-disk assembly location.");

            ServerBuildInfo.SolutionRootProvider = () => binaryDir;
            var underRoot = new ServerInfo().BinaryPath;
            Assert.That(underRoot, Is.EqualTo(Path.GetFileName(ServerBuildInfo.BinaryPath)));

            ServerBuildInfo.SolutionRootProvider = () => Path.Combine(Path.GetTempPath(), "unrelated-" + Guid.NewGuid().ToString("N"));
            Assert.That(new ServerInfo().BinaryPath, Is.EqualTo(ServerBuildInfo.BinaryPath));

            ServerBuildInfo.SolutionRootProvider = () => null;
            Assert.That(new ServerInfo().BinaryPath, Is.EqualTo(ServerBuildInfo.BinaryPath));

            ServerBuildInfo.SolutionRootProvider = null;
            Assert.That(new ServerInfo().BinaryPath, Is.EqualTo(ServerBuildInfo.BinaryPath));
        }
        finally
        {
            ServerBuildInfo.SolutionRootProvider = original;
        }
    }

    [Test]
    public void RawBinaryPath_StaysAbsolute_WhateverTheProviderSays()
    {
        // ServerBuildInfo.BinaryPath (what McpServerStatus.serverBinaryPath reports) is never rewritten.
        var original = ServerBuildInfo.SolutionRootProvider;
        try
        {
            var binaryDir = Path.GetDirectoryName(ServerBuildInfo.BinaryPath);
            Assume.That(binaryDir, Is.Not.Null.And.Not.Empty);

            ServerBuildInfo.SolutionRootProvider = () => binaryDir;

            Assert.That(Path.IsPathRooted(ServerBuildInfo.BinaryPath), Is.True);
            Assert.That(ServerBuildInfo.BinaryPath, Is.Not.EqualTo(new ServerInfo().BinaryPath));
        }
        finally
        {
            ServerBuildInfo.SolutionRootProvider = original;
        }
    }

    [Test]
    public void McpServerStatus_ReportsAbsoluteBinaryPath_EvenWhenTheEnvelopeIsRelative()
    {
        var original = ServerBuildInfo.SolutionRootProvider;
        try
        {
            using var manager = new PersistentWorkspaceManager(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>.Instance);
            var binaryDir = Path.GetDirectoryName(ServerBuildInfo.BinaryPath);
            Assume.That(binaryDir, Is.Not.Null.And.Not.Empty, "The test host has no on-disk assembly location.");
            ServerBuildInfo.SolutionRootProvider = () => binaryDir;

            var empty = new HashSet<string>();
            var tools = new RoslynSentinel.Tools.Basic.ServerStatusTools(
                manager,
                new ActiveToolSurface("test", empty, empty, empty, empty),
                new StoppedByScriptMarker(WasFound: false, Details: null));

            var result = (SentinelCallToolResult<RoslynSentinel.Tools.Basic.McpServerStatusResult>)tools.McpServerStatus();

            Assert.That(result.SuccessData!.ServerBinaryPath, Is.EqualTo(ServerBuildInfo.BinaryPath),
                "McpServerStatus is the ground truth for stale-binary checks and must stay absolute.");
            Assert.That(result.ServerInfo.BinaryPath, Is.EqualTo(Path.GetFileName(ServerBuildInfo.BinaryPath)),
                "The per-response envelope is the relativized one.");
        }
        finally
        {
            ServerBuildInfo.SolutionRootProvider = original;
        }
    }
}
