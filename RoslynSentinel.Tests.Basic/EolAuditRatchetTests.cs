using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet mirror of scripts/Get-EolAudit.ps1 -FailOn lf,mixed (the build.ps1 EOL audit), so agent-only sessions get the same
// signal under RunTest. A file fails when its raw bytes hold a lone LF or a lone CR; a file with no line breaks passes.
// Exclusions match the script: Worktree (PlanStepRunner clones) plus bin, obj, .git, .roslynsentinel and node_modules.
// The script has no default allowlist file, so none is honoured here; fix the files, do not allowlist them.
// If this fails on a fresh Linux checkout without autocrlf, the repo is not in the CRLF state the policy expects.
[TestFixture]
[Category("EolAuditRatchet")]
public class EolAuditRatchetTests
{
    private static readonly string[] ExcludedDirectoryNames = new[] { "bin", "obj", ".git", ".roslynsentinel", "node_modules", "Worktree" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    // Walks the tree by hand so excluded directories are never entered. The extension check is exact because "*.cs" on Windows also
    // matches names such as "x.csproj".
    private static IEnumerable<string> EnumerateCsFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetExtension(file), ".cs", StringComparison.Ordinal))
            {
                yield return file;
            }
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (Array.IndexOf(ExcludedDirectoryNames, Path.GetFileName(subdirectory)) >= 0)
            {
                continue;
            }

            foreach (var file in EnumerateCsFiles(subdirectory))
            {
                yield return file;
            }
        }
    }

    // One pass over the raw bytes: a CR followed by LF is a CRLF pair; any other LF or CR is a lone break.
    private static (int CrLf, int LoneLf, int LoneCr) CountLineBreaks(byte[] bytes)
    {
        const byte Cr = (byte)'\r';
        const byte Lf = (byte)'\n';
        var crLf = 0;
        var loneLf = 0;
        var loneCr = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == Cr)
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == Lf)
                {
                    crLf++;
                    i++;
                }
                else
                {
                    loneCr++;
                }
            }
            else if (bytes[i] == Lf)
            {
                loneLf++;
            }
        }

        return (crLf, loneLf, loneCr);
    }

    [Test]
    public void NoCsFileInTheRepoHasBareLfLineEndings()
    {
        var repo = RepoRoot();
        var files = EnumerateCsFiles(repo).OrderBy(path => path, StringComparer.Ordinal).ToList();
        Assert.That(files, Is.Not.Empty, "The scan found no .cs files; the repo root lookup is broken.");

        var failures = new List<(string RelativePath, int LoneLf, int LoneCr)>();
        foreach (var file in files)
        {
            var counts = CountLineBreaks(File.ReadAllBytes(file));
            if (counts.LoneLf > 0 || counts.LoneCr > 0)
            {
                failures.Add((Path.GetRelativePath(repo, file).Replace('\\', '/'), counts.LoneLf, counts.LoneCr));
            }
        }

        var listed = failures
            .Take(20)
            .Select(failure => "  " + failure.RelativePath + " (lone LF: " + failure.LoneLf + ", lone CR: " + failure.LoneCr + ")");
        var message = failures.Count + " .cs file(s) have bare LF or CR line endings (the lf/mixed EOL classes). "
            + "Run scripts/Get-EolAudit.ps1 -ShowFiles for the full list; normalize with delete + git checkout (autocrlf writes CRLF). "
            + "This ratchet mirrors the build.ps1 EOL audit so agent-only sessions get the same signal under RunTest."
            + Environment.NewLine + string.Join(Environment.NewLine, listed)
            + (failures.Count > 20 ? Environment.NewLine + "  ... and " + (failures.Count - 20) + " more" : string.Empty);

        Assert.That(failures, Is.Empty, message);
    }
}
