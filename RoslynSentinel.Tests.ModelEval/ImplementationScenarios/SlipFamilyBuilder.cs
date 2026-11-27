using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>Builds slip-first implementation scenarios from a SlipSiteSet.</summary>
internal static class SlipFamilyBuilder
{
    public const int TurnCap = 40;

    public static ImplementationScenario Build(string id, string title, string source, SlipSiteSet set)
    {
        var step = new ImplementationScenarioStep(
            Prompt: RenderPrompt(set),
            GoldenFiles: new Dictionary<string, string> { [set.FilePath] = RenderFile(set, true) },
            AllowedPaths: [set.FilePath],
            HiddenTestSource: null,
            ExtraCheck: null,
            CannedFinalMessage: $"DONE: made every listed edit in {set.ClassName}.cs.",
            Sites: set);
        return new ImplementationScenario(
            Id: id, Title: title, Axis: $"Transcription: per-site graded slip family ({title})", Source: source,
            SeedFiles: new Dictionary<string, string> { [set.FilePath] = RenderFile(set, false) },
            Steps: [step], TurnCap: TurnCap, PreferredHost: "113", PrimaryAxis: EvalAxis.Transcription);
    }

    public static IReadOnlyList<SlipSite> Expand(SlipSiteSet set)
    {
        if (set.Sites.Any(site => site.Role == SiteRole.Separator))
        {
            throw new InvalidOperationException($"Set '{set.ClassName}' lists a Separator in Sites; Expand generates the separators.");
        }

        var expanded = new List<SlipSite>(set.Sites.Count * 2);
        for (var i = 0; i < set.Sites.Count; i++)
        {
            if (i > 0)
            {
                // Separator n sits between observed sites n-1 and n (0-based), so N sites give N-1 separators.
                var n = i;
                expanded.Add(new SlipSite($"{SlipFamilyLayout.SeparatorPrefix}{n:00}", $"public static int {SlipFamilyLayout.SeparatorPrefix}{n:00}() => {n};", [], SiteRole.Separator));
            }

            expanded.Add(set.Sites[i]);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in expanded)
        {
            if (!seen.Add(site.Name))
            {
                throw new InvalidOperationException($"Set '{set.ClassName}' has more than one site named '{site.Name}'.");
            }
        }

        return expanded;
    }

