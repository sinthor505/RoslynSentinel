using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class FakeWorkspaceManagerAccessTests
{
    private const string AuditEnvVar = "ROSLYNSENTINEL_FAKE_WRITE_AUDIT";
    private const string OriginalText = "public class Target { public int Value; }\n";
    private const string NewText = "public class Target { public int Value = 1; }\n";

    private static readonly string TargetPath = Path.Combine(Path.GetTempPath(), "FakeAccessProject", "Target.cs");

    private static FakeWorkspaceManager CreateFake(WorkspaceAccess access)
    {
        var fake = new FakeWorkspaceManager(access);
        fake.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "FakeAccessProject",
            Path.Combine(Path.GetTempPath(), "FakeAccessProject", "FakeAccessProject.csproj"),
            [("Target.cs", OriginalText, TargetPath)]));
        return fake;
    }

    private static async Task<string> ReadTargetTextAsync(FakeWorkspaceManager fake)
    {
        var solution = fake.CurrentSolution!;
        var documentId = solution.GetDocumentIdsWithFilePath(TargetPath).Single();
        var text = await solution.GetDocument(documentId)!.GetTextAsync();
        return text.ToString();
    }

    private static Dictionary<FilePathWrapper, string> ChangeTarget() =>
        new() { [TargetPath] = NewText };

    [Test]
    public async Task ReadOnly_ApplyProposedChanges_ThrowsAndLeavesSolutionUnchanged()
    {
        var fake = CreateFake(WorkspaceAccess.ReadOnly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fake.ApplyProposedChangesAsync(ChangeTarget()));
        var text = await ReadTargetTextAsync(fake);

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("ReadOnly").And.Contain("ReadWrite"));
            Assert.That(fake.RejectedWriteAttempts, Is.EqualTo(1));
            Assert.That(text, Is.EqualTo(OriginalText));
        });
    }

    [Test]
    public async Task ReadOnly_ApplyProposedChanges_EachRefusedCallIsCounted()
    {
        var fake = CreateFake(WorkspaceAccess.ReadOnly);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fake.ApplyProposedChangesAsync(ChangeTarget()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fake.ApplyProposedChangesAsync(ChangeTarget()));

        Assert.That(fake.RejectedWriteAttempts, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadWrite_ApplyProposedChanges_AppliesEditAndCountsNoRejection()
    {
        var fake = CreateFake(WorkspaceAccess.ReadWrite);

        var result = await fake.ApplyProposedChangesAsync(ChangeTarget());
        var text = await ReadTargetTextAsync(fake);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(text, Is.EqualTo(NewText));
            Assert.That(fake.RejectedWriteAttempts, Is.Zero);
        });
    }

    [Test]
    public async Task ReadOnly_WithAuditEnvironmentVariable_AppendsTestNameToAuditFile()
    {
        // The audit env var is process-wide, so it is restored in finally. Other parallel tests that refuse
        // writes may append to the same file, hence Contains rather than equality.
        var auditPath = Path.Combine(Path.GetTempPath(), $"FakeWriteAudit_{Guid.NewGuid():n}.txt");
        var previous = Environment.GetEnvironmentVariable(AuditEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AuditEnvVar, auditPath);
            var fake = CreateFake(WorkspaceAccess.ReadOnly);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fake.ApplyProposedChangesAsync(ChangeTarget()));

            var audit = File.ReadAllText(auditPath);
            Assert.That(audit, Does.Contain(TestContext.CurrentContext.Test.FullName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditEnvVar, previous);
            if (File.Exists(auditPath))
            {
                File.Delete(auditPath);
            }
        }
    }
}
