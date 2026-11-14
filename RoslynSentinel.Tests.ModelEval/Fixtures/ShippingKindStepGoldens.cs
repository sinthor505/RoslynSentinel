namespace RoslynSentinel.Tests.ModelEval.Fixtures;

internal static class ShippingKindStepGoldens
{
    public const int LastStep = 5;

    private const string ShippingDir = "ContosoOrders.Core/Shipping/";

    private const string ShipmentSeed = """
        namespace ContosoOrders.Core;

        public sealed record Shipment(string OrderId, string Kind, decimal WeightKg);

        """;

    private const string ShippingFeeCalculatorSeed = """
        namespace ContosoOrders.Core;

        public static class ShippingFeeCalculator
        {
            public static decimal Calculate(Shipment shipment)
            {
                switch (shipment.Kind)
                {
                    case "standard":
                        return 4.99m + 0.50m * shipment.WeightKg;
                    case "express":
                        return 9.99m + 0.75m * shipment.WeightKg;
                    case "overnight":
                        return 19.99m + 1.00m * shipment.WeightKg;
                    case "pickup":
                        return 0m;
                    default:
                        throw new ArgumentException($"Unknown shipping kind: {shipment.Kind}", nameof(shipment));
                }
            }
        }

        """;

    private const string ShipmentValidatorSeed = """
        namespace ContosoOrders.Core;

        public static class ShipmentValidator
        {
            public static bool IsValid(Shipment shipment)
            {
                var known = shipment.Kind == "standard"
                    || shipment.Kind == "express"
                    || shipment.Kind == "overnight"
                    || shipment.Kind == "pickup";
                if (!known)
                {
                    return false;
                }

                if (shipment.Kind == "pickup")
                {
                    return shipment.WeightKg >= 0;
                }

                return shipment.WeightKg > 0;
            }
        }

        """;

    private const string DeliveryEstimatorSeed = """
        namespace ContosoOrders.Core;

        public static class DeliveryEstimator
        {
            public static int EstimateDays(string kind)
            {
                if (kind == "express" || kind == "overnight") return kind == "overnight" ? 1 : 2;
                if (kind == "pickup") return 0;
                if (kind == "standard") return 5;
                throw new ArgumentException($"Unknown shipping kind: {kind}", nameof(kind));
            }
        }

        """;

    private const string CarrierSelectorSeed = """
        namespace ContosoOrders.Core;

        public static class CarrierSelector
        {
            public static string Select(Shipment shipment)
            {
                if (shipment.Kind == "pickup") return "none";
                if (shipment.Kind == "express" || shipment.Kind == "overnight") return "AirFast";
                if (shipment.Kind == "standard") return "GroundPost";
                throw new ArgumentException($"Unknown shipping kind: {shipment.Kind}", nameof(shipment));
            }
        }

        """;

    private const string ShippingFeeCalculatorG4 = """
        namespace ContosoOrders.Core;

        public static class ShippingFeeCalculator
        {
            public static decimal Calculate(Shipment shipment)
            {
                switch (shipment.Kind)
                {
                    case ShippingKinds.Standard:
                        return 4.99m + 0.50m * shipment.WeightKg;
                    case ShippingKinds.Express:
                        return 9.99m + 0.75m * shipment.WeightKg;
                    case ShippingKinds.Overnight:
                        return 19.99m + 1.00m * shipment.WeightKg;
                    case ShippingKinds.Pickup:
                        return 0m;
                    default:
                        throw new ArgumentException($"Unknown shipping kind: {shipment.Kind}", nameof(shipment));
                }
            }
        }

        """;

    private const string ShipmentValidatorG4 = """
        namespace ContosoOrders.Core;

        public static class ShipmentValidator
        {
            public static bool IsValid(Shipment shipment)
            {
                var known = shipment.Kind == ShippingKinds.Standard
                    || shipment.Kind == ShippingKinds.Express
                    || shipment.Kind == ShippingKinds.Overnight
                    || shipment.Kind == ShippingKinds.Pickup;
                if (!known)
                {
                    return false;
                }

                if (shipment.Kind == ShippingKinds.Pickup)
                {
                    return shipment.WeightKg >= 0;
                }

                return shipment.WeightKg > 0;
            }
        }

        """;

    private const string DeliveryEstimatorG5 = """
        namespace ContosoOrders.Core;

        public static class DeliveryEstimator
        {
            public static int EstimateDays(string kind)
            {
                if (ShippingKinds.IsExpedited(kind)) return kind == ShippingKinds.Overnight ? 1 : 2;
                if (kind == ShippingKinds.Pickup) return 0;
                if (kind == ShippingKinds.Standard) return 5;
                throw new ArgumentException($"Unknown shipping kind: {kind}", nameof(kind));
            }
        }

        """;

