using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public static class DataSeeder
{
    public static void Seed(AppDbContext db)
    {
        db.Database.EnsureCreated();
        EnsureParochosSchemaCompatibility(db);
        EnsureBankPosSchema(db);

        if (!db.Users.Any())
        {
            db.Users.Add(new User
            {
                FirstName = "Διαχειριστής",
                CodeHash = PasswordHasher.Hash("1234"),
                Role = Role.Admin,
                IsActive = true
            });
            db.SaveChanges();
        }
    }

    /// <summary>
    /// Το project χρησιμοποιεί EnsureCreated() αντί για migrations. Αυτές οι ALTER TABLE
    /// εντολές επιτρέπουν σε υπάρχον restaurant.db να πάρει τα νέα Parochos fields χωρίς
    /// να χρειάζεται διαγραφή της βάσης.
    /// </summary>
    private static void EnsureParochosSchemaCompatibility(AppDbContext db)
    {
        // Νέα δυνατότητα: απενεργοποίηση κατηγορίας χωρίς διαγραφή της από την υπάρχουσα βάση.
        AddColumnIfMissing(db, "MenuCategories", "IsActive", "INTEGER NOT NULL DEFAULT 1");

        AddColumnIfMissing(db, "CompanySettings", "UseParochosProvider", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "CompanySettings", "TaxOffice", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "MainActivity", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosTransmissionType", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(db, "CompanySettings", "ParochosCardTerminalId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosNspCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "CompanySettings", "ParochosSpecialInvoiceCategory", "INTEGER NOT NULL DEFAULT 12");
        AddColumnIfMissing(db, "CompanySettings", "ParochosPollingAttempts", "INTEGER NOT NULL DEFAULT 40");
        AddColumnIfMissing(db, "CompanySettings", "ParochosPollingDelayMs", "INTEGER NOT NULL DEFAULT 750");
        AddColumnIfMissing(db, "InvoiceSeries", "IsActive", "INTEGER NOT NULL DEFAULT 1");
        EnsureActiveInvoiceSeries(db);

        AddColumnIfMissing(db, "Orders", "ParochosPaymentSignature", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "ParochosPaymentUid", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardTransactionId", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardTerminalId", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CardNspCode", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartStreetNumber", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartTaxOffice", "TEXT NULL");
        AddColumnIfMissing(db, "Orders", "CounterpartMainActivity", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "ProcessId", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "IsCancelled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "CancelledAt", "TEXT NULL");
        AddColumnIfMissing(db, "FiscalOrderDocuments", "CancellationMark", "INTEGER NULL");
        AddColumnIfMissing(db, "OrderItems", "IsCancelled", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "OrderItems", "CancelledAt", "TEXT NULL");
        AddColumnIfMissing(db, "OrderItems", "CancelReason", "TEXT NULL");
        AddColumnIfMissing(db, "OrderItems", "OrderPrintBatchId", "INTEGER NULL");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS OrderPrintBatches (
            Id INTEGER NOT NULL CONSTRAINT PK_OrderPrintBatches PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            Number INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            CONSTRAINT FK_OrderPrintBatches_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES Orders (Id) ON DELETE CASCADE
        );");

        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_OrderPrintBatches_OrderId_Number
            ON OrderPrintBatches (OrderId, Number);");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS FiscalOrderCancellations (
            Id INTEGER NOT NULL CONSTRAINT PK_FiscalOrderCancellations PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            ExternalSystemId TEXT NOT NULL,
            ProcessId TEXT NULL,
            Mark INTEGER NULL,
            CreatedAt TEXT NOT NULL,
            Reason TEXT NULL,
            CONSTRAINT FK_FiscalOrderCancellations_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES Orders (Id) ON DELETE CASCADE
        );");

        // Πολλοί εκτυπωτές ανά κατηγορία. Δημιουργείται και σε υπάρχουσες βάσεις
        // (το EnsureCreated δεν προσθέτει νέους πίνακες σε ήδη υπάρχον restaurant.db).
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS MenuCategoryPrinterStations (
            MenuCategoryId INTEGER NOT NULL,
            PrinterStationId INTEGER NOT NULL,
            CONSTRAINT PK_MenuCategoryPrinterStations PRIMARY KEY (MenuCategoryId, PrinterStationId),
            CONSTRAINT FK_MenuCategoryPrinterStations_MenuCategories_MenuCategoryId
                FOREIGN KEY (MenuCategoryId) REFERENCES MenuCategories (Id) ON DELETE CASCADE,
            CONSTRAINT FK_MenuCategoryPrinterStations_PrinterStations_PrinterStationId
                FOREIGN KEY (PrinterStationId) REFERENCES PrinterStations (Id) ON DELETE CASCADE
        );");

        // Μεταφέρει χωρίς απώλεια τις ήδη υπάρχουσες αντιστοιχίσεις από την παλιά στήλη.
        db.Database.ExecuteSqlRaw(@"INSERT OR IGNORE INTO MenuCategoryPrinterStations (MenuCategoryId, PrinterStationId)
            SELECT Id, PrinterStationId FROM MenuCategories WHERE PrinterStationId IS NOT NULL;");
    }


    private static void EnsureBankPosSchema(AppDbContext db)
    {
        // EnsureCreated does not add tables/columns to an existing SQLite database.
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS BankPosTerminalSettings (
            Id INTEGER NOT NULL CONSTRAINT PK_BankPosTerminalSettings PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT 'Κύριο POS', Provider TEXT NOT NULL DEFAULT 'Viva',
            Protocol TEXT NOT NULL DEFAULT 'Viva Cloud', UseProduction INTEGER NOT NULL DEFAULT 0,
            IsEnabled INTEGER NOT NULL DEFAULT 0, TerminalId TEXT NOT NULL DEFAULT '',
            IdUrl TEXT NOT NULL DEFAULT '', RedeemCode TEXT NOT NULL DEFAULT '', ProviderCode TEXT NOT NULL DEFAULT '',
            ParochosNspCode TEXT NOT NULL DEFAULT '', ProviderId INTEGER NOT NULL DEFAULT 19,
            IsAppToApp INTEGER NOT NULL DEFAULT 0, MerchantId TEXT NOT NULL DEFAULT '', ApiKey TEXT NOT NULL DEFAULT '', ApiSecret TEXT NOT NULL DEFAULT '',
            Username TEXT NOT NULL DEFAULT '', Password TEXT NOT NULL DEFAULT '', Endpoint TEXT NOT NULL DEFAULT 'https://webecr-test.epayworldwide.com/',
            AuthEndpoint TEXT NOT NULL DEFAULT '', RedeemEndpoint TEXT NOT NULL DEFAULT '',
            IpAddress TEXT NOT NULL DEFAULT '', Port INTEGER NOT NULL DEFAULT 0,
            TimeoutSeconds INTEGER NOT NULL DEFAULT 60, Notes TEXT NOT NULL DEFAULT '', UpdatedAt TEXT NOT NULL
        );");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "IdUrl", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "RedeemCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ProviderCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ParochosNspCode", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "ProviderId", "INTEGER NOT NULL DEFAULT 19");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "IsAppToApp", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "AuthEndpoint", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "BankPosTerminalSettings", "RedeemEndpoint", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureActiveInvoiceSeries(AppDbContext db)
    {
        foreach (var kind in Enum.GetValues<FiscalDocumentKind>())
        {
            if (db.InvoiceSeries.Any(x => x.Kind == kind && x.IsActive))
                continue;

            var latest = db.InvoiceSeries
                .Where(x => x.Kind == kind)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (latest is not null)
            {
                latest.IsActive = true;
                db.SaveChanges();
            }
        }
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string definition)
    {
        var exists = db.Database.SqlQueryRaw<long>(
            $"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') WHERE name = '{column}'")
            .Single() > 0;

        if (!exists)
        {
            db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
        }
    }
}
