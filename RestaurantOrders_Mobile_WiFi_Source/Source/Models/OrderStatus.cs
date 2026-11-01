namespace RestaurantOrders.Models;

public enum OrderStatus
{
    Open = 0,       // Ανοιχτή στο τραπέζι, μπορεί να προστεθούν είδη
    Closed = 1,     // Κλεισμένη / πληρωμένη
    Transferred = 2 // Συγχωνεύθηκε σε άλλο τραπέζι· δεν είναι είσπραξη
}
