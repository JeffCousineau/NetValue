using NetValue.Data;
using NetValue.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var cash = new Account { Name = "Cash", Kind = AccountKind.Cash };
var home = new Account { Name = "Home", Kind = AccountKind.Property };
var mortgage = new Account { Name = "Mortgage", Kind = AccountKind.LongTermDebt };
var profile = new Profile { Name = "One", Accounts = [cash, home, mortgage] };
profile.Months["2026-04"] = new() { [cash.Id] = 300, [home.Id] = 1000, [mortgage.Id] = 800 };
profile.Months["2026-01"] = new() { [cash.Id] = 100, [home.Id] = 1000, [mortgage.Id] = 1200 };
profile.Months["2026-02"] = new() { [cash.Id] = 200, [home.Id] = 1000, [mortgage.Id] = 1100 };
profile.Months["2026-05"] = new() { [cash.Id] = 50 };
profile.Months["2026-06"] = [];
profile.Months["invalid"] = new() { [cash.Id] = 999 };
var rows = MonthlyProgress.For(profile);
Check(rows.Count == 4 && rows[0].Month.Month == 1, "History must sort valid recorded months and omit empty months.");
Check(rows[0].Assets == 1100 && rows[0].Liabilities == 1200 && rows[0].NetWorth == -100, "Property and mortgage must count exactly once.");
Check(MonthlyProgress.Change(rows[1], rows[0]) == 200, "Consecutive monthly change must be calculated.");
Check(MonthlyProgress.Change(rows[2], rows[1]) is null, "Gaps must not produce a monthly change.");
Check(MonthlyProgress.Change(rows[0], null) is null, "First month must not show a change.");
Check(MonthlyProgress.Percent(200, -100) == 200, "Negative starting net worth must use its absolute value.");
Check(MonthlyProgress.Percent(100, 0) is null, "Zero starting values must have no percentage.");
Check(rows[^1].EnteredAccounts == 1 && rows[^1].TotalAccounts == 3 && rows[^1].Assets == 50, "Partial months must count only recorded balances.");
var second = new Profile { Name = "Two", Accounts = [cash] };
second.Months["2026-01"] = new() { [cash.Id] = 5000 };
Check(MonthlyProgress.For(second)[0].NetWorth == 5000 && MonthlyProgress.For(profile)[0].NetWorth == -100, "Profiles must have independent history.");
var household = MonthlyProgress.ForHousehold([profile, second]);
Check(household.Count == 4 && household[0].NetWorth == 4900 && household[0].Assets == 6100 && household[0].Liabilities == 1200, "Household must combine each month's assets and debts across profiles.");
Check(household[1].NetWorth == 100 && household[1].EnteredAccounts == 3 && household[1].TotalAccounts == 4, "Missing profile months must be partial and must not carry forward balances.");
Check(MonthlyProgress.Change(household[2], household[1]) is null, "Household gaps must not show a monthly change.");
Check(MonthlyProgress.ForHousehold([]).Count == 0, "An empty household must have no history.");
Console.WriteLine("All monthly progress checks passed.");

using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
async Task<string> Render(Profile? value) => await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<ProgressView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Profile"] = value }));
    return output.ToHtmlString();
});
var historyHtml = await Render(profile);
Check(historyHtml.Contains("polyline") && historyHtml.Contains("Monthly history") && historyHtml.Contains("Partial"), "Multi-month view must render chart, table, and partial status.");
var singleHtml = await Render(second);
Check(singleHtml.Contains("circle") && !singleHtml.Contains("polyline") && singleHtml.Contains("Two"), "Single-month profile must render its own point without an invalid chart line.");
Check((await Render(new Profile { Name = "Empty" })).Contains("first month"), "Empty profile must explain how to start.");
Check((await Render(null)).Contains("Add a profile"), "An empty household must explain how to add a profile.");
var householdHtml = await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<ProgressView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["HouseholdProfiles"] = new[] { profile, second } }));
    return output.ToHtmlString();
});
Check(householdHtml.Contains("Household") && householdHtml.Contains("$4,900.00") && householdHtml.Contains("Partial"), "Household progress must render combined history and partial months.");
Console.WriteLine("All progress rendering checks passed.");

async Task<string> RenderTotal(IEnumerable<Profile> values, string month, MetricTotal.TotalKind kind) => await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<MetricTotal>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Profiles"] = values, ["Month"] = month, ["Kind"] = kind
    }));
    return output.ToHtmlString();
});
Check((await RenderTotal([profile], "2026-01", MetricTotal.TotalKind.Assets)).Contains("$1,100.00"), "Assets total must use the requested month's assets only.");
Check((await RenderTotal([profile], "2026-01", MetricTotal.TotalKind.Liabilities)).Contains("$1,200.00"), "Liabilities total must use only debt balances.");
Check((await RenderTotal([profile], "2026-01", MetricTotal.TotalKind.NetWorth)).Contains("-$100.00"), "Net worth must subtract debt even when the result is negative.");
Check((await RenderTotal([profile, second], "2026-01", MetricTotal.TotalKind.NetWorth)).Contains("$4,900.00"), "Household breakdown must combine the selected profiles.");
Check((await RenderTotal([profile], "2026-03", MetricTotal.TotalKind.NetWorth)).Contains("$0.00"), "Missing months must count as zero.");
Console.WriteLine("All metric total rendering checks passed.");

