using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public class EmployeeSummary
{
    public int WaiterUserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public decimal CashTotal { get; set; }
    public decimal CardTotal { get; set; }
    public int OrderCount { get; set; }
    public decimal GrandTotal => CashTotal + CardTotal;
}

/// <summary>Ένα μεμονωμένο, ολοκληρωμένο "Κλείσιμο Ημέρας" - με τη δική του ώρα και ανάλυση ανά υπάλληλο.</summary>
public class DayCloseRecord
{
    public int Id { get; set; }
    public DateTime ClosedAt { get; set; }
    public string ClosedByName { get; set; } = "";
    public List<EmployeeSummary> Employees { get; set; } = new();
    public decimal GrandTotal => Employees.Sum(e => e.GrandTotal);
}

public static class CashierService
{
    static CashierService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static decimal OrderTotal(Order o) => o.Items.Sum(i => i.Quantity * ((i.MenuItem?.Price ?? 0) + i.ExtrasPrice));

    private static List<EmployeeSummary> Summarize(List<Order> orders)
    {
        return orders
            .GroupBy(o => new { o.WaiterUserId, Name = o.WaiterUser?.FullName ?? "(άγνωστος)" })
            .Select(g => new EmployeeSummary
            {
                WaiterUserId = g.Key.WaiterUserId,
                EmployeeName = g.Key.Name,
                CashTotal = g.Where(o => o.PaymentMethod == PaymentMethod.Cash).Sum(OrderTotal),
                CardTotal = g.Where(o => o.PaymentMethod == PaymentMethod.Card).Sum(OrderTotal),
                OrderCount = g.Count()
            })
            .OrderByDescending(x => x.GrandTotal)
            .ToList();
    }

    /// <summary>
    /// "Τρέχον ταμείο": κλεισμένες παραγγελίες που ΔΕΝ έχουν μπει ακόμα σε κανένα
    /// "Κλείσιμο Ημέρας". Μηδενίζεται (αδειάζει) κάθε φορά που γίνεται κλείσιμο.
    /// </summary>
    public static async Task<List<EmployeeSummary>> GetCurrentSummary(AppDbContext db)
    {
        var orders = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .Include(o => o.WaiterUser)
            .ToListAsync();

        return Summarize(orders);
    }

    /// <summary>
    /// Όλα τα ξεχωριστά "Κλεισίματα Ημέρας" που έγιναν μια συγκεκριμένη ημερομηνία —
    /// το καθένα με τη δική του ανάλυση, ΧΩΡΙΣ να αθροίζονται μεταξύ τους.
    /// </summary>
    public static async Task<List<DayCloseRecord>> GetClosingsForDate(AppDbContext db, DateTime date)
    {
        var fromInclusive = date.Date;
        var toExclusive = date.Date.AddDays(1);

        var closes = await db.DayCloses
            .Where(dc => dc.ClosedAt >= fromInclusive && dc.ClosedAt < toExclusive)
            .Include(dc => dc.ClosedByUser)
            .OrderBy(dc => dc.ClosedAt)
            .ToListAsync();

        var result = new List<DayCloseRecord>();
        foreach (var dc in closes)
        {
            var orders = await db.Orders
                .Where(o => o.DayCloseId == dc.Id)
                .Include(o => o.Items).ThenInclude(i => i.MenuItem)
                .Include(o => o.WaiterUser)
                .ToListAsync();

            result.Add(new DayCloseRecord
            {
                Id = dc.Id,
                ClosedAt = dc.ClosedAt,
                ClosedByName = dc.ClosedByUser?.FullName ?? "-",
                Employees = Summarize(orders)
            });
        }

        return result;
    }

    /// <summary>Φέρνει ένα συγκεκριμένο ιστορικό κλείσιμο (για εκτύπωση/PDF).</summary>
    public static async Task<DayCloseRecord?> GetClosingById(AppDbContext db, int dayCloseId)
    {
        var dc = await db.DayCloses.Include(x => x.ClosedByUser).FirstOrDefaultAsync(x => x.Id == dayCloseId);
        if (dc is null) return null;

        var orders = await db.Orders
            .Where(o => o.DayCloseId == dc.Id)
            .Include(o => o.Items).ThenInclude(i => i.MenuItem)
            .Include(o => o.WaiterUser)
            .ToListAsync();

        return new DayCloseRecord
        {
            Id = dc.Id,
            ClosedAt = dc.ClosedAt,
            ClosedByName = dc.ClosedByUser?.FullName ?? "-",
            Employees = Summarize(orders)
        };
    }

