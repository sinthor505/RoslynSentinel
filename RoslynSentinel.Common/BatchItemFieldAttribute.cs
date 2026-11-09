using System;

namespace RoslynSentinel.Common;

/// <summary>
/// Describes a batch-edit item property in the emitted tool schema; Required adds it to the item's required array.
/// Schema only - runtime checks stay in the batch methods.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BatchItemFieldAttribute(string description, bool required = false) : Attribute
{
    /// <summary>Description emitted for the property in the item schema.</summary>
    public string Description { get; } = description;

    /// <summary>When true, the property is listed in the item schema's required array.</summary>
    public bool Required { get; set; } = required;
}