    public static string ApplyChanges(SlipSite site)
    {
        var ranges = new List<(int Start, int Length, string Old, string New)>();
        foreach (var change in site.Changes)
        {
            var hits = FindOccurrences(site.SeedText, change.Old);
            if (hits.Count != 1)
            {
                throw new InvalidOperationException($"Site '{site.Name}': fragment '{change.Old}' occurs {hits.Count} times in its seed text; expected exactly one.");
            }

            ranges.Add((hits[0], change.Old.Length, change.Old, change.New));
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i - 1].Start + ranges[i - 1].Length > ranges[i].Start)
            {
                throw new InvalidOperationException($"Site '{site.Name}': changes for '{ranges[i - 1].Old}' and '{ranges[i].Old}' overlap.");
            }
        }

        // Replace from the highest index down so the earlier offsets stay valid.
        var text = site.SeedText;
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var range = ranges[i];
            text = text.Substring(0, range.Start) + range.New + text.Substring(range.Start + range.Length);
        }

        return text;
    }

    public static string RenderFile(SlipSiteSet set, bool golden)
    {
        var text = new StringBuilder();
        text.Append("namespace ").Append(set.Namespace).Append(";\n\n");
        var preamble = (set.Preamble ?? string.Empty).Trim();
        if (preamble.Length > 0)
        {
            text.Append(preamble).Append("\n\n");
        }

        text.Append("public static class ").Append(set.ClassName).Append("\n{\n");
        var sites = Expand(set);
        for (var i = 0; i < sites.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\n');
            }

            // Trailing line breaks would add extra blank lines; the blank line between members comes from the loop.
            var member = (golden ? ApplyChanges(sites[i]) : sites[i].SeedText).TrimEnd('\r', '\n');
            foreach (var line in member.Replace("\r\n", "\n").Split('\n'))
            {
                text.Append(line.Length > 0 ? "    " + line : string.Empty).Append('\n');
            }
        }

        text.Append("}\n");
        return ImplementationScenario.NormalizeToCrlf(text.ToString());
    }

    public static string RenderChangeLine(SlipSite site)
    {
        var changes = site.Changes.Select(change => $"change `{change.Old}` to `{change.New}`");
        return $"- {site.Name}: {string.Join("; ", changes)}.";
    }

    public static string RenderPrompt(SlipSiteSet set)
    {
        var directory = (Path.GetDirectoryName(set.FilePath) ?? string.Empty).Replace('\\', '/');
        var changedLines = set.Sites.Where(site => site.Role == SiteRole.Changed).Select(RenderChangeLine);
        var task = new ScenarioTask(
            Goal: $"Make the listed text edits in {set.ClassName}.",
            Files: $"{set.ClassName}.cs in {directory}. No other file.",
            IfFails: EvalContractOptions.PlainIfFails,
            DoneWhen: "every listed edit is made and the solution builds.",
            Conditions: "Make exactly these edits and change nothing else:\n" + string.Join("\n", changedLines));
        return EvalContractOptions.Render(task);
    }

    public static IReadOnlyList<string> Validate(SlipSiteSet set)
    {
        var violations = new List<string>();
        if (set is null)
        {
            violations.Add("set is null.");
            return violations;
        }

        // Guards only: Validate never calls Expand or ApplyChanges, so it cannot throw on a bad set.
        IReadOnlyList<SlipSite> sites = set.Sites ?? Array.Empty<SlipSite>();
        if (sites.Count != SlipFamilyLayout.ObservedSiteCount)
        {
            violations.Add($"observed site count is {sites.Count}; expected {SlipFamilyLayout.ObservedSiteCount}.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var position = i + 1;
            if (site is null)
            {
                violations.Add($"site at position {position} is null.");
                continue;
            }

            var name = site.Name ?? string.Empty;
            if (site.Role == SiteRole.Separator)
            {
                violations.Add($"site '{name}' is a Separator; Expand generates separators, so Sites must not list one.");
            }
            else
            {
                var expected = SlipFamilyLayout.ControlPositions.Contains(position) ? SiteRole.Control : SiteRole.Changed;
                if (site.Role != expected)
                {
                    violations.Add($"site '{name}' at position {position} has role {site.Role}; expected {expected}.");
                }
            }

            if (name.StartsWith(SlipFamilyLayout.SeparatorPrefix, StringComparison.Ordinal))
            {
                violations.Add($"site '{name}' starts with the separator prefix '{SlipFamilyLayout.SeparatorPrefix}'.");
            }

            if (!names.Add(name))
            {
                violations.Add($"site name '{name}' is not unique.");
            }

            ValidateSite(site, name, violations);
        }

        return violations;
    }

    private static List<int> FindOccurrences(string text, string fragment)
    {
        var hits = new List<int>();
        if (fragment.Length == 0)
        {
            return hits;
        }

        // Non-overlapping ordinal scan: resume after each hit.
        var index = text.IndexOf(fragment, 0, StringComparison.Ordinal);
        while (index >= 0)
        {
            hits.Add(index);
            index = text.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal);
        }

        return hits;
    }

    private static void ValidateSite(SlipSite site, string name, List<string> violations)
    {
        var seed = site.SeedText ?? string.Empty;
        var changes = site.Changes ?? Array.Empty<SiteChange>();
        var expectedChanges = site.Role == SiteRole.Changed ? SlipFamilyLayout.ChangesPerSite : 0;
        if (site.Role != SiteRole.Separator && changes.Count != expectedChanges)
        {
            violations.Add($"site '{name}': has {changes.Count} changes; expected {expectedChanges}.");
        }

        var ranges = new List<(int Start, int Length, string Old)>();
        var allSingle = true;
        foreach (var change in changes)
        {
            var old = change.Old ?? string.Empty;
            var replacement = change.New ?? string.Empty;
            if (!Regex.IsMatch(old, SlipFamilyLayout.PlainFragmentPattern))
            {
                violations.Add($"site '{name}': Old '{old}' does not match PlainFragmentPattern.");
            }

            if (!Regex.IsMatch(replacement, SlipFamilyLayout.PlainFragmentPattern))
            {
                violations.Add($"site '{name}': New '{replacement}' does not match PlainFragmentPattern.");
            }

            var hits = FindOccurrences(seed, old);
            if (hits.Count != 1)
            {
                allSingle = false;
                violations.Add($"site '{name}': Old '{old}' occurs {hits.Count} times in its seed text; expected exactly one.");
                continue;
            }

            ranges.Add((hits[0], old.Length, old));
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i - 1].Start + ranges[i - 1].Length > ranges[i].Start)
            {
                violations.Add($"site '{name}': changes for '{ranges[i - 1].Old}' and '{ranges[i].Old}' overlap.");
            }
        }

        // Spread is measured between the first and last Old only; skipped when a count error already makes positions unreliable.
        if (allSingle && site.Role == SiteRole.Changed && ranges.Count >= 2)
        {
            var spread = ranges[ranges.Count - 1].Start - ranges[0].Start;
            if (spread < SlipFamilyLayout.MinChangeSpread)
            {
                violations.Add($"site '{name}': first and last Old start {spread} characters apart; the minimum is {SlipFamilyLayout.MinChangeSpread}.");
            }
        }

        var docLines = 0;
        foreach (var line in seed.Split('\n'))
        {
            if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                docLines++;
            }
        }

        if (docLines > SlipFamilyLayout.MaxDocCommentLines)
        {
            violations.Add($"site '{name}': has {docLines} doc-comment lines; the maximum is {SlipFamilyLayout.MaxDocCommentLines}.");
        }
    }
}
