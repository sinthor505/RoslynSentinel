using System.Collections.Generic;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>Role a site plays in a slip-first family: changed, control or separator.</summary>
internal enum SiteRole { Changed, Control, Separator }

/// <summary>How a slip-first prompt states its edits: per-site change lists, full new declarations, or a type map.</summary>
internal enum SlipPromptStyle { PerSiteChanges, FullLine, TypeMap }

/// <summary>One exact-text replacement applied to a slip site's seed text.</summary>
internal sealed record SiteChange(string Old, string New);

/// <summary>One member site in a slip-first family, with its seed text, changes and role.</summary>
internal sealed record SlipSite(string Name, string SeedText, IReadOnlyList<SiteChange> Changes, SiteRole Role);

/// <summary>The full set of slip sites for one file, with the class shell they render into.</summary>
internal sealed record SlipSiteSet(string FilePath, string Namespace, string ClassName, string Preamble, IReadOnlyList<SlipSite> Sites, SlipPromptStyle PromptStyle = SlipPromptStyle.PerSiteChanges, IReadOnlyList<SiteChange>? TypeMap = null);

/// <summary>Fixed layout constants for the slip-first site family.</summary>
internal static class SlipFamilyLayout
{
    public const int ChangedSiteCount = 30;
    public static readonly IReadOnlyList<int> ControlPositions = [4, 10, 16, 22, 28, 34]; // 1-based
    public static int ObservedSiteCount => ChangedSiteCount + ControlPositions.Count;
    public const int ChangesPerSite = 2;
    public const int MinChangeSpread = 20;
    public const string PlainFragmentPattern = @"^[A-Za-z0-9 _.,:;!?'-]+$";
    public const int MaxDocCommentLines = 3;
    public const string SeparatorPrefix = "Calc";
}
