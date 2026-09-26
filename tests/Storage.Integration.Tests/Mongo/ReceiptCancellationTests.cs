using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

public sealed class ReceiptCancellationTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_receipt_saved_before_cancelling_existed_can_be_cancelled_once()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = new MongoStockStore(db, new FixedTenant(Shop));
        var receipt = await ReceiveAsync(store);

        // As a receipt written by an older version looks: no status, no version.
        await db.GoodsReceipts.UpdateOneAsync(
            Builders<GoodsReceipt>.Filter.Eq(stored => stored.Id, receipt.Id),
            Builders<GoodsReceipt>.Update.Unset(stored => stored.Status).Unset(stored => stored.Version),
            cancellationToken: Token);

        var first = (await store.FindReceiptAsync(receipt.Id, Token))!;
        var second = (await store.FindReceiptAsync(receipt.Id, Token))!;
        Assert.Equal(GoodsReceiptStatus.Received, first.Status);

        await CancelAsync(store, first);
        var conflict = await Assert.ThrowsAsync<UseCaseException>(() => CancelAsync(store, second));

        Assert.Equal(ErrorCodes.StockChangedConcurrently, conflict.Code);
        Assert.Equal(GoodsReceiptStatus.Cancelled, (await store.FindReceiptAsync(receipt.Id, Token))!.Status);
    }

    private static async Task<GoodsReceipt> ReceiveAsync(MongoStockStore store)
    {
        var soap = Product.Create(Shop, "Sabão em barra", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(450), Gtin.Parse("7891000000021"));
        soap.SetExpiryTracking(false);

        var receipt = GoodsReceipt.Open(Shop, supplierId: null, "NF 1", note: null, Guid.CreateVersion7(), Now);
        var batch = receipt.Receive(soap, Gtin.Parse("7891000000021"), 10, Money.FromCents(300), null, DateOnly.FromDateTime(Now.UtcDateTime));

        var changes = new StockChanges();
        changes.Add(batch);
        changes.Record(StockMovement.Receipt(batch, Now, receipt.UserId, receipt.Id));
        changes.Attach(receipt);
        await store.CommitAsync(changes, Token);

        return receipt;
    }

    private static Task CancelAsync(MongoStockStore store, GoodsReceipt receipt)
    {
        var version = receipt.Version;
        receipt.Cancel(Guid.CreateVersion7(), Now.AddMinutes(1));

        var changes = new StockChanges();
        changes.Update(receipt, version);
        return store.CommitAsync(changes, Token);
    }
}
