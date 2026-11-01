namespace RestaurantOrders.Models;

/// <summary>
/// Επιλογή "Extra" (π.χ. γλυκός/μέτριος/σκέτος, χωρίς ζάχαρη, με πάγο κ.λπ.).
/// Αν MenuCategoryId είναι null, εμφανίζεται για όλες τις κατηγορίες.
/// </summary>
public class ExtraOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; } = 0; // επιπλέον χρέωση (0 αν δεν κοστίζει τίποτα)

    public int? MenuCategoryId { get; set; }
    public MenuCategory? MenuCategory { get; set; }
}
