using System.ComponentModel.DataAnnotations.Schema;

namespace RestaurantOrders.Models;

public class User
{
    public int Id { get; set; }

    public string FirstName { get; set; } = "";
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; } // π.χ. "Σερβιτόρος", "Barman" - μόνο περιγραφικό, δεν επηρεάζει δικαιώματα

    // Προαιρετικός κωδικός εισόδου. Αν είναι null, ο χρήστης μπαίνει πατώντας
    // απλά την καρτέλα του, χωρίς να του ζητηθεί τίποτα.
    public string? CodeHash { get; set; }

    public Role Role { get; set; } = Role.Waiter;
    public bool IsActive { get; set; } = true;

    [NotMapped]
    public bool HasCode => !string.IsNullOrEmpty(CodeHash);

    [NotMapped]
    public string FullName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";
}
