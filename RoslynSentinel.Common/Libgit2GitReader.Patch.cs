using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// Patch text for the Git tool's <c>diff</c> (staged, refA..refB) and <c>show</c>, served from <c>Diff.Compare&lt;Patch&gt;</c> only where the text is the one
/// <c>git -c core.quotePath=false diff</c> prints, byte for byte (GitPatchParityCorpusTests and GitPatchParityCorpusMoreTests hold the evidence: plain modify, hunks,
/// binary, mode change, CRLF, no newline at end of file, non-UTF-8 content, ambiguous hunk placement and hunk-header function context are identical;
/// rename, empty-file add/delete, non-ASCII path, typechange, diff.context, diff.interHunkContext, color.diff and diff.external differ). Everything else throws
/// <see cref="NotSupportedException"/> (so <see cref="FallbackGitReader"/> counts a decline and the CLI answers). Declined, with a fact-only reason:
/// <list type="bullet">
/// <item>a comparison against the working tree (never reaches this reader's routing: only "staged" and refA..refB are accepted);</item>
/// <item>GIT_DIFF_OPTS or GIT_EXTERNAL_DIFF in the process environment;</item>
/// <item>any <c>diff.*</c> or <c>color.*</c> config key (every level) except diff.algorithm, diff.indentHeuristic, diff.noprefix, diff.renames,
///       diff.wsErrorHighlight (core.whitespace is not scanned: it is on the verified list) and the two keys named in <see cref="PatchNeutralConfigKeys"/>;
///       diff.renames other than a plain boolean (copies), diff.algorithm other than myers/default/patience/minimal, a non-boolean diff.indentHeuristic;</item>
/// <item>a rename or copy; an added path together with a deleted one while renames are on (git may pair them); a typechange; a symbolic link; a submodule entry;</item>
/// <item>a file that is empty on either side (empty-file add/delete, and an intent-to-add entry);</item>
/// <item>a path (entry or pathspec) with a space, a non-ASCII character or one git quotes; a pathspec that needs git's pathspec rules;</item>
/// <item>a path an attributes file gives a <c>diff</c> attribute (<c>diff</c>, <c>-diff</c>, <c>diff=driver</c>) or the <c>binary</c> macro (diff drivers, textconv, binary handling);</item>
/// <item>unmerged index entries, an unborn HEAD, a working directory below the repository root.</item>
/// </list>
/// What is set to match git: context 3, no inter-hunk lines, the indent heuristic (git has it on unless diff.indentHeuristic is false), diff.algorithm, no rename detection,
/// path order (byte-wise, as git's tree walk), and the <c>index</c> header line re-abbreviated to the length <c>git rev-parse --short</c> prints (libgit2's own is 7).
/// </summary>
internal sealed partial class Libgit2GitReader
{
    private static readonly string[] PatchEnvironmentVariables = ["GIT_DIFF_OPTS", "GIT_EXTERNAL_DIFF"];

