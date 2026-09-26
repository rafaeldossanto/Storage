namespace Storage.Application.Abstractions;

/// <summary>
/// The shop the current request belongs to.
/// </summary>
/// <remarks>
/// Repositories read the tenant from here instead of taking it as a parameter. That is
/// deliberate: a parameter can be forgotten at one call site and leak another shop's
/// data, while a filter applied inside the repository cannot be skipped by accident.
/// </remarks>
public interface ITenantContext
{
    Guid TenantId { get; }
}
