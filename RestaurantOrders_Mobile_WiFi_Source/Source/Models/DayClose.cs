namespace RestaurantOrders.Models;

/// <summary>
/// Ένα μεμονωμένο "Κλείσιμο Ημέρας/Ταμείου" - στιγμιότυπο. Οι παραγγελίες που ήταν
/// "εκκρεμείς" (κλεισμένες αλλά όχι ακόμα μετρημένες) τη στιγμή του κλεισίματος
/// συνδέονται με αυτό το αναγνωριστικό, ώστε να μην ξαναμετρηθούν στο επόμενο.
/// </summary>
public class DayClose
{
    public int Id { get; set; }
    public DateTime ClosedAt { get; set; } = DateTime.Now;

    public int ClosedByUserId { get; set; }
    public User? ClosedByUser { get; set; }
}
