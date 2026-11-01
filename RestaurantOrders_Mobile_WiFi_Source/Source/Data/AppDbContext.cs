using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Models;

namespace RestaurantOrders.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RestaurantTable> Tables => Set<RestaurantTable>();
    public DbSet<TableArea> TableAreas => Set<TableArea>();
    public DbSet<PrinterStation> PrinterStations => Set<PrinterStation>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderPrintBatch> OrderPrintBatches => Set<OrderPrintBatch>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<OrderPaymentItem> OrderPaymentItems => Set<OrderPaymentItem>();
    public DbSet<FiscalOrderDocumentItem> FiscalOrderDocumentItems => Set<FiscalOrderDocumentItem>();
    public DbSet<ExtraOption> ExtraOptions => Set<ExtraOption>();
    public DbSet<DayClose> DayCloses => Set<DayClose>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<BankPosTerminalSettings> BankPosTerminalSettings => Set<BankPosTerminalSettings>();
    public DbSet<InvoiceSeries> InvoiceSeries => Set<InvoiceSeries>();
    public DbSet<FiscalOrderDocument> FiscalOrderDocuments => Set<FiscalOrderDocument>();
    public DbSet<FiscalOrderCancellation> FiscalOrderCancellations => Set<FiscalOrderCancellation>();
    public DbSet<TableTransfer> TableTransfers => Set<TableTransfer>();

    // Only the transfer service may finalize the records locked by its own journal.
    internal bool FinalizingTableTransfer { get; set; }

    public Task<bool> HasPendingTableTransferAsync(int orderId) => Orders
        .Where(o => o.Id == orderId)
        .AnyAsync(o => TableTransfers.Any(t => t.Stage != TableTransferStage.Completed && t.Stage != TableTransferStage.Rejected &&
            (t.SourceTableId == o.RestaurantTableId || t.TargetTableId == o.RestaurantTableId)));

    private void CheckTransferLocks()
    {
        if (FinalizingTableTransfer) return;
        ChangeTracker.DetectChanges();
        var orders = new HashSet<int>();
        var tables = new HashSet<int>();
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is Order order)
            {
                if (order.Id > 0) orders.Add(order.Id);
                tables.Add(order.RestaurantTableId);
                if (entry.State != EntityState.Added) tables.Add(entry.OriginalValues.GetValue<int>(nameof(Order.RestaurantTableId)));
            }
            else if (entry.Entity is RestaurantTable table && entry.State != EntityState.Added) tables.Add(table.Id);
            else if (entry.Entity is OrderItem item) orders.Add(item.OrderId);
            else if (entry.Entity is OrderPayment payment) orders.Add(payment.OrderId);
            else if (entry.Entity is OrderPrintBatch batch) orders.Add(batch.OrderId);
            else if (entry.Entity is FiscalOrderDocument document) orders.Add(document.OrderId);
            else if (entry.Entity is FiscalOrderCancellation cancellation) orders.Add(cancellation.OrderId);
            else if (entry.Entity is FiscalOrderDocumentItem documentItem)
            {
                var id = documentItem.OrderItemId;
                foreach (var orderId in OrderItems.Where(i => i.Id == id).Select(i => i.OrderId)) orders.Add(orderId);
            }
            else if (entry.Entity is OrderPaymentItem paymentItem)
            {
                foreach (var orderId in OrderPayments.Where(p => p.Id == paymentItem.OrderPaymentId).Select(p => p.OrderId)) orders.Add(orderId);
            }
            if (entry.State != EntityState.Added && entry.Metadata.FindProperty("OrderId")?.ClrType == typeof(int))
                orders.Add(entry.OriginalValues.GetValue<int>("OrderId"));
        }
        if (orders.Count == 0 && tables.Count == 0) return;
        foreach (var id in Orders.Where(o => orders.Contains(o.Id)).Select(o => o.RestaurantTableId)) tables.Add(id);
        if (TableTransfers.Any(t => t.Stage != TableTransferStage.Completed && t.Stage != TableTransferStage.Rejected &&
            (tables.Contains(t.SourceTableId) || tables.Contains(t.TargetTableId))))
            throw new InvalidOperationException("Υπάρχει εκκρεμής μεταφορά τραπεζιού. Ολοκλήρωσε τον επανέλεγχο πριν από αλλαγές ή πληρωμές.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        using var transaction = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        CheckTransferLocks();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        transaction?.Commit();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await using var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        CheckTransferLocks();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TableTransfer>().HasIndex(t => new { t.Stage, t.SourceTableId, t.TargetTableId });
        modelBuilder.Entity<MenuCategory>()
            .HasOne(c => c.PrinterStation)
            .WithMany()
            .HasForeignKey(c => c.PrinterStationId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MenuCategory>()
            .HasMany(c => c.PrinterStations)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "MenuCategoryPrinterStation",
                right => right.HasOne<PrinterStation>()
                    .WithMany()
                    .HasForeignKey("PrinterStationId")
                    .OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne<MenuCategory>()
                    .WithMany()
                    .HasForeignKey("MenuCategoryId")
                    .OnDelete(DeleteBehavior.Cascade),
                join =>
                {
                    join.ToTable("MenuCategoryPrinterStations");
                    join.HasKey("MenuCategoryId", "PrinterStationId");
                });

        modelBuilder.Entity<MenuItem>()
            .HasOne(i => i.MenuCategory)
            .WithMany()
            .HasForeignKey(i => i.MenuCategoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<RestaurantTable>()
            .HasOne(t => t.TableArea)
            .WithMany()
            .HasForeignKey(t => t.TableAreaId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.RestaurantTable)
            .WithMany()
            .HasForeignKey(o => o.RestaurantTableId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.WaiterUser)
            .WithMany()
            .HasForeignKey(o => o.WaiterUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(o => o.DayClose)
            .WithMany()
            .HasForeignKey(o => o.DayCloseId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DayClose>()
            .HasOne(dc => dc.ClosedByUser)
            .WithMany()
            .HasForeignKey(dc => dc.ClosedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<FiscalOrderDocument>()
            .HasOne(f => f.Order)
            .WithMany()
            .HasForeignKey(f => f.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FiscalOrderCancellation>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(c => c.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(oi => oi.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPrintBatch>()
            .HasOne(b => b.Order)
            .WithMany()
            .HasForeignKey(b => b.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPrintBatch>()
            .HasIndex(b => new { b.OrderId, b.Number })
            .IsUnique();

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.OrderPrintBatch)
            .WithMany(b => b.Items)
            .HasForeignKey(oi => oi.OrderPrintBatchId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<OrderItem>()
            .HasOne(oi => oi.MenuItem)
            .WithMany()
            .HasForeignKey(oi => oi.MenuItemId)
            .OnDelete(DeleteBehavior.Restrict);


        modelBuilder.Entity<OrderPayment>()
            .HasOne(p => p.Order)
            .WithMany(o => o.Payments)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPaymentItem>()
            .HasOne(pi => pi.OrderPayment)
            .WithMany(p => p.Items)
            .HasForeignKey(pi => pi.OrderPaymentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPaymentItem>()
            .HasOne(pi => pi.OrderItem)
            .WithMany()
            .HasForeignKey(pi => pi.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExtraOption>()
            .HasOne(e => e.MenuCategory)
            .WithMany()
            .HasForeignKey(e => e.MenuCategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
