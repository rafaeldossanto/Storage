using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Stock;

/// <param name="CostCents">What one of the scanned packaging cost - the price on the invoice.</param>
/// <param name="ExpiryDate">The last day it may be sold. Required for products that track expiry.</param>
public sealed record ReceiveGoodsLine(string Barcode, int Quantity, long CostCents, DateOnly? ExpiryDate = null);

public sealed record ReceiveGoodsRequest(
    IReadOnlyList<ReceiveGoodsLine> Lines,
    Guid? SupplierId = null,
    string? InvoiceNumber = null,
    string? Note = null);

public sealed record ReceiptLineDto(
    Guid ProductId,
    string ProductName,
    string Barcode,
    int Quantity,
    int BaseUnits,
    long PackagingCostCents,
    long UnitCostCents,
    long TotalCents,
    DateOnly? ExpiryDate,
    Guid BatchId);

public sealed record ReceiptDto(
    Guid Id,
    DateTimeOffset ReceivedAt,
    Guid? SupplierId,
    string? SupplierName,
    string? InvoiceNumber,
    string? Note,
    long TotalCostCents,
    IReadOnlyList<ReceiptLineDto> Lines,
    GoodsReceiptStatus Status,
    DateTimeOffset CancellableUntil);

public sealed record ReceiptSummaryDto(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string? SupplierName,
    string? InvoiceNumber,
    int LineCount,
    long TotalCostCents,
    GoodsReceiptStatus Status,
    DateTimeOffset CancellableUntil);

