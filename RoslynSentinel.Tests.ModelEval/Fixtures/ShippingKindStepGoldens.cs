namespace RoslynSentinel.Tests.ModelEval.Fixtures;

internal static class ShippingKindStepGoldens
{
    public const int LastStep = 5;

    private const string ShippingDir = "ContosoOrders.Core/Shipping/";

    private const string VisibleShippingTestsPath = "ContosoOrders.Tests/ShippingBehaviourTests.cs";

    private const string ShipmentSeed = """
        namespace ContosoOrders.Core;

        public sealed record Shipment(string OrderId, string Kind, decimal WeightKg);

        """;

    private const string VisibleShippingTestsSeed = """
        using ContosoOrders.Core;
        using Xunit;

        namespace ContosoOrders.Tests;

        public class ShippingBehaviourTests
        {
            [Theory]
            [InlineData("standard", 5)]
            [InlineData("express", 2)]
            [InlineData("overnight", 1)]
            [InlineData("pickup", 0)]
            public void EstimateDays_ReturnsTheDocumentedDays(string kind, int expected)
            {
                Assert.Equal(expected, DeliveryEstimator.EstimateDays(kind));
            }

            [Fact]
            public void EstimateDays_RejectsAnUnknownKind()
            {
                Assert.Throws<ArgumentException>(() => DeliveryEstimator.EstimateDays("teleport"));
            }

            [Theory]
            [InlineData("standard", "GroundPost")]
            [InlineData("express", "AirFast")]
            [InlineData("overnight", "AirFast")]
            [InlineData("pickup", "none")]
            public void Select_ReturnsTheDocumentedCarrier(string kind, string expected)
            {
                Assert.Equal(expected, CarrierSelector.Select(new Shipment("o1", kind, 1m)));
            }

            [Fact]
            public void Select_RejectsAnUnknownKind()
            {
                Assert.Throws<ArgumentException>(() => CarrierSelector.Select(new Shipment("o1", "teleport", 1m)));
            }
        }

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

    public static IReadOnlyDictionary<string, string> FilesForStep(int step) => FilesForStep(step, withVisibleTests: false);

    /// <summary>
    /// withVisibleTests adds a visible xunit file with the shipping behaviour table to the step-0 commit so a model's RunTest can exercise it (V12/V13 only).
    /// </summary>
    public static IReadOnlyDictionary<string, string> FilesForStep(int step, bool withVisibleTests)
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
                if (withVisibleTests) { files[VisibleShippingTestsPath] = Crlf(VisibleShippingTestsSeed); }
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

    private const string PromptStep1V1 = """
        The files under ContosoOrders.Core/Shipping compare shipping kinds as string literals (for example "express"). Find every such comparison or case label. Do not change any file. Reply with one line per occurrence: file name, method name, literal.
        """;

    private const string PromptStep2V1 = """
        Create ContosoOrders.Core/Shipping/ShippingKinds.cs with a public static class ShippingKinds in namespace ContosoOrders.Core. It has four public const string fields: Standard = "standard", Express = "express", Overnight = "overnight", Pickup = "pickup", and a public static IReadOnlySet<string> property All holding the four values. Change no other file. Reply with what you created.
        """;

    private const string PromptStep3V1 = """
        Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs). bool IsKnown(string? kind): true when kind is exactly one of the four constants. bool IsExpedited(string? kind): true for Express or Overnight. string Normalize(string kind): trims and lower-cases kind, returns it when IsKnown, otherwise throws ArgumentException. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep4V1 = """
        In ShippingFeeCalculator.cs and ShipmentValidator.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Behaviour must not change. Change no other file. Reply with the number of literals replaced per file.
        """;

    private const string PromptStep5V1 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express), and replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep2V2 = """
        Create ContosoOrders.Core/Shipping/ShippingKinds.cs with a public static class ShippingKinds. The folder is Shipping but the namespace is exactly ContosoOrders.Core (not ContosoOrders.Core.Shipping): every other file in the folder uses it and callers depend on it. Use a file-scoped namespace: namespace ContosoOrders.Core; It has four public const string fields: Standard = "standard", Express = "express", Overnight = "overnight", Pickup = "pickup", and a public static IReadOnlySet<string> property All holding the four values. Change no other file. Reply with what you created.
        """;

    private const string PromptStep3V2 = """
        Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs). bool IsKnown(string? kind): true only when kind is exactly one of the four constants. bool IsExpedited(string? kind): true only for Express or Overnight, false for every other value including null. string Normalize(string kind): `var n = kind.Trim().ToLowerInvariant();` return n when IsKnown(n), otherwise throw ArgumentException. Do not use string.Normalize. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V2 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Wherever the code checks 'express or overnight' (kind == Express || kind == Overnight, however it is written), replace the whole condition with a call to ShippingKinds.IsExpedited(kind). No || comparison of those two constants may remain. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep3V3 = """
        Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs). bool IsKnown(string? kind): true only when kind is exactly one of the four constants. bool IsExpedited(string? kind): true only for Express or Overnight, false for every other value including null. string Normalize(string kind): `var n = kind.Trim().ToLowerInvariant();` return n when IsKnown(n), otherwise throw ArgumentException. Do not use string.Normalize. Compare against the constants themselves, never their names written as text. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V3 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), do two things. First, replace every shipping kind string literal in both files with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express), including literals inside other expressions such as a ternary. Then replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep2V5 = """
        Create ContosoOrders.Core/Shipping/ShippingKinds.cs: a public static class ShippingKinds in the same namespace, written the same way, as the other files in that folder. It has four public const string fields, Standard, Express, Overnight and Pickup, whose values are the lower-case names ("standard", "express", "overnight", "pickup"), and a public static IReadOnlySet<string> property All holding the four values. Change no other file. Reply with what you created.
        """;

    private const string PromptStep3V5 = """
        Add three public static methods to ShippingKinds (ContosoOrders.Core/Shipping/ShippingKinds.cs): IsKnown(string? kind), IsExpedited(string? kind) and string Normalize(string kind). IsKnown is true only for the four constant values: IsKnown("pickup") is true; IsKnown("Pickup"), IsKnown("rocket") and IsKnown(null) are false. IsExpedited is true only for the Express and Overnight values: IsExpedited("express") is true; IsExpedited("pickup") and IsExpedited(null) are false. Normalize returns the trimmed, lower-case form of kind when that is a known kind, so Normalize("  Pickup ") returns "pickup"; otherwise it throws ArgumentException, so Normalize("rocket") throws. The methods compare against the constants, never their names written as text. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V5 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), express every shipping kind through ShippingKinds. When you finish, neither file contains a quoted shipping kind value anywhere (comparisons, ternaries, return values), each is written as qualified ShippingKinds.<Name>, and no condition tests Express and Overnight separately, because ShippingKinds.IsExpedited covers that. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V6 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), express every shipping kind through ShippingKinds. When you finish, neither file contains a quoted shipping kind value anywhere (comparisons, ternaries, return values), each is written as qualified ShippingKinds.<Name>, and no condition tests Express and Overnight separately, because ShippingKinds.IsExpedited covers that. Behaviour must not change. Change no other file. Before replying, search both files for quoted shipping kind values and fix any that remain. Reply with what you changed.
        """;

    private const string PromptStep1V4 = """
        Files: ContosoOrders.Core/Shipping/*.cs (read only)
        Symbols: every comparison or case label against a shipping kind string literal
        Operation: find every use of the quoted values "standard", "express", "overnight", "pickup" in a comparison or case label
        Acceptance: the reply has one line per occurrence (file name, method name, literal) and names every file that has one
        Out of scope: change no file
        """;

    private const string PromptStep2V4 = """
        Files: ContosoOrders.Core/Shipping/ShippingKinds.cs (new file)
        Symbols: public static class ShippingKinds, in a file-scoped namespace written like the other files in the folder
        Operation: add public const string Standard = "standard", Express = "express", Overnight = "overnight", Pickup = "pickup"; add public static IReadOnlySet<string> All { get; } = new HashSet<string> { Standard, Express, Overnight, Pickup };
        Acceptance: the project still compiles; reply with what you created
        Out of scope: change no other file
        """;

    private const string PromptStep3V4 = """
        Files: ContosoOrders.Core/Shipping/ShippingKinds.cs
        Symbols: add IsKnown, IsExpedited, Normalize to ShippingKinds
        Operation: bool IsKnown(string? kind) => kind == Standard || kind == Express || kind == Overnight || kind == Pickup; bool IsExpedited(string? kind) => kind == Express || kind == Overnight; string Normalize(string kind): var n = kind.Trim().ToLowerInvariant(); return n when IsKnown(n), otherwise throw ArgumentException
        Acceptance: the project still compiles; reply with what you changed
        Out of scope: change no other file; do not use string.Normalize
        """;

    private const string PromptStep4V4 = """
        Files: ContosoOrders.Core/Shipping/ShippingFeeCalculator.cs, ContosoOrders.Core/Shipping/ShipmentValidator.cs
        Symbols: every shipping kind string literal in both files
        Operation: replace each quoted kind value with the matching ShippingKinds.<Name> (Standard, Express, Overnight, Pickup), then search each file for quoted values that remain
        Acceptance: none remain in either file, behaviour is unchanged, the project still compiles; reply with the number of literals replaced per file
        Out of scope: change no other file
        """;

    private const string PromptStep5V4 = """
        Files: ContosoOrders.Core/Shipping/DeliveryEstimator.cs, ContosoOrders.Core/Shipping/CarrierSelector.cs
        Symbols: every shipping kind string literal and every 'express or overnight' condition in both files
        Operation: replace every quoted kind value, including inside a ternary, with the matching ShippingKinds.<Name>; replace each express-or-overnight condition with ShippingKinds.IsExpedited(kind); then search both files for quoted values that remain
        Acceptance: none remain, no condition tests Express and Overnight separately, behaviour is unchanged, the project still compiles; reply with what you changed
        Out of scope: change no other file
        """;

    private const string PromptStep5V7 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), do two things in this order. First, replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Then replace every remaining shipping kind string literal in both files, including literals inside other expressions such as a ternary, with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V8 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), do two things. First, replace every shipping kind string literal in both files with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express), including literals inside other expressions such as a ternary. Then replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. ShippingKinds.IsExpedited(kind) is true for exactly Express and Overnight, so it replaces such a check even when the same line goes on to branch on Overnight. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V9 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), do two things in this order. First, replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Then replace every remaining shipping kind string literal in both files, including literals inside other expressions such as a ternary, with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). For example, if (x == "express" || x == "overnight") return x == "overnight" ? 1 : 2; becomes if (ShippingKinds.IsExpedited(x)) return x == ShippingKinds.Overnight ? 1 : 2; Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V10 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), each file has one check for 'express or overnight' and two checks for a single kind; in DeliveryEstimator the express-or-overnight line also contains a ternary on overnight. Each express-or-overnight check becomes a call to ShippingKinds.IsExpedited, and every shipping kind string literal that remains, including the one in that ternary, becomes the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V11 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), replace every shipping kind string literal with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express), and replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. When you finish, each of the two files contains exactly one call to ShippingKinds.IsExpedited and no quoted shipping kind value. Behaviour must not change. Change no other file. Reply with what you changed.
        """;

    private const string PromptStep5V12 = """
        In DeliveryEstimator.cs and CarrierSelector.cs (ContosoOrders.Core/Shipping), do two things in this order. First, replace each check for 'express or overnight' with a call to ShippingKinds.IsExpedited. Then replace every remaining shipping kind string literal in both files, including literals inside other expressions such as a ternary, with the matching ShippingKinds constant, written qualified as ShippingKinds.<Name> (for example ShippingKinds.Express). Behaviour must not change. Change no other file. When the edits are done, run the ContosoOrders tests with RunTest and confirm that none fail; if any fail, fix the cause before replying. Reply with what you changed.
        """;

    public static string PromptForStep(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V1,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V2 changes only steps 2, 3 and 5; steps 1 and 4 reuse V1 as an in-batch control.
    /// </summary>
    public static string PromptForStepV2(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V2,
            3 => PromptStep3V2,
            4 => PromptStep4V1,
            5 => PromptStep5V2,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V3 = V2 plus a constants-not-names line (step 3) and a literals-then-IsExpedited step 5; steps 1 and 4 reuse V1.
    /// </summary>
    public static string PromptForStepV3(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V2,
            3 => PromptStep3V3,
            4 => PromptStep4V1,
            5 => PromptStep5V3,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V5 = behaviour/postcondition wording, no search nudge; steps 1 and 4 reuse V1.
    /// </summary>
    public static string PromptForStepV5(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V5,
            3 => PromptStep3V5,
            4 => PromptStep4V1,
            5 => PromptStep5V5,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V6 = V5 plus a search-before-replying nudge in step 5 only.
    /// </summary>
    public static string PromptForStepV6(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V5,
            3 => PromptStep3V5,
            4 => PromptStep4V1,
            5 => PromptStep5V6,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V4 = Haiku-style brief for all five steps (the most prescriptive variant; a ceiling test).
    /// </summary>
    public static string PromptForStepV4(int step)
    {
        return step switch
        {
            1 => PromptStep1V4,
            2 => PromptStep2V4,
            3 => PromptStep3V4,
            4 => PromptStep4V4,
            5 => PromptStep5V4,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V7 = step 5 only: IsExpedited check first, then the remaining literals; steps 1-4 reuse V1.
    /// </summary>
    public static string PromptForStepV7(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V7,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V8 = step 5 only: V3 plus an equivalence sentence for IsExpedited; steps 1-4 reuse V1.
    /// </summary>
    public static string PromptForStepV8(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V8,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V9 = step 5 only: V7 plus a worked example on invented names; steps 1-4 reuse V1.
    /// </summary>
    public static string PromptForStepV9(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V9,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V10 = step 5 only: describes the shape of the two files without code; steps 1-4 reuse V1.
    /// </summary>
    public static string PromptForStepV10(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V10,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V11 = step 5 only: V1-style instruction plus an exactly-one-IsExpedited end-state; steps 1-4 reuse V1.
    /// </summary>
    public static string PromptForStepV11(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V11,
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>
    /// V12 = step 5 only: V7 plus a final instruction to run the tests; run with the visible-test fixture.
    /// </summary>
    public static string PromptForStepV12(int step)
    {
        return step switch
        {
            1 => PromptStep1V1,
            2 => PromptStep2V1,
            3 => PromptStep3V1,
            4 => PromptStep4V1,
            5 => PromptStep5V12,
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
