namespace Storage.Domain.Catalog;

/// <summary>
/// How a product is counted in stock.
/// </summary>
/// <remarks>
/// Version 1 sells whole units only; weighed goods need scale integration, which is out
/// of scope. The other members exist so a 1 kg package can be described honestly at
/// registration time, not so it can be sold by weight.
/// </remarks>
public enum UnitOfMeasure
{
    Unit = 0,
    Kilogram = 1,
    Liter = 2,
}
