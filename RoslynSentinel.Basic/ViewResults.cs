namespace RoslynSentinel.Basic;

/// <summary>
/// Result shape for Member(operation: view) - lists a container's direct members. Mirrors the
/// element shape GetContainerMembersAsync returns (RefactoringEngine.ContainerMemberInfo,
/// RefactoringEngine.cs:5524), unchanged rather than reprojected, so the tool's wire shape
/// matches the engine's own record 1:1.
/// </summary>
public record MemberViewResult(IReadOnlyList<RefactoringEngine.ContainerMemberInfo> Members);

/// <summary>
/// Result shape for UsingDirective(operation: view) - lists a file's using directives. Element
/// type is UsingDirectiveInfo (Name/IsStatic/Alias, RefactoringEngine.cs:18), the actual return
/// element of GetUsingDirectivesAsync - not a bare string as originally planned; confirmed by a
/// CS1503 the write-chokepoint's compile check caught before this landed on disk.
/// </summary>
public record UsingDirectiveViewResult(IReadOnlyList<UsingDirectiveInfo> Usings);

/// <summary>
/// Result shape for SummaryComment(operation: view) - the XML summary comment text for a target,
/// or null if it has none.
/// </summary>
public record SummaryCommentViewResult(string? SummaryText);

/// <summary>
/// Result shape for MethodSignature(operation: view) - lists a method's parameters. NOT shared
/// with ConstructorParameter(operation: view): GetMethodParametersAsync and
/// GetConstructorParametersAsync return genuinely different element types
/// (MethodParameterInfo's third field is DefaultValue; ConstructorParameterInfo's is FieldName -
/// RefactoringEngine.cs:4786,4847), so a single ParameterViewResult would either drop one of
/// those fields or force an artificial common shape neither engine method actually returns.
/// See docs/current/blockers/blocking_error_membershaped_success_types_not_unified.md's
/// resolution notes for why this diverges from the originally planned single shared record.
/// </summary>
public record MethodSignatureViewResult(IReadOnlyList<RefactoringEngine.MethodParameterInfo> Parameters);

/// <summary>
/// Result shape for ConstructorParameter(operation: view) - lists a class's constructor
/// parameters alongside their inferred backing field. See MethodSignatureViewResult's remarks
/// for why this is a distinct type rather than a shared ParameterViewResult.
/// </summary>
public record ConstructorParameterViewResult(IReadOnlyList<RefactoringEngine.ConstructorParameterInfo> Parameters);
