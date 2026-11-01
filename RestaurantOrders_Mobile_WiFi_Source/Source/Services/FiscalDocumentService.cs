using Microsoft.EntityFrameworkCore;
using System.Globalization;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class FiscalIssueResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool ProviderUsed { get; init; } = true;
    public bool RequiresRecovery { get; init; }

    // Data returned by Parochos/ΑΑΔΕ for printing.
    public long? Mark { get; init; }
    public string? QrCode { get; init; }
}

public sealed class CardPaymentPreparation
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string ExternalSystemId { get; init; } = "";
    public string SeriesName { get; init; } = "";
    public long InvoiceNumber { get; init; }
    public string Signature { get; init; } = "";
    public string? Uid { get; init; }
    public string TerminalId { get; init; } = "";
    public string NspCode { get; init; } = "";
    public decimal Amount { get; init; }
    public ProviderPaymentData? ProviderData { get; init; }
}

/// <summary>
/// Builds Parochos v3 source documents and coordinates transmission, EFT-POS and order forms.
/// </summary>
public sealed class FiscalDocumentService
{
    private readonly ParochosService _parochos;
    private readonly ILogger<FiscalDocumentService> _logger;

    public FiscalDocumentService(ParochosService parochos, ILogger<FiscalDocumentService> logger)
    {
        _parochos = parochos;
        _logger = logger;
    }

