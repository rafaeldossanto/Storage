namespace Storage.Application.Abstractions;

/// <summary>A short-lived pass to the sales area, handed out for the right PIN.</summary>
public sealed record SalesAccess(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues the pass. Checking it happens at the edge, where requests come in.</summary>
public interface ISalesAccessIssuer
{
    SalesAccess Issue(Guid tenantId, Guid userId);
}
