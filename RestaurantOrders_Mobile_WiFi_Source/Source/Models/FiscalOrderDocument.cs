namespace RestaurantOrders.Models;

/// <summary>
/// Ένα φορολογικό "Δελτίο Παραγγελίας" (Order Form) που εκδόθηκε στην ΑΑΔΕ μέσω Parochos
/// τη στιγμή που ο σερβιτόρος έστειλε είδη στην κουζίνα/μπαρ. Η τελική Απόδειξη/Τιμολόγιο
/// στο κλείσιμο του τραπεζιού τα συσχετίζει (correlatedInvoices) όλα μαζί.
/// </summary>
public class FiscalOrderDocument
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public string ExternalSystemId { get; set; } = "";
    public string SeriesName { get; set; } = "";
    public long Number { get; set; }
    public long? Mark { get; set; }
    public string? ProcessId { get; set; }
    public string? Uid { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.Now;

    // When Parochos cancels the open Order Tickets, keep the original
    // Order Form rows as historical evidence instead of deleting them.
    public bool IsCancelled { get; set; } = false;
    public DateTime? CancelledAt { get; set; }
    public long? CancellationMark { get; set; }

    public List<FiscalOrderDocumentItem> Items { get; set; } = new();
}