    public async Task<CardPaymentPreparation> PrepareCardPaymentAsync(
        AppDbContext db,
        Order order,
        FiscalDocumentKind kind,
        BankPosTerminalSettings? selectedPos = null)
    {
        var pos = selectedPos ?? await GetDefaultWebEcrPosAsync(db);
        if (CommonWebEcrProviderProfile.IsCommonRest(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            if (!MellonConnectionSettings.IsConnected(pos!) || string.IsNullOrWhiteSpace(pos!.ParochosNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Το Mellon POS χρειάζεται ενεργή σύνδεση, φυσικό TID και Parochos NSP Code." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                pos.ParochosNspCode.Trim(), pos.ProviderId > 0 ? pos.ProviderId : 19, pos.TerminalId.Trim(),
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEpayWebEcr(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var epayTerminalId = pos!.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(epayTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του ePay POS στην καρτέλα Τραπεζικά POS." };
            var epayNspCode = string.IsNullOrWhiteSpace(pos.ParochosNspCode) ? "2" : pos.ParochosNspCode.Trim();
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                epayNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, epayTerminalId,
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEdpsWebEcr(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var edpsNspCode = !string.IsNullOrWhiteSpace(pos!.ParochosNspCode)
                ? pos.ParochosNspCode.Trim()
                : "5";
            var edpsTerminalId = pos.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(edpsNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το NSP Code του EDPS στην καρτέλα Τραπεζικά POS." };
            if (string.IsNullOrWhiteSpace(edpsTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του EDPS POS στην καρτέλα Τραπεζικά POS." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                edpsNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, edpsTerminalId);
        }
        if (pos is { Provider: "Viva", Protocol: "Viva Cloud" })
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var vivaTerminalId = pos.TerminalId?.Trim() ?? "";
            var vivaNspCode = pos.ParochosNspCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(vivaTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του Viva POS." };
            if (string.IsNullOrWhiteSpace(vivaNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το Parochos NSP Code στις ρυθμίσεις Viva." };
            return await PrepareProviderDataAsync(db, order,
                order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList(), kind,
                vivaNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, vivaTerminalId);
        }
        var settings = await GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localTotals = CalculateTotals(order);
            return new CardPaymentPreparation { Success = true, Amount = localTotals.Gross };
        }
        if (!ValidateSettings(settings, out var error))
            return new CardPaymentPreparation { Success = false, ErrorMessage = error };

        var terminalId = pos?.TerminalId?.Trim() ?? "";
        var nspCode = pos?.ParochosNspCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(nspCode))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Επίλεξε POS και συμπλήρωσε Terminal ID και Parochos NSP Code στην καρτέλα Τραπεζικά POS."
            };
        }

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        var series = await GetOrCreateSeriesAsync(db, kind);


        var invoiceNumber =
            order.FiscalStatus == FiscalStatus.Issued && order.InvoiceNumber.HasValue
             ? order.InvoiceNumber.Value
              : series.NextNumber;


        var seriesName = order.InvoiceSeriesName ?? series.SeriesName;
        var externalSystemId = order.ParochosExternalId ?? $"order-{order.Id}-{kind}-{Guid.NewGuid():N}";
        var totals = CalculateTotals(order);
        var invoiceType = kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType;

        var payment = await _parochos.RequestPaymentAsync(
            settings,
            externalSystemId,
            settings.VatNumber,
            DateTimeOffset.Now,
            settings.Branch,
            invoiceType,
            seriesName,
            invoiceNumber,
            totals.Net,
            totals.Vat,
            totals.Gross,
            totals.Gross,
            terminalId,
            nspCode);

        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε payment signature."
            };
        }

        order.ParochosExternalId = externalSystemId;
        order.InvoiceSeriesName = seriesName;
        order.InvoiceNumber = invoiceNumber;
        order.ParochosPaymentSignature = payment.Signature;
        order.ParochosPaymentUid = payment.Uid;
        order.CardTerminalId = terminalId;
        order.CardNspCode = nspCode;
        order.FiscalStatus = FiscalStatus.Pending;

        return new CardPaymentPreparation
        {
            Success = true,
            ExternalSystemId = externalSystemId,
            SeriesName = seriesName,
            InvoiceNumber = invoiceNumber,
            Signature = payment.Signature,
            Uid = payment.Uid,
            TerminalId = terminalId,
            NspCode = nspCode,
            Amount = totals.Gross
        };
    }

    public async Task<FiscalIssueResult> IssueAsync(
        AppDbContext db,
        Order order,
        FiscalDocumentKind kind,
        string? cardTransactionId = null)
    {
        var settings = await GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localItems = await db.OrderItems.Where(i => i.OrderId == order.Id && !i.IsCancelled && i.Quantity > 0).ToListAsync();
            if (localItems.Count == 0)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχουν ενεργά είδη για εξόφληση." };
            var localTotals = CalculateTotals(localItems);
            var localPayment = new OrderPayment
            {
                OrderId = order.Id, Amount = localTotals.Gross, PaymentMethod = order.PaymentMethod,
                DocumentKind = kind, FiscalStatus = FiscalStatus.PaidLocally,
                CardTransactionId = cardTransactionId ?? order.CardTransactionId
            };
            foreach (var item in localItems)
                localPayment.Items.Add(new OrderPaymentItem { OrderItemId = item.Id, Quantity = item.Quantity });
            db.OrderPayments.Add(localPayment);
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        }
        if (!ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        var series = await GetOrCreateSeriesAsync(db, kind);
        var seriesName = order.InvoiceSeriesName ?? series.SeriesName;
        var externalSystemId = order.ParochosExternalId ?? $"order-{order.Id}-{Guid.NewGuid():N}";
        var totals = CalculateTotals(order);
        var invoiceType = kind == FiscalDocumentKind.Invoice ? settings!.InvoiceInvoiceType : settings!.ReceiptInvoiceType;
        var pointOfService = await ResolvePointOfServiceAsync(db, order);

        if (order.PaymentMethod == PaymentMethod.Card)
        {
            var signature = order.ParochosPaymentSignature;
            var transactionId = cardTransactionId ?? order.CardTransactionId;
            var hasEpayWebEcr = await db.BankPosTerminalSettings.AnyAsync(x => x.IsEnabled &&
                (x.Provider.Contains("ePay") || x.Provider.Contains("Euronet") || x.Protocol.Contains("Common WebECR")));
            if (string.IsNullOrWhiteSpace(transactionId))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπει το Transaction ID που επέστρεψε το POS." };
            if (!hasEpayWebEcr && string.IsNullOrWhiteSpace(signature))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχει payment signature. Πάτησε ξανά προετοιμασία πληρωμής κάρτας." };
            if (!hasEpayWebEcr && (string.IsNullOrWhiteSpace(order.CardTerminalId) || string.IsNullOrWhiteSpace(order.CardNspCode)))
                return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπουν Terminal ID / NSP Code της κάρτας." };
            order.CardTransactionId = transactionId.Trim();
        }

        var priorOrderForms = await db.FiscalOrderDocuments
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .ToListAsync();

        // ------------------------------------------------------------
        // RECOVERY:
        // If the final fiscal document was already issued, do NOT send
        // another receipt. Only finish the missing Order Form correlation
        // and close the Point of Service.
        // ------------------------------------------------------------
        if (order.FiscalStatus == FiscalStatus.Issued &&
            order.ParochosMark is not null)
        {
            if (!string.IsNullOrWhiteSpace(pointOfService) &&
                !string.IsNullOrWhiteSpace(order.ParochosProcessId))
            {
                var assignExisting = await _parochos.AssignDocumentAsync(
                    settings,
                    pointOfService,
                    order.ParochosProcessId!,
                    order.ParochosExternalId ?? $"order-{order.Id}");

                if (!assignExisting.Success && assignExisting.ErrorCode != "00130002")
                {
                    _logger.LogWarning(
                        "Recovery AssignDocument απέτυχε για {ExternalSystemId}: {Error}",
                        order.ParochosExternalId,
                        assignExisting.ErrorMessage);

                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage =
                            "Η απόδειξη έχει ήδη εκδοθεί (ΜΑΡΚ: " +
                            order.ParochosMark.Value +
                            "), αλλά δεν μπόρεσε να αντιστοιχιστεί στο τραπέζι στον Parochos: " +
                            assignExisting.ErrorMessage
                    };
                }

                if (!assignExisting.Success && assignExisting.ErrorCode == "00130002")
                {
                    _logger.LogInformation(
                        "Recovery AssignDocument: το παραστατικό {ExternalSystemId} είναι ήδη αντιστοιχισμένο στο PointOfService {PointOfService}; συνεχίζουμε με το κλείσιμο.",
                        order.ParochosExternalId,
                        pointOfService);
                }
            }

            var allFormsConnected = true;

            foreach (var form in priorOrderForms)
            {
                var connect = await _parochos.ConnectDocumentsAsync(
                    settings,
                    form.ProcessId,
                    form.ExternalSystemId,
                    new[] { order.ParochosMark.Value });

                if (!connect.Success)
                {
                    allFormsConnected = false;

                    _logger.LogWarning(
                        "Recovery ConnectDocuments απέτυχε για Order Form {ExternalSystemId}: Status failure, ConnectedCount={ConnectedCount}, ErrorCode={ErrorCode}, Error={Error}",
                        form.ExternalSystemId,
                        connect.ConnectedCount,
                        connect.ErrorCode,
                        connect.ErrorMessage);
                }
                else if (connect.ConnectedCount == 0)
                {
                    _logger.LogInformation(
                        "Recovery ConnectDocuments ολοκληρώθηκε χωρίς νέες συνδέσεις για {ExternalSystemId}. Η απόδειξη περιέχει correlatedInvoices από την αρχική αποστολή. ReceiptMark={ReceiptMark}",
                        form.ExternalSystemId,
                        order.ParochosMark.Value);
                }
            }

            if (!allFormsConnected)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη έχει ήδη εκδοθεί (ΜΑΡΚ: " +
                        order.ParochosMark.Value +
                        "), αλλά δεν ολοκληρώθηκε η σύνδεση όλων των Δελτίων Παραγγελίας. " +
                        "Δεν θα ξαναεκδοθεί απόδειξη."
                };
            }

            if (!string.IsNullOrWhiteSpace(pointOfService) &&
                settings.IssueOrderFormsToAade)
            {
                var closeExisting = await _parochos.ClosePointOfServiceAsync(
                    settings,
                    pointOfService);

                if (!closeExisting.Success)
                {
                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage =
                            "Η απόδειξη έχει ήδη εκδοθεί και τα Δελτία Παραγγελίας έχουν συνδεθεί, " +
                            "αλλά απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " +
                            closeExisting.ErrorMessage
                    };
                }
            }

            return new FiscalIssueResult
            {
                Success = true,
                Mark = order.ParochosMark,
                QrCode = order.ParochosQrCode
            };
        }

        // Keep the local series synchronized with every successfully issued document
        // that we know about. The first test receipt may have been issued directly
        // against Parochos before its local Order record was persisted, so the
        // Parochos duplicate-code recovery below is also intentionally kept.
        var lastOrderNumber = await db.Orders
            .Where(x =>
                x.Id != order.Id &&
                x.DocumentKind == kind &&
                x.FiscalStatus == FiscalStatus.Issued &&
                x.InvoiceSeriesName == seriesName &&
                x.InvoiceNumber.HasValue)
            .Select(x => (long?)x.InvoiceNumber)
            .MaxAsync() ?? 0L;

        var lastFiscalDocumentNumber = await db.FiscalOrderDocuments
            .Where(x => x.SeriesName == seriesName && x.Mark != null)
            .Select(x => (long?)x.Number)
            .MaxAsync() ?? 0L;

        var localNextNumber = Math.Max(
            Math.Max(lastOrderNumber, lastFiscalDocumentNumber) + 1L,
            (long)series.NextNumber);

        var invoiceNumber =
            order.PaymentMethod == PaymentMethod.Card && order.InvoiceNumber.HasValue
                ? order.InvoiceNumber.Value
                : localNextNumber;

        var invoiceDict = BuildInvoice(
            settings!, order, kind, seriesName, invoiceNumber, totals, invoiceType, pointOfService,
            closePointOfService: false,
            orderForms: priorOrderForms);

        var source = new Dictionary<string, object?> { ["invoice"] = invoiceDict };

        // The beta account can already contain a document whose local number was
        // issued before our database knew about it. If Parochos rejects the first
        // number with duplicate-code 00010022, advance the AA and retry.
        // This is deliberately limited to that exact error and to non-card issues;
        // we must never change an AA after an EFT-POS payment signature was created.
        var maxDuplicateRetries = order.PaymentMethod == PaymentMethod.Card ? 0 : 5;
        ParochosSendResult result = new();

        var pointOfServiceOpenRetryUsed = false;

        for (var attempt = 0; attempt <= maxDuplicateRetries; attempt++)
        {
            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);

            if (result.Success)
                break;

            // 00010014 = the Point of Service is not currently available.
            // The final accounting document must not be reissued blindly. Instead,
            // open the table in Parochos once and retry the SAME accounting document.
            if (result.ErrorCode == "00010014" &&
                !pointOfServiceOpenRetryUsed &&
                !string.IsNullOrWhiteSpace(pointOfService))
            {
                pointOfServiceOpenRetryUsed = true;

                var openForFinal = await _parochos.OpenPointOfServiceAsync(
                    settings,
                    pointOfService);

                if (openForFinal.Success)
                {
                    _logger.LogInformation(
                        "Parochos PointOfService {PointOfService} ήταν μη διαθέσιμο. Άνοιξε επιτυχώς και γίνεται retry της ίδιας απόδειξης.",
                        pointOfService);

                    invoiceDict = BuildInvoice(
                        settings!, order, kind, seriesName, invoiceNumber, totals, invoiceType, pointOfService,
                        closePointOfService: false,
                        orderForms: priorOrderForms,
                        openPointOfService: true);
                    source["invoice"] = invoiceDict;

                    result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
                    if (result.Success)
                        break;
                }
                else
                {
                    _logger.LogWarning(
                        "Parochos OpenPointOfService απέτυχε για {PointOfService}: {Error}. Δεν θα γίνει τυφλό retry.",
                        pointOfService,
                        openForFinal.ErrorMessage);
                }
            }

            if (result.Success)
                break;

            if (result.ErrorCode != "00010022" || attempt == maxDuplicateRetries)
                break;

            invoiceNumber++;
            series.NextNumber = checked((int)Math.Max((long)series.NextNumber, invoiceNumber));

            _logger.LogWarning(
                "Parochos duplicate invoice code. Retrying {ExternalSystemId} with Series={Series} AA={AA} (attempt {Attempt}/{MaxAttempts}).",
                externalSystemId, seriesName, invoiceNumber, attempt + 1, maxDuplicateRetries);

            invoiceDict = BuildInvoice(
                settings!, order, kind, seriesName, invoiceNumber, totals, invoiceType, pointOfService,
                closePointOfService: false,
                orderForms: priorOrderForms,
                openPointOfService: false);
            source["invoice"] = invoiceDict;
        }

        var requiresCardReceiptRecovery = false;
        string? cardReceiptRecoveryMessage = null;
        if (!result.Success && order.PaymentMethod == PaymentMethod.Card &&
            result.ErrorCode == "00010022")
        {
            // A duplicate AA can mean the previous /api/send succeeded but its
            // response was lost. Query the same externalSystemId before treating
            // it as a new failure. This never calls the POS or changes signed AA.
            var existingDocument = await _parochos.GetDocumentByExternalSystemIdAsync(
                settings!, externalSystemId);
            if (existingDocument.Success && existingDocument.Mark is > 0)
            {
                _logger.LogInformation(
                    "Recovered already transmitted card receipt for {ExternalSystemId} with MARK {Mark}.",
                    externalSystemId, existingDocument.Mark);
                result = existingDocument;
            }
            else
            {
                // Keep the approved transaction's original signed AA intact, but
                // move the active series forward so the next payment starts at
                // the next unused number. The current transaction needs provider
                // reconciliation and must not be retried with a different AA.
                if (invoiceNumber < int.MaxValue)
                    series.NextNumber = Math.Max(series.NextNumber, checked((int)invoiceNumber + 1));

                requiresCardReceiptRecovery = true;
                cardReceiptRecoveryMessage =
                    $"Ο Parochos αναφέρει ότι το {seriesName} {invoiceNumber} υπάρχει ήδη. " +
                    (invoiceNumber < int.MaxValue
                        ? $"Η επόμενη νέα απόδειξη θα χρησιμοποιήσει ΑΑ {series.NextNumber}. "
                        : "Η αρίθμηση έφτασε το μέγιστο επιτρεπτό όριο. ") +
                    $"Η εγκεκριμένη πληρωμή παραμένει συνδεδεμένη με την υπογραφή για ΑΑ {invoiceNumber}. " +
                    $"Δεν θα γίνει νέα χρέωση ούτε αλλαγή ΑΑ. Έλεγξε στον Parochos το ExternalSystemId {externalSystemId}.";
            }
        }

        order.ParochosExternalId = externalSystemId;
        order.DocumentKind = kind;
        order.InvoiceSeriesName = seriesName;

        if (!result.Success)
        {
            order.FiscalStatus = FiscalStatus.Failed;
            order.FiscalErrorMessage = cardReceiptRecoveryMessage ?? result.ErrorMessage;
            if (requiresCardReceiptRecovery)
                await db.SaveChangesAsync();
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = order.FiscalErrorMessage,
                RequiresRecovery = requiresCardReceiptRecovery
            };
        }

        order.InvoiceNumber = invoiceNumber;
        order.FiscalStatus = FiscalStatus.Issued;
        order.ParochosProcessId = result.ProcessId;
        order.ParochosMark = result.Mark;
        order.ParochosUid = result.Uid;
        order.ParochosAuthenticationCode = result.AuthenticationCode;
        order.ParochosQrCode = result.QrCode;
        order.ParochosPdfUrl = result.PdfFileUrl;
        order.FiscalErrorMessage = null;

        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, invoiceNumber + 1L));
        await db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(pointOfService) && !string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success && assign.ErrorCode != "00130002")
            {
                _logger.LogWarning("Parochos AssignDocument απέτυχε για {ExternalSystemId}: {Error}", externalSystemId, assign.ErrorMessage);

                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη εκδόθηκε με ΜΑΡΚ " +
                        result.Mark +
                        ", αλλά δεν μπόρεσε να αντιστοιχιστεί στο τραπέζι στον Parochos: " +
                        assign.ErrorMessage +
                        ". Δεν θα ξαναεκδοθεί· η επόμενη προσπάθεια θα κάνει recovery."
                };
            }

            if (!assign.Success && assign.ErrorCode == "00130002")
            {
                _logger.LogInformation(
                    "Parochos AssignDocument: το παραστατικό {ExternalSystemId} είναι ήδη αντιστοιχισμένο στο PointOfService {PointOfService}; συνεχίζουμε.",
                    externalSystemId,
                    pointOfService);
            }

            _logger.LogInformation(
                "Parochos AssignDocument ολοκληρώθηκε για {ExternalSystemId} στο PointOfService {PointOfService}.",
                externalSystemId,
                pointOfService);
        }

        var finalAllFormsConnected = true;

        if (priorOrderForms.Any() && result.Mark is not null)
        {
            foreach (var form in priorOrderForms)
            {
                var connect = await _parochos.ConnectDocumentsAsync(
                    settings,
                    form.ProcessId,
                    form.ExternalSystemId,
                    new[] { result.Mark.Value });

                _logger.LogInformation(
                    "PAROCHOS ORDER FORM CORRELATION >>> OrderId={OrderId} | OrderFormMark={OrderFormMark} | OrderFormExternalSystemId={OrderFormExternalSystemId} | ReceiptMark={ReceiptMark} | Success={Success} | ConnectedCount={ConnectedCount} | ErrorCode={ErrorCode} | Error={Error}",
                    order.Id,
                    form.Mark,
                    form.ExternalSystemId,
                    result.Mark.Value,
                    connect.Success,
                    connect.ConnectedCount,
                    connect.ErrorCode,
                    connect.ErrorMessage);

                if (!connect.Success)
                {
                    finalAllFormsConnected = false;

                    _logger.LogWarning(
                        "Parochos ConnectDocuments απέτυχε για OrderForm {ExternalSystemId}: ConnectedCount={ConnectedCount}, ErrorCode={ErrorCode}, Error={Error}",
                        form.ExternalSystemId,
                        connect.ConnectedCount,
                        connect.ErrorCode,
                        connect.ErrorMessage);
                }
                else if (connect.ConnectedCount == 0)
                {
                    _logger.LogInformation(
                        "Parochos ConnectDocuments ολοκληρώθηκε χωρίς νέες συνδέσεις για {ExternalSystemId}. Το MARK {ReceiptMark} είχε ήδη σταλεί στο correlatedInvoices της απόδειξης.",
                        form.ExternalSystemId,
                        result.Mark.Value);
                }
            }
        }

        if (!finalAllFormsConnected)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage =
                    "Η απόδειξη εκδόθηκε με ΜΑΡΚ " +
                    result.Mark +
                    ", αλλά δεν ολοκληρώθηκε η σύνδεση όλων των Δελτίων Παραγγελίας. " +
                    "Δεν θα ξαναεκδοθεί από επανάληψη· η επόμενη προσπάθεια θα κάνει μόνο recovery."
            };
        }

        // Κλείσιμο Point of Service ΜΟΝΟ αφού συνδεθούν όλα τα Order Forms
        // με το τελικό accounting document.
        if (!string.IsNullOrWhiteSpace(pointOfService) &&
            settings.IssueOrderFormsToAade)
        {
            var close = await _parochos.ClosePointOfServiceAsync(
                settings,
                pointOfService);

            if (!close.Success)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    ErrorMessage =
                        "Η απόδειξη εκδόθηκε (ΜΑΡΚ: " +
                        result.Mark +
                        ") και τα Δελτία Παραγγελίας συνδέθηκαν, " +
                        "αλλά απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " +
                        close.ErrorMessage
                };
            }
        }

        return new FiscalIssueResult
        {
            Success = true,
            Mark = result.Mark,
            QrCode = result.QrCode
        };
    }

    public async Task<FiscalIssueResult> CancelSentOrderItemAsync(
        AppDbContext db,
        Order order,
        int orderItemId,
        string reason = "Ακύρωση είδους από τον σερβιτόρο")
    {
        var settings = await GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχουν ρυθμιστεί οι φορολογικές ρυθμίσεις." };

        var item = await db.OrderItems
            .Include(i => i.MenuItem).ThenInclude(i => i!.MenuCategory)
            .FirstOrDefaultAsync(i => i.Id == orderItemId && i.OrderId == order.Id);

        if (item is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Το είδος δεν βρέθηκε." };
        if (item.IsCancelled)
            return new FiscalIssueResult { Success = true };

        // Local-only mode is allowed only when fiscal order forms are disabled
        // or the Parochos provider is not in use. IsPrinted is a local printing
        // flag and must NOT decide whether a provider-side fiscal ticket exists.
        // A fiscal order form can already have a MARK even if local printing failed.
        if (!settings.UseParochosProvider || !settings.IssueOrderFormsToAade)
        {
            item.IsCancelled = true;
            item.CancelledAt = DateTime.Now;
            item.CancelReason = reason;
            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true };
        }

        var allForms = await db.FiscalOrderDocuments
            .Include(f => f.Items)
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .ToListAsync();

        if (allForms.Count == 0)
        {
            // No fiscal ticket exists in the local fiscal-document ledger.
            // An unprinted item can be cancelled locally; a printed/sent item
            // must fail closed because its provider MARK cannot be identified.
            if (!item.IsPrinted)
            {
                item.IsCancelled = true;
                item.CancelledAt = DateTime.Now;
                item.CancelReason = reason;
                await db.SaveChangesAsync();
                return new FiscalIssueResult { Success = true };
            }

            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Το είδος φαίνεται σταλμένο, αλλά δεν βρέθηκε ενεργό Δελτίο Παραγγελίας με MARK. Δεν έγινε τοπική ή φορολογική ακύρωση. Ελέγξτε τα δελτία στον Parochos πριν επαναλάβετε."
            };
        }

        // Prefer exact item-to-ticket mapping. Older records without item mappings are
        // only safe when there is exactly one active ticket for this order.
        var targetForms = allForms
            .Where(f => f.Items.Any(fi => fi.OrderItemId == item.Id))
            .ToList();

        if (targetForms.Count == 0 && allForms.Count == 1 && allForms[0].Items.Count == 0)
        {
            var activeCount = await db.OrderItems.CountAsync(i => i.OrderId == order.Id && !i.IsCancelled);
            if (activeCount == 1)
                targetForms.Add(allForms[0]);
        }

        if (targetForms.Count == 0)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν μπόρεσα να προσδιορίσω με ασφάλεια ποιο Δελτίο Παραγγελίας περιέχει το είδος. Δεν έγινε ακύρωση."
            };
        }

        var pointOfService = await ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού." };

        // Αν είναι το τελευταίο ενεργό είδος, η σωστή πράξη είναι ολική ακύρωση δελτίων.
        var otherActiveItems = await db.OrderItems
            .CountAsync(i => i.OrderId == order.Id && !i.IsCancelled && i.Id != item.Id);
        if (otherActiveItems == 0)
            return await CancelWholeOrderAsync(db, order, reason);

        // ΑΑΔΕ/Parochos: ακύρωση είδους από ήδη διαβιβασμένο 8.6 = νέο 8.6 στο ίδιο
        // Point of Service με γραμμή recType = 7 (αρνητικό πρόσημο, θετικές αξίες)
        // και συσχέτιση με το MARK του αρχικού δελτίου. Το αρχικό δελτίο μένει ενεργό.
        var negative = await IssueNegativeOrderFormAsync(db, order, pointOfService, new List<OrderItem> { item }, targetForms);
        if (!negative.Success)
            return negative;

        item.IsCancelled = true;
        item.CancelledAt = DateTime.Now;
        item.CancelReason = reason;
        await db.SaveChangesAsync();

        return negative;
    }

    /// <summary>8.6 με γραμμές recType = 7 (αρνητικό πρόσημο) για ακύρωση/μεταφορά ειδών.</summary>
    private async Task<FiscalIssueResult> IssueNegativeOrderFormAsync(
        AppDbContext db,
        Order order,
        string pointOfService,
        List<OrderItem> items,
        List<FiscalOrderDocument> correlatedForms)
    {
        var settings = (await GetSettingsAsync(db))!;
        var series = await GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var thisNumber = series.NextNumber;
        var externalSystemId = $"orderform-neg-{order.Id}-{Guid.NewGuid():N}";
        var lines = BuildLines(items);
        foreach (var line in lines.OfType<Dictionary<string, object?>>())
            line["recType"] = 7;
        var totals = CalculateTotals(items);

        ParochosSendResult result = new();
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var source = new Dictionary<string, object?>
            {
                ["invoice"] = new Dictionary<string, object?>
                {
                    ["issuer"] = BuildIssuer(settings),
                    ["counterpart"] = new Dictionary<string, object?>
                    {
                        ["vatNumber"] = "",
                        ["branch"] = 0,
                        ["name"] = "Πελάτης Λιανικής",
                        ["country"] = "GR"
                    },
                    ["invoiceHeader"] = new Dictionary<string, object?>
                    {
                        ["series"] = series.SeriesName,
                        ["aa"] = thisNumber,
                        ["issueDate"] = IssueDateToday(),
                        ["currency"] = "EUR",
                        ["invoiceType"] = settings.OrderFormInvoiceType,
                        ["invoiceTypeUbl"] = "380",
                        ["OpenPointOfService"] = 0,
                        ["ClosePointOfService"] = 0,
                        ["CancelConnected"] = 0,
                        ["PointOfService"] = pointOfService
                    },
                    ["invoiceDetails"] = lines,
                    ["paymentMethods"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = 5, ["amount"] = 0m }
                    },
                    ["invoiceSummary"] = new Dictionary<string, object?>
                    {
                        ["totalNetValue"] = totals.Net,
                        ["totalVatAmount"] = totals.Vat,
                        ["totalValue"] = totals.Gross
                    },
                    ["correlatedInvoices"] = correlatedForms
                        .Where(f => f.Mark.HasValue)
                        .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value })
                        .ToList()
                }
            };

            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
            if (result.Success || result.ErrorCode != "00010022")
                break;
            thisNumber++;
        }

        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        db.FiscalOrderDocuments.Add(new FiscalOrderDocument
        {
            OrderId = order.Id,
            ExternalSystemId = externalSystemId,
            SeriesName = series.SeriesName,
            Number = thisNumber,
            Mark = result.Mark,
            Uid = result.Uid,
            ProcessId = result.ProcessId
        });
        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber + 1L));

        if (!string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success && assign.ErrorCode != "00130002")
                _logger.LogWarning("Αρνητικό 8.6 {ExternalSystemId}: AssignDocument απέτυχε: {Error}", externalSystemId, assign.ErrorMessage);
        }

        await db.SaveChangesAsync();
        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    private async Task<FiscalIssueResult> CancelOrderTicketsAsync(
        AppDbContext db,
        Order order,
        string pointOfService,
        List<FiscalOrderDocument> priorOrderForms,
        List<OrderItem> activeItems,
        string reason,
        bool cancelConnected = false)
    {
        var settings = await GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos." };

        if (priorOrderForms.Count == 0 || priorOrderForms.Any(f => !f.Mark.HasValue))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν υπάρχουν έγκυρα MARK για τα δελτία που ζητήθηκε να ακυρωθούν." };

        var series = await GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var externalSystemId = $"cancel-orderform-{order.Id}-{Guid.NewGuid():N}";
        var totals = CalculateTotals(activeItems);

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = BuildIssuer(settings),
            ["counterpart"] = new Dictionary<string, object?>
            {
                ["vatNumber"] = "",
                ["branch"] = 0,
                ["name"] = "Πελάτης Λιανικής",
                ["country"] = "GR"
            },
            ["invoiceHeader"] = new Dictionary<string, object?>
            {
                ["CancelConnected"] = cancelConnected ? 1 : 0,
                ["PointOfService"] = pointOfService,
                ["series"] = series.SeriesName,
                ["aa"] = series.NextNumber,
                ["issueDate"] = IssueDateToday(),
                ["invoiceType"] = settings.OrderFormInvoiceType,
                ["invoiceTypeUbl"] = "380",
                ["currency"] = "EUR",
                ["OpenPointOfService"] = 0,
                ["ClosePointOfService"] = 0
            },
            ["invoiceDetails"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["quantity"] = 1m,
                    ["entityName"] = string.IsNullOrWhiteSpace(reason) ? "Ακύρωση Δελτίου Παραγγελίας" : reason,
                    ["netValue"] = 0m,
                    ["totalValue"] = 0m,
                    ["vatCategory"] = 8,
                    ["vatCategoryUbl"] = "Z",
                    ["vatAmount"] = 0m,
                    ["vatPercent"] = 0m,
                    ["measurementUnit"] = 1,
                    ["measurementUnitUbl"] = "H87"
                }
            },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = 0m,
                ["totalVatAmount"] = 0m,
                ["totalValue"] = totals.Gross
            },
            ["correlatedInvoices"] = priorOrderForms
                .Where(f => f.Mark.HasValue)
                .Select(f => (object?)new Dictionary<string, object?> { ["mark"] = f.Mark!.Value })
                .ToList()
        };

        var source = new Dictionary<string, object?> { ["invoice"] = invoice };
        var result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId, transmissionType: 5);
        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        series.NextNumber++;
        db.FiscalOrderCancellations.Add(new FiscalOrderCancellation
        {
            OrderId = order.Id,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            Reason = reason
        });
        await db.SaveChangesAsync();

        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    public async Task<FiscalIssueResult> CancelWholeOrderAsync(
        AppDbContext db,
        Order order,
        string reason = "Ακύρωση ολόκληρης παραγγελίας από τον σερβιτόρο")
    {
        var settings = await GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos."
            };

        var activeItems = await db.OrderItems
            .Include(i => i.MenuItem).ThenInclude(i => i!.MenuCategory)
            .Where(i => i.OrderId == order.Id && !i.IsCancelled)
            .ToListAsync();

        if (activeItems.Count == 0)
            return await CloseEmptyOrderAsync(db, order);

        // Χωρίς πάροχο ή χωρίς φορολογικά Order Forms, η ακύρωση γίνεται μόνο τοπικά.
        if (!settings.UseParochosProvider || !settings.IssueOrderFormsToAade)
        {
            foreach (var item in activeItems)
            {
                item.IsCancelled = true;
                item.CancelledAt = DateTime.Now;
                item.CancelReason = reason;
            }

            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true };
        }

        var priorOrderForms = await db.FiscalOrderDocuments
            .Include(f => f.Items)
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .ToListAsync();

        // Αν δεν υπάρχει φορολογικό Order Form, δεν υπάρχει κάτι να ακυρωθεί
        // στον Parochos. Ακυρώνουμε μόνο τοπικά.
        if (priorOrderForms.Count == 0)
        {
            foreach (var item in activeItems)
            {
                item.IsCancelled = true;
                item.CancelledAt = DateTime.Now;
                item.CancelReason = reason;
            }

            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true };
        }

        var pointOfService = await ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού."
            };
        }

        // Parochos v3.0.29 (Hospitality - Cancel Connected): ολική ακύρωση δελτίων 8.6
        // γίνεται με CancelConnected = 1 και correlatedInvoices τα MARK τους.
        // Επειδή αυτό αναιρεί τα ανοιχτά δελτία του Point of Service, όταν στο ίδιο
        // τραπέζι υπάρχει άλλη ενεργή παραγγελία χρησιμοποιούμε 8.6 με recType = 7.
        var otherOpenOrderOnSameTable = await db.Orders
            .Where(o => o.Id != order.Id && o.RestaurantTableId == order.RestaurantTableId &&
                        o.Status == OrderStatus.Open && o.FiscalStatus != FiscalStatus.Issued)
            .AnyAsync(o => db.OrderItems.Any(i => i.OrderId == o.Id && !i.IsCancelled));

        var cancel = otherOpenOrderOnSameTable
            ? await IssueNegativeOrderFormAsync(db, order, pointOfService, activeItems, priorOrderForms)
            : await CancelOrderTicketsAsync(db, order, pointOfService, priorOrderForms, activeItems, reason, cancelConnected: true);
        if (!cancel.Success)
            return cancel;

        foreach (var form in priorOrderForms)
        {
            form.IsCancelled = true;
            form.CancelledAt = DateTime.Now;
            form.CancellationMark = cancel.Mark;
        }
        foreach (var item in activeItems)
        {
            item.IsCancelled = true;
            item.CancelledAt = DateTime.Now;
            item.CancelReason = reason;
        }
        await db.SaveChangesAsync();
        return await CloseEmptyOrderAsync(db, order);

    }

    public async Task<FiscalIssueResult> CloseEmptyOrderAsync(AppDbContext db, Order order)
    {
        var settings = await GetSettingsAsync(db);
        if (settings is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν οι ρυθμίσεις Parochos." };
        if (!settings.UseParochosProvider)
            return new FiscalIssueResult { Success = true, ProviderUsed = false };

        var hasActiveItems = await db.OrderItems
            .AnyAsync(i => i.OrderId == order.Id && !i.IsCancelled);

        if (hasActiveItems)
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Το τραπέζι δεν είναι άδειο. Υπάρχουν ενεργά είδη."
            };
        }

        // Αν δεν εκδόθηκε ποτέ Order Form για το συγκεκριμένο τραπέζι,
        // δεν υπάρχει Point of Service στον Parochos που να χρειάζεται κλείσιμο.
        if (!settings.IssueOrderFormsToAade)
            return new FiscalIssueResult { Success = true };

        var hadOrderForms = await db.FiscalOrderDocuments
            .AnyAsync(f => f.OrderId == order.Id && f.Mark != null);

        if (!hadOrderForms)
            return new FiscalIssueResult { Success = true };

        var pointOfService = await ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού."
            };
        }

        // A Point of Service may contain multiple independent local orders/tickets.
        // Closing one empty order must not close the provider table while another
        // order for the same restaurant table still has active items.
        var otherOpenOrderOnSameTable = await db.Orders
            .Where(o => o.Id != order.Id && o.RestaurantTableId == order.RestaurantTableId &&
                        o.Status == OrderStatus.Open && o.FiscalStatus != FiscalStatus.Issued)
            .AnyAsync(o => db.OrderItems.Any(i => i.OrderId == o.Id && !i.IsCancelled));

        if (otherOpenOrderOnSameTable)
        {
            _logger.LogInformation(
                "Δεν κλείνει το PointOfService {PointOfService}: υπάρχει άλλη ενεργή τοπική παραγγελία στο ίδιο τραπέζι.",
                pointOfService);
            return new FiscalIssueResult { Success = true };
        }

        var close = await _parochos.ClosePointOfServiceAsync(settings, pointOfService);

        if (!close.Success)
        {
            _logger.LogWarning(
                "Parochos ClosePointOfService απέτυχε για άδειο τραπέζι {PointOfService}: {Error}",
                pointOfService,
                close.ErrorMessage);

            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Απέτυχε το κλείσιμο του τραπεζιού στον Parochos: " + close.ErrorMessage
            };
        }

        _logger.LogInformation(
            "Parochos PointOfService {PointOfService} έκλεισε επιτυχώς για τραπέζι χωρίς ενεργά είδη.",
            pointOfService);

        return new FiscalIssueResult { Success = true };
    }

    public async Task<CardPaymentPreparation> PrepareSelectedCardPaymentAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> selectedItems,
        FiscalDocumentKind kind,
        BankPosTerminalSettings? selectedPos = null)
    {
        var items = selectedItems.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        var pos = selectedPos ?? await GetDefaultWebEcrPosAsync(db);
        if (CommonWebEcrProviderProfile.IsCommonRest(pos))
        {
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            if (!MellonConnectionSettings.IsConnected(pos!) || string.IsNullOrWhiteSpace(pos!.ParochosNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Το Mellon POS χρειάζεται ενεργή σύνδεση, φυσικό TID και Parochos NSP Code." };
            return await PrepareProviderDataAsync(db, order, items, kind,
                pos.ParochosNspCode.Trim(), pos.ProviderId > 0 ? pos.ProviderId : 19, pos.TerminalId.Trim(),
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEpayWebEcr(pos))
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var epayTerminalId = pos!.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(epayTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του ePay POS στην καρτέλα Τραπεζικά POS." };
            var epayNspCode = string.IsNullOrWhiteSpace(pos.ParochosNspCode) ? "2" : pos.ParochosNspCode.Trim();
            return await PrepareProviderDataAsync(db, order, items, kind,
                epayNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, epayTerminalId,
                useWebEcrSignatureForFiscalDocument: true);
        }
        if (IsEdpsWebEcr(pos))
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var edpsNspCode = !string.IsNullOrWhiteSpace(pos!.ParochosNspCode)
                ? pos.ParochosNspCode.Trim()
                : "5";
            var edpsTerminalId = pos.TerminalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(edpsNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το NSP Code του EDPS στην καρτέλα Τραπεζικά POS." };
            if (string.IsNullOrWhiteSpace(edpsTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του EDPS POS στην καρτέλα Τραπεζικά POS." };
            return await PrepareProviderDataAsync(db, order, items, kind, edpsNspCode,
                pos.ProviderId > 0 ? pos.ProviderId : 19, edpsTerminalId);
        }
        if (pos is { Provider: "Viva", Protocol: "Viva Cloud" })
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };
            var vivaTerminalId = pos.TerminalId?.Trim() ?? "";
            var vivaNspCode = pos.ParochosNspCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(vivaTerminalId))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το φυσικό Terminal ID του Viva POS." };
            if (string.IsNullOrWhiteSpace(vivaNspCode))
                return new CardPaymentPreparation { Success = false, ErrorMessage = "Συμπλήρωσε το Parochos NSP Code στις ρυθμίσεις Viva." };
            return await PrepareProviderDataAsync(db, order, items, kind,
                vivaNspCode, pos.ProviderId > 0 ? pos.ProviderId : 19, vivaTerminalId);
        }
        var settings = await GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            if (items.Count == 0) return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
            return new CardPaymentPreparation { Success = true, Amount = CalculateTotals(items).Gross };
        }
        if (!ValidateSettings(settings, out var error))
            return new CardPaymentPreparation { Success = false, ErrorMessage = error };
        if (items.Count == 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν επιλέχθηκαν είδη." };
        var terminalId = pos?.TerminalId?.Trim() ?? "";
        var nspCode = pos?.ParochosNspCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(nspCode))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Επίλεξε POS και συμπλήρωσε Terminal ID και Parochos NSP Code στην καρτέλα Τραπεζικά POS." };
        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        var series = await GetOrCreateSeriesAsync(db, kind);
        var lastNumber = await db.FiscalOrderDocuments.Where(x => x.SeriesName == series.SeriesName && x.Mark != null).Select(x => (long?)x.Number).MaxAsync() ?? 0L;
        var invoiceNumber = Math.Max((long)series.NextNumber, lastNumber + 1L);
        var externalSystemId = $"payment-{order.Id}-{Guid.NewGuid():N}";
        var totals = CalculateTotals(items);
        var invoiceType = kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType;

        var payment = await _parochos.RequestPaymentAsync(
            settings,
            externalSystemId,
            settings.VatNumber,
            DateTimeOffset.Now,
            settings.Branch,
            invoiceType,
            series.SeriesName,
            invoiceNumber,
            totals.Net,
            totals.Vat,
            totals.Gross,
            totals.Gross,
            terminalId,
            nspCode);

        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature))
            return new CardPaymentPreparation { Success = false, ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε payment signature." };

        return new CardPaymentPreparation
        {
            Success = true,
            ExternalSystemId = externalSystemId,
            SeriesName = series.SeriesName,
            InvoiceNumber = invoiceNumber,
            Signature = payment.Signature,
            Uid = payment.Uid,
            TerminalId = terminalId,
            NspCode = nspCode,
            Amount = totals.Gross
        };
    }

    private static string NormalizeParochosSignatureForWebEcr(string signature)
    {
        var value = signature.Trim();
        if (value.Length > 0 && value.All(Uri.IsHexDigit))
            return value.ToUpperInvariant();

        var base64 = value.Replace('-', '+').Replace('_', '/');
        var remainder = base64.Length % 4;
        if (remainder == 1)
            throw new FormatException("Parochos signature is neither HEX nor valid Base64.");
        if (remainder != 0)
            base64 = base64.PadRight(base64.Length + (4 - remainder), '=');

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new FormatException("Parochos signature is neither HEX nor valid Base64."); }

        // AADE provider signatures are ECDSA P-256 encoded as ASN.1/DER and then HEX.
        // A DER ECDSA signature is normally 70–72 bytes; a 96-character Base64 value
        // can represent the same 72-byte signature and must be converted to HEX for WebECR.
        if (signatureBytes.Length is < 70 or > 72 || signatureBytes[0] != 0x30 || signatureBytes[1] != signatureBytes.Length - 2)
            throw new FormatException("Parochos Base64 signature is not an ASN.1/DER ECDSA signature.");

        return Convert.ToHexString(signatureBytes);
    }

    private static bool TryParseSignedAmountToCents(string value, out int cents)
    {
        cents = 0;
        var text = value.Trim();
        if (text.Length == 0) return false;

        // The Parochos API example uses integer minor units, while some responses
        // have been observed with decimal euro values. Keep both representations
        // explicit so a decimal value is never accidentally treated as cents.
        var hasDecimalSeparator = text.Contains('.') || text.Contains(',');
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            return false;

        try
        {
            cents = decimal.ToInt32(decimal.Round(hasDecimalSeparator ? amount * 100m : amount,
                0, MidpointRounding.AwayFromZero));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadSignedPaymentAmounts(
        string[]? parts,
        int expectedNetCents,
        int expectedVatCents,
        int expectedTotalCents,
        out int netCents,
        out int vatCents,
        out int totalCents,
        out int paymentCents,
        out string fieldOrder,
        out string signedTuple)
    {
        netCents = vatCents = totalCents = paymentCents = 0;
        fieldOrder = "unknown";
        signedTuple = parts is { Length: >= 7 }
            ? string.Join(";", parts.Skip(3).Take(4).Select(x => x.Trim()))
            : "<missing>";

        if (parts is not { Length: >= 8 }) return false;
        var values = parts.Skip(3).Take(4).Select(x => x.Trim()).ToArray();
        var parsed = new int[4];
        for (var i = 0; i < values.Length; i++)
            if (!TryParseSignedAmountToCents(values[i], out parsed[i])) return false;

        // Parochos API 3.0.29 response example: Net;VAT;Total;Payment.
        var parochosOrderMatches = parsed[0] == expectedNetCents &&
            parsed[1] == expectedVatCents && parsed[2] == expectedTotalCents &&
            parsed[3] == expectedTotalCents;
        // AADE token-crypto proposal: Payment;Net;VAT;Total.
        var aadeOrderMatches = parsed[0] == expectedTotalCents &&
            parsed[1] == expectedNetCents && parsed[2] == expectedVatCents &&
            parsed[3] == expectedTotalCents;

        if (parochosOrderMatches)
        {
            netCents = parsed[0]; vatCents = parsed[1]; totalCents = parsed[2]; paymentCents = parsed[3];
            fieldOrder = "Parochos Net;VAT;Total;Payment";
            return true;
        }
        if (aadeOrderMatches)
        {
            paymentCents = parsed[0]; netCents = parsed[1]; vatCents = parsed[2]; totalCents = parsed[3];
            fieldOrder = "AADE Payment;Net;VAT;Total";
            return true;
        }
        return false;
    }

    private async Task<CardPaymentPreparation> PrepareProviderDataAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> items,
        FiscalDocumentKind kind,
        string nspCode,
        int providerId,
        string terminalId,
        bool useWebEcrSignatureForFiscalDocument = false)
    {
        var activeItems = items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        if (activeItems.Count == 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Δεν υπάρχουν ενεργά είδη με ποσότητα για πληρωμή. Δεν στάλθηκε αίτημα στον Parochos." };

        // Some callers provide OrderItems without their MenuItem navigation loaded.
        // Resolve prices explicitly here so RequestPayment never receives zero totals
        // merely because the caller used Include(Items) without Include(MenuItem).
        var menuItemIds = activeItems.Select(i => i.MenuItemId).Distinct().ToArray();
        var menuItemsById = await db.MenuItems
            .Include(menuItem => menuItem.MenuCategory)
            .Where(menuItem => menuItemIds.Contains(menuItem.Id))
            .ToDictionaryAsync(menuItem => menuItem.Id);
        var missingMenuItemIds = activeItems
            .Where(item => !menuItemsById.ContainsKey(item.MenuItemId))
            .Select(item => item.MenuItemId)
            .Distinct()
            .ToArray();
        if (missingMenuItemIds.Length > 0)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Δεν βρέθηκαν τιμές καταλόγου για τα MenuItemId: {string.Join(", ", missingMenuItemIds)}. Δεν στάλθηκε αίτημα στον Parochos."
            };
        foreach (var item in activeItems) item.MenuItem = menuItemsById[item.MenuItemId];
        items = activeItems;

        var settings = await GetSettingsAsync(db);
        if (!ValidateSettings(settings, out var validationError))
            return new CardPaymentPreparation { Success = false, ErrorMessage = validationError };
        if (string.IsNullOrWhiteSpace(terminalId))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για υπογραφή ProviderData απαιτείται το Terminal ID του επιλεγμένου POS." };
        if (string.IsNullOrWhiteSpace(nspCode))
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Για υπογραφή ProviderData απαιτείται Parochos NSP Code." };
        if (providerId <= 0)
            return new CardPaymentPreparation { Success = false, ErrorMessage = "Το ProviderId της υπογραφής Parochos πρέπει να είναι θετικός ακέραιος." };

        var totals = CalculateTotals(items);
        if (totals.Gross <= 0m || totals.Net <= 0m)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Δεν υπολογίστηκαν έγκυρες καθαρές και μικτές αξίες για την πληρωμή. Έλεγξε ότι φορτώθηκαν τα είδη και οι τιμές τους. Δεν ξεκίνησε χρέωση."
            };
        var series = await GetOrCreateSeriesAsync(db, kind);
        var lastNumber = await db.FiscalOrderDocuments.Where(x => x.SeriesName == series.SeriesName && x.Mark != null).Select(x => (long?)x.Number).MaxAsync() ?? 0L;
        var invoiceNumber = Math.Max((long)series.NextNumber, lastNumber + 1L);
        var externalSystemId = $"payment-{order.Id}-{Guid.NewGuid():N}";
        var invoiceType = kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType;
        var payment = await _parochos.RequestPaymentAsync(settings, externalSystemId, settings.VatNumber, DateTimeOffset.Now,
            settings.Branch, invoiceType, series.SeriesName, invoiceNumber, totals.Net, totals.Vat, totals.Gross, totals.Gross,
            terminalId.Trim(), nspCode.Trim());
        if (!payment.Success || string.IsNullOrWhiteSpace(payment.Signature) || string.IsNullOrWhiteSpace(payment.Uid) || payment.Timestamp is null)
            return new CardPaymentPreparation { Success = false, ErrorMessage = payment.ErrorMessage ?? "Ο Parochos δεν επέστρεψε πλήρη υπογραφή πληρωμής (UID/Signature/Timestamp)." };

        string webEcrSignature;
        try { webEcrSignature = NormalizeParochosSignatureForWebEcr(payment.Signature); }
        catch (FormatException ex)
        {
            return new CardPaymentPreparation { Success = false, ErrorMessage = ex.Message + " No charge was started." };
        }

        // WebECR requires yyyyMMddHHmmss in Greece local time. Normalize the timestamp
        // from Parochos signatureClearText without changing the represented instant.
        var signatureClearTextParts = payment.SignatureClearText?.Split(';');
        var signedTimestampText = signatureClearTextParts is { Length: >= 3 }
            ? signatureClearTextParts[2].Trim()
            : null;
        var athensTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Athens");
        DateTimeOffset? signedTimestamp = null;
        if (signedTimestampText is { Length: 14 } &&
                 DateTime.TryParseExact(signedTimestampText, "yyyyMMddHHmmss",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedCompactTimestamp))
        {
            var localTimestamp = DateTime.SpecifyKind(parsedCompactTimestamp, DateTimeKind.Unspecified);
            signedTimestamp = new DateTimeOffset(localTimestamp, athensTimeZone.GetUtcOffset(localTimestamp));
        }
        else if (!string.IsNullOrWhiteSpace(signedTimestampText) &&
                 DateTimeOffset.TryParse(signedTimestampText, CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces, out var parsedIsoTimestamp))
        {
            signedTimestamp = parsedIsoTimestamp;
        }

        string? signatureTimestamp = null;
        if (signedTimestamp.HasValue &&
            Math.Abs((signedTimestamp.Value.UtcDateTime - payment.Timestamp.Value.UtcDateTime).TotalSeconds) <= 1.1)
        {
            signatureTimestamp = TimeZoneInfo.ConvertTime(signedTimestamp.Value, athensTimeZone)
                .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }
        if (signatureTimestamp is null)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Could not match the signatureClearText timestamp to the Parochos response timestamp. Signed value: '{signedTimestampText ?? "<missing>"}', response timestamp: '{payment.Timestamp:O}'. No charge was started."
            };

        var expectedNetCents = decimal.ToInt32(decimal.Round(totals.Net * 100m, 0, MidpointRounding.AwayFromZero));
        var expectedVatCents = decimal.ToInt32(decimal.Round(totals.Vat * 100m, 0, MidpointRounding.AwayFromZero));
        var expectedTotalCents = decimal.ToInt32(decimal.Round(totals.Gross * 100m, 0, MidpointRounding.AwayFromZero));
        var providerTokenAmountCents = decimal.ToInt32(decimal.Round(payment.Amount * 100m, 0, MidpointRounding.AwayFromZero));
        if (providerTokenAmountCents != expectedTotalCents)
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Το ποσό που υπέγραψε ο Parochos ({providerTokenAmountCents} λεπτά) δεν ισούται με το σύνολο της παραγγελίας ({expectedTotalCents} λεπτά). Δεν ξεκίνησε χρέωση."
            };
        }

        if (!TryReadSignedPaymentAmounts(signatureClearTextParts, expectedNetCents, expectedVatCents,
                expectedTotalCents, out var signedNetCents, out var signedVatCents,
                out var signedTotalCents, out var signedPaymentCents, out var signedFieldOrder,
                out var signedAmountTuple))
        {
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = $"Τα ποσά που υπέγραψε ο Parochos δεν συμφωνούν με την παραγγελία ή δεν αναγνωρίζονται. " +
                    $"Υπογεγραμμένη τετράδα='{signedAmountTuple}', αναμενόμενα λεπτά ευρώ: καθαρή={expectedNetCents}, " +
                    $"ΦΠΑ={expectedVatCents}, σύνολο/πληρωμή={expectedTotalCents}. Δεν ξεκίνησε χρέωση."
            };
        }

        long? mark = null;
        var signatureDataMismatches = new List<string>();
        if (signatureClearTextParts is not { Length: >= 8 })
        {
            signatureDataMismatches.Add("signatureClearText has fewer than 8 fields");
        }
        else
        {
            if (!string.Equals(signatureClearTextParts[0].Trim(), payment.Uid, StringComparison.OrdinalIgnoreCase))
                signatureDataMismatches.Add("UID differs from signed UID");
            if (!string.IsNullOrWhiteSpace(signatureClearTextParts[1]))
            {
                if (long.TryParse(signatureClearTextParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMark))
                    mark = parsedMark;
                else
                    signatureDataMismatches.Add("signed MARK is not numeric");
            }
            if (!string.Equals(signatureClearTextParts[7].Trim(), terminalId.Trim(), StringComparison.OrdinalIgnoreCase))
                signatureDataMismatches.Add($"TerminalId in signed text is '{signatureClearTextParts[7]}' but selected POS TID is '{terminalId}'");
        }
        if (signatureDataMismatches.Count > 0)
            return new CardPaymentPreparation
            {
                Success = false,
                ErrorMessage = "Parochos signatureClearText does not match the payment fields: " + string.Join("; ", signatureDataMismatches) + ". No charge was started."
            };

        // Use the amounts proven by Parochos' signatureClearText. The parser above
        // has already checked them against the order, in either documented field order.
        var providerData = new ProviderPaymentData(payment.Uid!, signatureTimestamp,
            signedNetCents, signedVatCents, signedTotalCents, providerId, webEcrSignature, mark,
            signedAmountTuple, signedFieldOrder, payment.Signature.Trim());
        return new CardPaymentPreparation { Success = true, ExternalSystemId = externalSystemId, SeriesName = series.SeriesName,
            InvoiceNumber = invoiceNumber, Signature = useWebEcrSignatureForFiscalDocument ? webEcrSignature : payment.Signature, Uid = payment.Uid, TerminalId = terminalId.Trim(),
            NspCode = nspCode.Trim(), Amount = signedPaymentCents / 100m, ProviderData = providerData };
    }

    /// <summary>
    /// Εκδίδει τελικό παραστατικό μόνο για τα επιλεγμένα είδη μιας παραγγελίας.
    /// Η μέθοδος δεν κλείνει το Point of Service όταν παραμένουν ενεργά είδη.
    /// </summary>
    public async Task<FiscalIssueResult> IssueSelectedItemsPaymentAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> selectedItems,
        PaymentMethod paymentMethod,
        FiscalDocumentKind kind,
        string? cardTransactionId = null,
        string? cardSignature = null,
        string? cardTerminalId = null,
        string? cardNspCode = null,
        string? paymentExternalSystemId = null,
        string? paymentSeriesName = null,
        long? paymentInvoiceNumber = null)
    {
        var items = selectedItems.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        if (items.Count == 0)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν επιλέχθηκαν ενεργά είδη για πληρωμή." };

        var settings = await GetSettingsAsync(db);
        if (settings is { UseParochosProvider: false })
        {
            var localTotals = CalculateTotals(items);
            var localPayment = new OrderPayment
            {
                OrderId = order.Id, Amount = localTotals.Gross, PaymentMethod = paymentMethod,
                DocumentKind = kind, FiscalStatus = FiscalStatus.PaidLocally,
                CardTransactionId = cardTransactionId, CardTerminalId = cardTerminalId,
                CardNspCode = cardNspCode, PaymentSignature = cardSignature
            };
            foreach (var item in items)
                localPayment.Items.Add(new OrderPaymentItem { OrderItemId = item.Id, Quantity = item.Quantity });
            db.OrderPayments.Add(localPayment);
            await db.SaveChangesAsync();
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        }
        if (!ValidateSettings(settings, out var validationError))
            return new FiscalIssueResult { Success = false, ErrorMessage = validationError };

        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Για τιμολόγιο χρειάζεται ΑΦΜ πελάτη." };

        if (paymentMethod == PaymentMethod.Card &&
            (string.IsNullOrWhiteSpace(paymentExternalSystemId) ||
             string.IsNullOrWhiteSpace(paymentSeriesName) ||
             paymentInvoiceNumber is null or <= 0))
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Λείπουν τα στοιχεία της αρχικής υπογραφής Parochos (ExternalSystemId/σειρά/ΑΑ). Δεν στάλθηκε παραστατικό."
            };

        var series = await GetOrCreateSeriesAsync(db, kind);
        var seriesName = paymentMethod == PaymentMethod.Card
            ? paymentSeriesName!.Trim()
            : series.SeriesName;
        var lastNumber = await db.FiscalOrderDocuments
            .Where(x => x.SeriesName == seriesName && x.Mark != null)
            .Select(x => (long?)x.Number)
            .MaxAsync() ?? 0L;
        var invoiceNumber = paymentMethod == PaymentMethod.Card
            ? paymentInvoiceNumber!.Value
            : Math.Max((long)series.NextNumber, lastNumber + 1L);
        if (invoiceNumber > int.MaxValue)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Το ΑΑ της σειράς ξεπέρασε το επιτρεπτό όριο." };

        var externalSystemId = paymentMethod == PaymentMethod.Card
            ? paymentExternalSystemId!.Trim()
            : $"payment-{order.Id}-{Guid.NewGuid():N}";
        var totals = CalculateTotals(items);
        var pointOfService = await ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το Point of Service του τραπεζιού." };

        var payment = new Dictionary<string, object?>
        {
            ["type"] = paymentMethod == PaymentMethod.Card ? 7 : ParsePaymentCode(settings!.CashPaymentTypeCode, 1),
            ["amount"] = totals.Gross
        };
        if (paymentMethod == PaymentMethod.Card)
        {
            payment["signature"] = cardSignature;
            payment["transactionId"] = cardTransactionId;
            payment["terminalId"] = cardTerminalId;
            payment["tipAmount"] = 0m;
        }

        // Always correlate split receipts to the ORIGINAL Order Form for this table.
        // Do not create or select a new Order Form for each split payment.
        var originalOrderForm = await db.FiscalOrderDocuments
            .Where(f => f.OrderId == order.Id && f.Mark != null && !f.IsCancelled)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync();
        if (originalOrderForm is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκε το αρχικό δελτίο παραγγελίας του τραπεζιού. Δεν εκδόθηκε απόδειξη ώστε να αποφευχθεί ασύνδετο παραστατικό." };

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = BuildIssuer(settings!),
            ["counterpart"] = kind == FiscalDocumentKind.Invoice ? BuildInvoiceCounterpart(order) : BuildRetailCounterpart(),
            ["invoiceHeader"] = new Dictionary<string, object?>
            {
                ["series"] = seriesName,
                ["aa"] = invoiceNumber,
                ["issueDate"] = IssueDateToday(),
                ["currency"] = "EUR",
                ["invoiceType"] = kind == FiscalDocumentKind.Invoice ? settings.InvoiceInvoiceType : settings.ReceiptInvoiceType,
                ["invoiceTypeUbl"] = "380",
                ["selfPricing"] = false,
                ["fuelInvoice"] = false,
                ["OpenPointOfService"] = 0,
                ["ClosePointOfService"] = 0,
                ["PointOfService"] = pointOfService
            },
            ["invoiceDetails"] = BuildLines(
                items,
                kind == FiscalDocumentKind.Invoice ? "E3_561_001" : "E3_561_003"),
            ["paymentMethods"] = new List<object?> { payment },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = totals.Net,
                ["totalVatAmount"] = totals.Vat,
                ["totalValue"] = totals.Gross
            },
            // Each split receipt references the same initial table Order Form MARK.
            ["correlatedInvoices"] = new List<object?>
            {
                new Dictionary<string, object?> { ["mark"] = originalOrderForm.Mark!.Value }
            }
        };

        _logger.LogInformation(
            "SPLIT RECEIPT CORRELATION >>> OrderId={OrderId}, OriginalOrderFormId={OrderFormId}, OrderFormMark={OrderFormMark}, ReceiptExternalSystemId={ReceiptExternalSystemId}, Amount={Amount}",
            order.Id, originalOrderForm.Id, originalOrderForm.Mark, externalSystemId, totals.Gross);

        var source = new Dictionary<string, object?> { ["invoice"] = invoice };
        var result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);
        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId ?? "", externalSystemId);
        if (!assign.Success && assign.ErrorCode != "00130002")
        {
            return new FiscalIssueResult
            {
                Success = false,
                Mark = result.Mark,
                QrCode = result.QrCode,
                ErrorMessage = "Το παραστατικό εκδόθηκε με MARK " + result.Mark + " αλλά απέτυχε το AssignDocument: " + assign.ErrorMessage
            };
        }

        // Connect every split receipt to the SAME original Order Form, not to a newly
        // generated form and not only to forms whose item IDs happen to match.
        var previousPaymentMarks = await db.OrderPayments
            .Where(p => p.OrderId == order.Id && p.FiscalStatus == FiscalStatus.Issued && p.Mark != null)
            .Select(p => p.Mark!.Value)
            .ToListAsync();
        var marksToConnect = previousPaymentMarks
            .Append(result.Mark!.Value)
            .Distinct()
            .ToList();

        var connect = await _parochos.ConnectDocumentsAsync(
            settings,
            originalOrderForm.ProcessId,
            originalOrderForm.ExternalSystemId,
            marksToConnect);

        _logger.LogInformation(
            "SPLIT RECEIPT CONNECT RESULT >>> OrderFormMark={OrderFormMark}, ReceiptMark={ReceiptMark}, SubmittedMarks={SubmittedMarks}, Success={Success}, ErrorCode={ErrorCode}, Error={Error}",
            originalOrderForm.Mark, result.Mark, string.Join(",", marksToConnect), connect.Success, connect.ErrorCode, connect.ErrorMessage);

        if (!connect.Success)
        {
            return new FiscalIssueResult
            {
                Success = false,
                Mark = result.Mark,
                QrCode = result.QrCode,
                ErrorMessage = "Η απόδειξη εκδόθηκε (MARK " + result.Mark + "), αλλά η σύνδεση με το ΑΡΧΙΚΟ δελτίο παραγγελίας (MARK " + originalOrderForm.Mark + ") απέτυχε: " + connect.ErrorMessage
            };
        }

        var paymentRecord = new OrderPayment
        {
            OrderId = order.Id,
            Amount = totals.Gross,
            PaymentMethod = paymentMethod,
            DocumentKind = kind,
            FiscalStatus = FiscalStatus.Issued,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            Uid = result.Uid,
            QrCode = result.QrCode,
            AuthenticationCode = result.AuthenticationCode,
            PdfUrl = result.PdfFileUrl,
            SeriesName = seriesName,
            InvoiceNumber = invoiceNumber,
            CardTransactionId = cardTransactionId,
            CardTerminalId = cardTerminalId,
            CardNspCode = cardNspCode,
            PaymentSignature = cardSignature
        };

        foreach (var item in items)
        {
            paymentRecord.Items.Add(new OrderPaymentItem
            {
                OrderItemId = item.Id,
                Quantity = item.Quantity
            });
        }
        db.OrderPayments.Add(paymentRecord);
        series.NextNumber = checked((int)(invoiceNumber + 1L));
        await db.SaveChangesAsync();

        var remaining = await db.OrderItems
            .Where(i => i.OrderId == order.Id && !i.IsCancelled)
            .ToListAsync();
        var paidQuantities = items
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var remainingItems = remaining
            .Where(i => !paidQuantities.TryGetValue(i.Id, out var paid) || paid < i.Quantity)
            .ToList();

        if (remainingItems.Count == 0)
        {
            var close = await _parochos.ClosePointOfServiceAsync(settings, pointOfService);
            if (!close.Success)
            {
                return new FiscalIssueResult
                {
                    Success = false,
                    Mark = result.Mark,
                    QrCode = result.QrCode,
                    ErrorMessage = "Το παραστατικό εκδόθηκε, αλλά απέτυχε το κλείσιμο του τραπεζιού: " + close.ErrorMessage
                };
            }
        }

        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    public async Task<FiscalIssueResult> IssueOrderFormAsync(AppDbContext db, Order order, List<OrderItem> newlyAddedItems)
    {
        var settings = await GetSettingsAsync(db);
        if (settings is null || !settings.UseParochosProvider || !settings.IssueOrderFormsToAade)
            return new FiscalIssueResult { Success = true, ProviderUsed = false };
        if (string.IsNullOrWhiteSpace(settings.OrderFormInvoiceType))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχει οριστεί ο κωδικός Order Form. Ο Parochos v3 ορίζει το 8.6 για Order Forms." };
        if (string.IsNullOrWhiteSpace(settings.VatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν έχει οριστεί ΑΦΜ επιχείρησης." };
        if (newlyAddedItems.Count == 0)
            return new FiscalIssueResult { Success = true };

        var series = await GetOrCreateSeriesAsync(db, FiscalDocumentKind.OrderForm, settings.OrderFormSeriesName);
        var thisNumber = series.NextNumber;
        var isFirstOrderForm = !await db.FiscalOrderDocuments.AnyAsync(f => f.OrderId == order.Id);
        var externalSystemId = $"orderform-{order.Id}-{Guid.NewGuid():N}";
        var pointOfService = await ResolvePointOfServiceAsync(db, order);
        if (string.IsNullOrWhiteSpace(pointOfService))
        {
            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = "Δεν βρέθηκε το Point of Service (τραπέζι) της παραγγελίας."
            };
        }

        var openPointOfService = 0;

        if (isFirstOrderForm)
        {
            var open = await _parochos.OpenPointOfServiceAsync(settings, pointOfService);

            if (open.Success)
            {
                openPointOfService = 0;

                _logger.LogInformation(
                    "Parochos PointOfService {PointOfService} άνοιξε επιτυχώς.",
                    pointOfService);
            }
            else
            {
                // Το τραπέζι μπορεί να είναι ήδη ανοιχτό στον Parochos.
                // Σε αυτή την περίπτωση ΔΕΝ ξαναζητάμε άνοιγμα.
                _logger.LogWarning(
                    "Parochos OpenPointOfService απέτυχε για {PointOfService}: {Error}. Θα συνεχίσουμε με OpenPointOfService=0.",
                    pointOfService,
                    open.ErrorMessage);
            }
        }

        var lines = BuildLines(newlyAddedItems);
        var totals = CalculateTotals(newlyAddedItems);

        // Ο beta λογαριασμός του Parochos μπορεί να περιέχει ήδη
        // Order Form με τον ίδιο ΑΑ, ενώ η τοπική βάση δεν το γνωρίζει.
        // Αν επιστραφεί 00010022, προχωράμε στον επόμενο ΑΑ και ξαναστέλνουμε.
        const int maxDuplicateRetries = 5;
        ParochosSendResult result = new();

        for (var attempt = 0; attempt <= maxDuplicateRetries; attempt++)
        {
            var source = new Dictionary<string, object?>
            {
                ["invoice"] = new Dictionary<string, object?>
                {
                    ["issuer"] = BuildIssuer(settings),
                    // Για Order Form 8.6 η ΑΑΔΕ δεν πρέπει να πάρει τον ίδιο
                    // ΑΦΜ με τον εκδότη. Χρησιμοποιούμε κενό ΑΦΜ, όπως στο
                    // παράδειγμα Order Form του Parochos για λιανική.
                    ["counterpart"] = new Dictionary<string, object?>
                    {
                        ["vatNumber"] = "",
                        ["branch"] = 0,
                        ["name"] = "Πελάτης Λιανικής",
                        ["country"] = "GR"
                    },
                    ["invoiceHeader"] = new Dictionary<string, object?>
                    {
                        ["series"] = series.SeriesName,
                        ["aa"] = thisNumber,
                        ["issueDate"] = IssueDateToday(),
                        ["currency"] = "EUR",
                        ["invoiceType"] = settings.OrderFormInvoiceType,
                        ["invoiceTypeUbl"] = "380",
                        ["selfPricing"] = false,
                        ["fuelInvoice"] = false,
                        ["OpenPointOfService"] = openPointOfService,
                        ["ClosePointOfService"] = 0,
                        ["PointOfService"] = pointOfService
                    },
                    ["invoiceDetails"] = lines,
                    ["paymentMethods"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = 5, ["amount"] = 0m }
                    },
                    ["invoiceSummary"] = new Dictionary<string, object?>
                    {
                        ["totalNetValue"] = totals.Net,
                        ["totalVatAmount"] = totals.Vat,
                        ["totalValue"] = totals.Gross
                    }
                }
            };

            _logger.LogInformation(
                "PAROCHOS ORDER FORM SEND >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA} | Attempt={Attempt}/{MaxAttempts}",
                externalSystemId,
                series.SeriesName,
                thisNumber,
                attempt + 1,
                maxDuplicateRetries + 1);

            LogFiscalIssueDate("ORDER_FORM", externalSystemId, source);

            result = await _parochos.SendAndWaitAsync(settings, source, externalSystemId);

            if (result.Success)
            {
                _logger.LogInformation(
                    "PAROCHOS ORDER FORM SUCCESS >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA} | MARK={Mark}",
                    externalSystemId,
                    series.SeriesName,
                    thisNumber,
                    result.Mark);
                break;
            }

            // 00010022 = υπάρχει ήδη καταχωρημένο παραστατικό με τον ίδιο κωδικό.
            if (result.ErrorCode == "00010022")
            {
                _logger.LogWarning(
                    "PAROCHOS ORDER FORM DUPLICATE >>> Series={Series} | AA={AA}. Θα δοκιμαστεί ο επόμενος αριθμός. Attempt={Attempt}/{MaxAttempts}",
                    series.SeriesName,
                    thisNumber,
                    attempt + 1,
                    maxDuplicateRetries + 1);

                if (attempt == maxDuplicateRetries)
                {
                    return new FiscalIssueResult
                    {
                        Success = false,
                        ErrorMessage =
                            $"Ο Parochos απέρριψε τον αριθμό {series.SeriesName} {thisNumber} ως ήδη καταχωρημένο " +
                            $"μετά από {maxDuplicateRetries + 1} προσπάθειες. Τελευταίο σφάλμα: {result.ErrorMessage}"
                    };
                }

                thisNumber++;
                series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber));
                continue;
            }

            _logger.LogError(
                "PAROCHOS ORDER FORM FAILED >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA} | ErrorCode={ErrorCode} | Error={Error}",
                externalSystemId,
                series.SeriesName,
                thisNumber,
                result.ErrorCode,
                result.ErrorMessage);

            return new FiscalIssueResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage
            };
        }

        var document = new FiscalOrderDocument
        {
            OrderId = order.Id,
            ExternalSystemId = externalSystemId,
            SeriesName = series.SeriesName,
            Number = thisNumber,
            Mark = result.Mark,
            Uid = result.Uid,
            ProcessId = result.ProcessId
        };

        db.FiscalOrderDocuments.Add(document);

        // Κρατάμε ακριβώς ποια είδη περιείχε το συγκεκριμένο Order Form.
        // Αυτό είναι απαραίτητο για split payments και για μεταφορά επιλεγμένων ειδών.
        foreach (var item in newlyAddedItems.Where(i => !i.IsCancelled))
        {
            document.Items.Add(new FiscalOrderDocumentItem
            {
                OrderItemId = item.Id,
                Quantity = item.Quantity
            });
        }

        // Ο επόμενος αριθμός πρέπει να είναι μετά από αυτόν που τελικά χρησιμοποιήθηκε.
        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, thisNumber + 1L));

        if (!string.IsNullOrWhiteSpace(result.ProcessId))
        {
            var assign = await _parochos.AssignDocumentAsync(settings, pointOfService, result.ProcessId!, externalSystemId);
            if (!assign.Success)
                _logger.LogWarning("Order Form {ExternalSystemId} εκδόθηκε αλλά AssignDocument απέτυχε: {Error}", externalSystemId, assign.ErrorMessage);
        }

        await db.SaveChangesAsync();

        return new FiscalIssueResult
        {
            Success = true,
            Mark = result.Mark,
            QrCode = result.QrCode
        };
    }

    private static Dictionary<string, object?> BuildInvoice(
        CompanySettings settings,
        Order order,
        FiscalDocumentKind kind,
        string seriesName,
        long invoiceNumber,
        (decimal Net, decimal Vat, decimal Gross) totals,
        string invoiceType,
        string? pointOfService,
        bool closePointOfService,
        List<FiscalOrderDocument> orderForms,
        bool openPointOfService = false)
    {
        var payment = new Dictionary<string, object?>
        {
            ["type"] = order.PaymentMethod == PaymentMethod.Card ? 7 : ParsePaymentCode(settings.CashPaymentTypeCode, 3),
            ["amount"] = totals.Gross
        };

        if (order.PaymentMethod == PaymentMethod.Card)
        {
            payment["signature"] = order.ParochosPaymentSignature;
            payment["transactionId"] = order.CardTransactionId;
            payment["terminalId"] = order.CardTerminalId;
            payment["tipAmount"] = 0m;
        }

        var invoice = new Dictionary<string, object?>
        {
            ["issuer"] = BuildIssuer(settings),
            ["counterpart"] = kind == FiscalDocumentKind.Invoice ? BuildInvoiceCounterpart(order) : BuildRetailCounterpart(),
            ["invoiceHeader"] = new Dictionary<string, object?>
            {
                ["series"] = seriesName,
                ["aa"] = invoiceNumber,
                ["issueDate"] = IssueDateToday(),
                ["currency"] = "EUR",
                ["invoiceType"] = invoiceType,
                ["invoiceTypeUbl"] = "380",
                ["selfPricing"] = false,
                ["fuelInvoice"] = false,
                ["OpenPointOfService"] = openPointOfService ? 1 : 0,
                ["ClosePointOfService"] = closePointOfService ? 1 : 0,
                ["PointOfService"] = pointOfService
            },
            ["invoiceDetails"] = BuildLines(
                order.Items.Where(i => !i.IsCancelled),
                kind == FiscalDocumentKind.Invoice ? "E3_561_001" : "E3_561_003"),
            ["paymentMethods"] = new List<object?> { payment },
            ["invoiceSummary"] = new Dictionary<string, object?>
            {
                ["totalNetValue"] = totals.Net,
                ["totalVatAmount"] = totals.Vat,
                ["totalValue"] = totals.Gross
            },
            // For split payments, every accounting document must explicitly
            // reference the original restaurant Order Form (8.6) by MARK.
            ["correlatedInvoices"] = orderForms
                .Where(f => f.Mark.HasValue)
                .Select(f => (object?)new Dictionary<string, object?>
                {
                    ["mark"] = f.Mark!.Value
                })
                .ToList()
        };

        // Order Forms (8.6) are also connected to the final Accounting Document
        // through Parochos /api/OrderForms/ConnectDocuments after the final
        // document receives its MARK.
        return invoice;
    }

    private static Dictionary<string, object?> BuildIssuer(CompanySettings settings) => new()
    {
        ["vatNumber"] = settings.VatNumber,
        ["branch"] = settings.Branch,
        ["country"] = settings.Country,
        ["city"] = settings.City,
        ["street"] = settings.Street,
        ["streetNumber"] = settings.StreetNumber,
        ["postalCode"] = settings.PostalCode,
        ["contact"] = new Dictionary<string, object?>
        {
            ["name"] = settings.CompanyName,
            ["telephone"] = settings.ContactPhone,
            ["electronicMail"] = settings.ContactEmail
        }
    };

    private static Dictionary<string, object?> BuildRetailCounterpart() => new()
    {
        ["vatNumber"] = "000000000",
        ["branch"] = 0,
        ["name"] = "Ιδιώτης πελάτης",
        ["country"] = "GR"
    };

    private static Dictionary<string, object?> BuildInvoiceCounterpart(Order order) => new()
    {
        ["vatNumber"] = order.CounterpartVatNumber,
        ["branch"] = 0,
        ["name"] = order.CounterpartName,
        ["country"] = "GR",
        ["city"] = order.CounterpartCity,
        ["street"] = order.CounterpartStreet,
        ["streetNumber"] = order.CounterpartStreetNumber,
        ["postalCode"] = order.CounterpartPostalCode
    };

    private static List<object?> BuildLines(IEnumerable<OrderItem> items, string? classificationType = null)
    {
        return items.Where(i => !i.IsCancelled).Select((oi, idx) =>
        {
            var gross = LineGross(oi);
            var vatPercent = oi.MenuItem?.VatPercentOverride ?? oi.MenuItem?.MenuCategory?.VatPercent ?? 24m;
            var vatCategoryCode = int.TryParse(oi.MenuItem?.VatCategoryCodeOverride ?? oi.MenuItem?.MenuCategory?.VatCategoryCode, out var vc) ? vc : 1;
            var net = LineNet(oi);
            var vat = Math.Round(gross - net, 2);

            var line = new Dictionary<string, object?>
            {
                ["lineNumber"] = idx,
                ["invoiceDetailType"] = 0,
                ["quantity"] = oi.Quantity,
                ["entityName"] = oi.MenuItem?.Name ?? "Είδος",
                ["netValue"] = net,
                ["totalValue"] = gross,
                ["vatCategory"] = vatCategoryCode,
                ["vatCategoryUbl"] = "S",
                ["vatAmount"] = vat,
                ["vatPercent"] = vatPercent,
                ["measurementUnit"] = 1,
                ["measurementUnitUbl"] = "H87"
            };

            if (!string.IsNullOrWhiteSpace(classificationType))
            {
                line["classificationCategory"] =
                    oi.MenuItem?.MenuCategory?.IncomeClassificationCategory ?? "category1_1";
                line["classificationType"] = classificationType;
            }

            return (object?)line;
        }).ToList();
    }

    private static (decimal Net, decimal Vat, decimal Gross) CalculateTotals(Order order) => CalculateTotals(order.Items.Where(i => !i.IsCancelled));

    private static (decimal Net, decimal Vat, decimal Gross) CalculateTotals(IEnumerable<OrderItem> items)
    {
        items = items.Where(i => !i.IsCancelled);
        var gross = Math.Round(items.Sum(LineGross), 2);
        var net = Math.Round(items.Sum(LineNet), 2);
        var vat = Math.Round(gross - net, 2);
        return (net, vat, gross);
    }

    private static bool IsEpayWebEcr(BankPosTerminalSettings? terminal) => terminal is not null &&
        (terminal.Provider.Contains("ePay", StringComparison.OrdinalIgnoreCase) ||
         terminal.Provider.Contains("Euronet", StringComparison.OrdinalIgnoreCase));

    private static bool IsEdpsWebEcr(BankPosTerminalSettings? terminal) => terminal is not null &&
        terminal.Provider.Equals("EDPS", StringComparison.OrdinalIgnoreCase) &&
        terminal.Protocol.Contains("WebECR API v6", StringComparison.OrdinalIgnoreCase);

    private static Task<BankPosTerminalSettings?> GetDefaultWebEcrPosAsync(AppDbContext db) =>
        db.BankPosTerminalSettings
            .Where(x => x.IsEnabled &&
                ((x.Provider.Contains("ePay") || x.Provider.Contains("Euronet") || x.Protocol.Contains("Common WebECR")) ||
                 (x.Provider == "EDPS" && x.Protocol.Contains("WebECR API v6"))))
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync();

    private async Task<CompanySettings?> GetSettingsAsync(AppDbContext db) =>
        await db.CompanySettings.FirstOrDefaultAsync();

    private static bool ValidateSettings(CompanySettings? settings, out string? error)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.ParochosSubscriptionKey))
        {
            error = "Δεν έχουν ρυθμιστεί τα στοιχεία σύνδεσης με τον Parochos.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.VatNumber))
        {
            error = "Δεν έχει οριστεί ΑΦΜ επιχείρησης.";
            return false;
        }
        error = null;
        return true;
    }

    private static async Task<InvoiceSeries> GetOrCreateSeriesAsync(AppDbContext db, FiscalDocumentKind kind, string? preferredSeriesName = null)
    {
        // The active series selected in Admin > Fiscal Settings is authoritative.
        // The old implementation returned the first row for this kind, which could
        // be an inactive legacy series (e.g. "ΠΑΡ") even when another series was active.
        var series = await db.InvoiceSeries
            .Where(s => s.Kind == kind && s.IsActive)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        if (series is not null) return series;

        // If no active series exists, prefer the configured series name.
        if (!string.IsNullOrWhiteSpace(preferredSeriesName))
        {
            series = await db.InvoiceSeries.FirstOrDefaultAsync(
                s => s.Kind == kind && s.SeriesName == preferredSeriesName.Trim());
            if (series is not null)
            {
                series.IsActive = true;
                await db.SaveChangesAsync();
                return series;
            }
        }

        // Fall back to an existing series of this kind, activating it explicitly.
        series = await db.InvoiceSeries
            .Where(s => s.Kind == kind)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        if (series is not null)
        {
            series.IsActive = true;
            await db.SaveChangesAsync();
            return series;
        }

        var seriesName = !string.IsNullOrWhiteSpace(preferredSeriesName)
            ? preferredSeriesName.Trim()
            : kind switch
            {
                FiscalDocumentKind.Invoice => "ΤΙΜ",
                FiscalDocumentKind.OrderForm => "ΠΑΡ",
                FiscalDocumentKind.CreditReceipt => "ΠΣΛ",
                FiscalDocumentKind.CreditInvoice => "ΠΤΙΜ",
                _ => "Α"
            };
        series = new InvoiceSeries { SeriesName = seriesName, Kind = kind, NextNumber = 1, IsActive = true };
        db.InvoiceSeries.Add(series);
        await db.SaveChangesAsync();
        return series;
    }

    private static int ParsePaymentCode(string? value, int fallback) =>
        int.TryParse(value, out var code) ? code : fallback;

    private static async Task<string?> ResolvePointOfServiceAsync(AppDbContext db, Order order)
    {
        if (!string.IsNullOrWhiteSpace(order.RestaurantTable?.Name))
            return order.RestaurantTable!.Name.Trim();

        if (order.RestaurantTableId > 0)
        {
            return await db.Tables
                .Where(t => t.Id == order.RestaurantTableId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();
        }

        return null;
    }

    private static decimal LineGross(OrderItem oi) =>
        Math.Round(oi.Quantity * ((oi.MenuItem?.Price ?? 0) + oi.ExtrasPrice), 2);

    private static decimal LineNet(OrderItem oi)
    {
        var vatPercent = oi.MenuItem?.VatPercentOverride ?? oi.MenuItem?.MenuCategory?.VatPercent ?? 24m;
        return Math.Round(LineGross(oi) / (1 + vatPercent / 100m), 2);
    }

    // Parochos source: invoiceHeader.issueDate τύπου Date (π.χ. "2025-03-14").
    private static string IssueDateToday() => DateTime.Now.ToString("yyyy-MM-dd");

    public static string CreditExternalPrefix(int orderId, int? orderPaymentId) =>
        $"credit-{orderId}-{orderPaymentId ?? 0}-";

    /// <summary>
    /// Ακύρωση εκδοθείσας απόδειξης/τιμολογίου. Ο Parochos /api/Cancel δέχεται μόνο
    /// δελτία αποστολής/παραλαβής (9.x/10.x), οπότε η ακύρωση παραστατικού αξίας γίνεται
    /// με έκδοση πιστωτικού (11.4 για απόδειξη, 5.1 για τιμολόγιο) συσχετισμένου με το MARK.
    /// </summary>
    public async Task<FiscalIssueResult> CreditIssuedDocumentAsync(
        AppDbContext db, int orderId, int? orderPaymentId, string reason)
    {
        var settings = await GetSettingsAsync(db);
        if (!ValidateSettings(settings, out var error))
            return new FiscalIssueResult { Success = false, ErrorMessage = error };
        if (!settings!.UseParochosProvider)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Ο πάροχος δεν είναι ενεργός." };

        var order = await db.Orders
            .Include(o => o.Items).ThenInclude(i => i.MenuItem).ThenInclude(m => m!.MenuCategory)
            .Include(o => o.Payments).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Η παραγγελία δεν βρέθηκε." };

        var prefix = CreditExternalPrefix(orderId, orderPaymentId);
        if (await db.FiscalOrderCancellations.AnyAsync(c => c.OrderId == orderId && c.ExternalSystemId.StartsWith(prefix) && c.Mark != null))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Έχει ήδη εκδοθεί πιστωτικό για αυτό το παραστατικό." };

        long? originalMark;
        FiscalDocumentKind kind;
        List<OrderItem> items;
        if (orderPaymentId is int pid)
        {
            var payment = order.Payments.FirstOrDefault(p => p.Id == pid);
            if (payment?.Mark is null)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Η πληρωμή δεν έχει MARK." };
            originalMark = payment.Mark;
            kind = payment.DocumentKind;
            items = payment.Items
                .Select(pi => (Src: order.Items.FirstOrDefault(i => i.Id == pi.OrderItemId), pi.Quantity))
                .Where(x => x.Src != null && x.Quantity > 0)
                .Select(x => new OrderItem { MenuItem = x.Src!.MenuItem, Quantity = x.Quantity, ExtrasPrice = x.Src.ExtrasPrice })
                .ToList();
        }
        else
        {
            if (order.FiscalStatus != FiscalStatus.Issued || order.ParochosMark is null)
                return new FiscalIssueResult { Success = false, ErrorMessage = "Η παραγγελία δεν έχει εκδοθεί με MARK." };
            originalMark = order.ParochosMark;
            kind = order.DocumentKind ?? FiscalDocumentKind.Receipt;
            items = order.Items.Where(i => !i.IsCancelled && i.Quantity > 0).ToList();
        }

        if (items.Count == 0)
            return new FiscalIssueResult { Success = false, ErrorMessage = "Δεν βρέθηκαν είδη για το πιστωτικό." };
        if (kind == FiscalDocumentKind.Invoice && string.IsNullOrWhiteSpace(order.CounterpartVatNumber))
            return new FiscalIssueResult { Success = false, ErrorMessage = "Λείπει ο ΑΦΜ πελάτη του αρχικού τιμολογίου." };

        var isInvoice = kind == FiscalDocumentKind.Invoice;
        var creditKind = isInvoice ? FiscalDocumentKind.CreditInvoice : FiscalDocumentKind.CreditReceipt;
        var series = await GetOrCreateSeriesAsync(db, creditKind);
        var number = (long)series.NextNumber;
        var externalSystemId = prefix + Guid.NewGuid().ToString("N");
        var totals = CalculateTotals(items);

        ParochosSendResult result = new();
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var invoice = new Dictionary<string, object?>
            {
                ["issuer"] = BuildIssuer(settings),
                ["counterpart"] = isInvoice ? BuildInvoiceCounterpart(order) : BuildRetailCounterpart(),
                ["invoiceHeader"] = new Dictionary<string, object?>
                {
                    ["series"] = series.SeriesName,
                    ["aa"] = number,
                    ["issueDate"] = IssueDateToday(),
                    ["currency"] = "EUR",
                    ["invoiceType"] = isInvoice ? "5.1" : "11.4",
                    ["invoiceTypeUbl"] = "381",
                    ["selfPricing"] = false,
                    ["fuelInvoice"] = false
                },
                ["invoiceDetails"] = BuildLines(items, isInvoice ? "E3_561_001" : "E3_561_003"),
                // Επιστροφή χρημάτων: μετρητά. Επιστροφή σε κάρτα (type 7) απαιτεί νέο
                // requestPayment/υπογραφή στο EFT-POS και δεν γίνεται από εδώ.
                ["paymentMethods"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = ParsePaymentCode(settings.CashPaymentTypeCode, 3),
                        ["amount"] = totals.Gross
                    }
                },
                ["invoiceSummary"] = new Dictionary<string, object?>
                {
                    ["totalNetValue"] = totals.Net,
                    ["totalVatAmount"] = totals.Vat,
                    ["totalValue"] = totals.Gross
                },
                ["correlatedInvoices"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["mark"] = originalMark!.Value }
                }
            };

            result = await _parochos.SendAndWaitAsync(settings, new Dictionary<string, object?> { ["invoice"] = invoice }, externalSystemId);
            if (result.Success || result.ErrorCode != "00010022")
                break;
            number++;
        }

        if (!result.Success)
            return new FiscalIssueResult { Success = false, ErrorMessage = result.ErrorMessage };

        series.NextNumber = checked((int)Math.Max((long)series.NextNumber, number + 1L));
        db.FiscalOrderCancellations.Add(new FiscalOrderCancellation
        {
            OrderId = orderId,
            ExternalSystemId = externalSystemId,
            ProcessId = result.ProcessId,
            Mark = result.Mark,
            Reason = $"{series.SeriesName} {number} | {reason}"
        });
        await db.SaveChangesAsync();

        return new FiscalIssueResult { Success = true, Mark = result.Mark, QrCode = result.QrCode };
    }

    private void LogFiscalIssueDate(string kind, string externalSystemId, Dictionary<string, object?> source)
    {
        try
        {
            string? issueDate = null;

            if (source.TryGetValue("invoice", out var invoiceObj) &&
                invoiceObj is Dictionary<string, object?> invoice &&
                invoice.TryGetValue("invoiceHeader", out var headerObj) &&
                headerObj is Dictionary<string, object?> header &&
                header.TryGetValue("issueDate", out var dateObj))
            {
                issueDate = dateObj?.ToString();
            }

            _logger.LogInformation(
                "PAROCHOS ISSUE DATE DEBUG >>> Kind={Kind} | ExternalSystemId={ExternalSystemId} | IssueDateSent={IssueDateSent} | LocalNow={LocalNow} | UtcNow={UtcNow}",
                kind,
                externalSystemId,
                issueDate ?? "<NULL>",
                DateTimeOffset.Now.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAROCHOS ISSUE DATE DEBUG failed for {ExternalSystemId}", externalSystemId);
        }
    }

}
