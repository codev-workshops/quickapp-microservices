namespace Identity.Domain.Interfaces;

/// <summary>
/// Extension point replacing the monolith's cross-domain "user has orders" check.
/// Other bounded contexts (e.g. Order) can veto user deletion without Identity referencing them.
/// </summary>
public interface IUserDeletionGuard
{
    Task<IEnumerable<string>> GetDeletionBlockersAsync(string userId);
}
