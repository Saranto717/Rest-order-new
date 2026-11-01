namespace RestaurantOrders.Models;

public class MenuItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;

    public int? MenuCategoryId { get; set; }
    public MenuCategory? MenuCategory { get; set; }

    // Προαιρετική παράκαμψη ΦΠΑ - αν είναι null, χρησιμοποιείται το ΦΠΑ της κατηγορίας.
    public decimal? VatPercentOverride { get; set; }
    public string? VatCategoryCodeOverride { get; set; }
}
