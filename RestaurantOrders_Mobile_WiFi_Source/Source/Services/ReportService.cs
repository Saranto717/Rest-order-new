using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;

namespace RestaurantOrders.Services;

public class ReportRow
{
    public string Category { get; set; } = "";
    public string ItemName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Revenue { get; set; }
}

public static class ReportService
{
    /// <summary>
    /// Πωλήσεις (ανά κατηγορία + είδος) για είδη που έχουν πράγματι σταλεί/επιβεβαιωθεί
    /// (IsPrinted) μέσα στο δοθέν διάστημα ημερομηνιών (συμπεριλαμβανομένων και των δύο άκρων).
    /// </summary>
    public static async Task<List<ReportRow>> GetSalesReport(AppDbContext db, DateTime fromDate, DateTime toDate)
    {
        var fromInclusive = fromDate.Date;
        var toExclusive = toDate.Date.AddDays(1);

        var orderItems = await db.OrderItems
            .Where(oi => oi.IsPrinted && oi.PrintedAt != null && oi.PrintedAt >= fromInclusive && oi.PrintedAt < toExclusive)
            .Include(oi => oi.MenuItem).ThenInclude(mi => mi!.MenuCategory)
            .ToListAsync();

        var rows = orderItems
            .GroupBy(oi => new
            {
                Category = oi.MenuItem?.MenuCategory?.Name ?? "Χωρίς κατηγορία",
                ItemName = oi.MenuItem?.Name ?? "(διαγραμμένο είδος)"
            })
            .Select(g => new ReportRow
            {
                Category = g.Key.Category,
                ItemName = g.Key.ItemName,
                Quantity = g.Sum(oi => oi.Quantity),
                Revenue = g.Sum(oi => oi.Quantity * ((oi.MenuItem?.Price ?? 0) + oi.ExtrasPrice))
            })
            .OrderBy(r => r.Category).ThenByDescending(r => r.Quantity)
            .ToList();

        return rows;
    }

    /// <summary>Φτιάχνει ένα .xlsx αρχείο (byte array) από τις γραμμές αναφοράς.</summary>
    public static byte[] BuildExcel(List<ReportRow> rows, DateTime fromDate, DateTime toDate)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Αναφορά Πωλήσεων");

        sheet.Cell(1, 1).Value = "Αναφορά Πωλήσεων";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        sheet.Cell(2, 1).Value = $"Περίοδος: {fromDate:dd/MM/yyyy} - {toDate:dd/MM/yyyy}";

        var headerRow = 4;
        sheet.Cell(headerRow, 1).Value = "Κατηγορία";
        sheet.Cell(headerRow, 2).Value = "Είδος";
        sheet.Cell(headerRow, 3).Value = "Ποσότητα";
        sheet.Cell(headerRow, 4).Value = "Έσοδα (€)";
        var headerRange = sheet.Range(headerRow, 1, headerRow, 4);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F2A44");
        headerRange.Style.Font.FontColor = XLColor.White;

        var r = headerRow + 1;
        foreach (var row in rows)
        {
            sheet.Cell(r, 1).Value = row.Category;
            sheet.Cell(r, 2).Value = row.ItemName;
            sheet.Cell(r, 3).Value = row.Quantity;
            sheet.Cell(r, 4).Value = row.Revenue;
            sheet.Cell(r, 4).Style.NumberFormat.Format = "0.00";
            r++;
        }

        var totalRow = r + 1;
        sheet.Cell(totalRow, 2).Value = "Σύνολο:";
        sheet.Cell(totalRow, 2).Style.Font.Bold = true;
        sheet.Cell(totalRow, 3).Value = rows.Sum(x => x.Quantity);
        sheet.Cell(totalRow, 3).Style.Font.Bold = true;
        sheet.Cell(totalRow, 4).Value = rows.Sum(x => x.Revenue);
        sheet.Cell(totalRow, 4).Style.NumberFormat.Format = "0.00";
        sheet.Cell(totalRow, 4).Style.Font.Bold = true;

        sheet.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
