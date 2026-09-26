using Bipe.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bipe.Infrastructure.Persistence.Conversions;

/// <summary>
/// Money goes to the database as an INTEGER count of cents.
/// </summary>
/// <remarks>
/// SQLite would store a decimal as TEXT (which sorts "9,90" after "10,00") or as REAL
/// (which cannot represent 0.10 exactly). Cents in an INTEGER column keep SUM, ORDER BY
/// and equality exact.
/// </remarks>
public sealed class MoneyConverter() : ValueConverter<Money, long>(
    money => money.Cents,
    cents => Money.FromCents(cents));

/// <summary>
/// A barcode goes to the database in its normalised 14 digit form, so looking a product
/// up by any printed length is a single indexed equality match.
/// </summary>
public sealed class GtinConverter() : ValueConverter<Gtin, string>(
    gtin => gtin.Value,
    value => Gtin.Parse(value));
