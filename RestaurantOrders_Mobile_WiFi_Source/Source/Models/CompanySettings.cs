namespace RestaurantOrders.Models;

/// <summary>
/// Στοιχεία επιχείρησης (εκδότη) και σύνδεσης με τον πάροχο ηλεκτρονικής τιμολόγησης
/// Parochos. Μία μόνο γραμμή σε αυτόν τον πίνακα.
/// </summary>
public class CompanySettings
{
    public int Id { get; set; }

    // Στοιχεία επιχείρησης (εκδότης παραστατικού)
    public string VatNumber { get; set; } = "";
    public int Branch { get; set; } = 0;
    public string CompanyName { get; set; } = "";
    public string Street { get; set; } = "";
    public string StreetNumber { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "GR";
    public string ContactPhone { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string TaxOffice { get; set; } = "";
    public string MainActivity { get; set; } = "";

    // Όταν είναι false, η εφαρμογή λειτουργεί μόνο ως POS/διαχείριση παραγγελιών χωρίς φορολογική έκδοση μέσω παρόχου.
    public bool UseParochosProvider { get; set; } = true;

    // Σύνδεση με Parochos
    public string ParochosSubscriptionKey { get; set; } = "";   // "API key"
    public string ParochosUsername { get; set; } = "";          // email σύνδεσης στον Parochos
    public string ParochosPassword { get; set; } = "";
    public bool ParochosUseProduction { get; set; } = false;    // false = δοκιμαστικό (beta) περιβάλλον
    // API v3: το identifier του /api/send είναι υποχρεωτικά "Parochos".
    // Το παλιό UI field διατηρείται για συμβατότητα με την παλιά έκδοση της εφαρμογής,
    // αλλά δεν χρησιμοποιείται πλέον για το API request.
    public string? ParochosTemplateIdentifier { get; set; }

    // Μεταφορά παραστατικού: 1 = μετάδοση + PDF, 5 = αυτόματη μετάδοση χωρίς PDF.
    public int ParochosTransmissionType { get; set; } = 1;

    // EFT-POS στοιχεία που απαιτούνται από requestPayment.
    public string ParochosCardTerminalId { get; set; } = "";
    public string ParochosNspCode { get; set; } = "";
    public int ParochosSpecialInvoiceCategory { get; set; } = 12;

    // Polling /get.
    public int ParochosPollingAttempts { get; set; } = 40;
    public int ParochosPollingDelayMs { get; set; } = 750;

    // Προεπιλεγμένοι κωδικοί ΑΑΔΕ/Parochos - ΕΠΙΒΕΒΑΙΩΣΕ ΜΕ ΤΟΝ ΛΟΓΙΣΤΗ Ή ΤΟΝ PAROCHOS ΠΡΙΝ ΤΗΝ ΠΑΡΑΓΩΓΙΚΗ ΧΡΗΣΗ
    public string ReceiptInvoiceType { get; set; } = "11.2";    // Απόδειξη Παροχής Υπηρεσιών (σύνηθες για εστιατόρια)
    public string InvoiceInvoiceType { get; set; } = "1.1";     // Τιμολόγιο Πώλησης
    public string CashPaymentTypeCode { get; set; } = "3";      // ΑΑΔΕ: 3 = Μετρητά (1 = Επαγγ. Λογαριασμός Ημεδαπής)
    public string CardPaymentTypeCode { get; set; } = "7";      // ΑΑΔΕ: 7 = POS / e-POS

    // Δελτίο Παραγγελίας (Order Form) προς ΑΑΔΕ - ΑΠΕΝΕΡΓΟΠΟΙΗΜΕΝΟ ΑΠΟ ΠΡΟΕΠΙΛΟΓΗ.
    // Ενεργοποίησέ το μόνο αφού επιβεβαιώσεις με τον Parochos τον σωστό invoiceType κωδικό.
    public bool IssueOrderFormsToAade { get; set; } = false;
    public string? OrderFormInvoiceType { get; set; } = "8.6";   // 8.6 = Order Form σύμφωνα με το API v3.
    public string OrderFormSeriesName { get; set; } = "ΠΑΡ";
}
