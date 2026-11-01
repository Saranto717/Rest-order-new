namespace RestaurantOrders.Models;

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int MenuItemId { get; set; }

    // Η συγκεκριμένη αποστολή/παραγγελία στην οποία ανήκει το είδος.
    public int? OrderPrintBatchId { get; set; }
    public OrderPrintBatch? OrderPrintBatch { get; set; }
    public MenuItem? MenuItem { get; set; }

    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
    public decimal ExtrasPrice { get; set; } = 0; // άθροισμα τιμών επιλεγμένων extra, ανά τεμάχιο

    // Γίνεται true μόλις σταλεί/τυπωθεί στον αντίστοιχο εκτυπωτή
    public bool IsPrinted { get; set; } = false;
    public DateTime? PrintedAt { get; set; }

    // Audit-safe cancellation. We never delete a sent fiscal item from history.
    public bool IsCancelled { get; set; } = false;
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}
