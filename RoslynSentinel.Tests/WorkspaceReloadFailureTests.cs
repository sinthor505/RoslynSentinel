using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class WorkspaceReloadFailureTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _manager.Dispose();
    }

    [TestCase(@"C:\Users\x\proj\Foo.csproj", "Foo.csproj")]
    [TestCase("/home/u/src/Foo.sln", "Foo.sln")]
    [TestCase("first line\nsecond line\r\nthird", "first line")]
    [TestCase("\n\r\n  second  ", "second")]
    [TestCase("alpha   beta\t\tgamma", "alpha beta gamma")]
    [TestCase(null, "unknown")]
    [TestCase("", "unknown")]
    [TestCase("   \t  ", "unknown")]
    public void Sanitize_String_ReturnsExpectedCause(string? input, string expected)
    {
        Assert.That(WorkspaceReloadCause.Sanitize(input), Is.EqualTo(expected));
    }

    [Test]
    public void Sanitize_String_TruncatesTo200CharactersWithEllipsis()
    {
        var result = WorkspaceReloadCause.Sanitize(new string('x', 300));

        Assert.That(result, Is.EqualTo(new string('x', 200) + "..."));
    }

    [Test]
    public void Sanitize_Exception_PrefixesTypeNameAndStripsUnixPath()
    {
        var ex = new ArgumentException("bad input /home/u/src/Foo.sln");

        Assert.That(WorkspaceReloadCause.Sanitize(ex), Is.EqualTo("ArgumentException: bad input Foo.sln"));
    }

    [Test]
    public void Sanitize_Exception_StripsWindowsPathDirectories()
    {
        var ex = new InvalidOperationException(@"failed reading C:\Users\x\proj\Foo.csproj");

        Assert.That(WorkspaceReloadCause.Sanitize(ex), Is.EqualTo("InvalidOperationException: failed reading Foo.csproj"));
    }

    [Test]
    public void Sanitize_Exception_EmptyMessage_ReturnsTypeNameOnly()
    {
        var ex = new InvalidOperationException(string.Empty);

        Assert.That(WorkspaceReloadCause.Sanitize(ex), Is.EqualTo("InvalidOperationException"));
    }

    [Test]
    public void ReloadFailure_IsNullForNewManager()
    {
        Assert.That(_manager.ReloadFailure, Is.Null);
    }

    [Test]
    public void RecordReloadFailure_StoresSanitizedRecord()
    {
        var before = DateTime.UtcNow;
        _manager.RecordReloadFailure(
            WorkspaceReloadTriggerKind.ProjectFileChange,
            @"C:\repo\src\Foo.csproj",
            new InvalidOperationException(@"failed at C:\repo\src\Bar.props"),
            true);
        var after = DateTime.UtcNow;

        var record = _manager.ReloadFailure;
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ProjectFileChange));
        Assert.That(record.TriggerFile, Is.EqualTo("Foo.csproj"));
        Assert.That(record.Attempts, Is.EqualTo(1));
        Assert.That(record.OldWorkspaceKept, Is.True);
        Assert.That(record.Cause, Is.EqualTo("InvalidOperationException: failed at Bar.props"));
        Assert.That(record.FailedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(record.FailedAtUtc, Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(after));
    }

    [Test]
    public void RecordReloadFailure_SecondCall_IncrementsAttempts()
    {
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ExplicitLoad, null, new InvalidOperationException("first"), false);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.WatcherError, null, new InvalidOperationException("second"), false);

        Assert.That(_manager.ReloadFailure?.Attempts, Is.EqualTo(2));
        Assert.That(_manager.ReloadFailure?.Cause, Is.EqualTo("InvalidOperationException: second"));
    }

    [Test]
    public void ClearReloadFailure_ResetsToNull()
    {
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ExplicitLoad, null, new InvalidOperationException("x"), true);

        _manager.ClearReloadFailure();

        Assert.That(_manager.ReloadFailure, Is.Null);
    }

    [Test]
    public void RecordReloadFailure_AfterClear_StartsAttemptsAtOne()
    {
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ExplicitLoad, null, new InvalidOperationException("x"), true);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ExplicitLoad, null, new InvalidOperationException("y"), true);
        _manager.ClearReloadFailure();

        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.StartupLoad, null, new InvalidOperationException("z"), false);

        Assert.That(_manager.ReloadFailure?.Attempts, Is.EqualTo(1));
        Assert.That(_manager.ReloadFailure?.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.StartupLoad));
    }
}
