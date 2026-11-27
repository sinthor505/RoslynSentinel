namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Slip family of one-line message methods that each reference two of four near-duplicate enum members sharing the Over prefix; not registered until a later step.
/// </summary>
internal static class NearDuplicateIdentifiersSlipScenario
{
    // Initialisation order matters: Sites must be declared before Definition. Positions 4, 10, 16, 22, 28 and 34 are untouched controls.
    private static readonly SlipSiteSet Sites = new(
        FilePath: "ContosoOrders.Core/Evals/NearDuplicateIdentifiersSlip/ShippingKindMessages.cs",
        Namespace: "ContosoOrders.Evals.NearDuplicateIdentifiersSlip",
        ClassName: "ShippingKindMessages",
        Preamble: "public enum ShippingKinds { Overnight, Overland, Overseas, Overweight }",
        Sites:
        [
            Changed("TransitNotice", """public static string TransitNotice(int n) => $"Parcel {n} moves by {ShippingKinds.Overnight} first, then {ShippingKinds.Overland} for the last leg.";""", "Overnight", "Overseas", "Overland", "Overweight"),
            Changed("FreightQuote", """public static string FreightQuote(int n) => $"Quote {n}: ground rate with {ShippingKinds.Overland} pricing or the faster {ShippingKinds.Overnight} option.";""", "Overland", "Overweight", "Overnight", "Overseas"),
            Changed("PickupWindow", """public static string PickupWindow(int n) => $"Pickup slot {n} is open for {ShippingKinds.Overseas} cargo and for {ShippingKinds.Overweight} cargo.";""", "Overseas", "Overnight", "Overweight", "Overland"),
            Control("DeliveryEstimate", """public static string DeliveryEstimate(int n) => $"Estimate {n}: allow extra days for {ShippingKinds.Overweight} items and for {ShippingKinds.Overseas} items.";"""),
            Changed("WarehouseRoute", """public static string WarehouseRoute(int n) => $"Route {n} sends rush stock by {ShippingKinds.Overnight} truck, bulk stock by {ShippingKinds.Overseas} vessel.";""", "Overnight", "Overland", "Overseas", "Overweight"),
            Changed("CustomsHold", """public static string CustomsHold(int n) => $"Hold {n} applies to {ShippingKinds.Overseas} freight, not to {ShippingKinds.Overnight} freight.";""", "Overseas", "Overweight", "Overnight", "Overland"),
            Changed("CarrierHandoff", """public static string CarrierHandoff(int n) => $"Handoff {n} lists {ShippingKinds.Overland} carriers followed by {ShippingKinds.Overweight} specialists.";""", "Overland", "Overnight", "Overweight", "Overseas"),
            Changed("HeavyLoadAlert", """public static string HeavyLoadAlert(int n) => $"Alert {n}: {ShippingKinds.Overweight} loads need a permit unlike {ShippingKinds.Overland} loads.";""", "Overweight", "Overseas", "Overland", "Overnight"),
            Changed("DockSchedule", """public static string DockSchedule(int n) => $"Dock {n} handles {ShippingKinds.Overnight} pallets at dawn and {ShippingKinds.Overweight} pallets at noon.";""", "Overnight", "Overland", "Overweight", "Overseas"),
            Control("PackingSlipHint", """public static string PackingSlipHint(int n) => $"Slip {n} marks {ShippingKinds.Overland} boxes in blue and {ShippingKinds.Overseas} boxes in green.";"""),
            Changed("RateTableLine", """public static string RateTableLine(int n) => $"Row {n}: the {ShippingKinds.Overweight} surcharge is higher than the {ShippingKinds.Overnight} surcharge.";""", "Overweight", "Overland", "Overnight", "Overseas"),
            Changed("BillingMemo", """public static string BillingMemo(int n) => $"Memo {n} bills {ShippingKinds.Overseas} shipments monthly and {ShippingKinds.Overland} shipments weekly.";""", "Overseas", "Overnight", "Overland", "Overweight"),
            Changed("TrackingBanner", """public static string TrackingBanner(int n) => $"Tracking {n} shows the {ShippingKinds.Overnight} scan, then the {ShippingKinds.Overland} scan.";""", "Overnight", "Overweight", "Overland", "Overseas"),
            Changed("RefundPolicyLine", """public static string RefundPolicyLine(int n) => $"Refund {n} is full for {ShippingKinds.Overland} orders but partial for {ShippingKinds.Overnight} orders.";""", "Overland", "Overseas", "Overnight", "Overweight"),
            Changed("ClaimDeskNote", """public static string ClaimDeskNote(int n) => $"Claim {n} on {ShippingKinds.Overseas} goods goes to the port desk, {ShippingKinds.Overweight} goods to the depot.";""", "Overseas", "Overland", "Overweight", "Overnight"),
            Control("CouponFooter", """public static string CouponFooter(int n) => $"Coupon {n} excludes {ShippingKinds.Overnight} upgrades and also {ShippingKinds.Overweight} fees.";"""),
            Changed("InsuranceRider", """public static string InsuranceRider(int n) => $"Rider {n} covers {ShippingKinds.Overnight} transit up to a limit, {ShippingKinds.Overseas} transit up to double.";""", "Overnight", "Overweight", "Overseas", "Overland"),
            Changed("AddressLabel", """public static string AddressLabel(int n) => $"Label {n}: print the {ShippingKinds.Overseas} stamp near the top and the {ShippingKinds.Overnight} stamp below.";""", "Overseas", "Overland", "Overnight", "Overweight"),
            Changed("FuelSurchargeNote", """public static string FuelSurchargeNote(int n) => $"Fuel note {n} adds a fee to {ShippingKinds.Overland} trips and a larger fee to {ShippingKinds.Overweight} trips.";""", "Overland", "Overseas", "Overweight", "Overnight"),
            Changed("SupplierBrief", """public static string SupplierBrief(int n) => $"Brief {n} asks suppliers to label {ShippingKinds.Overweight} crates before {ShippingKinds.Overland} crates.";""", "Overweight", "Overnight", "Overland", "Overseas"),
            Changed("StockTransferLog", """public static string StockTransferLog(int n) => $"Transfer {n} moved {ShippingKinds.Overnight} stock out and brought {ShippingKinds.Overweight} stock in.";""", "Overnight", "Overseas", "Overweight", "Overland"),
            Control("AuditStamp", """public static string AuditStamp(int n) => $"Audit {n} checked {ShippingKinds.Overseas} paperwork and then {ShippingKinds.Overland} paperwork.";"""),
            Changed("ReturnsDeskTip", """public static string ReturnsDeskTip(int n) => $"Tip {n}: inspect {ShippingKinds.Overweight} returns twice, but {ShippingKinds.Overnight} returns once.";""", "Overweight", "Overseas", "Overnight", "Overland"),
            Changed("SortingLaneCue", """public static string SortingLaneCue(int n) => $"Lane {n} takes {ShippingKinds.Overseas} bags on the left and {ShippingKinds.Overland} bags on the right.";""", "Overseas", "Overweight", "Overland", "Overnight"),
            Changed("HubArrivalAlert", """public static string HubArrivalAlert(int n) => $"Hub alert {n}: the {ShippingKinds.Overnight} van is early while the {ShippingKinds.Overland} truck is late.";""", "Overnight", "Overseas", "Overland", "Overnight"),
            Changed("LoadingBayRule", """public static string LoadingBayRule(int n) => $"Bay {n} rule: load {ShippingKinds.Overland} goods last and {ShippingKinds.Overnight} goods first.";""", "Overland", "Overweight", "Overnight", "Overland"),
            Changed("DepotCapacityNote", """public static string DepotCapacityNote(int n) => $"Depot {n} has room for {ShippingKinds.Overseas} containers but not for {ShippingKinds.Overweight} containers.";""", "Overseas", "Overnight", "Overweight", "Overland"),
            Control("ManifestFooter", """public static string ManifestFooter(int n) => $"Manifest {n} ends with {ShippingKinds.Overland} totals followed by {ShippingKinds.Overweight} totals.";"""),
            Changed("PalletTagText", """public static string PalletTagText(int n) => $"Pallet {n} tag: {ShippingKinds.Overweight} pallets are red, {ShippingKinds.Overseas} pallets are white.";""", "Overweight", "Overnight", "Overseas", "Overland"),
            Changed("TermsClause", """public static string TermsClause(int n) => $"Clause {n}: liability for {ShippingKinds.Overnight} service differs from {ShippingKinds.Overseas} service.";""", "Overnight", "Overseas", "Overseas", "Overweight"),
            Changed("DamageReportLine", """public static string DamageReportLine(int n) => $"Report {n} lists damage found on {ShippingKinds.Overseas} cargo and on {ShippingKinds.Overnight} cargo.";""", "Overseas", "Overweight", "Overnight", "Overland"),
            Changed("LabelPrintHint", """public static string LabelPrintHint(int n) => $"Print hint {n}: use thick paper for {ShippingKinds.Overland} labels, thin paper for {ShippingKinds.Overseas} labels.";""", "Overland", "Overnight", "Overseas", "Overweight"),
            Changed("RouteOverrideNote", """public static string RouteOverrideNote(int n) => $"Override {n} lets planners switch {ShippingKinds.Overweight} routes, though {ShippingKinds.Overland} routes stay fixed.";""", "Overweight", "Overland", "Overland", "Overseas"),
            Control("EtaDisclaimer", """public static string EtaDisclaimer(int n) => $"Disclaimer {n}: dates for {ShippingKinds.Overnight} service are firm, dates for {ShippingKinds.Overseas} service are not.";"""),
            Changed("PriorityUpgradeText", """public static string PriorityUpgradeText(int n) => $"Upgrade {n} turns {ShippingKinds.Overweight} handling into priority and {ShippingKinds.Overnight} handling into express.";""", "Overweight", "Overseas", "Overnight", "Overland"),
            Changed("YardTransferNote", """public static string YardTransferNote(int n) => $"Yard note {n}: park {ShippingKinds.Overland} trailers at gate one, {ShippingKinds.Overweight} trailers at gate two.";""", "Overland", "Overseas", "Overweight", "Overnight"),
        ]);

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "NearDuplicateIdentifiersSlip",
        "Swap enum member references among four near-duplicate Over* names in 30 messages",
        "plans/plan_slip_first_scenarios.md (NearDuplicateIdentifiersSlip)",
        Sites);

    private static SlipSite Changed(string name, string seedText, string firstOld, string firstNew, string secondOld, string secondNew) =>
        new(name, seedText, [new SiteChange(firstOld, firstNew), new SiteChange(secondOld, secondNew)], SiteRole.Changed);

    private static SlipSite Control(string name, string seedText) =>
        new(name, seedText, [], SiteRole.Control);
}
