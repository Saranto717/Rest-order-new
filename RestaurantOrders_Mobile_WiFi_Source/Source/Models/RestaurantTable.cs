namespace RestaurantOrders.Models;

public class RestaurantTable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; } = 0;

    public int? TableAreaId { get; set; }
    public TableArea? TableArea { get; set; }

    // Θέση στην ελεύθερη κάτοψη (ποσοστό 0-100 του πλάτους/ύψους του καμβά). Null = δεν έχει τοποθετηθεί ακόμα.
    public double? PosX { get; set; }
    public double? PosY { get; set; }
}
