namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Slip family of one-line fields and methods whose types are deeply nested generics (at least three levels), each holding two
/// of the type-map keys Dictionary, Queue and Stack once; the prompt gives the map and the member names. Not registered until a later step.
/// </summary>
internal static class ConcurrentCollectionsSlipScenario
{
    // Initialisation order matters: Sites must be declared before Definition. Positions 4, 10, 16, 22, 28 and 34 are untouched controls.
    private static readonly SlipSiteSet Sites = new(
        FilePath: "ContosoOrders.Core/Evals/ConcurrentCollectionsSlip/ConcurrentCollectionDeclarations.cs",
        Namespace: "ContosoOrders.Evals.ConcurrentCollectionsSlip",
        ClassName: "ConcurrentCollectionDeclarations",
        Preamble: "using System.Collections.Concurrent;",
        Sites:
        [
            Changed("VendorContracts", """public static readonly Dictionary<string, List<Queue<KeyValuePair<int, Guid>>>> VendorContracts = new();""", "Dictionary", "ConcurrentDictionary", "Queue", "ConcurrentQueue"),
            Changed("ShiftRosters", """public static Queue<HashSet<KeyValuePair<DateTime, Dictionary<int, short>>>> ShiftRosters() => null!;""", "Queue", "ConcurrentQueue", "Dictionary", "ConcurrentDictionary"),
            Changed("LabelBatches", """public static readonly List<Dictionary<Guid, SortedSet<Queue<byte>>>> LabelBatches = new();""", "Dictionary", "ConcurrentDictionary", "Queue", "ConcurrentQueue"),
            Control("ReturnDockets", """public static readonly List<SortedSet<HashSet<KeyValuePair<string, int>>>> ReturnDockets = new();"""),
            Changed("TariffWindows", """public static HashSet<Queue<KeyValuePair<int, List<Dictionary<string, decimal>>>>> TariffWindows() => null!;""", "Queue", "ConcurrentQueue", "Dictionary", "ConcurrentDictionary"),
            Changed("DispatchLanes", """public static readonly Dictionary<int, SortedList<string, HashSet<List<Queue<double>>>>> DispatchLanes = new();""", "Dictionary", "ConcurrentDictionary", "Queue", "ConcurrentQueue"),
            Changed("ParcelHistory", """public static Queue<SortedList<string, Dictionary<Guid, List<long>>>> ParcelHistory() => null!;""", "Queue", "ConcurrentQueue", "Dictionary", "ConcurrentDictionary"),
            Changed("ArrivalSchedules", """public static readonly List<Dictionary<DateTime, Queue<HashSet<byte>>>> ArrivalSchedules = new();""", "Dictionary", "ConcurrentDictionary", "Queue", "ConcurrentQueue"),
            Changed("PackagingSpecs", """public static HashSet<Queue<List<KeyValuePair<int, Dictionary<string, char>>>>> PackagingSpecs() => null!;""", "Queue", "ConcurrentQueue", "Dictionary", "ConcurrentDictionary"),
            Control("VoucherBatches", """public static SortedSet<List<SortedList<int, HashSet<Uri>>>> VoucherBatches() => null!;"""),
            Changed("ReplenishmentPlans", """public static readonly Queue<List<KeyValuePair<string, Dictionary<int, SortedSet<char>>>>> ReplenishmentPlans = new();""", "Queue", "ConcurrentQueue", "Dictionary", "ConcurrentDictionary"),
            Changed("CheckoutSessions", """public static Dictionary<DateTime, Queue<List<HashSet<decimal>>>> CheckoutSessions() => null!;""", "Dictionary", "ConcurrentDictionary", "Queue", "ConcurrentQueue"),
            Changed("HoldingBays", """public static readonly Stack<KeyValuePair<Guid, Dictionary<string, List<int>>>> HoldingBays = new();""", "Stack", "ConcurrentStack", "Dictionary", "ConcurrentDictionary"),
            Changed("MerchantProfiles", """public static Dictionary<string, List<HashSet<Stack<short>>>> MerchantProfiles() => null!;""", "Dictionary", "ConcurrentDictionary", "Stack", "ConcurrentStack"),
            Changed("LedgerEntries", """public static readonly List<Dictionary<int, SortedList<string, Stack<byte>>>> LedgerEntries = new();""", "Dictionary", "ConcurrentDictionary", "Stack", "ConcurrentStack"),
            Control("ShelfLabels", """public static readonly HashSet<KeyValuePair<Guid, List<SortedSet<long>>>> ShelfLabels = new();"""),
            Changed("CampaignAudiences", """public static HashSet<Stack<KeyValuePair<DateTime, Dictionary<string, char>>>> CampaignAudiences() => null!;""", "Stack", "ConcurrentStack", "Dictionary", "ConcurrentDictionary"),
            Changed("ShelfAllocations", """public static readonly Stack<List<HashSet<SortedList<int, Dictionary<string, decimal>>>>> ShelfAllocations = new();""", "Stack", "ConcurrentStack", "Dictionary", "ConcurrentDictionary"),
            Changed("ClaimDecisions", """public static Dictionary<Guid, KeyValuePair<int, Stack<List<double>>>> ClaimDecisions() => null!;""", "Dictionary", "ConcurrentDictionary", "Stack", "ConcurrentStack"),
            Changed("FreightQuotes", """public static readonly List<SortedSet<Stack<KeyValuePair<Uri, Dictionary<string, long>>>>> FreightQuotes = new();""", "Stack", "ConcurrentStack", "Dictionary", "ConcurrentDictionary"),
            Changed("CatalogRevisions", """public static List<SortedSet<Dictionary<string, KeyValuePair<Guid, Stack<byte>>>>> CatalogRevisions() => null!;""", "Dictionary", "ConcurrentDictionary", "Stack", "ConcurrentStack"),
            Control("CartSnapshots", """public static List<HashSet<SortedList<string, SortedSet<double>>>> CartSnapshots() => null!;"""),
            Changed("SubscriptionTerms", """public static readonly Stack<SortedSet<HashSet<Dictionary<DateTime, List<int>>>>> SubscriptionTerms = new();""", "Stack", "ConcurrentStack", "Dictionary", "ConcurrentDictionary"),
            Changed("WishlistItems", """public static HashSet<Dictionary<DateTime, Stack<List<long>>>> WishlistItems() => null!;""", "Dictionary", "ConcurrentDictionary", "Stack", "ConcurrentStack"),
            Changed("ReceiptBatches", """public static readonly Queue<SortedList<string, Stack<List<int>>>> ReceiptBatches = new();""", "Queue", "ConcurrentQueue", "Stack", "ConcurrentStack"),
            Changed("PalletMoves", """public static Stack<SortedSet<HashSet<Queue<KeyValuePair<int, Guid>>>>> PalletMoves() => null!;""", "Stack", "ConcurrentStack", "Queue", "ConcurrentQueue"),
            Changed("ReorderTriggers", """public static readonly List<Queue<KeyValuePair<string, Stack<decimal>>>> ReorderTriggers = new();""", "Queue", "ConcurrentQueue", "Stack", "ConcurrentStack"),
            Control("SupplierNotes", """public static readonly SortedList<DateTime, List<KeyValuePair<int, HashSet<string>>>> SupplierNotes = new();"""),
            Changed("CourierHandoffs", """public static HashSet<Stack<SortedList<int, Queue<double>>>> CourierHandoffs() => null!;""", "Stack", "ConcurrentStack", "Queue", "ConcurrentQueue"),
            Changed("InvoiceRuns", """public static readonly Queue<List<KeyValuePair<Guid, HashSet<Stack<short>>>>> InvoiceRuns = new();""", "Queue", "ConcurrentQueue", "Stack", "ConcurrentStack"),
            Changed("SurveyAnswers", """public static Stack<KeyValuePair<DateTime, Queue<List<byte>>>> SurveyAnswers() => null!;""", "Stack", "ConcurrentStack", "Queue", "ConcurrentQueue"),
            Changed("TicketEscalations", """public static readonly List<SortedSet<Queue<KeyValuePair<Uri, Stack<char>>>>> TicketEscalations = new();""", "Queue", "ConcurrentQueue", "Stack", "ConcurrentStack"),
            Changed("PricingTiers", """public static List<HashSet<Stack<SortedList<string, Queue<decimal>>>>> PricingTiers() => null!;""", "Stack", "ConcurrentStack", "Queue", "ConcurrentQueue"),
            Control("CarrierBids", """public static HashSet<SortedSet<KeyValuePair<string, List<Guid>>>> CarrierBids() => null!;"""),
            Changed("DepotTransfers", """public static readonly HashSet<Queue<List<KeyValuePair<long, Stack<int>>>>> DepotTransfers = new();""", "Queue", "ConcurrentQueue", "Stack", "ConcurrentStack"),
            Changed("NotificationPrefs", """public static List<Stack<SortedSet<KeyValuePair<Guid, Queue<double>>>>> NotificationPrefs() => null!;""", "Stack", "ConcurrentStack", "Queue", "ConcurrentQueue"),
        ],
        PromptStyle: SlipPromptStyle.TypeMap,
        TypeMap:
        [
            new SiteChange("Dictionary", "ConcurrentDictionary"),
            new SiteChange("Queue", "ConcurrentQueue"),
            new SiteChange("Stack", "ConcurrentStack"),
        ]);

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "ConcurrentCollectionsSlip",
        "Make the collections in 30 long nested generic declarations concurrent",
        "plans/plan_longgenerics_transcription_variants.md (variant B)",
        Sites);

    private static SlipSite Changed(string name, string seedText, string firstOld, string firstNew, string secondOld, string secondNew) =>
        new(name, seedText, [new SiteChange(firstOld, firstNew), new SiteChange(secondOld, secondNew)], SiteRole.Changed);

    private static SlipSite Control(string name, string seedText) =>
        new(name, seedText, [], SiteRole.Control);
}
