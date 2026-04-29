namespace ExpressRecipe.Services.Domain.FeatureGates;

// This file mimics the structure of the problematic file from the ExpressRecipe solution.
// The primary type in the original file was FeatureGateErrorResponse, but the file was named FeatureGateResult.cs

public sealed class FeatureGateResult
{
    public bool IsEnabled { get; set; }
}

public sealed record FeatureGateErrorResponse
{
    public string ErrorMessage { get; init; }
    public string FeatureName { get; init; }
}
