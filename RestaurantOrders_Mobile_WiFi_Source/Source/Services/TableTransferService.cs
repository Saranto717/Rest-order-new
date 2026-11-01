using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed record TableTransferResult(bool Success, bool Pending, string Message, int TargetTableId = 0, string? TransferId = null);

/// <summary>Parochos v3.0.29 p32: whole-table Transfer=2. No cancellation or duplicate 8.6.</summary>
public sealed class TableTransferService(IDbContextFactory<AppDbContext> factory, ParochosService parochos)
{
    private static string Fingerprint(CompanySettings? settings) => settings is null ? "local" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Branch}|{settings.ParochosUseProduction}|{settings.ParochosSubscriptionKey}|{settings.ParochosUsername}|{settings.UseParochosProvider}")));
    private static long[] Marks(string json) => JsonSerializer.Deserialize<long[]>(json) ?? Array.Empty<long>();
    private static bool Active(TableTransfer t) => t.Stage is not TableTransferStage.Completed and not TableTransferStage.Rejected;
    private static bool HasFinancialActivity(Order order) => order.FiscalStatus != FiscalStatus.NotIssued || order.CardTransactionId is { Length: > 0 } ||
        order.ParochosPaymentSignature is { Length: > 0 } || order.ParochosMark.HasValue ||
        order.Payments.Any(p => p.FiscalStatus is FiscalStatus.Issued or FiscalStatus.PaidLocally || !string.IsNullOrWhiteSpace(p.CardTransactionId));

    public async Task<TableTransferResult> MoveAllAsync(int sourceOrderId, int targetTableId, int userId)
    {
        using var db = await factory.CreateDbContextAsync();
        var sourceTableId = await db.Orders.Where(o => o.Id == sourceOrderId).Select(o => (int?)o.RestaurantTableId).SingleOrDefaultAsync();
        if (sourceTableId is null || sourceTableId == targetTableId)
            return new(false, false, "Επίλεξε διαφορετικό ενεργό τραπέζι προορισμού.");
        using var lease = await TableOperationCoordinator.EnterAsync(sourceTableId.Value, targetTableId);
        TableTransfer? transfer = null;
        try
        {
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var source = await db.Orders.Include(o => o.Items).Include(o => o.Payments)
                    .SingleOrDefaultAsync(o => o.Id == sourceOrderId && o.Status == OrderStatus.Open && o.RestaurantTableId == sourceTableId);
                var sourceTable = await db.Tables.FindAsync(sourceTableId.Value);
                var targetTable = await db.Tables.FindAsync(targetTableId);
                if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && (u.Role == Role.Admin || u.Role == Role.Waiter)))
                    return new(false, false, "Χρειάζεται ενεργός χρήστης σερβιτόρου ή διαχειριστή για μεταφορά.");
                if (source is null || sourceTable is null || targetTable is not { IsActive: true })
                    return new(false, false, "Η παραγγελία ή το τραπέζι άλλαξε. Ανανέωσε τη σελίδα.");
                if (!source.Items.Any(i => !i.IsCancelled && i.Quantity > 0))
                    return new(false, false, "Δεν υπάρχουν ενεργά είδη για μεταφορά.");
                if (string.IsNullOrWhiteSpace(sourceTable.Name) || string.IsNullOrWhiteSpace(targetTable.Name))
                    return new(false, false, "Τα τραπέζια χρειάζονται όνομα Point of Service.");
                if (sourceTable.Name.Trim().Equals(targetTable.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new(false, false, "Τα δύο τραπέζια πρέπει να έχουν διαφορετικό όνομα Point of Service.");
                var overlapping = await db.TableTransfers.FirstOrDefaultAsync(t => t.Stage != TableTransferStage.Completed && t.Stage != TableTransferStage.Rejected &&
                    (t.SourceTableId == sourceTableId || t.TargetTableId == sourceTableId || t.SourceTableId == targetTableId || t.TargetTableId == targetTableId));
                if (overlapping is not null)
                    return new(false, true, "Υπάρχει ήδη εκκρεμής μεταφορά σε ένα από τα δύο τραπέζια. Χρησιμοποίησε τον επανέλεγχο.", overlapping.TargetTableId, overlapping.Id);
                if (await db.Orders.AnyAsync(o => o.Id != sourceOrderId && o.RestaurantTableId == sourceTableId && o.Status == OrderStatus.Open))
                    return new(false, false, "Υπάρχουν πολλαπλές ανοικτές παραγγελίες στο αρχικό τραπέζι. Χρειάζεται έλεγχος πριν τη μεταφορά.");
                var targets = await db.Orders.Include(o => o.Items).Include(o => o.Payments)
                    .Where(o => o.RestaurantTableId == targetTableId && o.Status == OrderStatus.Open).ToListAsync();
                if (targets.Count > 1 || HasFinancialActivity(source) || targets.Any(HasFinancialActivity))
                    return new(false, false, "Δεν επιτρέπεται μεταφορά με εξόφληση/μερική πληρωμή, δεσμευμένη υπογραφή ή πολλαπλές ανοικτές παραγγελίες. Ολοκλήρωσε πρώτα την εκκρεμότητα.");
                var target = targets.SingleOrDefault();
                var sourceForms = await db.FiscalOrderDocuments.Where(f => f.OrderId == sourceOrderId && !f.IsCancelled).ToListAsync();
                var targetForms = target is null ? new List<FiscalOrderDocument>() :
                    await db.FiscalOrderDocuments.Where(f => f.OrderId == target.Id && !f.IsCancelled).ToListAsync();
                if (sourceForms.Concat(targetForms).Any(f => f.Mark is null))
                    return new(false, false, "Υπάρχει φορολογικό δελτίο χωρίς επιβεβαιωμένο MARK. Χρειάζεται ανάκτηση πριν τη μεταφορά.");
                var settings = await db.CompanySettings.FirstOrDefaultAsync();
                if ((sourceForms.Count > 0 || targetForms.Count > 0) && settings is not { UseParochosProvider: true })
                    return new(false, false, "Υπάρχουν δελτία Parochos. Ενεργοποίησε τη σωστή σύνδεση παρόχου για να μεταφερθούν.");
                if (settings is { UseParochosProvider: true, IssueOrderFormsToAade: true } &&
                    ((sourceForms.Count == 0 && source.Items.Any(i => i.IsPrinted && !i.IsCancelled)) ||
                     (targetForms.Count == 0 && target is not null && target.Items.Any(i => i.IsPrinted && !i.IsCancelled))))
                    return new(false, false, "Υπάρχουν σταλμένα είδη χωρίς ενεργό φορολογικό δελτίο. Έλεγξε πρώτα την έκδοση/ανάκτηση δελτίων Parochos.");
                var merge = target is not null && (target.Items.Any(i => !i.IsCancelled && i.Quantity > 0) || targetForms.Count > 0);
                transfer = new TableTransfer
                {
                    SourceOrderId = source.Id, TargetOrderId = merge ? target!.Id : null,
                    SourceTableId = sourceTableId.Value, TargetTableId = targetTableId, RequestedByUserId = userId,
                    SourceName = sourceTable.Name.Trim(), TargetName = targetTable.Name.Trim(),
                    AccountFingerprint = Fingerprint(settings), ProviderRequired = sourceForms.Count > 0,
                    SourceMarksJson = JsonSerializer.Serialize(sourceForms.Select(f => f.Mark!.Value).Distinct()),
                    SourceItemIdsJson = JsonSerializer.Serialize(source.Items.Select(i => i.Id)),
                    TargetMarksJson = JsonSerializer.Serialize(targetForms.Select(f => f.Mark!.Value).Distinct())
                };
                db.TableTransfers.Add(transfer); await db.SaveChangesAsync(); await transaction.CommitAsync();
            }
            return await ProcessAsync(db, transfer);
        }
        catch (Exception ex) { return await RecordFailureAsync(db, transfer, ex.Message); }
    }

    public async Task<TableTransferResult> RecheckAsync(string transferId)
    {
        using var db = await factory.CreateDbContextAsync();
        var transfer = await db.TableTransfers.FindAsync(transferId);
        if (transfer is null) return new(false, false, "Η μεταφορά δεν βρέθηκε.");
        using var lease = await TableOperationCoordinator.EnterAsync(transfer.SourceTableId, transfer.TargetTableId);
        await db.Entry(transfer).ReloadAsync();
        if (transfer.Stage == TableTransferStage.Completed) return Completed(transfer);
        if (transfer.Stage == TableTransferStage.Rejected) return new(false, false, transfer.LastError ?? "Η μεταφορά δεν εκτελέστηκε.");
        try { return await ProcessAsync(db, transfer); }
        catch (Exception ex) { return await RecordFailureAsync(db, transfer, ex.Message); }
    }

    private async Task<TableTransferResult> ProcessAsync(AppDbContext db, TableTransfer transfer)
    {
        var settings = await db.CompanySettings.AsNoTracking().FirstOrDefaultAsync();
        if (Fingerprint(settings) != transfer.AccountFingerprint)
        {
            if (transfer.Stage == TableTransferStage.Prepared)
                throw new InvalidOperationException("Οι ρυθμίσεις Parochos άλλαξαν πριν σταλεί η μεταφορά. Δεν έγινε μετακίνηση· δοκίμασε ξανά με τις σωστές ρυθμίσεις.");
            return await PendingAsync(db, transfer, "Άλλαξε ο λογαριασμός ή το περιβάλλον Parochos. Επανέφερε τις ρυθμίσεις της αρχικής μεταφοράς πριν τον επανέλεγχο.");
        }
        if (transfer.Stage == TableTransferStage.Prepared)
        {
            var checkProvider = settings is { UseParochosProvider: true } &&
                (settings.IssueOrderFormsToAade || Marks(transfer.SourceMarksJson).Length > 0 || Marks(transfer.TargetMarksJson).Length > 0);
            if (checkProvider)
            {
                var source = await parochos.ListPointOfServiceDetailsAsync(settings!, transfer.SourceName);
                var target = await parochos.ListPointOfServiceDetailsAsync(settings!, transfer.TargetName);
                if (!source.Success || !target.Success)
                    throw new InvalidOperationException("Δεν επιβεβαιώθηκαν τα δύο τραπέζια στον Parochos. Δεν στάλθηκε μεταφορά. " + (source.ErrorMessage ?? target.ErrorMessage));
                if (!MatchesExactly(source, Marks(transfer.SourceMarksJson)) || !MatchesExactly(target, Marks(transfer.TargetMarksJson)))
                    throw new InvalidOperationException("Τα ενεργά δελτία στον Parochos διαφέρουν από την τοπική βάση. Δεν στάλθηκε μεταφορά ώστε να μη μετακινηθούν ξένα δελτία.");
                transfer.SourceBalance = Balance(source); transfer.TargetBalance = Balance(target);
                if (!transfer.ProviderRequired && transfer.SourceBalance != 0)
                    throw new InvalidOperationException("Ο Parochos εμφανίζει υπόλοιπο χωρίς αναγνωρισμένο τοπικό δελτίο. Δεν έγινε μεταφορά.");
                transfer.Stage = TableTransferStage.Submitted;
                await SaveJournalAsync(db, transfer); // Persist BEFORE the non-idempotent provider request.
                if (transfer.ProviderRequired)
                {
                    var result = await parochos.SetPointOfServiceStateAsync(settings!, transfer.SourceName, 2, transfer.TargetName);
                    if (!result.Success)
                    {
                        if (result.ErrorCategory is "BED" or "BTA")
                        {
                            transfer.Stage = TableTransferStage.Rejected;
                            transfer.LastError = result.ErrorMessage ?? "Ο Parochos απέρριψε τη μεταφορά.";
                            await SaveJournalAsync(db, transfer);
                            return new(false, false, transfer.LastError);
                        }
                        return await PendingAsync(db, transfer, "Δεν επιβεβαιώθηκε το αποτέλεσμα της μεταφοράς. Δεν θα σταλεί δεύτερη εντολή. " + result.ErrorMessage);
                    }
                }
            }
            else
            {
                transfer.Stage = TableTransferStage.ProviderConfirmed;
                await SaveJournalAsync(db, transfer);
            }
        }
        if (transfer.Stage == TableTransferStage.Submitted)
        {
            var source = await parochos.ListPointOfServiceDetailsAsync(settings!, transfer.SourceName);
            var target = await parochos.ListPointOfServiceDetailsAsync(settings!, transfer.TargetName);
            var expected = Marks(transfer.SourceMarksJson).Concat(Marks(transfer.TargetMarksJson)).Distinct().ToArray();
            if (!source.Success || !target.Success || source.Details.Count != 0 || Balance(source) != 0 ||
                !MatchesExactly(target, expected) || Balance(target) != transfer.SourceBalance + transfer.TargetBalance)
                return await PendingAsync(db, transfer, "Δεν επιβεβαιώθηκε ακόμη ότι το αρχικό τραπέζι άδειασε και τα δελτία/υπόλοιπα πέρασαν στον προορισμό. Πάτησε επανέλεγχο· η εντολή μεταφοράς δεν ξαναστέλνεται.");
            // Some installations retain an empty open source table after Transfer.
            if (source.PointOfService is not null)
            {
                await parochos.ClosePointOfServiceAsync(settings!, transfer.SourceName);
                source = await parochos.ListPointOfServiceDetailsAsync(settings!, transfer.SourceName);
                if (!source.Success || source.PointOfService is not null || source.Details.Count > 0 || Balance(source) != 0)
                    return await PendingAsync(db, transfer, "Τα δελτία μεταφέρθηκαν αλλά δεν επιβεβαιώθηκε το κλείσιμο του αρχικού τραπεζιού. Χρειάζεται επανέλεγχος.");
            }
            transfer.Stage = TableTransferStage.ProviderConfirmed;
            await SaveJournalAsync(db, transfer);
        }
        return await FinalizeAsync(db, transfer);
    }

    private static bool MatchesExactly(ParochosPointOfServiceResult result, long[] expected)
    {
        if (result.Details.Any(d => !d.Mark.HasValue || d.InvoiceType != "8.6")) return false;
        return result.Details.Select(d => d.Mark!.Value).ToHashSet().SetEquals(expected);
    }
    private static decimal Balance(ParochosPointOfServiceResult result) => result.Balance ??
        (result.PointOfService is null && result.Details.Count == 0 ? 0m : throw new InvalidOperationException("Δεν επιστράφηκε υπόλοιπο τραπεζιού από τον Parochos."));

    private static async Task SaveJournalAsync(AppDbContext db, TableTransfer transfer)
    {
        transfer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
    private static TableTransferResult Completed(TableTransfer transfer) => new(true, false,
        $"Η παραγγελία μεταφέρθηκε από {transfer.SourceName} στο {transfer.TargetName}.", transfer.TargetTableId, transfer.Id);
    private static async Task<TableTransferResult> PendingAsync(AppDbContext db, TableTransfer transfer, string message)
    {
        transfer.LastError = message; await SaveJournalAsync(db, transfer);
        return new(false, true, message, transfer.TargetTableId, transfer.Id);
    }
    private static async Task<TableTransferResult> RecordFailureAsync(AppDbContext db, TableTransfer? transfer, string message)
    {
        if (transfer is null) return new(false, false, message);
        db.ChangeTracker.Clear();
        transfer = await db.TableTransfers.FindAsync(transfer.Id);
        if (transfer is null) return new(false, false, message);
        if (transfer.Stage == TableTransferStage.Completed) return Completed(transfer);
        // A prepared operation has not sent any mutating request. Submitted operations
        // remain locked across process restarts until provider state proves completion.
        if (transfer.Stage == TableTransferStage.Prepared) transfer.Stage = TableTransferStage.Rejected;
        try { return (await PendingAsync(db, transfer, message)) with { Pending = Active(transfer) }; }
        catch { return new(false, true, "Η μεταφορά χρειάζεται επανέλεγχο μετά από σφάλμα αποθήκευσης. " + message, transfer.TargetTableId, transfer.Id); }
    }

    private static async Task<TableTransferResult> FinalizeAsync(AppDbContext db, TableTransfer transfer)
    {
        if (transfer.Stage != TableTransferStage.ProviderConfirmed) throw new InvalidOperationException("Η μεταφορά δεν έχει επιβεβαιωθεί.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.FinalizingTableTransfer = true;
        try
        {
            transfer = await db.TableTransfers.SingleAsync(t => t.Id == transfer.Id);
            if (transfer.Stage == TableTransferStage.Completed) return Completed(transfer);
            var source = await db.Orders.SingleAsync(o => o.Id == transfer.SourceOrderId);
            if (source.Status != OrderStatus.Open || source.RestaurantTableId != transfer.SourceTableId)
                throw new InvalidOperationException("Η αρχική παραγγελία άλλαξε. Η επιβεβαιωμένη μεταφορά χρειάζεται έλεγχο.");
            if (transfer.TargetOrderId is { } targetOrderId)
            {
                var target = await db.Orders.SingleAsync(o => o.Id == targetOrderId);
                if (target.Status != OrderStatus.Open || target.RestaurantTableId != transfer.TargetTableId)
                    throw new InvalidOperationException("Η παραγγελία προορισμού άλλαξε. Χρειάζεται έλεγχος.");
                foreach (var item in await db.OrderItems.Where(i => i.OrderId == source.Id).ToListAsync()) item.OrderId = target.Id;
                foreach (var form in await db.FiscalOrderDocuments.Where(f => f.OrderId == source.Id).ToListAsync()) form.OrderId = target.Id;
                foreach (var cancellation in await db.FiscalOrderCancellations.Where(c => c.OrderId == source.Id).ToListAsync()) cancellation.OrderId = target.Id;
                foreach (var payment in await db.OrderPayments.Where(p => p.OrderId == source.Id).ToListAsync()) payment.OrderId = target.Id;
                var number = await db.OrderPrintBatches.Where(b => b.OrderId == target.Id).MaxAsync(b => (int?)b.Number) ?? 0;
                foreach (var batch in await db.OrderPrintBatches.Where(b => b.OrderId == source.Id).OrderBy(b => b.Number).ToListAsync())
                { batch.OrderId = target.Id; batch.Number = ++number; }
                source.Status = OrderStatus.Transferred; source.ClosedAt = DateTime.Now;
            }
            else
            {
                foreach (var draft in await db.Orders.Where(o => o.RestaurantTableId == transfer.TargetTableId && o.Status == OrderStatus.Open).ToListAsync())
                { draft.Status = OrderStatus.Transferred; draft.ClosedAt = DateTime.Now; }
                source.RestaurantTableId = transfer.TargetTableId;
                transfer.TargetOrderId = source.Id;
            }
            transfer.Stage = TableTransferStage.Completed; transfer.LastError = null; transfer.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return Completed(transfer);
        }
        finally { db.FinalizingTableTransfer = false; }
    }
}