public sealed class ReceivingService(
    IStockStore stock,
    IProductRepository products,
    ISupplierRepository suppliers,
    IShopCalendar calendar,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    /// <summary>Where the failing line of a receipt travels, 1-based, on the exception.</summary>
    public const string LineKey = "line";

    /// <summary>
    /// Takes a delivery in: one batch per scanned line, one movement per batch and the
    /// receipt document, all committed together.
    /// </summary>
    public async Task<ReceiptDto> ReceiveAsync(ReceiveGoodsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Lines is not { Count: > 0 })
        {
            throw UseCaseException.Invalid(ErrorCodes.ReceiptEmpty, "A receipt needs at least one line.");
        }

        var supplier = request.SupplierId is { } supplierId
            ? await suppliers.FindAsync(supplierId, cancellationToken)
              ?? throw UseCaseException.NotFound(ErrorCodes.SupplierNotFound, $"Supplier {supplierId} does not exist.")
            : null;

        var today = await calendar.TodayAsync(cancellationToken);

        var receipt = GoodsReceipt.Open(
            tenant.TenantId,
            supplier?.Id,
            request.InvoiceNumber,
            request.Note,
            currentUser.UserId,
            clock.GetUtcNow());

        var changes = new StockChanges();
        var names = new Dictionary<Guid, string>();

        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];

            try
            {
                var gtin = Gtin.TryParse(line.Barcode, out var parsed)
                    ? parsed
                    : throw UseCaseException.Invalid(ErrorCodes.BarcodeInvalid, $"'{line.Barcode}' is not a valid barcode.");

                // Unknown codes are not created here: registering a product has its own
                // screen and rules. The receiving screen checks each scan as it happens.
                var product = await products.FindByGtinAsync(gtin, cancellationToken)
                    ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"No product answers to {gtin.ToDisplay()}.");

                var batch = receipt.Receive(product, gtin, line.Quantity, Money.FromCents(line.CostCents), line.ExpiryDate, today);

                changes.Add(batch);
                changes.Record(StockMovement.Receipt(batch, receipt.ReceivedAt, currentUser.UserId, receipt.Id));
                names[product.Id] = product.Name;
            }
            catch (Exception refusal) when (refusal is DomainException or UseCaseException)
            {
                // In a delivery of forty lines, "expiry date already passed" is only useful
                // together with which line it was.
                refusal.Data[LineKey] = index + 1;
                throw;
            }
        }

        changes.Attach(receipt);
        await stock.CommitAsync(changes, cancellationToken);

        return ToDto(receipt, supplier?.Name, names);
    }

    /// <summary>Deliveries, newest first, a page at a time - the whole history stays reachable.</summary>
    public async Task<Paged<ReceiptSummaryDto>> ListAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var receipts = await stock.ListReceiptsAsync(page, cancellationToken);
        var supplierNames = (await suppliers.ListAsync(cancellationToken)).ToDictionary(s => s.Id, s => s.Name);

        return receipts.Map(receipt => new ReceiptSummaryDto(
            receipt.Id,
            receipt.ReceivedAt,
            receipt.SupplierId is { } id ? supplierNames.GetValueOrDefault(id) : null,
            receipt.InvoiceNumber,
            receipt.Lines.Count,
            receipt.TotalCost.Cents,
            receipt.Status,
            receipt.ReceivedAt + GoodsReceipt.CancellationWindow));
    }

    /// <summary>
    /// Takes back a receipt entered moments ago - 100 typed instead of 10. Only while every
    /// unit it brought is still on the shelf: goods already sold, lost or counted cannot be
    /// un-received, and the ledger keeps both movements, in and back out.
    /// </summary>
    public async Task<ReceiptDto> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var receipt = await stock.FindReceiptAsync(id, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ReceiptNotFound, $"Receipt {id} does not exist.");

        var readVersion = receipt.Version;
        var now = clock.GetUtcNow();
        receipt.Cancel(currentUser.UserId, now);

        var batches = await stock.ListBatchesByIdsAsync(receipt.Lines.Select(line => line.BatchId).ToArray(), cancellationToken);

        if (batches.Count != receipt.Lines.Count
            || batches.Any(batch => batch.Status != BatchStatus.Available || batch.RemainingQuantity != batch.InitialQuantity))
        {
            throw UseCaseException.Conflict(
                ErrorCodes.ReceiptStockMoved, "Some of these goods already left the shelf; the receipt can no longer be cancelled.");
        }

        var changes = new StockChanges();

        foreach (var batch in batches)
        {
            var quantity = batch.RemainingQuantity;
            changes.Change(batch, returned => returned.Take(quantity));
            changes.Record(StockMovement.Outflow(
                MovementType.ReceiptCancellation, batch, quantity, now, currentUser.UserId, receipt.Id));
        }

        // Written only if nobody cancelled it in the meantime; a sale landing on one of its
        // batches in between fails the batch check, and nothing is written either way.
        changes.Update(receipt, readVersion);
        await stock.CommitAsync(changes, cancellationToken);

        return await GetAsync(receipt.Id, cancellationToken);
    }

    public async Task<ReceiptDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var receipt = await stock.FindReceiptAsync(id, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ReceiptNotFound, $"Receipt {id} does not exist.");

        var productIds = receipt.Lines.Select(line => line.ProductId).Distinct().ToArray();
        var names = (await products.ListByIdsAsync(productIds, cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        var supplierName = receipt.SupplierId is { } supplierId
            ? (await suppliers.FindAsync(supplierId, cancellationToken))?.Name
            : null;

        return ToDto(receipt, supplierName, names);
    }

    private static ReceiptDto ToDto(GoodsReceipt receipt, string? supplierName, IReadOnlyDictionary<Guid, string> names) => new(
        receipt.Id,
        receipt.ReceivedAt,
        receipt.SupplierId,
        supplierName,
        receipt.InvoiceNumber,
        receipt.Note,
        receipt.TotalCost.Cents,
        receipt.Lines
            .Select(line => new ReceiptLineDto(
                line.ProductId,
                // A product deleted since would have no name; products are deactivated,
                // never deleted, so this is a safety net rather than a real case.
                names.GetValueOrDefault(line.ProductId, string.Empty),
                line.Gtin.ToDisplay(),
                line.Quantity,
                line.BaseUnits,
                line.PackagingCost.Cents,
                line.UnitCost.Cents,
                line.Total.Cents,
                line.ExpiryDate,
                line.BatchId))
            .ToArray(),
        receipt.Status,
        receipt.ReceivedAt + GoodsReceipt.CancellationWindow);
}
