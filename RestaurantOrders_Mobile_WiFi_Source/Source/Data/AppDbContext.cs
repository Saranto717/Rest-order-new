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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
