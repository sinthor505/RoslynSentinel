using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;

/// <summary>
/// The precondition an asyncify bridge checks before rewriting. Carried on
/// <see cref="AsyncBridgePreconditionException"/> so catch sites match the cause, not the message text.
/// </summary>
public enum AsyncBridgePreconditionKind
{
    OverloadExists,
    EventHandler,
    AlreadyAsync,
    NoBridgeCalls,
    Other
}

/// <summary>
/// An asyncify bridge precondition was not met. The target exists, but this bridge cannot be built for it.
/// Maps to <see cref="ToolErrorCode.TargetIneligible"/>.
/// </summary>
public sealed class AsyncBridgePreconditionException : ToolException
{
    public AsyncBridgePreconditionException(AsyncBridgePreconditionKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    /// <summary>Which precondition failed.</summary>
    public AsyncBridgePreconditionKind Kind { get; }

    public override string ErrorCode => ToolErrorCode.TargetIneligible;
}
