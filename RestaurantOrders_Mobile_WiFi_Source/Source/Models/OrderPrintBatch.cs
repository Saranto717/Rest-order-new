namespace RestaurantOrders.Models;

public class OrderPrintBatch
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    // Αύξων αριθμός παραγγελίας για όλα τα τραπέζια μέσα στο τρέχον ταμειακό/ημερήσιο κύκλο.
    public int Number { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<OrderItem> Items { get; set; } = new();
}
