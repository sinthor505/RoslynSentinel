using System.Net;
using System.Net.Sockets;
using System.Text;

using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// SubAgentModelRun.AwaitSolutionLoadAsync only waits when a load is running (SolutionLoadStatus.Loading).
// FakeWorkspaceManager never reports Loading, so only the "nothing to wait for" path is covered here.
// TODO cover the Loading-and-times-out path (needs a fake whose SolutionLoadStatus is Loading and whose WaitForLoadAsync returns Loading)
[TestFixture]
public class SubAgentModelRunTests
{
    [Test]
    public async Task AwaitSolutionLoadAsync_NoSolutionLoaded_ReturnsNullImmediately()
    {
        using var workspaceManager = FakeWorkspaceManager.ReadOnly();

        var error = await SubAgentModelRun.AwaitSolutionLoadAsync(workspaceManager, CancellationToken.None);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task AwaitSolutionLoadAsync_SolutionLoaded_ReturnsNullImmediately()
    {
        using var workspaceManager = FakeWorkspaceManager.ReadOnly();
        workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("A.cs", "public class A {}")]));

        var error = await SubAgentModelRun.AwaitSolutionLoadAsync(workspaceManager, CancellationToken.None);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_UnknownAlias_NamesTheKnownAliases()
    {
        using var table = TempHostTable.With("http://127.0.0.1:1/v1");

        var (_, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("nope", "any-model", CancellationToken.None, table.FilePath);

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(RoslynSentinel.Common.ToolErrorCode.InvalidArgument));
        Assert.That(error.Message, Does.Contain("'nope'").And.Contain("Known aliases: testhost"));
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_NoHostTableFile_SaysNoAliasIsKnown()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid().ToString("N"), "eval-hosts.json");

        var (_, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("testhost", "any-model", CancellationToken.None, missing);

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("Known aliases: none"));
        Assert.That(error.Message, Does.Not.Contain(missing), "the table path must not leak into the result");
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_ClosedPort_RefusesAsUnreachable_NamingUrlAndExceptionKind()
    {
        // Bind then release a port so nothing is listening on it.
        var baseUrl = $"http://127.0.0.1:{ModelsServer.FreePort()}/v1";
        using var table = TempHostTable.With(baseUrl);

        var (_, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("testhost", "any-model", CancellationToken.None, table.FilePath);

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(RoslynSentinel.Common.ToolErrorCode.PreconditionNotMet));
        Assert.That(error.Message, Does.Contain("unreachable").And.Contain(baseUrl + "/models").And.Contain("HttpRequestException"));
        Assert.That(error.Message, Does.Not.Contain("   at "), "no stack trace in the result");
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_HostAnswersWithAnError_RefusesNamingTheStatus()
    {
        using var server = new ModelsServer(HttpStatusCode.ServiceUnavailable, "{}");
        using var table = TempHostTable.With(server.BaseUrl);

        var (_, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("testhost", "any-model", CancellationToken.None, table.FilePath);

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("503"));
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_ModelMissingFromTheList_RefusesAndListsThePresentModels()
    {
        using var server = new ModelsServer(HttpStatusCode.OK, "{\"data\":[{\"id\":\"alpha-model\"},{\"id\":\"vendor/beta-model\"}]}");
        using var table = TempHostTable.With(server.BaseUrl);

        var (_, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("testhost", "gamma-model", CancellationToken.None, table.FilePath);

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.ErrorCode, Is.EqualTo(RoslynSentinel.Common.ToolErrorCode.NotFound));
        Assert.That(error.Message, Does.Contain("'gamma-model'").And.Contain("alpha-model").And.Contain("vendor/beta-model"));
    }

    [Test]
    public async Task ResolveAndCheckHostAsync_ModelPresent_ReturnsTheAliasBaseUrlAndNoError()
    {
        using var server = new ModelsServer(HttpStatusCode.OK, "{\"data\":[{\"id\":\"alpha-model\"},{\"id\":\"vendor/beta-model\"}]}");
        using var table = TempHostTable.With(server.BaseUrl);

        // The id match ignores the vendor prefix and case, as LlamaServerModels.IdsMatch does.
        var (baseUrl, error) = await SubAgentModelRun.ResolveAndCheckHostAsync("TESTHOST", "BETA-model", CancellationToken.None, table.FilePath);

        Assert.That(error, Is.Null);
        Assert.That(baseUrl, Is.EqualTo(server.BaseUrl));
    }

    /// <summary>A temp eval-hosts.json with one alias, "testhost".</summary>
    private sealed class TempHostTable : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid().ToString("N"));

        public string FilePath
        {
            get;
        }

        private TempHostTable(string baseUrl)
        {
            Directory.CreateDirectory(_directory);
            FilePath = System.IO.Path.Combine(_directory, "eval-hosts.json");
            File.WriteAllText(FilePath,
                "{\"hosts\":[{\"alias\":\"testhost\",\"machine\":\"testmachine\",\"baseUrl\":\"" + baseUrl + "\"," +
                "\"backend\":\"llama-server\",\"suffix\":\"testhost\",\"default\":true}]}");
        }

        public static TempHostTable With(string baseUrl) => new(baseUrl);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Answers GET /v1/models with a fixed status and body on a free loopback port.</summary>
    private sealed class ModelsServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public string BaseUrl
        {
            get;
        }

        public ModelsServer(HttpStatusCode status, string body)
        {
            BaseUrl = $"http://127.0.0.1:{FreePort()}/v1";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return; // listener closed
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }
}
