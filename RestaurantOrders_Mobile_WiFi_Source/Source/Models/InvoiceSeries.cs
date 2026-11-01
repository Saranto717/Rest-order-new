namespace RestaurantOrders.Models;

/// <summary>
/// Σειρά παραστατικών (π.χ. "Α" για αποδείξεις, "ΤΙΜ" για τιμολόγια) με συνεχόμενη,
/// αύξουσα αρίθμηση χωρίς κενά - όπως απαιτεί ο νόμος.
/// </summary>
public class InvoiceSeries
{
    public int Id { get; set; }
    public string SeriesName { get; set; } = "";
    public FiscalDocumentKind Kind { get; set; }
    public int NextNumber { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