    // The diff.* keys that do not change the text of "git diff" here. The first five are the owner's verified-identical list (Step 12 gate) minus diff.mnemonicPrefix: git applies
    // mnemonic prefixes (c/, i/, w/) only when the index or working tree is compared and keeps a/ and b/ for commit against commit, while libgit2 applies them to a range too
    // (diff --git c/x c/x), so a set diff.mnemonicPrefix is declined for every patch target. diff.tool and diff.guitool are the
    // two keys git documents as read by git difftool only, which Git for Windows' installer and many global configs set (a diff.<driver>.<var> key, such as the shipped
    // diff.astextplain.textconv, is neutral through IsPatchNeutralConfigKey: it only acts on a path that selects the driver, and such a path is declined by the attribute rule).
    // TODO owner to confirm diff.tool, diff.guitool and the diff.<driver>.<var> exemption (a wider list than the Step 12 decision); without them every Git for Windows machine declines
    private static readonly HashSet<string> PatchNeutralConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "diff.algorithm", "diff.indentheuristic", "diff.noprefix", "diff.renames", "diff.wserrorhighlight",
        "diff.tool", "diff.guitool",
    };

    private static readonly Regex IndexHeaderLine = new(@"^index ([0-9a-f]+)\.\.([0-9a-f]+)( [0-7]{6})?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string? GetPatchText(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        // Same scope as GetNameStatus: tree to index ("staged") and tree to tree (refA..refB); a comparison with working-tree files stays with git.
        var isRange = target.Contains("..", StringComparison.Ordinal);
        if (target != "staged" && !isRange)
        {
            throw Decline($"'{target}' compares against the working tree, whose EOL-filtered content libgit2's diff does not reproduce, so git answers.");
        }

        var specs = ParsePatchPathspecs(paths);
        RequirePatchEnvironment();
        using var repo = isRange ? Open(workingDirectory) : OpenForIndexRead(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        var settings = RequirePatchRepository(repo, workingDirectory);
        var options = BuildPatchOptions(settings);
        var pathLimit = PatchPathLimit(specs);
        Patch patch;
        if (!isRange)
        {
            RequirePlainHistory(repo);
            if (repo.Index.Conflicts.Any())
            {
                throw Decline("The index has unmerged entries, which git prints as 'Unmerged path' rows.");
            }

            var head = repo.Head.Tip ?? throw Decline("HEAD is unborn, so git diffs the index against the empty tree.");
            patch = repo.Diff.Compare<Patch>(head.Tree, DiffTargets.Index, pathLimit, null, options);
        }
        else
        {
            var sides = ResolveRangeSides(repo, workingDirectory, target, paths);
            if (sides is null)
            {
                return null;
            }

            patch = repo.Diff.Compare<Patch>(sides.Value.From.Tree, sides.Value.To.Tree, pathLimit, options);
        }

        using (patch)
        {
            return AssemblePatchText(repo, workingDirectory, patch, specs, settings, cliTimeout);
        }
    }

    public string? GetCommitPatchText(string workingDirectory, string revision, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        var specs = ParsePatchPathspecs(paths);
        RequirePatchEnvironment();
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        var settings = RequirePatchRepository(repo, workingDirectory);
        // The tool's header call has no "--", so the name is checked for a clash with a file whatever the paths are (as GetCommitNameStatus does).
        var commit = ResolveSimpleCommit(repo, workingDirectory, revision, pathsGiven: false);
        if (commit is null)
        {
            return null;
        }

        // git diff <rev>^ <rev>: a merge commit against its first parent, a root commit against the empty tree.
        using var patch = repo.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, PatchPathLimit(specs), BuildPatchOptions(settings));
        return AssemblePatchText(repo, workingDirectory, patch, specs, settings, cliTimeout);
    }

    /// <summary>The commits of a plain refA..refB (null when a side does not resolve to a commit, so the CLI reports git's own error); the same rules as <see cref="GetNameStatus"/>.</summary>
    // TODO GetNameStatus carries a copy of this range resolution; have it call this helper
    private static (Commit From, Commit To)? ResolveRangeSides(Repository repo, string workingDirectory, string target, IReadOnlyList<string>? paths)
    {
        // refA..refB with a plain revision each side: tree to tree. refA...refB (merge base) and an empty side (meaning HEAD) are left to git.
        var sides = target.Split("..");
        if (sides.Length != 2 || target.Contains("...", StringComparison.Ordinal) || sides.Any(side => side.Length == 0))
        {
            throw Decline($"'{target}' is not a plain refA..refB range, so git resolves it.");
        }

        // A "--" in the command (paths given, even none) makes a name that is both a revision and a file unambiguous.
        var pathsGiven = paths is not null;
        if (!pathsGiven && PathExists(workingDirectory, target))
        {
            throw Decline($"'{target}' is also a path in the working directory; git decides whether that is ambiguous.");
        }

        var from = ResolveSimpleCommit(repo, workingDirectory, sides[0], pathsGiven);
        var to = ResolveSimpleCommit(repo, workingDirectory, sides[1], pathsGiven);
        return from is null || to is null ? null : (from, to);
    }

    /// <summary>The pathspecs as <see cref="ParseLogPathspecs"/> reads them (null when there are none), declining a name git would quote, decode differently or print with a trailing tab.</summary>
    private static List<(string Path, bool DirectoryOnly)>? ParsePatchPathspecs(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 })
        {
            return null;
        }

        var specs = ParseLogPathspecs(paths);
        foreach (var raw in paths)
        {
            if (!IsPlainPatchPath(raw))
            {
                throw Decline($"Pathspec '{raw}' holds a space, a non-ASCII character or one git quotes; those header forms are not reproduced, so git answers.");
            }
        }

        return specs;
    }

    private static List<string>? PatchPathLimit(List<(string Path, bool DirectoryOnly)>? specs) =>
        specs is null ? null : [.. specs.Select(spec => spec.DirectoryOnly ? spec.Path + "/" : spec.Path)];

    /// <summary>ASCII graphic characters only, and no quote or backslash: the names whose "diff --git" and ---/+++ lines git and libgit2 print the same way.</summary>
    private static bool IsPlainPatchPath(string path) => path.All(ch => ch is > ' ' and < '\u007f' and not '"' and not '\\');

    /// <summary>GIT_DIFF_OPTS changes the context git prints and GIT_EXTERNAL_DIFF replaces the whole diff; libgit2 reads neither.</summary>
    private static void RequirePatchEnvironment()
    {
        foreach (var name in PatchEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
            {
                throw Decline($"{name} is set in the process environment; git honours it and libgit2 does not.");
            }
        }
    }

    /// <summary>
    /// The repository-wide checks of a patch read: git runs at the repository root, and every <c>diff.*</c> and <c>color.*</c> config key at any level is on the neutral list
    /// (see <see cref="PatchNeutralConfigKeys"/>). Not <see cref="RequireNameStatusRepository"/>: that one declines any diff.renames, which a patch may carry as false.
    /// Returns what the comparison needs to match git: the diff algorithm, the indent heuristic, and whether git would detect renames.
    /// </summary>
    private static (DiffAlgorithm Algorithm, bool IndentHeuristic, bool RenamesPossible) RequirePatchRepository(Repository repo, string workingDirectory)
    {
        if (RelativeToWorkdir(repo, workingDirectory).Length != 0)
        {
            throw Decline("The working directory is below the repository root, so git resolves the pathspecs against it.");
        }

        // Even a tree-to-tree patch loads the index: libgit2 looks attributes up (diff drivers, binary detection) through the index, which fails on a split index with
        // "unsupported mandatory extension: 'link'" (GetCommitNameStatus never asks for attributes, so it is still served there).
        RequireNoSplitIndex(repo);

        // libgit2 reads core.abbrev itself while it builds a patch and throws on a value it cannot map ('no', 'auto', 'false'); the index line is re-abbreviated afterwards,
        // so only a plain number it accepts (7 to 40 digits) is served.
        if (ConfigValue(repo, "core.abbrev") is { } abbrev && !(int.TryParse(abbrev.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var digits) && digits is >= 7 and <= 40))
        {
            throw Decline($"core.abbrev is '{abbrev}', which libgit2 cannot map while it prints a patch, so git answers.");
        }

        foreach (var key in ConfigKeys(repo))
        {
            if (!IsPatchNeutralConfigKey(key))
            {
                throw Decline($"{key} is configured; its effect on patch text is not reproduced, so git answers.");
            }
        }

        var algorithm = ConfigValue(repo, "diff.algorithm")?.Trim().ToLowerInvariant() switch
        {
            null or "default" or "myers" => DiffAlgorithm.Myers,
            "patience" => DiffAlgorithm.Patience,
            "minimal" => DiffAlgorithm.Minimal,
            var other => throw Decline($"diff.algorithm is '{other}', which libgit2 does not offer, so git answers."),
        };
        var indentHeuristic = PatchBool("diff.indentHeuristic", ConfigValue(repo, "diff.indentHeuristic"), unsetValue: true);
        var renamesPossible = PatchBool("diff.renames", ConfigValue(repo, "diff.renames"), unsetValue: true);
        return (algorithm, indentHeuristic, renamesPossible);
    }

    /// <summary>A key is neutral outside the diff and color sections, or when it is one of <see cref="PatchNeutralConfigKeys"/>, or a <c>diff.&lt;driver&gt;.&lt;var&gt;</c> driver setting.</summary>
    private static bool IsPatchNeutralConfigKey(string key)
    {
        var dot = key.IndexOf('.');
        if (dot < 0)
        {
            return true;
        }

        var section = key[..dot];
        if (section.Equals("diff", StringComparison.OrdinalIgnoreCase))
        {
            return PatchNeutralConfigKeys.Contains(key) || key.IndexOf('.', dot + 1) > 0;
        }

        return !section.Equals("color", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PatchBool(string key, string? value, bool unsetValue)
    {
        if (value is null)
        {
            return unsetValue;
        }

        // A valueless key comes back as the empty string (see ConfigValue), which git reads as true where "key=" is false: libgit2 cannot say which.
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw Decline($"{key} is '{value}', which is not a plain true or false, so git reads it."),
        };
    }

    /// <summary>Every key of every config level, read like <see cref="ConfigValue"/> (the main repository's config, plus global and system, in a linked worktree).</summary>
    private static List<string> ConfigKeys(Repository repo)
    {
        if (!IsLinkedWorktree(repo))
        {
            return [.. repo.Config.Select(entry => entry.Key)];
        }

        using var config = Configuration.BuildFrom(Path.Combine(CommonDirectory(repo), "config"));
        return [.. config.Select(entry => entry.Key)];
    }

    private static CompareOptions BuildPatchOptions((DiffAlgorithm Algorithm, bool IndentHeuristic, bool RenamesPossible) settings) => new()
    {
        // git's rename detection is not reproduced: a result that could hold a rename is declined, and similarity detection stays off.
        Similarity = SimilarityOptions.None,
        ContextLines = 3,
        InterhunkLines = 0,
        IndentHeuristic = settings.IndentHeuristic,
        Algorithm = settings.Algorithm,
    };

    /// <summary>
    /// The text of the entries of <paramref name="patch"/> that <paramref name="specs"/> let through (git diffs only the limited paths, so the checks apply to those),
    /// in git's byte-wise path order, each entry's <c>index</c> line re-abbreviated. Empty string when nothing differs (git prints nothing and exits 0).
    /// </summary>
    private string AssemblePatchText(
        Repository repo,
        string workingDirectory,
        Patch patch,
        List<(string Path, bool DirectoryOnly)>? specs,
        (DiffAlgorithm Algorithm, bool IndentHeuristic, bool RenamesPossible) settings,
        TimeSpan cliTimeout)
    {
        var items = new List<(string Path, PatchEntryChanges Entry)>();
        foreach (var entry in patch)
        {
            // On Windows LibGit2Sharp hands paths back with backslashes; elsewhere a backslash is part of a name (and git quotes it).
            var path = OperatingSystem.IsWindows() ? entry.Path.Replace('\\', '/') : entry.Path;
            if (specs is not null && !specs.Any(spec => (!spec.DirectoryOnly && path == spec.Path) || path.StartsWith(spec.Path + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            RequirePatchEntryReproducible(entry, path);
            items.Add((path, entry));
        }

        if (settings.RenamesPossible && items.Any(item => item.Entry.Status == ChangeKind.Added) && items.Any(item => item.Entry.Status == ChangeKind.Deleted))
        {
            throw Decline("The result has added and deleted paths, which git may pair as renames.");
        }

        items.Sort((left, right) => Utf8PathOrder.Compare(left.Path, right.Path));
        RequireNoDiffAttributes(repo, [.. items.Select(item => item.Path)]);

        var text = new StringBuilder();
        var length = 0;
        foreach (var (path, entry) in items)
        {
            length = length == 0 ? ShortLength(repo, workingDirectory, cliTimeout) : length;
            text.Append(ReabbreviateIndexLine(repo, entry, path, length));
        }

        return text.ToString();
    }

    /// <summary>Declines the entry kinds whose text is not identical to git's (corpus scenarios 3, 8, 9, 10, 15).</summary>
    private static void RequirePatchEntryReproducible(PatchEntryChanges entry, string path)
    {
        if (!IsPlainPatchPath(path))
        {
            throw Decline($"'{path}' holds a space, a non-ASCII character or one git quotes; those header forms are not reproduced, so git answers.");
        }

        switch (entry.Status)
        {
            case ChangeKind.Added or ChangeKind.Deleted or ChangeKind.Modified:
                break;
            case ChangeKind.TypeChanged:
                throw Decline($"'{path}' changed type (file, symbolic link or directory); git prints that as its own header form, so git answers.");
            case ChangeKind.Renamed or ChangeKind.Copied:
                throw Decline($"'{path}' is a rename or copy, whose headers libgit2 does not reproduce, so git answers.");
            default:
                throw Decline($"A {entry.Status} entry is left to git.");
        }

        if (entry.Mode == Mode.GitLink || entry.OldMode == Mode.GitLink)
        {
            throw Decline("A submodule entry changed; git compares it by its own rules.");
        }

        if (entry.Mode == Mode.SymbolicLink || entry.OldMode == Mode.SymbolicLink)
        {
            throw Decline($"'{path}' is a symbolic link; its patch text is not covered by the parity corpus, so git answers.");
        }

        // An empty file added or deleted prints no ---/+++ lines in git; an intent-to-add path sits in the index as an empty blob that git leaves out of a staged diff.
        if (entry.OldOid.Sha == EmptyBlobSha || entry.Oid.Sha == EmptyBlobSha)
        {
            throw Decline($"'{path}' is empty on one side (an empty file added or deleted, or an intent-to-add entry); git prints that differently, so git answers.");
        }
    }

    /// <summary>
    /// libgit2 prints <c>index &lt;old&gt;..&lt;new&gt;</c> with 7 hex digits; git uses the repository's abbreviation (<see cref="ShortLength"/>, extended until unique as
    /// <see cref="ShortId"/> does) and zeros of that length for a missing side. Returns the entry's text with only that line replaced; a patch with no index line
    /// (a mode change alone) is returned as is.
    /// </summary>
    private static string ReabbreviateIndexLine(Repository repo, PatchEntryChanges entry, string path, int length)
    {
        var text = entry.Patch;
        var position = 0;
        while (position < text.Length)
        {
            var newline = text.IndexOf('\n', position);
            var end = newline < 0 ? text.Length : newline;
            var line = text.AsSpan(position, end - position);
            if (line.StartsWith("@@") || line.StartsWith("--- ") || line.StartsWith("+++ ") || line.StartsWith("Binary files ") || line.StartsWith("GIT binary patch"))
            {
                break;
            }

            if (line.StartsWith("index "))
            {
                var match = IndexHeaderLine.Match(line.ToString());
                if (!match.Success
                    || !entry.OldOid.Sha.StartsWith(match.Groups[1].Value, StringComparison.Ordinal)
                    || !entry.Oid.Sha.StartsWith(match.Groups[2].Value, StringComparison.Ordinal))
                {
                    throw Decline($"The index line of '{path}' is not in the form this reader re-abbreviates, so git answers.");
                }

                var replaced = $"index {AbbreviateObject(repo, entry.OldOid, length)}..{AbbreviateObject(repo, entry.Oid, length)}{match.Groups[3].Value}";
                return string.Concat(text.AsSpan(0, position), replaced, text.AsSpan(end));
            }

            if (newline < 0)
            {
                break;
            }

            position = newline + 1;
        }

        return text;
    }

    private static string AbbreviateObject(Repository repo, ObjectId id, int length)
    {
        // A side that does not exist (an added or deleted file) is the all-zero id, printed as zeros of the abbreviation length.
        if (id.Sha.AsSpan().IndexOfAnyExcept('0') < 0)
        {
            return new string('0', Math.Min(length, 40));
        }

        var target = repo.Lookup(id) ?? throw Decline($"Object {id.Sha} named by a patch line is not in the object store, so git answers.");
        return ShortId(repo, target, length);
    }

    /// <summary>
    /// Declines when an attributes file gives one of <paramref name="paths"/> a <c>diff</c> attribute (<c>diff</c>, <c>-diff</c>, <c>!diff</c>, <c>diff=driver</c>) or the <c>binary</c> macro, which
    /// switch diff drivers, textconv and binary handling that libgit2's patch does not reproduce. Sources, as git reads them: the .gitattributes of every directory from the root down to
    /// each file, info/attributes, core.attributesFile (or the XDG default) and the system gitattributes. A pattern is matched case-insensitively and a pattern this reader cannot
    /// translate matches everything below its file, so the check can only decline more than git would apply (never less); a macro definition that mentions diff declines every path.
    /// </summary>
    // TODO git also falls back to the index copy of an attributes file that is missing from the working tree; only the working-tree files are read here
    private static void RequireNoDiffAttributes(Repository repo, List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var workdir = repo.Info.WorkingDirectory ?? throw Decline("The repository has no working tree.");
        var sources = new List<(string File, string BaseDirectory)> { (Path.Combine(workdir, ".gitattributes"), string.Empty) };
        foreach (var directory in paths.SelectMany(PatchParentDirectories).Where(directory => directory.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sources.Add((Path.Combine(workdir, directory + ".gitattributes"), directory));
        }

        sources.Add((Path.Combine(CommonDirectory(repo), "info", "attributes"), string.Empty));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (EffectiveConfigValue(repo, "core.attributesfile") is { Length: > 0 } configured)
        {
            sources.Add((configured.StartsWith('~') ? home + configured[1..] : configured, string.Empty));
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            sources.Add((Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "git", "attributes"), string.Empty));
        }

        foreach (var directory in GlobalSettings.GetConfigSearchPaths(ConfigurationLevel.System))
        {
            sources.Add((Path.Combine(directory, "gitattributes"), string.Empty));
        }

        foreach (var (file, baseDirectory) in sources)
        {
            var rules = LoadDiffAttributeRules(file, baseDirectory);
            if (rules.Count == 0)
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (path.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase) && rules.Any(rule => rule is null || rule.IsMatch(path[baseDirectory.Length..])))
                {
                    throw Decline($"'{path}' falls under a diff attribute in {Path.GetFileName(file)}; diff drivers, textconv and binary handling are not reproduced, so git answers.");
                }
            }
        }
    }

    /// <summary>"a/b/c.txt" gives "", "a/", "a/b/": the directories whose .gitattributes apply to the file.</summary>
    private static IEnumerable<string> PatchParentDirectories(string path)
    {
        yield return string.Empty;
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            yield return path[..(slash + 1)];
        }
    }

    /// <summary>
    /// The patterns of <paramref name="file"/> whose attribute list names a diff attribute or the binary macro; a null entry matches everything below the file's directory
    /// (a pattern this reader cannot translate, a quoted pattern, a macro definition that mentions diff). Empty when the file is absent or has no such line.
    /// </summary>
    private static List<Regex?> LoadDiffAttributeRules(string file, string baseDirectory)
    {
        var rules = new List<Regex?>();
        if (!File.Exists(file))
        {
            return rules;
        }

        foreach (var rawLine in File.ReadLines(file))
        {
            var line = rawLine.Trim('﻿', ' ', '\t', '\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("[attr]", StringComparison.Ordinal))
            {
                if (MentionsDiffAttribute(line["[attr]".Length..]))
                {
                    rules.Add(null);
                }

                continue;
            }

            if (line[0] == '"')
            {
                if (MentionsDiffAttribute(line))
                {
                    rules.Add(null);
                }

                continue;
            }

            var split = line.IndexOfAny([' ', '\t']);
            if (split > 0 && MentionsDiffAttribute(line[(split + 1)..]))
            {
                rules.Add(AttributePatternRegex(line[..split]));
            }
        }

        return rules;
    }

    private static bool MentionsDiffAttribute(string attributes) =>
        attributes.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token is "diff" or "-diff" or "!diff" or "binary" || token.StartsWith("diff=", StringComparison.Ordinal));

    /// <summary>
    /// A gitattributes pattern as a regex over the path relative to the attributes file's directory (no slash in the pattern: any basename below; a slash: anchored), or null when it
    /// cannot be translated (an unclosed bracket, a trailing backslash). Case-insensitive on purpose: more matches only mean more declines.
    /// </summary>
    private static Regex? AttributePatternRegex(string pattern)
    {
        if (pattern.EndsWith('/'))
        {
            pattern = pattern[..^1];
        }

        var anchored = pattern.StartsWith('/');
        if (anchored)
        {
            pattern = pattern[1..];
        }

        anchored |= pattern.Contains('/');
        if (pattern.Length == 0)
        {
            return null;
        }

        var body = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    var atStart = i == 0 || pattern[i - 1] == '/';
                    if (atStart && i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        body.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        body.Append(".*");
                        i++;
                    }

                    break;
                case '*':
                    body.Append("[^/]*");
                    break;
                case '?':
                    body.Append("[^/]");
                    break;
                case '[':
                    var start = i + 1;
                    var negated = start < pattern.Length && pattern[start] is '!' or '^';
                    var close = pattern.IndexOf(']', negated ? start + 2 : start + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    body.Append(negated ? "[^" : "[");
                    foreach (var ch in pattern[(negated ? start + 1 : start)..close])
                    {
                        body.Append(ch is '\\' or '[' or ']' or '^' ? "\\" + ch : ch.ToString());
                    }

                    body.Append(']');
                    i = close;
                    break;
                case '\\':
                    if (i + 1 >= pattern.Length)
                    {
                        return null;
                    }

                    i++;
                    body.Append(Regex.Escape(pattern[i].ToString()));
                    break;
                default:
                    body.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        try
        {
            return new Regex("^" + (anchored ? string.Empty : "(?:.*/)?") + body + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
