namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Slip family of one-line message methods whose string and regex literals are escape-heavy (regular interpolated, verbatim and regex patterns); not registered until a later step.
/// </summary>
internal static class EscapeLiteralsSlipScenario
{
    // Initialisation order matters: Sites must be declared before Definition. Positions 4, 10, 16, 22, 28 and 34 are untouched controls.
    private static readonly SlipSiteSet Sites = new(
        FilePath: "ContosoOrders.Core/Evals/EscapeLiteralsSlip/EscapeLiteralMessages.cs",
        Namespace: "ContosoOrders.Evals.EscapeLiteralsSlip",
        ClassName: "EscapeLiteralMessages",
        Preamble: "",
        Sites:
        [
            Changed("GreetingQuoted", """public static string GreetingQuoted(string name) => $"Dear \"{name}\",\n\tyour parcel has shipped.\n";""", "Dear", "Hello", "has shipped", "was sent"),
            Changed("QuotedDirective", """public static string QuotedDirective() => @"She said ""ship it today"" and ""hold the rest"" until Monday.";""", "ship it today", "send it now", "Monday", "Tuesday"),
            Changed("LegacyCodePattern", """public static string LegacyCodePattern() => @"^\d{4}-(?:Order|Return)-\d{2}-(?:Open|Closed)\.$";""", "Return", "Refund", "Closed", "Shut"),
            Control("ReceiptFooter", """public static string ReceiptFooter(string id) => $"Receipt \"{id}\"\n\tThank you for shopping.\n";"""),
            Changed("TemplateBraces", """public static string TemplateBraces(string key) => $@"{{ ""key"": ""{key}"" }} stored in ""cache"" until reload";""", "stored in", "kept in", "reload", "expiry"),
            Changed("PhoneNumberPattern", """public static string PhoneNumberPattern() => @"^(?:Tel|Fax):\s\(\d{3}\)\s\d{3}-\d{4}\s(?:mobile|office|home)\b";""", "Tel", "Phone", "home", "house"),
            Changed("InvoiceHeader", """public static string InvoiceHeader(string id, decimal total) => $"Invoice \"{id}\"\n\tTotal due: {total:F2}\n\tPay within 30 days.";""", "Total due", "Amount owed", "Pay within 30 days", "Pay by month end"),
            Changed("WindowsShare", """public static string WindowsShare(string server) => $@"\\{server}\public\""reports"" folder, mirrored every night to the backup site";""", "folder", "share", "backup site", "cold storage"),
            Changed("EmailPattern", """public static string EmailPattern() => @"^[a-z]+\d*@(?:corp|staff)\.(?:com|net|org)\b";""", "corp", "firm", "org", "edu"),
            Control("HelpBanner", """public static string HelpBanner(string topic) => $@"Help for ""{topic}"" lists {{all}} commands.";"""),
            Changed("PathReport", """public static string PathReport(string folder, string file) => $"Saved to \"{folder}\\{file}\"\n\tbackup kept in \\\\archive\\old.\n";""", "Saved to", "Moved to", "backup kept", "copy stored"),
            Changed("SqlQuoted", """"public static string SqlQuoted(string table) => $@"SELECT ""Id"", ""Name"" FROM ""{table}"" WHERE ""Active"" = 1 ORDER BY ""Name""";"""", "Id", "Key", "Active", "Enabled"),
            Changed("VersionPattern", """public static string VersionPattern() => @"^release-v\d+\.\d+\.\d+-(?:alpha|beta|rc)\d*$";""", "release", "build", "beta", "pre"),
            Changed("TabTable", """public static string TabTable(string label, int qty) => $"{label}\t{qty}\tunits in stock\n{label}\t0\tbackorder\n";""", "units", "pieces", "backorder", "pending"),
            Changed("JsonTemplate", """public static string JsonTemplate(string id) => $@"{{ ""order"": {id}, ""status"": ""pending"" }}";""", "order", "basket", "pending", "shipped"),
            Control("PostalPattern", """public static string PostalPattern() => @"^\d{5}(?:-\d{4})?\s(?:USA|CAN)\b";"""),
            Changed("DatePattern", """public static string DatePattern() => @"^(?:Mon|Tue|Wed)\s\d{2}/\d{2}/\d{4}\s(?:AM|PM)\b";""", "Tue", "Thu", "PM", "UTC"),
            Changed("WarningBanner", """public static string WarningBanner(string who) => $"WARNING:\t\"{who}\" exceeded the limit\n\tcontact support at once.\n";""", "exceeded the limit", "passed the quota", "contact support", "call the helpdesk"),
            Changed("ConsoleMenu", """public static string ConsoleMenu(string option) => $@"Menu: ""Open"" | ""Close"" | ""{option}"" {{default}} selected on startup";""", "Open", "Create", "selected on startup", "chosen at launch"),
            Changed("QuotedFieldPattern", """public static string QuotedFieldPattern() => @"^""(?:input|output)""\s*=\s*""(?:yes|no)""$";""", "output", "result", "yes", "true"),
            Changed("ConfirmationLine", """public static string ConfirmationLine(string customer, int count) => $"Thanks, \"{customer}\"!\n\tYou ordered {count} items\n\tand we will ship soon.\n";""", "You ordered", "You bought", "will ship soon", "ship at dawn"),
            Control("ReturnNotice", """public static string ReturnNotice(string sku) => $"Return \"{sku}\"\tno restock fee\\n";"""),
            Changed("ErrorTemplate", """public static string ErrorTemplate(string code, string detail) => $@"Error {{{code}}}: ""{detail}"" was rejected by the ""payment gateway"" after retry.";""", "rejected", "refused", "after retry", "on timeout"),
            Changed("IpRangePattern", """public static string IpRangePattern() => @"^(?:10|192)\.(?:\d{1,3}\.){2}\d{1,3}\s(?:private|shared)\b";""", "192", "172", "private", "internal"),
            Changed("AuditEntry", """public static string AuditEntry(string user, string action) => $"[{user}]\t\"{action}\"\n\tlogged by the nightly audit service now.\n";""", "logged by", "recorded by", "now", "today"),
            Changed("CsvRow", """"public static string CsvRow(string a, string b) => $@"""{a}"",""{b}"",""pending approval"",""{{none yet}}""";"""", "pending approval", "approved today", "none yet", "unknown"),
            Changed("HexColorPattern", """public static string HexColorPattern() => @"^#(?:[0-9a-f]{6}|[0-9a-f]{3})\b\s*(?:light|dark)\s+(?:theme|mode)\s+(?:only|first)\b";""", "light", "soft", "first", "last"),
            Control("AliasEntry", """public static string AliasEntry(string name) => $@"alias ""{name}"" maps to {{target}} for ""admins"" only";"""),
            Changed("WindowsPathNote", """public static string WindowsPathNote(string drive) => $"Install under {drive}:\\Program Files\\Contoso\\bin\n\tthen reboot the machine.\n";""", "Program Files", "Program Data", "reboot the machine", "restart the server"),
            Changed("ListFormat", """public static string ListFormat(string first, string last) => $@"Items: ""{first}"" through ""{last}"" in {{sorted}} order, ties broken {{by id}}.";""", "through", "until", "ties broken", "ties resolved"),
            Changed("LogLinePattern", """public static string LogLinePattern() => @"^\[(?:INFO|WARN)\]\s""(?:start|stop)""\s\d+ms$";""", "INFO", "DEBUG", "stop", "halt"),
            Changed("JsonSnippet", """public static string JsonSnippet(string key, string value) => $"{{\"{key}\": \"{value}\"}}\n\twritten by the nightly exporter job.\n";""", "written by", "emitted by", "job", "task"),
            Changed("PathWithBraces", """public static string PathWithBraces(string dir) => $@"C:\Data\""{dir}""\{{backup}}\latest build copy ""snapshot"" only";""", "backup", "archive", "snapshot", "image"),
            Control("FlagPattern", """public static string FlagPattern() => @"^--(?:verbose|quiet)\s*=\s*""(?:on|off)""$";"""),
            Changed("SkuPattern", """public static string SkuPattern() => @"^SKU\d{4}\.(?:red|blue)\.""(?:small|large)""$";""", "red", "green", "large", "huge"),
            Changed("StatusLine", """public static string StatusLine(string state, int pct) => $"State:\t\"{state}\"\n\tProgress: {pct}% complete\n\tEstimated finish tonight.\n";""", "Progress", "Done", "Estimated finish", "Expected end"),
        ]);

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "EscapeLiteralsSlip",
        "Make word edits inside 30 escape-heavy string and regex literals",
        "plans/plan_slip_first_scenarios.md (EscapeLiteralsSlip)",
        Sites);

    private static SlipSite Changed(string name, string seedText, string firstOld, string firstNew, string secondOld, string secondNew) =>
        new(name, seedText, [new SiteChange(firstOld, firstNew), new SiteChange(secondOld, secondNew)], SiteRole.Changed);

    private static SlipSite Control(string name, string seedText) =>
        new(name, seedText, [], SiteRole.Control);
}