    private const string CarrierSelectorG5 = """
        namespace ContosoOrders.Core;

        public static class CarrierSelector
        {
            public static string Select(Shipment shipment)
            {
                if (shipment.Kind == ShippingKinds.Pickup) return "none";
                if (ShippingKinds.IsExpedited(shipment.Kind)) return "AirFast";
                if (shipment.Kind == ShippingKinds.Standard) return "GroundPost";
                throw new ArgumentException($"Unknown shipping kind: {shipment.Kind}", nameof(shipment));
            }
        }

        """;

    private const string ShippingKindsV2 = """
        namespace ContosoOrders.Core;

        public static class ShippingKinds
        {
            public const string Standard = "standard";
            public const string Express = "express";
            public const string Overnight = "overnight";
            public const string Pickup = "pickup";

            public static IReadOnlySet<string> All { get; } = new HashSet<string> { Standard, Express, Overnight, Pickup };
        }

        """;

    private const string ShippingKindsV3 = """
        namespace ContosoOrders.Core;

        public static class ShippingKinds
        {
            public const string Standard = "standard";
            public const string Express = "express";
            public const string Overnight = "overnight";
            public const string Pickup = "pickup";

            public static IReadOnlySet<string> All { get; } = new HashSet<string> { Standard, Express, Overnight, Pickup };

            public static bool IsKnown(string? kind) =>
                kind == Standard || kind == Express || kind == Overnight || kind == Pickup;

            public static bool IsExpedited(string? kind) => kind == Express || kind == Overnight;

            public static string Normalize(string kind)
            {
                var normalized = kind.Trim().ToLowerInvariant();
                if (IsKnown(normalized))
                {
                    return normalized;
                }

                throw new ArgumentException($"Unknown shipping kind: {kind}", nameof(kind));
            }
        }

        """;

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    public static IReadOnlyDictionary<string, string> FilesForStep(int step)
    {
        var files = new Dictionary<string, string>();
        switch (step)
        {
            case 0:
                files[ShippingDir + "Shipment.cs"] = Crlf(ShipmentSeed);
                files[ShippingDir + "ShippingFeeCalculator.cs"] = Crlf(ShippingFeeCalculatorSeed);
                files[ShippingDir + "ShipmentValidator.cs"] = Crlf(ShipmentValidatorSeed);
                files[ShippingDir + "DeliveryEstimator.cs"] = Crlf(DeliveryEstimatorSeed);
                files[ShippingDir + "CarrierSelector.cs"] = Crlf(CarrierSelectorSeed);
                break;
            case 1:
                break;
            case 2:
            case 3:
                files[ShippingDir + "ShippingKinds.cs"] = Crlf(step == 2 ? ShippingKindsV2 : ShippingKindsV3);
                break;
            case 4:
                files[ShippingDir + "ShippingFeeCalculator.cs"] = Crlf(ShippingFeeCalculatorG4);
                files[ShippingDir + "ShipmentValidator.cs"] = Crlf(ShipmentValidatorG4);
                break;
            case 5:
                files[ShippingDir + "DeliveryEstimator.cs"] = Crlf(DeliveryEstimatorG5);
                files[ShippingDir + "CarrierSelector.cs"] = Crlf(CarrierSelectorG5);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }

        return files;
    }

    private const string PromptStep1 = """
        The files under ContosoOrders.Core/Shipping compare shipping kinds as string literals (for example "express"). Find every such comparison or case label. Do not change any file. Reply with one line per occurrence: file name, method name, literal.
        """;

    private const string PromptStep2 = """
        Create ContosoOrders.Core/Shipping/ShippingKinds.cs with a public static class ShippingKinds in namespace ContosoOrders.Core. It has four public const string fields: Standard = "standard", Express = "express", Overnight = "overnight", Pickup = "pickup", and a public static IReadOnlySet<string> property All holding the four values. Change no other file. Reply with what you created.
        """;

    private const string PromptStep3 = """
        Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs). bool IsKnown(string? kind): true when kind is exactly one of the four constants. bool IsExpedited(string? kind): true for Express or Overnight. string Normalize(string kind): trims and lower-cases kind, returns it when IsKnown, otherwise throws ArgumentException. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep4 = """
        In ShippingFeeCalculator.cs and ShipmentValidator.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Behaviour must not change. Change no other file. Reply with the number of literals replaced per file.
        """;

    private const string PromptStep5 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express), and replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    public static string PromptForStep(int step)
    {
        return step switch
        {
            1 => PromptStep1,
            2 => PromptStep2,
            3 => PromptStep3,
            4 => PromptStep4,
            5 => PromptStep5,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    public static IReadOnlyList<string> AllowedPathsForStep(int step)
    {
        return step switch
        {
            0 or 1 => Array.Empty<string>(),
            2 or 3 => new[] { ShippingDir + "ShippingKinds.cs" },
            4 => new[] { ShippingDir + "ShippingFeeCalculator.cs", ShippingDir + "ShipmentValidator.cs" },
            5 => new[] { ShippingDir + "DeliveryEstimator.cs", ShippingDir + "CarrierSelector.cs" },
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }
}
