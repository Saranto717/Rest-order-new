namespace RestaurantOrders.Models;

/// <summary>Audit record for a Parochos CancelConnected operation on Order Forms.</summary>
public class FiscalOrderCancellation
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string ExternalSystemId { get; set; } = "";
    public string? ProcessId { get; set; }
    public long? Mark { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Reason { get; set; }
}
