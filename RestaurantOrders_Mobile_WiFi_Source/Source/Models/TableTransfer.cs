namespace RestaurantOrders.Models;

public enum TableTransferStage { Prepared = 0, Submitted = 1, ProviderConfirmed = 2, Completed = 3, Rejected = 4 }

/// <summary>Durable journal for a whole-table move/merge, including uncertain provider outcomes.</summary>
public class TableTransfer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SourceOrderId { get; set; }
    public int? TargetOrderId { get; set; }
    public int SourceTableId { get; set; }
    public int TargetTableId { get; set; }
    public int RequestedByUserId { get; set; }
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string AccountFingerprint { get; set; } = "";
    public bool ProviderRequired { get; set; }
    public string SourceMarksJson { get; set; } = "[]";
    public string SourceItemIdsJson { get; set; } = "[]";
    public string TargetMarksJson { get; set; } = "[]";
    public decimal SourceBalance { get; set; }
    public decimal TargetBalance { get; set; }
    public TableTransferStage Stage { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
