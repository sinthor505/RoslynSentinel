using System.Collections.Generic;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One file's namespace rename, old to new.</summary>
public sealed record NamespaceMove(FilePathWrapper FilePath, string OldNamespace, string NewNamespace);

/// <summary>A batch of namespace moves to plan together.</summary>
public sealed record ChangeNamespaceRequest(IReadOnlyList<NamespaceMove> Moves);

/// <summary>Two text edits in one file whose spans overlap.</summary>
public sealed record EditOverlap(string FilePath, int FirstStart, int FirstLength, int SecondStart, int SecondLength);

/// <summary>How a dangling using directive was handled.</summary>
public enum DanglingUsingResolution { Removed, RefusedGlobal }

/// <summary>A using directive left dangling by a namespace move.</summary>
public sealed record DanglingUsing(string FilePath, string Namespace, DanglingUsingResolution Resolution);
