namespace RestaurantOrders.Models;

/// <summary>
/// Parochos provider signature fields forwarded to an EFT-POS WebECR API.
/// Signature contains the existing ePay-normalized form; OriginalSignature
/// preserves the original provider encoding for APIs such as EDPS WebECR.
/// </summary>
public sealed record ProviderPaymentData(
    string Uid,
    string SignatureTimestamp,
    int NetAmount,
    int VatAmount,
    int TotalAmount,
    int ProviderId,
    string Signature,
    long? Mark = null,
    string? SignedAmountTuple = null,
    string? SignedFieldOrder = null,
    string? OriginalSignature = null);
