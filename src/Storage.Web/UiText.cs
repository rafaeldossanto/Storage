namespace Storage.Web;

/// <summary>
/// Marker type for the shared user-facing strings, resolved through
/// <c>IStringLocalizer&lt;UiText&gt;</c> against <c>Resources/UiText.resx</c>.
/// </summary>
/// <remarks>
/// Identifiers, tables and enums in this codebase are English; everything the shopkeeper
/// reads lives in a resource file. This type is the seam between the two.
/// </remarks>
public sealed class UiText;
