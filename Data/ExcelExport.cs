using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace NetValue.Data;

/// <summary>Produces a standard Office Open XML workbook without an Excel installation.</summary>
public static class ExcelExport
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private sealed record Cell(string? Text = null, decimal? Number = null, string? Formula = null);
    private static Cell Text(string value) => new(Text: value);
    private static Cell Number(decimal value) => new(Number: value);
    private static string Category(AccountKind kind) => kind switch
    {
        AccountKind.Cash => "Cash & bank accounts", AccountKind.Investment => "Investments & retirement",
        AccountKind.Property => "Property", AccountKind.ShortTermDebt => "Short-term liabilities", _ => "Long-term liabilities"
    };

    public static byte[] Create(IReadOnlyList<Profile> profiles)
    {
        var profileRows = new List<Cell[]> { new Cell[] { Text("Profile ID"), Text("Profile name") } };
        var accountRows = new List<Cell[]> { new Cell[] { Text("Profile ID"), Text("Profile"), Text("Account ID"), Text("Account"), Text("Institution"), Text("Category"), Text("Type") } };
        var balanceRows = new List<Cell[]> { new Cell[] { Text("Profile ID"), Text("Profile"), Text("Month"), Text("Account ID"), Text("Account"), Text("Institution"), Text("Category"), Text("Type"), Text("Balance (CAD)") } };
        foreach (var profile in profiles)
        {
            profileRows.Add([Text(profile.Id.ToString()), Text(profile.Name)]);
            foreach (var account in profile.Accounts)
                accountRows.Add([Text(profile.Id.ToString()), Text(profile.Name), Text(account.Id.ToString()), Text(account.Name), Text(account.Institution), Text(Category(account.Kind)), Text(IsDebt(account) ? "Liabilities" : "Assets")]);
            foreach (var (month, balances) in profile.Months.OrderBy(entry => entry.Key))
                foreach (var account in profile.Accounts)
                    if (balances.TryGetValue(account.Id, out var amount))
                        balanceRows.Add([Text(profile.Id.ToString()), Text(profile.Name), Text(month), Text(account.Id.ToString()), Text(account.Name), Text(account.Institution), Text(Category(account.Kind)), Text(IsDebt(account) ? "Liabilities" : "Assets"), Number(amount)]);
        }
        var summaryRows = new List<Cell[]> { new Cell[] { Text("Profile ID"), Text("Profile"), Text("Month"), Text("Assets (CAD)"), Text("Liabilities (CAD)"), Text("Net worth (CAD)"), Text("Cash (CAD)") } };
        var end = Math.Max(2, balanceRows.Count);
        foreach (var profile in profiles)
        {
            foreach (var (month, balances) in profile.Months.OrderBy(entry => entry.Key))
            {
                var row = summaryRows.Count + 1;
                decimal Sum(Func<Account, bool> predicate) => profile.Accounts.Where(predicate).Sum(account => balances.GetValueOrDefault(account.Id));
                var assets = Sum(account => !IsDebt(account));
                var debts = Sum(IsDebt);
                string SumIf(string column, string criterion) => $"SUMIFS(Balances!$I$2:$I${end},Balances!$A$2:$A${end},$A{row},Balances!$C$2:$C${end},$C{row},Balances!${column}$2:${column}${end},\"{criterion}\")";
                summaryRows.Add([Text(profile.Id.ToString()), Text(profile.Name), Text(month), new(Number: assets, Formula: SumIf("H", "Assets")), new(Number: debts, Formula: SumIf("H", "Liabilities")), new(Number: assets - debts, Formula: $"D{row}-E{row}"), new(Number: Sum(account => account.Kind == AccountKind.Cash), Formula: SumIf("G", Category(AccountKind.Cash)))]);
            }
        }
        string[] names = ["Monthly summary", "Profiles", "Accounts", "Balances"];
        List<Cell[]>[] sheets = [summaryRows, profileRows, accountRows, balanceRows];
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            Write(zip, "[Content_Types].xml", new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                names.Select((_, index) => new XElement(types + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{index + 1}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
            Write(zip, "_rels/.rels", Relationships(("rId1", "officeDocument", "xl/workbook.xml")));
            Write(zip, "xl/workbook.xml", new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Rel),
                new XElement(Main + "sheets", names.Select((name, index) => new XElement(Main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", index + 1), new XAttribute(Rel + "id", $"rId{index + 1}")))),
                new XElement(Main + "calcPr", new XAttribute("calcId", "191029"), new XAttribute("fullCalcOnLoad", "1"))));
            Write(zip, "xl/_rels/workbook.xml.rels", Relationships(names.Select((_, index) => ($"rId{index + 1}", "worksheet", $"worksheets/sheet{index + 1}.xml")).Append(("rId5", "styles", "styles.xml")).ToArray()));
            Write(zip, "xl/styles.xml", Styles());
            for (var index = 0; index < sheets.Length; index++) Write(zip, $"xl/worksheets/sheet{index + 1}.xml", Sheet(sheets[index]));
        }
        return output.ToArray();
    }

    private static bool IsDebt(Account account) => account.Kind is AccountKind.ShortTermDebt or AccountKind.LongTermDebt;
    private static XElement Relationships(params (string Id, string Type, string Target)[] links)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        return new XElement(ns + "Relationships", links.Select(link => new XElement(ns + "Relationship", new XAttribute("Id", link.Id), new XAttribute("Type", Rel.NamespaceName + "/" + link.Type), new XAttribute("Target", link.Target))));
    }
    private static void Write(ZipArchive zip, string path, XElement root)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(stream);
    }
    private static XElement Sheet(List<Cell[]> rows) => new(Main + "worksheet",
        new XElement(Main + "dimension", new XAttribute("ref", $"A1:{Column(rows[0].Length)}{rows.Count}")),
        new XElement(Main + "sheetViews", new XElement(Main + "sheetView", new XAttribute("workbookViewId", "0"), new XElement(Main + "pane", new XAttribute("ySplit", "1"), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
        new XElement(Main + "cols", Enumerable.Range(1, rows[0].Length).Select(column => new XElement(Main + "col", new XAttribute("min", column), new XAttribute("max", column), new XAttribute("width", Width(rows, column - 1)), new XAttribute("customWidth", "1")))),
        new XElement(Main + "sheetData", rows.Select((cells, row) => new XElement(Main + "row", new XAttribute("r", row + 1), new XAttribute("ht", row == 0 ? "28" : "22"), new XAttribute("customHeight", "1"), cells.Select((cell, column) => CellXml(cell, row, column))))),
        new XElement(Main + "autoFilter", new XAttribute("ref", $"A1:{Column(rows[0].Length)}{rows.Count}")));
    private static int Width(List<Cell[]> rows, int column) => Math.Clamp(rows.Max(row => row[column].Text?.Length ?? 18) + 3, 20, 65);
    private static string Column(int column) => ((char)('A' + column - 1)).ToString();
    private static XElement CellXml(Cell cell, int row, int column)
    {
        var element = new XElement(Main + "c", new XAttribute("r", $"{Column(column + 1)}{row + 1}"), new XAttribute("s", row == 0 ? "1" : cell.Number.HasValue ? "2" : "0"));
        if (cell.Text is not null) element.Add(new XAttribute("t", "inlineStr"), new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), cell.Text)));
        else
        {
            if (cell.Formula is not null) element.Add(new XElement(Main + "f", cell.Formula));
            element.Add(new XElement(Main + "v", cell.Number!.Value.ToString(CultureInfo.InvariantCulture)));
        }
        return element;
    }
    private static XElement Styles() => XElement.Parse("""
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00;[Red]-&quot;$&quot;#,##0.00;&quot;$&quot;0.00" /></numFmts>
          <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF254C3E"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """);
}
