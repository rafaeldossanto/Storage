namespace Storage.Application.Errors;

public enum ErrorKind
{
    /// <summary>The thing asked for does not exist in the current shop.</summary>
    NotFound,

    /// <summary>The request clashes with data that already exists, like a barcode in use.</summary>
    Conflict,

    /// <summary>The input cannot be understood, like a barcode with a wrong check digit.</summary>
    Invalid,
}

/// <summary>
/// A request the application refused, as opposed to a bug.
/// </summary>
/// <remarks>
/// Covers what only the application layer can know - that an id points nowhere, or that
/// another product already owns a barcode. Rules about a single entity are the domain's
/// and travel as <c>DomainException</c>.
/// </remarks>
public sealed class UseCaseException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static UseCaseException NotFound(string code, string message) =>
        new(ErrorKind.NotFound, code, message);

    public static UseCaseException Conflict(string code, string message) =>
        new(ErrorKind.Conflict, code, message);

    public static UseCaseException Invalid(string code, string message) =>
        new(ErrorKind.Invalid, code, message);
}

/// <summary>
/// Codes raised by the application layer. Part of the API contract: the front end keys its
/// Portuguese messages on them, so renaming one is a breaking change.
/// </summary>
public static class ErrorCodes
{
    public const string CategoryNotFound = "category.not_found";
    public const string ParentCategoryNotFound = "category.parent_not_found";
    public const string CategoryNameTaken = "category.name_taken";

    public const string ProductNotFound = "product.not_found";

    public const string BarcodeInvalid = "barcode.invalid";
    public const string BarcodeTaken = "barcode.taken";
}
