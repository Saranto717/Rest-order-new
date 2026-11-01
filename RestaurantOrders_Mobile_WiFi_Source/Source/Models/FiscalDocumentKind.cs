namespace RestaurantOrders.Models;

public enum FiscalDocumentKind
{
    Receipt = 0,    // Απόδειξη (Λιανικής / Παροχής Υπηρεσιών)
    Invoice = 1,    // Τιμολόγιο
    OrderForm = 2,   // Δελτίο Παραγγελίας
    CancelOrderForm = 3, // Ακυρωτικό Δελτίο Παραγγελίας
    CreditReceipt = 4,   // Πιστωτικό Στοιχείο Λιανικής (11.4)
    CreditInvoice = 5    // Πιστωτικό Τιμολόγιο (5.1)
}
