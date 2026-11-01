namespace RestaurantOrders.Models;

/// <summary>Τα είδη/ποσότητες που εξοφλήθηκαν από μία συγκεκριμένη πληρωμή.</summary>
public class OrderPaymentItem
{
    public int Id { get; set; }
    public int OrderPaymentId { get; set; }
    public OrderPayment? OrderPayment { get; set; }

    public int OrderItemId { get; set; }
    public OrderItem? OrderItem { get; set; }

    public int Quantity { get; set; }
}
