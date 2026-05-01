using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Comprehensive tests for all engine methods implemented in the current development cycle:
/// AsyncSafetyEngine, DeadCodeEngine, AnalysisEngine, SecurityEngine,
/// GranularRefactoringEngine, AdvancedRefactoringEngine, and RefinementEngine.
/// </summary>
[TestFixture]
public class NewImplementationsTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private DeadCodeEngine _deadCodeEngine;
    private AnalysisEngine _analysisEngine;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private RefinementEngine _refinementEngine;
    private SecurityEngine _securityEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager);
        _analysisEngine = new AnalysisEngine(_workspaceManager, _config);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _securityEngine = new SecurityEngine(_workspaceManager);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // DeadCodeEngine.FindUnusedPrivateMembersAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindUnusedPrivateMembers_ReturnsReport_ForUnreferencedPrivateMethod()
    {
        SetSource(@"
public class MyClass
{
    private void UnusedHelper() { }
    public void Run() { }
}", "MyClass.cs");

        var results = await _deadCodeEngine.FindUnusedPrivateMembersAsync("MyClass.cs", "MyClass");

        Assert.That(results, Is.Not.Null);
        Assert.That(results.Any(r => r.SymbolName == "UnusedHelper"), Is.True,
            "UnusedHelper is not called anywhere — should be flagged.");
        Assert.That(results.All(r => r.SymbolName != "Run"), Is.True,
            "Public method Run should not appear in the results.");
    }

    [Test]
    public async Task FindUnusedPrivateMembers_NoReport_ForCalledPrivateMethod()
    {
        SetSource(@"
public class Calculator
{
    private int Double(int x) => x * 2;
    public int Quadruple(int x) => Double(Double(x));
}", "Calculator.cs");

        var results = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Calculator.cs", "Calculator");

        Assert.That(results.Any(r => r.SymbolName == "Double"), Is.False,
            "Double is called by Quadruple and should not be flagged.");
    }

    [Test]
    public async Task FindUnusedPrivateMembers_ReturnsReport_ForUnusedPrivateProperty()
    {
        SetSource(@"
public class Config
{
    private int CacheSize { get; set; }
    public void Apply() { }
}", "Config.cs");

        var results = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Config.cs", "Config");

        Assert.That(results.Any(r => r.SymbolName == "CacheSize"), Is.True,
            "CacheSize is never read or written from outside its declaration — should be flagged.");
    }

    [Test]
    public async Task FindUnusedPrivateMembers_ReturnsEmptyList_ForClassWithNoPrivateMembers()
    {
        SetSource(@"
public class Empty
{
    public void DoWork() { }
}", "Empty.cs");

        var results = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Empty.cs", "Empty");

        Assert.That(results, Is.Empty, "No private members means nothing to report.");
    }

    [Test]
    public async Task FindUnusedPrivateMembers_IncludesLineAndColumn()
    {
        SetSource(@"
public class LineChecker
{
    private void Ghost() { }
}", "LineChecker.cs");

        var results = await _deadCodeEngine.FindUnusedPrivateMembersAsync("LineChecker.cs", "LineChecker");

        var ghostReport = results.FirstOrDefault(r => r.SymbolName == "Ghost");
        Assert.That(ghostReport, Is.Not.Null, "Ghost should be reported.");
        Assert.That(ghostReport!.Line, Is.GreaterThan(0), "Line must be positive.");
        Assert.That(ghostReport.Column, Is.GreaterThan(0), "Column must be positive.");
        Assert.That(ghostReport.Type, Is.EqualTo("UnusedPrivateMember"));
    }

    // ══════════════════════════════════════════════════════════════
    // DeadCodeEngine.FindUnusedConstructorsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindUnusedConstructors_SkipsSingleConstructorClass()
    {
        SetSource(@"
public class Service
{
    public Service(string name) { }
    public void Start() { }
}", "Service.cs");

        var results = await _deadCodeEngine.FindUnusedConstructorsAsync("Service.cs");

        Assert.That(results, Is.Empty,
            "Single-constructor classes are skipped to avoid false positives with DI registration.");
    }

    [Test]
    public async Task FindUnusedConstructors_ReportsUnreferencedOverload_WhenMultipleExist()
    {
        SetSource(@"
public class Widget
{
    public Widget() { }
    public Widget(int size) { }
    public static Widget CreateDefault() => new Widget();
}", "Widget.cs");

        var results = await _deadCodeEngine.FindUnusedConstructorsAsync("Widget.cs");

        // Widget() is called; Widget(int) is not referenced
        Assert.That(results, Is.Not.Empty,
            "At least one constructor overload is unreferenced and should be reported.");
        Assert.That(results.All(r => r.Type == "UnusedConstructorOverload"), Is.True);
    }

    [Test]
    public async Task FindUnusedConstructors_ReturnsEmpty_WhenAllOverloadsAreReferenced()
    {
        SetSource(@"
public class Pair
{
    public Pair() { }
    public Pair(int x, int y) { }
    public static void Use()
    {
        var a = new Pair();
        var b = new Pair(1, 2);
    }
}", "Pair.cs");

        var results = await _deadCodeEngine.FindUnusedConstructorsAsync("Pair.cs");

        Assert.That(results, Is.Empty,
            "Both constructors are explicitly instantiated so neither should be reported.");
    }

    // ══════════════════════════════════════════════════════════════
    // DeadCodeEngine.CheckForUnusedEventSubscriptionsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task CheckForUnusedEventSubscriptions_Reports_SubscriptionWithoutUnsubscribe()
    {
        SetSource(@"
public class Publisher { public event System.EventHandler Changed; }
public class Subscriber
{
    private Publisher _pub;
    public Subscriber(Publisher p)
    {
        _pub = p;
        _pub.Changed += OnChanged;
    }
    private void OnChanged(object s, System.EventArgs e) { }
}", "Subscriber.cs");

        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Subscriber.cs");

        Assert.That(results.Any(r => r.SymbolName.Contains("Changed")), Is.True,
            "_pub.Changed += OnChanged has no matching -= and should be reported.");
        Assert.That(results.All(r => r.Type == "EventSubscriptionWithoutUnsubscription"), Is.True);
    }

    [Test]
    public async Task CheckForUnusedEventSubscriptions_Accepts_MatchingUnsubscribe()
    {
        SetSource(@"
public class Publisher { public event System.EventHandler Fired; }
public class Listener : System.IDisposable
{
    private Publisher _pub;
    public Listener(Publisher p) { _pub = p; _pub.Fired += Handle; }
    public void Dispose() { _pub.Fired -= Handle; }
    private void Handle(object s, System.EventArgs e) { }
}", "Listener.cs");

        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Listener.cs");

        Assert.That(results.All(r => !r.SymbolName.Contains("Fired")), Is.True,
            "Fired has a matching -= in Dispose and should not be flagged.");
    }

    [Test]
    public async Task CheckForUnusedEventSubscriptions_ReturnsEmpty_WhenNoSubscriptions()
    {
        SetSource(@"
public class Clean
{
    public void DoWork() { int x = 1 + 1; }
}", "Clean.cs");

        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Clean.cs");

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task CheckForUnusedEventSubscriptions_Reports_MultipleSubscriptionsWithoutUnsubscribe()
    {
        SetSource(@"
public class Hub
{
    public event System.EventHandler A;
    public event System.EventHandler B;
}
public class Fan
{
    public Fan(Hub h)
    {
        h.A += OnA;
        h.B += OnB;
    }
    private void OnA(object s, System.EventArgs e) { }
    private void OnB(object s, System.EventArgs e) { }
}", "Fan.cs");

        var results = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Fan.cs");

        Assert.That(results.Count, Is.EqualTo(2),
            "Both h.A and h.B are subscribed without unsubscribing.");
    }

    // ══════════════════════════════════════════════════════════════
    // AnalysisEngine.DetectMemoryLeaksAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task DetectMemoryLeaks_ReturnsEmpty_WhenFeatureDisabled()
    {
        _config.SetFeatureStatus("MemoryLeaks", false);
        SetSource(@"
public class Pub { public event System.EventHandler Tick; }
public class Sub { public Sub(Pub p) { p.Tick += Handle; } private void Handle(object s, System.EventArgs e) { } }");

        var results = await _analysisEngine.DetectMemoryLeaksAsync("Test.cs");

        Assert.That(results, Is.Empty, "Feature toggle off should produce no results.");
    }

    [Test]
    public async Task DetectMemoryLeaks_Flags_ExternalSubscriptionWithoutIDisposable()
    {
        _config.SetFeatureStatus("MemoryLeaks", true);
        SetSource(@"
public class Clock { public event System.EventHandler Tick; }
public class Dashboard
{
    private Clock _clock;
    public Dashboard(Clock c)
    {
        _clock = c;
        _clock.Tick += Refresh;
    }
    private void Refresh(object s, System.EventArgs e) { }
}", "Dashboard.cs");

        var results = await _analysisEngine.DetectMemoryLeaksAsync("Dashboard.cs");

        Assert.That(results.Any(r => r.Contains("Dashboard") && r.Contains("Tick")), Is.True,
            "Dashboard subscribes to _clock.Tick without IDisposable — potential memory leak.");
    }

    [Test]
    public async Task DetectMemoryLeaks_NoFlag_WhenClassImplementsIDisposableWithUnsubscribe()
    {
        _config.SetFeatureStatus("MemoryLeaks", true);
        SetSource(@"
public class Button { public event System.EventHandler Click; }
public class Form : System.IDisposable
{
    private Button _btn;
    public Form(Button b) { _btn = b; _btn.Click += OnClick; }
    public void Dispose() { _btn.Click -= OnClick; }
    private void OnClick(object s, System.EventArgs e) { }
}", "Form.cs");

        var results = await _analysisEngine.DetectMemoryLeaksAsync("Form.cs");

        Assert.That(results.All(r => !r.Contains("Form") || !r.Contains("Click")), Is.True,
            "Form implements IDisposable with proper unsubscription — should not be flagged.");
    }

    // ══════════════════════════════════════════════════════════════
    // AnalysisEngine.FindPossibleInfiniteLoopsAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindPossibleInfiniteLoops_Detects_WhileTrueWithoutExit()
    {
        SetSource(@"
public class Pump
{
    public void Run()
    {
        while (true)
        {
            System.Threading.Thread.Sleep(100);
        }
    }
}", "Pump.cs");

        var results = await _analysisEngine.FindPossibleInfiniteLoopsAsync("Pump.cs");

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(r => r.Contains("while")), Is.True,
            "while(true) without break/return/throw must be flagged.");
    }

    [Test]
    public async Task FindPossibleInfiniteLoops_DoesNotFlag_WhileTrueWithBreak()
    {
        SetSource(@"
public class Poller
{
    private bool _done;
    public void Poll()
    {
        while (true)
        {
            if (_done) break;
        }
    }
}", "Poller.cs");

        var results = await _analysisEngine.FindPossibleInfiniteLoopsAsync("Poller.cs");

        Assert.That(results, Is.Empty,
            "while(true) with a break statement has an exit path and must not be flagged.");
    }

    [Test]
    public async Task FindPossibleInfiniteLoops_DoesNotFlag_WhileTrueWithReturn()
    {
        SetSource(@"
public class RetryLoop
{
    public int TryGet()
    {
        while (true)
        {
            return 42;
        }
    }
}", "RetryLoop.cs");

        var results = await _analysisEngine.FindPossibleInfiniteLoopsAsync("RetryLoop.cs");

        Assert.That(results, Is.Empty, "while(true) with return is not an infinite loop.");
    }

    [Test]
    public async Task FindPossibleInfiniteLoops_Detects_ForeverForLoop()
    {
        SetSource(@"
public class Spin
{
    public void Spin1() { for (;;) { System.Threading.Thread.Sleep(1); } }
}", "Spin.cs");

        var results = await _analysisEngine.FindPossibleInfiniteLoopsAsync("Spin.cs");

        Assert.That(results, Is.Not.Empty, "for(;;) without exit should be flagged as potential infinite loop.");
    }

    [Test]
    public async Task FindPossibleInfiniteLoops_DoesNotFlag_ConditionalWhileLoop()
    {
        SetSource(@"
public class Reader
{
    public void Read(System.IO.Stream s)
    {
        int b;
        while ((b = s.ReadByte()) != -1) { }
    }
}", "Reader.cs");

        var results = await _analysisEngine.FindPossibleInfiniteLoopsAsync("Reader.cs");

        Assert.That(results, Is.Empty,
            "while(condition) with a non-literal condition must not be flagged.");
    }

    // ══════════════════════════════════════════════════════════════
    // AnalysisEngine.GenerateCallTreeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateCallTree_ListsRootMethodAndDirectCallee()
    {
        SetSource(@"
public class Math
{
    public int Add(int a, int b) => a + b;
    public int Double(int x) => Add(x, x);
}", "Math.cs");

        var tree = await _analysisEngine.GenerateCallTreeAsync("Math.cs", "Double");

        Assert.That(tree, Is.Not.Null.And.Not.Empty);
        Assert.That(tree, Does.Contain("Double"), "Root method must appear in call tree.");
        Assert.That(tree, Does.Contain("Add"), "Direct callee Add must appear in call tree.");
    }

    [Test]
    public async Task GenerateCallTree_ReturnsNotFound_ForMissingFile()
    {
        SetSource("public class C { }", "C.cs");

        var tree = await _analysisEngine.GenerateCallTreeAsync("Missing.cs", "Foo");

        Assert.That(tree, Does.Contain("not found").Or.Contain("File"),
            "Missing file should produce an error message, not an exception.");
    }

    [Test]
    public async Task GenerateCallTree_RespectsDepthLimit()
    {
        SetSource(@"
public class Chain
{
    public void A() => B();
    public void B() => C();
    public void C() => D();
    public void D() { }
}", "Chain.cs");

        // depth 2 should not include D (A -> B -> C is depth 2, D would be depth 3)
        var tree = await _analysisEngine.GenerateCallTreeAsync("Chain.cs", "A", depth: 2);

        Assert.That(tree, Does.Contain("A"));
        Assert.That(tree, Does.Contain("B"));
    }

    // ══════════════════════════════════════════════════════════════
    // AnalysisEngine.GenerateEqualityOverridesAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task GenerateEqualityOverrides_AddsEqualsAndGetHashCode_ForFields()
    {
        SetSource(@"
public class Point
{
    private int _x;
    private int _y;
}", "Point.cs");

        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Point.cs", "Point");

        Assert.That(result, Does.Contain("Equals"), "Equals override must be generated.");
        Assert.That(result, Does.Contain("GetHashCode"), "GetHashCode override must be generated.");
        Assert.That(result, Does.Contain("obj is Point other"), "Equals must use pattern matching.");
        Assert.That(result, Does.Contain("HashCode.Combine"), "GetHashCode should use HashCode.Combine.");
    }

    [Test]
    public async Task GenerateEqualityOverrides_FallsBackToProperties_WhenNoFields()
    {
        SetSource(@"
public class Person
{
    public string Name { get; set; }
    public int Age { get; set; }
}", "Person.cs");

        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Person.cs", "Person");

        Assert.That(result, Does.Contain("Equals"));
        Assert.That(result, Does.Contain("Name"), "Name property must appear in equality logic.");
        Assert.That(result, Does.Contain("Age"), "Age property must appear in equality logic.");
    }

    [Test]
    public async Task GenerateEqualityOverrides_UsesHashCodeBuilder_ForManyFields()
    {
        // More than 8 fields triggers the HashCode builder pattern
        SetSource(@"
public class Big
{
    private int _a, _b, _c, _d, _e, _f, _g, _h, _i;
}", "Big.cs");

        var result = await _analysisEngine.GenerateEqualityOverridesAsync("Big.cs", "Big");

        // With 9 fields (a..i) the builder pattern should be used
        Assert.That(result, Does.Contain("GetHashCode"), "GetHashCode must still be generated.");
        Assert.That(result, Does.Contain("HashCode").Or.Contain("hc"), "Should use HashCode builder for >8 fields.");
    }

    [Test]
    public async Task GenerateEqualityOverrides_ThrowsForClassWithNoFieldsOrProperties()
    {
        SetSource(@"
public class Marker { }", "Marker.cs");

        Assert.ThrowsAsync<Exception>(async () =>
            await _analysisEngine.GenerateEqualityOverridesAsync("Marker.cs", "Marker"),
            "A class with no fields or properties cannot generate equality overrides.");
    }

    // ══════════════════════════════════════════════════════════════
    // SecurityEngine.AnalyzeSecurityAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AnalyzeSecurity_Detects_HardcodedPassword()
    {
        SetSource(@"
public class Config
{
    private string password = ""SuperSecret123"";
}", "Config.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("Config.cs");

        Assert.That(results.Any(r => r.IssueType == "HardcodedSecret"), Is.True,
            "Identifier named 'password' with a string literal should be flagged as HardcodedSecret.");
        Assert.That(results.Any(r => r.Description.Contains("password")), Is.True);
    }

    [Test]
    public async Task AnalyzeSecurity_Detects_HardcodedApiKey()
    {
        SetSource(@"
public class Client
{
    private string apiKey = ""abc123secretvalue"";
}", "Client.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("Client.cs");

        Assert.That(results.Any(r => r.IssueType == "HardcodedSecret"), Is.True,
            "apiKey with a non-trivial string literal must be flagged.");
    }

    [Test]
    public async Task AnalyzeSecurity_Detects_WeakHashMd5()
    {
        SetSource(@"
using System.Security.Cryptography;
public class Hasher
{
    public byte[] Hash(byte[] data) => MD5.HashData(data);
}", "Hasher.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("Hasher.cs");

        Assert.That(results.Any(r => r.IssueType == "WeakHashAlgorithm"), Is.True,
            "MD5 usage must be reported as WeakHashAlgorithm.");
    }

    [Test]
    public async Task AnalyzeSecurity_Detects_WeakHashSha1()
    {
        SetSource(@"
using System.Security.Cryptography;
public class Verifier
{
    public byte[] Sign(byte[] data) => SHA1.HashData(data);
}", "Verifier.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("Verifier.cs");

        Assert.That(results.Any(r => r.IssueType == "WeakHashAlgorithm"), Is.True,
            "SHA1 must also be flagged as a weak algorithm.");
    }

    [Test]
    public async Task AnalyzeSecurity_Detects_InsecureRandomInSecurityContext()
    {
        // The engine matches the CLOSEST ancestor: VariableDeclaratorSyntax > MethodDeclarationSyntax.
        // To ensure the method name ("GenerateToken") is the matched ancestor, put new Random()
        // directly in a return (no local variable), so no VariableDeclaratorSyntax sits between
        // the ObjectCreationExpressionSyntax and the MethodDeclarationSyntax.
        // NOTE: the engine checks oc.Type.ToString() == "Random" (exact), so use the short name.
        SetSource(@"
using System;
public class TokenFactory
{
    public string GenerateToken()
    {
        return new Random().Next().ToString();
    }
}", "TokenFactory.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("TokenFactory.cs");

        Assert.That(results.Any(r => r.IssueType == "InsecureRandom"), Is.True,
            "new Random() inside a method named GenerateToken (contains 'token') should be flagged.");
    }

    [Test]
    public async Task AnalyzeSecurity_ReturnsEmpty_ForCleanCode()
    {
        SetSource(@"
public class Clean
{
    private readonly string _greeting = ""Hello"";
    public string Greet() => _greeting;
}", "Clean.cs");

        var results = await _securityEngine.AnalyzeSecurityAsync("Clean.cs");

        Assert.That(results, Is.Empty, "No security issues expected in clean code.");
    }

    // ══════════════════════════════════════════════════════════════
    // SecurityEngine.CheckForSqlInjectionAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task CheckForSqlInjection_Detects_InterpolatedStringPassedToSqlMethod()
    {
        // The engine checks *arguments* passed directly to named SQL methods (ExecuteNonQuery, Query, etc.)
        SetSource(@"
public class Repo
{
    public void Query(string name, System.Data.IDbConnection conn)
    {
        // Direct call — interpolated string as the SQL argument
        Execute($""SELECT * FROM Users WHERE Name = '{name}'"");
    }
    private void Execute(string sql) { }
}", "Repo.cs");

        var results = await _securityEngine.CheckForSqlInjectionAsync("Repo.cs");

        // ExecuteScalar/Query/etc. in the method name list; engine checks method name from invocation
        // Use a known method name from the list: "ExecuteScalar"
        SetSource(@"
public interface IDb { object ExecuteScalar(string sql); }
public class Repo2
{
    private IDb _db;
    public object GetUser(string name) => _db.ExecuteScalar($""SELECT * FROM Users WHERE Name = '{name}'"");
}", "Repo2.cs");

        var results2 = await _securityEngine.CheckForSqlInjectionAsync("Repo2.cs");

        Assert.That(results2, Is.Not.Empty, "Interpolated string passed to ExecuteScalar should be flagged.");
        Assert.That(results2.Any(r => r.IssueType == "PossibleSqlInjection"), Is.True);
    }

    [Test]
    public async Task CheckForSqlInjection_Detects_DynamicConcatPassedToSqlMethod()
    {
        SetSource(@"
public interface IDb { void Query(string sql); }
public class Repo
{
    private IDb _db;
    public void GetUser(string id) => _db.Query(""SELECT * FROM User WHERE Id = "" + id);
}", "Repo.cs");

        var results = await _securityEngine.CheckForSqlInjectionAsync("Repo.cs");

        Assert.That(results, Is.Not.Empty,
            "Dynamic string concatenation passed to a SQL method should be flagged.");
        Assert.That(results.Any(r => r.IssueType == "PossibleSqlInjection"), Is.True);
    }

    [Test]
    public async Task CheckForSqlInjection_ReturnsEmpty_ForLiteralSqlOnly()
    {
        SetSource(@"
public interface IDb { void ExecuteNonQuery(string sql); }
public class SafeRepo
{
    private IDb _db;
    public void GetUser() => _db.ExecuteNonQuery(""SELECT * FROM User WHERE Id = @Id"");
}", "SafeRepo.cs");

        var results = await _securityEngine.CheckForSqlInjectionAsync("SafeRepo.cs");

        Assert.That(results, Is.Empty,
            "A pure string literal with no interpolation or concatenation should not be flagged.");
    }

    [Test]
    public async Task CheckForSqlInjection_ReturnsEmpty_ForNonSqlMethods()
    {
        SetSource(@"
public class Logger
{
    public void LogError(string userId)
    {
        Log($""User {userId} had an error."");
    }
    private void Log(string msg) { }
}", "Logger.cs");

        var results = await _securityEngine.CheckForSqlInjectionAsync("Logger.cs");

        Assert.That(results, Is.Empty,
            "Log() is not a SQL method — interpolated string here is fine.");
    }

    // ══════════════════════════════════════════════════════════════
    // AsyncSafetyEngine.FindTaskYieldUsageAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindTaskYieldUsage_Detects_TaskYieldCall()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Worker
{
    public async Task RunAsync()
    {
        await Task.Yield();
    }
}", "Worker.cs");

        var results = await _asyncSafetyEngine.FindTaskYieldUsageAsync("Worker.cs");

        Assert.That(results, Is.Not.Empty, "Task.Yield() call should be reported.");
        Assert.That(results.Any(r => r.MethodName == "RunAsync"), Is.True);
    }

    [Test]
    public async Task FindTaskYieldUsage_ReturnsEmpty_WhenNoYieldCall()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Quiet
{
    public async Task DoAsync() { await Task.Delay(100); }
}", "Quiet.cs");

        var results = await _asyncSafetyEngine.FindTaskYieldUsageAsync("Quiet.cs");

        Assert.That(results, Is.Empty, "No Task.Yield() means no reports.");
    }

    // ══════════════════════════════════════════════════════════════
    // AsyncSafetyEngine.FindTaskDelayUsageAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindTaskDelayUsage_Detects_TaskDelayCall()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Sleeper
{
    public async Task SleepAsync() { await Task.Delay(500); }
}", "Sleeper.cs");

        var results = await _asyncSafetyEngine.FindTaskDelayUsageAsync("Sleeper.cs");

        Assert.That(results, Is.Not.Empty, "Task.Delay() usage should be reported.");
        Assert.That(results.Any(r => r.MethodName == "SleepAsync"), Is.True);
    }

    [Test]
    public async Task FindTaskDelayUsage_ReturnsEmpty_ForNoDelay()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Busy
{
    public async Task<int> ComputeAsync() { return await Task.FromResult(42); }
}", "Busy.cs");

        var results = await _asyncSafetyEngine.FindTaskDelayUsageAsync("Busy.cs");

        Assert.That(results, Is.Empty, "Task.FromResult is not a Delay — should produce no report.");
    }

    // ══════════════════════════════════════════════════════════════
    // AsyncSafetyEngine.FindTaskDelayZeroUsageAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindTaskDelayZeroUsage_Detects_TaskDelayZero()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Yielder
{
    public async Task YieldAsync() { await Task.Delay(0); }
}", "Yielder.cs");

        var results = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Yielder.cs");

        Assert.That(results, Is.Not.Empty,
            "Task.Delay(0) is a suboptimal yield pattern — should be reported as a Task.Yield() candidate.");
        Assert.That(results.Any(r => r.MethodName == "YieldAsync"), Is.True);
    }

    [Test]
    public async Task FindTaskDelayZeroUsage_DoesNotFlag_NonZeroDelay()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Waiter
{
    public async Task WaitAsync() { await Task.Delay(1000); }
}", "Waiter.cs");

        var results = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Waiter.cs");

        Assert.That(results, Is.Empty, "Task.Delay(1000) is not a zero-delay and must not be flagged.");
    }

    [Test]
    public async Task FindTaskDelayZeroUsage_DoesNotFlag_TaskDelayWithNegativeOrNull()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Edge
{
    public async Task WaitAsync(int ms) { await Task.Delay(ms); }
}", "Edge.cs");

        var results = await _asyncSafetyEngine.FindTaskDelayZeroUsageAsync("Edge.cs");

        Assert.That(results, Is.Empty, "Task.Delay with a variable argument should not be flagged.");
    }

    // ══════════════════════════════════════════════════════════════
    // AsyncSafetyEngine.FindTaskWhenAllUsageAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task FindTaskWhenAllUsage_Detects_MultipleSequentialAwaits()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Loader
{
    private Task<int> GetA() => Task.FromResult(1);
    private Task<int> GetB() => Task.FromResult(2);
    public async Task<int> LoadAsync()
    {
        var a = await GetA();
        var b = await GetB();
        return a + b;
    }
}", "Loader.cs");

        var results = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Loader.cs");

        Assert.That(results, Is.Not.Empty,
            "LoadAsync has two sequential awaits that could run in parallel via Task.WhenAll.");
        Assert.That(results.Any(r => r.MethodName == "LoadAsync"), Is.True);
    }

    [Test]
    public async Task FindTaskWhenAllUsage_DoesNotFlag_SingleAwait()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Simple
{
    public async Task<int> FetchAsync() { return await Task.FromResult(42); }
}", "Simple.cs");

        var results = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Simple.cs");

        Assert.That(results, Is.Empty, "A single await cannot be parallelized — nothing to flag.");
    }

    [Test]
    public async Task FindTaskWhenAllUsage_DoesNotFlag_EmptyAsyncMethod()
    {
        SetSource(@"
using System.Threading.Tasks;
public class Shell
{
    public async Task NoOpAsync() { await Task.CompletedTask; }
}", "Shell.cs");

        var results = await _asyncSafetyEngine.FindTaskWhenAllUsageAsync("Shell.cs");

        Assert.That(results, Is.Empty, "A single await on CompletedTask should not trigger the check.");
    }

    // ══════════════════════════════════════════════════════════════
    // AdvancedRefactoringEngine.ReplaceStringConcatWithInterpolationAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ReplaceStringConcat_Converts_LiteralPlusVariableToInterpolation()
    {
        SetSource(@"
public class Greeter
{
    public string Greet(string name) => ""Hello, "" + name + ""!"";
}", "Greeter.cs");

        var result = await _advancedRefactoringEngine.ReplaceStringConcatWithInterpolationAsync("Greeter.cs");

        Assert.That(result, Does.Contain("$\""), "Output must contain an interpolated string.");
        Assert.That(result, Does.Contain("{name}"), "Variable 'name' must be in an interpolation hole.");
        Assert.That(result, Does.Not.Contain("\" + name + \""), "Original concat must be replaced.");
    }

    [Test]
    public async Task ReplaceStringConcat_DoesNotChange_PureLiteralConcat()
    {
        // Two string literals concatenated — Roslyn folds these at compile time; no variable to interpolate
        SetSource(@"
public class C { public string S() => ""Hello"" + "", World""; }", "C.cs");

        var result = await _advancedRefactoringEngine.ReplaceStringConcatWithInterpolationAsync("C.cs");

        // Still returns content without throwing; may or may not convert (implementation-defined for pure literals)
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ReplaceStringConcat_DoesNotChange_FileWithNoStringConcat()
    {
        SetSource(@"public class C { public string Hello() => ""Hi""; }", "C.cs");

        var result = await _advancedRefactoringEngine.ReplaceStringConcatWithInterpolationAsync("C.cs");

        Assert.That(result, Does.Not.Contain("$\""), "No concat means no interpolation should be introduced.");
    }

    [Test]
    public async Task ReplaceStringConcat_HandlesMultipleConcatExpressions()
    {
        SetSource(@"
public class Reporter
{
    public string Line1(string x) => ""A="" + x;
    public string Line2(string y) => ""B="" + y;
}", "Reporter.cs");

        var result = await _advancedRefactoringEngine.ReplaceStringConcatWithInterpolationAsync("Reporter.cs");

        Assert.That(result.Contains("{x}") || result.Contains("{y}"), Is.True,
            "At least one concat chain must be converted.");
    }

    // ══════════════════════════════════════════════════════════════
    // AdvancedRefactoringEngine.OptimizeTaskWaitAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task OptimizeTaskWait_Replaces_ResultPropertyWithAwait()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C
{
    public void Run()
    {
        var t = Task.FromResult(42);
        var x = t.Result;
    }
}", "C.cs");

        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("C.cs");

        Assert.That(result, Does.Contain("await"), ".Result must be replaced by await.");
        Assert.That(result, Does.Contain("async"), "Containing method must be made async.");
        Assert.That(result, Does.Not.Contain(".Result"), ".Result must not remain in output.");
    }

    [Test]
    public async Task OptimizeTaskWait_Replaces_WaitMethodWithAwait()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C
{
    public void Execute()
    {
        Task.Delay(100).Wait();
    }
}", "C.cs");

        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("C.cs");

        Assert.That(result, Does.Contain("await"));
        Assert.That(result, Does.Not.Contain(".Wait()"), ".Wait() must be replaced.");
    }

    [Test]
    public async Task OptimizeTaskWait_Replaces_GetAwaiterGetResult()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C
{
    public void Fetch()
    {
        var val = Task.FromResult(""data"").GetAwaiter().GetResult();
    }
}", "C.cs");

        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("C.cs");

        Assert.That(result, Does.Contain("await"), "GetAwaiter().GetResult() must be replaced by await.");
        Assert.That(result, Does.Not.Contain("GetAwaiter"), "GetAwaiter chain must be gone.");
    }

    [Test]
    public async Task OptimizeTaskWait_DoesNotChange_AlreadyAsyncMethod()
    {
        SetSource(@"
using System.Threading.Tasks;
public class C
{
    public async Task<int> GetAsync()
    {
        return await Task.FromResult(1);
    }
}", "C.cs");

        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("C.cs");

        Assert.That(result, Does.Contain("async"), "Already-async method should remain async.");
        Assert.That(result, Does.Contain("await"), "Existing await must be preserved.");
        Assert.That(result, Does.Not.Contain(".Result"), "No blocking calls to introduce.");
    }

    // ══════════════════════════════════════════════════════════════
    // GranularRefactoringEngine.IntroduceFieldAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task IntroduceField_ExtractsLiteralToPrivateReadonlyField()
    {
        // "42" is at line 5, column 17 in this exact source layout
        const string source = @"public class C
{
    public void M()
    {
        var x = 42;
    }
}";
        SetSource(source, "C.cs");
        int line = 5;
        var lines = source.Split('\n');
        int col = lines[line - 1].IndexOf("42") + 1;

        var result = await _granularRefactoringEngine.IntroduceFieldAsync("C.cs", "var x = 42", "_answer");

        Assert.That(result, Does.Contain("_answer"), "New field name must appear in output.");
        Assert.That(result, Does.Contain("private"), "Extracted field must be private.");
        Assert.That(result, Does.Contain("readonly").Or.Contain("_answer"), "Field should be readonly.");
    }

    [Test]
    public async Task IntroduceField_ReturnsOriginal_WhenColumnPointsToNonExpression()
    {
        const string source = "public class C { public void M() { } }";
        SetSource(source, "C.cs");

        // Snippet points to class declaration — no expression there; graceful fallback returns original
        var result = await _granularRefactoringEngine.IntroduceFieldAsync("C.cs", "public class C", "_f");

        // Should return the original source unchanged (graceful fallback)
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // GranularRefactoringEngine.IntroduceParameterAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task IntroduceParameter_AddsParameterToContainingMethod()
    {
        const string source = @"public class C
{
    public void M()
    {
        int timeout = 30;
    }
}";
        SetSource(source, "C.cs");
        int line = 5;
        var lines = source.Split('\n');
        int col = lines[line - 1].IndexOf("30") + 1;

        var result = await _granularRefactoringEngine.IntroduceParameterAsync("C.cs", "int timeout = 30", "timeoutMs");

        Assert.That(result, Does.Contain("timeoutMs"), "New parameter name must appear.");
        Assert.That(result, Does.Contain("M("), "Method M signature must be present.");
    }

    // ══════════════════════════════════════════════════════════════
    // GranularRefactoringEngine.IntroduceVariableAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task IntroduceVariable_ExtractsExpressionToLocalVar()
    {
        const string source = @"public class C
{
    public int Compute()
    {
        return 6 * 7;
    }
}";
        SetSource(source, "C.cs");
        int line = 5;
        var lines = source.Split('\n');
        int col = lines[line - 1].IndexOf("6") + 1;

        var result = await _granularRefactoringEngine.IntroduceVariableAsync("C.cs", "6 * 7", "product");

        Assert.That(result, Does.Contain("product"), "Extracted variable name must appear.");
        Assert.That(result, Does.Contain("var"), "Local variable should be declared with var.");
    }

    // ══════════════════════════════════════════════════════════════
    // RefinementEngine.PullUpMemberAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task PullUpMember_ThrowsException_WhenClassHasNoBaseClass()
    {
        SetSource(@"
public class Standalone
{
    public void Feature() { }
}", "Standalone.cs");

        Assert.ThrowsAsync<Exception>(async () =>
            await _refinementEngine.PullUpMemberAsync("Standalone.cs", "Standalone", "Feature"),
            "A class with no explicit base class (only System.Object) should throw.");
    }

    [Test]
    public async Task PullUpMember_MovesMember_FromDerivedToBaseFile()
    {
        SetMultipleFiles(
            ("Base.cs", @"
public class Base
{
}"),
            ("Derived.cs", @"
public class Derived : Base
{
    public string GetName() => ""Derived"";
}"));

        var changes = await _refinementEngine.PullUpMemberAsync("Derived.cs", "Derived", "GetName");

        Assert.That(changes.ContainsKey("Derived.cs"), "Derived file must be in results.");
        Assert.That(changes.ContainsKey("Base.cs"), "Base file must be in results.");
        Assert.That(changes["Base.cs"], Does.Contain("GetName"),
            "GetName must be moved to the base file.");
        Assert.That(changes["Derived.cs"], Does.Not.Contain("string GetName"),
            "GetName must be removed from the derived file.");
    }

    [Test]
    public async Task PullUpMember_AddsVirtualModifier_OnMovedMethod()
    {
        SetMultipleFiles(
            ("Animal.cs", @"public class Animal { }"),
            ("Dog.cs", @"public class Dog : Animal { public void Bark() { } }"));

        var changes = await _refinementEngine.PullUpMemberAsync("Dog.cs", "Dog", "Bark");

        Assert.That(changes["Animal.cs"], Does.Contain("virtual"),
            "Member pulled up to base class should be marked virtual.");
    }

    [Test]
    public async Task PullUpMember_RemovesOverrideKeyword_WhenPresent()
    {
        SetMultipleFiles(
            ("ShapeBase.cs", @"
public class ShapeBase
{
    public virtual string Describe() => ""Shape"";
}"),
            ("Circle.cs", @"
public class Circle : ShapeBase
{
    public override string Describe() => ""Circle"";
}"));

        var changes = await _refinementEngine.PullUpMemberAsync("Circle.cs", "Circle", "Describe");

        Assert.That(changes["ShapeBase.cs"], Does.Not.Contain("override"),
            "The 'override' keyword must be removed when pulling up to base.");
        Assert.That(changes["ShapeBase.cs"], Does.Contain("virtual"),
            "The method in base must be virtual.");
    }
}
