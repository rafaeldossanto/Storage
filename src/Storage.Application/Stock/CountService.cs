using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Stock;

public sealed record StartCountRequest(Guid? CategoryId = null);

public sealed record SetCountedRequest(int Quantity);

/// <param name="Quantity">How many of the scanned packaging - a twelve-pack scanned once counts twelve.</param>
public sealed record ScanRequest(string Barcode, int Quantity = 1);

public sealed record CloseCountRequest(string? Justification);

/// <param name="System">
/// For an open count, what the ledger holds right now - so differences show before closing.
/// For a closed one, what it held when the count was closed.
/// </param>
public sealed record CountItemDto(Guid ProductId, string ProductName, int Counted, int System, int Difference);

public sealed record CountDto(
    Guid Id,
    Guid? CategoryId,
    StockCountStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    string? Justification,
    IReadOnlyList<CountItemDto> Items);

public sealed record CountSummaryDto(
    Guid Id,
    Guid? CategoryId,
    StockCountStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    int ItemCount,
    int MissingUnits,
    int ExtraUnits);

public sealed class CountService(
    ICountStore counts,
    IStockStore stock,
    IProductRepository products,
    ICategoryRepository categories,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    /// <summary>
    /// Opens a count, of one branch or of the whole shop. One at a time: two overlapping
    /// counts would each adjust the same shelf.
    /// </summary>
    public async Task<CountDto> StartAsync(StartCountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CategoryId is { } categoryId && await categories.FindAsync(categoryId, cancellationToken) is null)
        {
            throw UseCaseException.NotFound(ErrorCodes.CategoryNotFound, $"Category {categoryId} does not exist.");
        }

        if (await counts.FindOpenAsync(cancellationToken) is not null)
        {
            throw UseCaseException.Conflict(ErrorCodes.CountAlreadyOpen, "Another count is open; close or cancel it first.");
        }

        var count = StockCount.Start(tenant.TenantId, request.CategoryId, currentUser.UserId, clock.GetUtcNow());
        await counts.AddAsync(count, cancellationToken);

        return await ToDtoAsync(count, cancellationToken);
    }

    public async Task<CountDto> SetCountedAsync(
        Guid countId,
        Guid productId,
        SetCountedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var quantity = CountedItem.ValidateQuantity(request.Quantity);
        var count = await RequireAsync(countId, cancellationToken);
        await RequireInScopeAsync(count, productId, cancellationToken);

        if (!await counts.SetCountedAsync(countId, productId, quantity, cancellationToken))
        {
            throw NotOpen();
        }

        return await ToDtoAsync(await RequireAsync(countId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The phone's way of counting: scan what is on the shelf, one packaging at a time. A
    /// twelve-pack adds twelve.
    /// </summary>
    public async Task<CountDto> ScanAsync(Guid countId, ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity < 1)
        {
            throw UseCaseException.Invalid(ErrorCodes.StockQuantityInvalid, "Scan at least one unit.");
        }

        var gtin = Gtin.TryParse(request.Barcode, out var parsed)
            ? parsed
            : throw UseCaseException.Invalid(ErrorCodes.BarcodeInvalid, $"'{request.Barcode}' is not a valid barcode.");

        var product = await products.FindByGtinAsync(gtin, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"No product answers to {gtin.ToDisplay()}.");

        var count = await RequireAsync(countId, cancellationToken);
        await RequireInScopeAsync(count, product.Id, cancellationToken);

        var units = checked(request.Quantity * product.BaseUnitsFor(gtin));

        if (!await counts.AddCountedAsync(countId, product.Id, units, cancellationToken))
        {
            throw NotOpen();
        }

        return await ToDtoAsync(await RequireAsync(countId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Brings the ledger in line with the shelf. Units that went missing come out of the
    /// batches that expire first; units found are booked as a new batch at the current
    /// average cost. All of it, and the closed count, in one commit.
    /// </summary>
    public async Task<CountDto> CloseAsync(Guid countId, CloseCountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var count = await RequireAsync(countId, cancellationToken);
        var readVersion = count.Version;

        var productIds = count.Items.Select(item => item.ProductId).ToArray();
        var levels = await stock.LevelsAsync(productIds, cancellationToken);

        var now = clock.GetUtcNow();
        var differences = count.Close(
            levels.ToDictionary(level => level.Key, level => level.Value.Quantity),
            request.Justification,
            currentUser.UserId,
            now);

        var changes = new StockChanges();

        foreach (var difference in differences)
        {
            if (difference.Difference < 0)
            {
                var batches = await stock.ListBatchesAsync(difference.ProductId, availableOnly: true, cancellationToken);

                foreach (var (batch, quantity) in Fefo.Allocate(batches, -difference.Difference))
                {
                    changes.Change(batch, taken => taken.Take(quantity));
                    changes.Record(StockMovement.Outflow(
                        MovementType.CountAdjustment, batch, quantity, now, currentUser.UserId, count.Id, count.Justification));
                }
            }
            else
            {
                // Found units carry no expiry of their own - nobody knows which delivery they
                // came from - and are valued at what the shelf costs on average right now.
                var found = Batch.Receive(
                    tenant.TenantId, difference.ProductId, difference.Difference, levels[difference.ProductId].AverageCost, null, now);

                changes.Add(found);
                changes.Record(StockMovement.Adjustment(found, now, currentUser.UserId, count.Id, count.Justification));
            }
        }

        changes.Update(count, readVersion);
        await stock.CommitAsync(changes, cancellationToken);

        return await ToDtoAsync(count, cancellationToken);
    }

    public async Task<CountDto> CancelAsync(Guid countId, CancellationToken cancellationToken = default)
    {
        var count = await RequireAsync(countId, cancellationToken);
        var readVersion = count.Version;

        count.Cancel(currentUser.UserId, clock.GetUtcNow());

        var changes = new StockChanges();
        changes.Update(count, readVersion);
        await stock.CommitAsync(changes, cancellationToken);

        return await ToDtoAsync(count, cancellationToken);
    }

    public async Task<CountDto> GetAsync(Guid countId, CancellationToken cancellationToken = default) =>
        await ToDtoAsync(await RequireAsync(countId, cancellationToken), cancellationToken);

    public async Task<Paged<CountSummaryDto>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        (await counts.ListAsync(page, cancellationToken))
            .Map(count => new CountSummaryDto(
                count.Id,
                count.CategoryId,
                count.Status,
                count.StartedAt,
                count.ClosedAt,
                count.Items.Count,
                -count.Items.Where(item => item.Difference < 0).Sum(item => item.Difference ?? 0),
                count.Items.Where(item => item.Difference > 0).Sum(item => item.Difference ?? 0)));

    private async Task<CountDto> ToDtoAsync(StockCount count, CancellationToken cancellationToken)
    {
        var productIds = count.Items.Select(item => item.ProductId).ToArray();
        var names = (await products.ListByIdsAsync(productIds, cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        // An open count shows today's ledger next to what was counted, so the differences
        // are visible before anyone commits to them.
        var live = count.Status == StockCountStatus.Open
            ? await stock.LevelsAsync(productIds, cancellationToken)
            : null;

        return new CountDto(
            count.Id,
            count.CategoryId,
            count.Status,
            count.StartedAt,
            count.ClosedAt,
            count.Justification,
            count.Items
                .Select(item =>
                {
                    var system = item.SystemQuantity ?? live?.GetValueOrDefault(item.ProductId)?.Quantity ?? 0;
                    return new CountItemDto(
                        item.ProductId,
                        names.GetValueOrDefault(item.ProductId, string.Empty),
                        item.CountedQuantity,
                        system,
                        item.CountedQuantity - system);
                })
                .OrderBy(item => item.ProductName, StringComparer.InvariantCultureIgnoreCase)
                .ToArray());
    }

    private async Task<StockCount> RequireAsync(Guid countId, CancellationToken cancellationToken) =>
        await counts.FindAsync(countId, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.CountNotFound, $"Count {countId} does not exist.");

    // A count of Beverages only takes products that belong to Beverages; anything else was
    // scanned on the wrong shelf.
    private async Task RequireInScopeAsync(StockCount count, Guid productId, CancellationToken cancellationToken)
    {
        var product = await products.FindAsync(productId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {productId} does not exist.");

        if (count.CategoryId is not { } scope)
        {
            return;
        }

        var category = await categories.FindAsync(product.CategoryId, cancellationToken);

        if (category is null || !category.SelfAndAncestorIds.Contains(scope))
        {
            throw UseCaseException.Invalid(ErrorCodes.CountProductOutOfScope, $"'{product.Name}' is not part of this count.");
        }
    }

    // The same refusal the domain gives when closing a count that is not open.
    private static DomainException NotOpen() =>
        new(DomainErrors.CountNotOpen, "This count is no longer open.");
}
