namespace RoslynSentinel.Git;

/// <summary>
/// Result of operation=config. Get sets Value (null when the key is unset) and Note; set and unset
/// (added in a later slice) also set PreviousValue and Changed.
/// </summary>
public record GitConfigResult : GitResult
{
    /// <summary>The action taken (get for now).</summary>
    public string Action { get; set; } = "";

    /// <summary>The config key as the caller gave it.</summary>
    public string Key { get; set; } = "";

    /// <summary>The value read for the key, exactly as stored; null when the key is unset.</summary>
    public string? Value
    {
        get; set;
    }

    /// <summary>The value before a set or unset; null for get.</summary>
    public string? PreviousValue
    {
        get; set;
    }

    /// <summary>True when a set or unset changed the stored value; false for get.</summary>
    public bool Changed
    {
        get; set;
    }

    /// <summary>A fact about the result, such as that the key is not set in any visible scope; null when the key has a value.</summary>
    public string? Note
    {
        get; set;
    }
}
