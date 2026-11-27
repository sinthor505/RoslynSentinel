using System;
using System.Collections.Generic;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>Builds slip-first implementation scenarios from a SlipSiteSet.</summary>
internal static class SlipFamilyBuilder
{
    public const int TurnCap = 40;

    public static ImplementationScenario Build(string id, string title, string source, SlipSiteSet set)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static IReadOnlyList<SlipSite> Expand(SlipSiteSet set)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static string ApplyChanges(SlipSite site)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static string RenderFile(SlipSiteSet set, bool golden)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static string RenderChangeLine(SlipSite site)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static string RenderPrompt(SlipSiteSet set)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static IReadOnlyList<string> Validate(SlipSiteSet set)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}
