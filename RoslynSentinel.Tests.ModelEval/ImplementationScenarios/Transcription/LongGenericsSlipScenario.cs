namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Slip family of one-line fields and methods whose types are deeply nested generics (at least three levels); the prompt names
/// two token changes per site ("change `Dictionary` to `SortedDictionary`; change `decimal` to `double`").
/// </summary>
/// <remarks>
/// Variant guide (plans/closed/plan_longgenerics_transcription_variants.md, D7; Qwen3.5-9B, 60-turn cap, hosts 112 / 113):
/// <code>
/// Id                          Runs   Pass      Residual (CI)                  Collateral   E2 runs
/// LongGenericsSlip            12/12  1/12,2/12 0.23 (0.17-0.29) / 0.19 (0.11-0.27)  0.10 / 0.07  119 / 226
/// LongGenericsFullLineSlip     6/6   4/6,3/6   0.01 (0-0.02) / 0.01 (0-0.02)        0.17 / 0.20    3 / 3
/// ConcurrentCollectionsSlip    6/6   5/6,2/6   0.02 (0-0.05) / 0.02 (0-0.04)        0.00 / 0.00   17 / 9
/// </code>
/// Strength: measures applying a described change at the right place inside a nested generic (locating the occurrence and level).
/// Weakness: as a transcription instrument it is poor; its Wrong sites are mostly comprehension (change applied at the wrong
/// nesting level, outer wrapper dropped, wrong type argument changed), not copy slips, and its variance makes E2 expensive.
/// Use it for nested-generic edit comprehension; for transcription slips use <see cref="ConcurrentCollectionsSlipScenario"/>,
/// for block-copy omissions <see cref="LongGenericsFullLineSlipScenario"/>.
/// </remarks>
internal static class LongGenericsSlipScenario
{
    // Initialisation order matters: Sites must be declared before Definition. Positions 4, 10, 16, 22, 28 and 34 are untouched controls.
    internal static readonly SlipSiteSet Sites = new(
        FilePath: "ContosoOrders.Core/Evals/LongGenericsSlip/LongGenericDeclarations.cs",
        Namespace: "ContosoOrders.Evals.LongGenericsSlip",
        ClassName: "LongGenericDeclarations",
        Preamble: "",
        Sites:
        [
            Changed("OrdersByRegion", """public static readonly Dictionary<string, List<KeyValuePair<int, HashSet<string>>>> OrdersByRegion = new();""", "Dictionary", "SortedDictionary", "int", "long"),
            Changed("PendingShipments", """public static readonly SortedDictionary<int, Dictionary<string, List<long>>> PendingShipments = new();""", "SortedDictionary", "SortedList", "long", "short"),
            Changed("BackorderIndex", """public static List<Dictionary<string, HashSet<KeyValuePair<int, string>>>> BackorderIndex() => null!;""", "Dictionary", "SortedDictionary", "int", "long"),
            Control("CustomerTiers", """public static readonly Queue<List<Dictionary<int, SortedSet<string>>>> CustomerTiers = new();"""),
            Changed("WarehouseBins", """public static readonly Stack<Dictionary<string, SortedList<int, HashSet<Guid>>>> WarehouseBins = new();""", "Stack", "Queue", "Guid", "Uri"),
            Changed("CarrierRoutes", """public static HashSet<KeyValuePair<string, Dictionary<int, SortedSet<byte>>>> CarrierRoutes() => null!;""", "HashSet", "SortedSet", "byte", "short"),
            Changed("PriceHistory", """public static readonly List<Dictionary<string, List<KeyValuePair<DateTime, decimal>>>> PriceHistory = new();""", "Dictionary", "SortedDictionary", "decimal", "double"),
            Changed("InventoryLevels", """public static readonly Queue<SortedDictionary<string, List<KeyValuePair<int, short>>>> InventoryLevels = new();""", "SortedDictionary", "SortedList", "short", "byte"),
            Changed("ReturnReasons", """public static Queue<HashSet<KeyValuePair<string, SortedDictionary<int, List<char>>>>> ReturnReasons() => null!;""", "Queue", "Stack", "char", "byte"),
            Control("SupplierCatalog", """public static Dictionary<string, SortedDictionary<int, List<HashSet<long>>>> SupplierCatalog() => null!;"""),
            Changed("CouponUsage", """public static readonly SortedSet<KeyValuePair<Guid, Dictionary<string, List<HashSet<int>>>>> CouponUsage = new();""", "SortedSet", "HashSet", "int", "long"),
            Changed("ShipmentLegs", """public static readonly List<SortedList<int, Dictionary<string, Stack<KeyValuePair<long, string>>>>> ShipmentLegs = new();""", "SortedList", "SortedDictionary", "long", "short"),
            Changed("OrderTimeline", """public static Dictionary<DateTime, List<SortedSet<KeyValuePair<string, int>>>> OrderTimeline() => null!;""", "SortedSet", "HashSet", "int", "long"),
            Changed("TaxBrackets", """public static readonly SortedDictionary<string, List<Dictionary<int, Queue<decimal>>>> TaxBrackets = new();""", "SortedDictionary", "Dictionary", "decimal", "double"),
            Changed("LoyaltyScores", """public static readonly HashSet<KeyValuePair<string, SortedDictionary<int, Queue<List<short>>>>> LoyaltyScores = new();""", "HashSet", "SortedSet", "short", "byte"),
            Control("RefundWindows", """public static List<Queue<Dictionary<string, SortedSet<long>>>> RefundWindows() => null!;"""),
            Changed("PromotionRules", """public static Stack<List<KeyValuePair<string, SortedDictionary<int, HashSet<double>>>>> PromotionRules() => null!;""", "Stack", "Queue", "double", "decimal"),
            Changed("AddressBook", """public static readonly HashSet<SortedList<string, Queue<KeyValuePair<int, Uri>>>> AddressBook = new();""", "SortedList", "SortedDictionary", "Uri", "Guid"),
            Changed("FraudSignals", """public static readonly SortedDictionary<long, Dictionary<string, List<HashSet<KeyValuePair<int, byte>>>>> FraudSignals = new();""", "SortedDictionary", "SortedList", "byte", "short"),
            Changed("DeliverySlots", """public static List<HashSet<Dictionary<string, SortedList<int, Stack<char>>>>> DeliverySlots() => null!;""", "HashSet", "SortedSet", "char", "byte"),
            Changed("SessionTokens", """public static readonly Dictionary<string, Stack<KeyValuePair<Guid, SortedSet<List<int>>>>> SessionTokens = new();""", "Stack", "Queue", "int", "long"),
            Control("ArchivedReceipts", """public static readonly SortedList<int, Dictionary<string, HashSet<Queue<byte>>>> ArchivedReceipts = new();"""),
            Changed("PickingTasks", """public static readonly Queue<Dictionary<string, List<KeyValuePair<int, SortedSet<long>>>>> PickingTasks = new();""", "Dictionary", "SortedDictionary", "long", "int"),
            Changed("ReviewScores", """public static readonly SortedDictionary<string, List<KeyValuePair<Guid, Stack<HashSet<int>>>>> ReviewScores = new();""", "SortedDictionary", "Dictionary", "int", "short"),
            Changed("WarrantyClaims", """public static Dictionary<int, List<SortedList<string, Queue<KeyValuePair<Uri, byte>>>>> WarrantyClaims() => null!;""", "SortedList", "SortedDictionary", "byte", "short"),
            Changed("GiftCardLedger", """public static readonly List<Stack<Dictionary<string, KeyValuePair<long, SortedSet<decimal>>>>> GiftCardLedger = new();""", "Stack", "Queue", "decimal", "double"),
            Changed("StockAlerts", """public static readonly HashSet<SortedDictionary<int, List<Queue<KeyValuePair<string, short>>>>> StockAlerts = new();""", "SortedDictionary", "Dictionary", "short", "long"),
            Control("ReturnLabels", """public static Stack<SortedDictionary<string, List<HashSet<int>>>> ReturnLabels() => null!;"""),
            Changed("CurrencyRates", """public static readonly Stack<SortedList<DateTime, Queue<KeyValuePair<Guid, double>>>> CurrencyRates = new();""", "SortedList", "Dictionary", "double", "decimal"),
            Changed("AuditTrails", """public static readonly List<Dictionary<Guid, Queue<List<HashSet<KeyValuePair<string, long>>>>>> AuditTrails = new();""", "Dictionary", "SortedDictionary", "long", "int"),
            Changed("TransferBatches", """public static Queue<SortedList<string, HashSet<Dictionary<int, List<short>>>>> TransferBatches() => null!;""", "SortedList", "SortedDictionary", "short", "byte"),
            Changed("DiscountMatrix", """public static readonly SortedList<int, Dictionary<string, HashSet<Stack<KeyValuePair<Guid, double>>>>> DiscountMatrix = new();""", "Stack", "Queue", "double", "decimal"),
            Changed("BinAssignments", """public static readonly Dictionary<string, Queue<List<SortedSet<KeyValuePair<int, Uri>>>>> BinAssignments = new();""", "Queue", "Stack", "Uri", "Guid"),
            Control("ColorVariants", """public static readonly HashSet<Dictionary<string, SortedList<int, Queue<char>>>> ColorVariants = new();"""),
            Changed("SeasonalOffers", """public static List<Queue<SortedDictionary<string, HashSet<KeyValuePair<int, long>>>>> SeasonalOffers() => null!;""", "Queue", "Stack", "long", "short"),
            Changed("RouteCache", """public static readonly Stack<KeyValuePair<Guid, SortedDictionary<string, List<HashSet<int>>>>> RouteCache = new();""", "SortedDictionary", "Dictionary", "int", "long"),
        ]);

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "LongGenericsSlip",
        "Change type names deep inside 30 long nested generic declarations",
        "plans/plan_slip_first_scenarios.md (LongGenericsSlip)",
        Sites);

    private static SlipSite Changed(string name, string seedText, string firstOld, string firstNew, string secondOld, string secondNew) =>
        new(name, seedText, [new SiteChange(firstOld, firstNew), new SiteChange(secondOld, secondNew)], SiteRole.Changed);

    private static SlipSite Control(string name, string seedText) =>
        new(name, seedText, [], SiteRole.Control);
}
