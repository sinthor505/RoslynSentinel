using System;
using System.Collections.Generic;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>Outcome of one slip site after grading the end-state file.</summary>
internal enum SiteOutcome { Correct, Unchanged, Wrong, Missing }

/// <summary>Graded result for one slip site, with its end-state text when present.</summary>
internal sealed record SiteGrade(string SiteName, SiteRole Role, SiteOutcome Outcome, string? EndStateText);

/// <summary>Grades slip-first sites against an end-state file and reads or writes the per-step sidecar.</summary>
internal static class SiteGrader
{
    public const string SidecarFileNameFormat = "step-{0}.sites.json";

    public static IReadOnlyList<SiteGrade> Grade(string? fileText, SlipSiteSet set)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }

    public static bool AllIntact(IReadOnlyList<SiteGrade> grades)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }

    public static string Summarize(IReadOnlyList<SiteGrade> grades)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }

    public static void WriteSidecar(string path, IReadOnlyList<SiteGrade> grades)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }

    public static IReadOnlyList<SiteGrade> ReadSidecar(string path)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }
}