    /// <summary>
    /// Εκτελεί ένα νέο "Κλείσιμο Ημέρας": παγώνει το τρέχον ταμείο σε ένα μόνιμο,
    /// ξεχωριστό στιγμιότυπο και αδειάζει το τρέχον ταμείο για μετά.
    /// </summary>
    public static async Task<DayCloseRecord> PerformDayClose(AppDbContext db, int closedByUserId)
    {
        var dayClose = new DayClose { ClosedAt = DateTime.Now, ClosedByUserId = closedByUserId };
        db.DayCloses.Add(dayClose);
        await db.SaveChangesAsync();

        var uncounted = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null)
            .ToListAsync();

        foreach (var o in uncounted)
        {
            o.DayCloseId = dayClose.Id;
        }
        await db.SaveChangesAsync();

        return (await GetClosingById(db, dayClose.Id))!;
    }

    /// <summary>
    /// Εκτελεί κλείσιμο ταμείου για ΕΝΑΝ ΜΟΝΟ υπάλληλο (π.χ. σχόλασε νωρίτερα) - παγώνει
    /// μόνο τις δικές του εκκρεμείς παραγγελίες σε ξεχωριστό στιγμιότυπο, αφήνοντας τους
    /// υπόλοιπους υπαλλήλους ανεπηρέαστους στο "Τρέχον Ταμείο".
    /// </summary>
    public static async Task<DayCloseRecord> PerformEmployeeClose(AppDbContext db, int waiterUserId, int closedByUserId)
    {
        var dayClose = new DayClose { ClosedAt = DateTime.Now, ClosedByUserId = closedByUserId };
        db.DayCloses.Add(dayClose);
        await db.SaveChangesAsync();

        var uncounted = await db.Orders
            .Where(o => o.Status == OrderStatus.Closed && o.DayCloseId == null && o.WaiterUserId == waiterUserId)
            .ToListAsync();

        foreach (var o in uncounted)
        {
            o.DayCloseId = dayClose.Id;
        }
        await db.SaveChangesAsync();

        return (await GetClosingById(db, dayClose.Id))!;
    }

    /// <summary>Όμορφα δομημένο PDF για ένα συγκεκριμένο κλείσιμο.</summary>
    public static byte[] BuildDayClosePdf(DayCloseRecord record)
    {
        var rows = record.Employees;
        var cashTotal = rows.Sum(r => r.CashTotal);
        var cardTotal = rows.Sum(r => r.CardTotal);
        var grandTotal = rows.Sum(r => r.GrandTotal);
        var orderCount = rows.Sum(r => r.OrderCount);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(11));

                page.Header().Column(col =>
                {
                    var title = rows.Count == 1 ? $"Κλείσιμο Ταμείου - {rows[0].EmployeeName}" : "Κλείσιμο Ταμείου";
                    col.Item().Text(title).FontSize(20).Bold();
                    col.Item().Text($"Ημερομηνία/Ώρα: {record.ClosedAt:dd/MM/yyyy HH:mm}").FontSize(12);
                    col.Item().Text($"Έγινε από: {record.ClosedByName}").FontSize(12);
                    col.Item().PaddingTop(10).LineHorizontal(1);
                });

                page.Content().PaddingTop(15).Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.RelativeColumn(1.5f);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("Υπάλληλος").Bold();
                            header.Cell().AlignRight().Text("Μετρητά").Bold();
                            header.Cell().AlignRight().Text("Κάρτα").Bold();
                            header.Cell().AlignRight().Text("Σύνολο").Bold();
                            header.Cell().AlignRight().Text("Παραγγ.").Bold();
                        });

                        foreach (var row in rows)
                        {
                            table.Cell().Text(row.EmployeeName);
                            table.Cell().AlignRight().Text($"€{row.CashTotal:0.00}");
                            table.Cell().AlignRight().Text($"€{row.CardTotal:0.00}");
                            table.Cell().AlignRight().Text($"€{row.GrandTotal:0.00}");
                            table.Cell().AlignRight().Text(row.OrderCount.ToString());
                        }
                    });

                    col.Item().PaddingTop(20).LineHorizontal(1);

                    col.Item().PaddingTop(10).Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Μετρητών:");
                        row.RelativeItem().AlignRight().Text($"€{cashTotal:0.00}").Bold();
                    });
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Καρτών:");
                        row.RelativeItem().AlignRight().Text($"€{cardTotal:0.00}").Bold();
                    });
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Σύνολο Παραγγελιών:");
                        row.RelativeItem().AlignRight().Text(orderCount.ToString()).Bold();
                    });
                    col.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem().Text("ΓΕΝΙΚΟ ΣΥΝΟΛΟ:").FontSize(14).Bold();
                        row.RelativeItem().AlignRight().Text($"€{grandTotal:0.00}").FontSize(14).Bold();
                    });
                });

                page.Footer().AlignCenter().Text($"Εκτυπώθηκε: {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(9);
            });
        });

        return doc.GeneratePdf();
    }
}
