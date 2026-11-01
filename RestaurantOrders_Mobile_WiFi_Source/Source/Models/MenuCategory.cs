namespace RestaurantOrders.Models;

public class MenuCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";   // π.χ. "Κυρίως", "Ποτά", "Επιδόρπια"

    // Ανενεργές κατηγορίες παραμένουν στη βάση, αλλά δεν εμφανίζονται στη λήψη παραγγελίας.
    public bool IsActive { get; set; } = true;

    // Legacy single-printer link retained for compatibility with existing restaurant.db files.
    // New assignments are stored in PrinterStations and support any number of printers.
    public int? PrinterStationId { get; set; }
    public PrinterStation? PrinterStation { get; set; }

    public ICollection<PrinterStation> PrinterStations { get; set; } = new List<PrinterStation>();

    // Φορολογικά στοιχεία για ηλεκτρονική τιμολόγηση (myDATA/Parochos)
    public decimal VatPercent { get; set; } = 24;      // π.χ. 13 για φαγητό, 24 για ποτά - ΕΠΙΒΕΒΑΙΩΣΕ ΜΕ ΛΟΓΙΣΤΗ
	public string VatCategoryCode { get; set; } = "1";

    // Χαρακτηρισμός εσόδων myDATA που κληρονομούν όλα τα είδη της κατηγορίας.
    // category1_1 = Εμπορεύματα, category1_2 = Προϊόντα, category1_3 = Υπηρεσίες.
    public string IncomeClassificationCategory { get; set; } = "category1_1";
}
