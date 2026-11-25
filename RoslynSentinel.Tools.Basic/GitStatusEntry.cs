namespace RoslynSentinel.Tools.Basic;

public record GitStatusEntry
{
    public string Status { get; set; } = "";
    public string Path { get; set; } = "";

    // Set only for renamed/copied entries: the path the file was renamed/copied FROM (Path is the new path).
    public string? OriginalPath
    {
        get; set;
    }
}
