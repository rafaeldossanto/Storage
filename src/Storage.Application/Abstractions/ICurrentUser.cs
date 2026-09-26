namespace Storage.Application.Abstractions;

/// <summary>
/// The person behind the current request, for the records that say who did what - every
/// stock movement a person causes carries their id.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}