var zero = new Account { Name = "=SUM(A1:A2) & <test>", Kind = AccountKind.Cash };
second.Accounts.Add(zero);
second.Months["2026-01"][zero.Id] = 0;
var excel = ExcelExport.Create([profile, second, new Profile { Name = "No balances" }]);
using var archive = new ZipArchive(new MemoryStream(excel), ZipArchiveMode.Read);
XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
XDocument ReadPart(string name)
{
    using var stream = archive.GetEntry(name)!.Open();
    return XDocument.Load(stream);
}
Check(ReadPart("xl/workbook.xml").Descendants(spreadsheet + "sheet").Count() == 4, "Export must include all four worksheets.");
Check(ReadPart("xl/worksheets/sheet2.xml").Descendants(spreadsheet + "row").Count() == 4, "Profiles without balances must still be exported.");
var exportedBalances = ReadPart("xl/worksheets/sheet4.xml");
Check(exportedBalances.Descendants(spreadsheet + "c").Any(cell => cell.Element(spreadsheet + "v")?.Value == "0"), "Entered zero balances must be exported as numbers.");
Check(exportedBalances.Descendants(spreadsheet + "t").Any(text => text.Value == zero.Name), "Account names must remain escaped literal text, including formula-like names.");
var summary = ReadPart("xl/worksheets/sheet1.xml");
Check(summary.Descendants(spreadsheet + "c").First(cell => cell.Attribute("r")?.Value == "F2").Element(spreadsheet + "v")?.Value == "-100", "Cached exported net worth must match app calculations.");
Check(summary.Descendants(spreadsheet + "f").Any(formula => formula.Value.StartsWith("SUMIFS(")), "Export totals must contain formulas linked to balances.");
Check(ReadPart("xl/styles.xml").Descendants(spreadsheet + "numFmt").Any(format => format.Attribute("formatCode")!.Value.Contains("0.00")), "Workbook must format zero with two decimal places.");
Check(ExcelExport.Create([]).Length > 0, "Empty data must export a valid header-only workbook.");
Console.WriteLine("All Excel export checks passed.");

second.Accounts[0].Institution = "Desjardins";
var backupBytes = PortfolioBackup.Create([second]);
var restoredProfiles = PortfolioBackup.Read(backupBytes);
Check(JsonSerializer.Serialize(restoredProfiles) == JsonSerializer.Serialize(new[] { second }), "Backup round trip must preserve all IDs, accounts, institutions, months, and zero balances.");
Check(PortfolioBackup.Read(JsonSerializer.SerializeToUtf8Bytes(new[] { second }))[0].Id == second.Id, "Legacy portfolio.json must be accepted.");
Check(PortfolioBackup.Read(PortfolioBackup.Create([])).Count == 0, "Empty backups must restore as an empty portfolio.");
void RejectBackup(byte[] invalid)
{
    try { PortfolioBackup.Read(invalid); throw new Exception("An invalid backup was accepted."); }
    catch (InvalidDataException) { }
}
RejectBackup(Encoding.UTF8.GetBytes("not json"));
RejectBackup(Encoding.UTF8.GetBytes("[{\"Name\":\"Missing IDs\"}]"));
RejectBackup(Encoding.UTF8.GetBytes("[null]"));
RejectBackup(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(backupBytes).Replace("\"Version\": 1", "\"Version\": 2")));
var invalidProfile = new Profile { Name = "Invalid", Accounts = [new Account { Name = "Invalid", Kind = (AccountKind)999 }] };
RejectBackup(PortfolioBackup.Create([invalidProfile]));
var negative = PortfolioBackup.Read(backupBytes);
negative[0].Months["2026-01"][cash.Id] = -1;
RejectBackup(PortfolioBackup.Create(negative));
var duplicate = PortfolioBackup.Read(backupBytes);
duplicate.Add(duplicate[0]);
RejectBackup(PortfolioBackup.Create(duplicate));
var testRoot = Path.Combine(Directory.GetCurrentDirectory(), "obj", "NetValue-backup-check-" + Guid.NewGuid().ToString("N"));
var testStore = new PortfolioStore(new StoreEnvironment(testRoot));
var original = new Profile { Name = "Original" };
testStore.Save([original]);
testStore.Restore(restoredProfiles);
Check(testStore.Load()[0].Id == second.Id, "Restore must update the persistent store.");
var recoveryFile = Directory.GetFiles(Path.Combine(testRoot, "App_Data", "backups")).Single();
Check(PortfolioBackup.Read(File.ReadAllBytes(recoveryFile))[0].Id == original.Id, "Restore must preserve the previous data in a restorable recovery copy.");
Console.WriteLine("All backup validation and recovery checks passed.");
HouseholdSecurityChecks.Run();
DatabaseChecks.Run();
