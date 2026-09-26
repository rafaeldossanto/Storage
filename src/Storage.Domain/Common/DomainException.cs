namespace Storage.Domain.Common;

/// <summary>
/// A business rule the caller broke - as opposed to a bug in the code.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="InvalidOperationException"/>: the framework throws that
/// for its own failures, and treating those as the client's fault would turn a server bug
/// into a 4xx and leak its message. The <see cref="Code"/> is stable and in English; the
/// front end translates it into what the shopkeeper reads.
/// </remarks>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The codes a <see cref="DomainException"/> can carry. Part of the API contract: the
/// front end keys its messages on them, so renaming one is a breaking change.
/// </summary>
public static class DomainErrors
{
    public const string CategoryNameInvalid = "category.name_invalid";
    public const string CategoryMoveIntoOwnBranch = "category.move_into_own_branch";
    public const string CategoryMoveAcrossShops = "category.move_across_shops";

    public const string ProductNameInvalid = "product.name_invalid";
    public const string ProductPriceNegative = "product.price_negative";
    public const string ProductMinimumStockNegative = "product.minimum_stock_negative";
    public const string ProductBarcodeRepeated = "product.barcode_repeated";
    public const string ProductBarcodeNotOnProduct = "product.barcode_not_on_product";
    public const string ProductDefaultPackagingRequired = "product.default_packaging_required";

    public const string PackagingNotFound = "packaging.not_found";
    public const string PackagingFactorInvalid = "packaging.factor_invalid";
    public const string PackagingNameInvalid = "packaging.name_invalid";

    public const string BatchQuantityInvalid = "batch.quantity_invalid";
    public const string BatchCostNegative = "batch.cost_negative";
    public const string BatchExpired = "batch.expired";

    public const string SaleEmpty = "sale.empty";
    public const string SaleTooManyLines = "sale.too_many_lines";
    public const string SaleQuantityInvalid = "sale.quantity_invalid";
    public const string SaleNotCompleted = "sale.not_completed";
    public const string SaleCancelWindowClosed = "sale.cancel_window_closed";

    public const string SalesPinFormat = "sales.pin_format";
    public const string StockInsufficient = "stock.insufficient";

    public const string ReceiptQuantityInvalid = "receipt.quantity_invalid";
    public const string ReceiptExpiryRequired = "receipt.expiry_required";
    public const string ReceiptAlreadyExpired = "receipt.already_expired";
    public const string ReceiptTooManyLines = "receipt.too_many_lines";
    public const string ReceiptFieldTooLong = "receipt.field_too_long";

    public const string CountNotOpen = "count.not_open";
    public const string CountEmpty = "count.empty";
    public const string CountJustificationRequired = "count.justification_required";
    public const string CountQuantityInvalid = "count.quantity_invalid";

    public const string DiscountNameInvalid = "discount.name_invalid";
    public const string DiscountValueInvalid = "discount.value_invalid";
    public const string DiscountTargetInvalid = "discount.target_invalid";
    public const string DiscountPeriodInvalid = "discount.period_invalid";
    public const string DiscountExpiryWindowInvalid = "discount.expiry_window_invalid";

    public const string SupplierNameInvalid = "supplier.name_invalid";
    public const string SupplierFieldTooLong = "supplier.field_too_long";

    public const string ShopNameInvalid = "shop.name_invalid";
    public const string ShopTimeZoneInvalid = "shop.time_zone_invalid";

    public const string EmailInvalid = "account.email_invalid";
    public const string UserNameInvalid = "account.name_invalid";
    public const string OwnerCannotBeDeactivated = "account.owner_cannot_be_deactivated";
}
