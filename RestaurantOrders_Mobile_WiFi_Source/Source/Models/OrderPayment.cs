namespace RestaurantOrders.Models;

/// <summary>
/// Μία πραγματική πληρωμή ενός Order. Ένα Order μπορεί να έχει πολλές πληρωμές
/// (π.χ. 4 άτομα που πληρώνουν από €3).
/// </summary>
public class OrderPayment
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.None;
    public FiscalDocumentKind DocumentKind { get; set; } = FiscalDocumentKind.Receipt;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public FiscalStatus FiscalStatus { get; set; } = FiscalStatus.NotIssued;

    public string? ExternalSystemId { get; set; }
    public string? ProcessId { get; set; }
    public long? Mark { get; set; }
    public string? Uid { get; set; }
    public string? QrCode { get; set; }
    public string? AuthenticationCode { get; set; }
    public string? PdfUrl { get; set; }
    public string? SeriesName { get; set; }
    public long? InvoiceNumber { get; set; }
    public string? FiscalErrorMessage { get; set; }

    public string? CardTransactionId { get; set; }
    public string? CardTerminalId { get; set; }
    public string? CardNspCode { get; set; }
    public string? PaymentSignature { get; set; }
    public string? PaymentUid { get; set; }

    public List<OrderPaymentItem> Items { get; set; } = new();
}
