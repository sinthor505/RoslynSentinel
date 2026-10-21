namespace RoslynSentinel.Tests.Fakes;

/// <summary>
/// Whether a FakeWorkspaceManager accepts edits. Choose explicitly: ReadOnly for a test that never applies edits,
/// ReadWrite for a test that does.
/// </summary>
public enum WorkspaceAccess
{
    /// <summary>ApplyProposedChangesAsync is refused (it throws and counts RejectedWriteAttempts).</summary>
    ReadOnly,

    /// <summary>Edits are applied to the in-memory solution.</summary>
    ReadWrite
}
