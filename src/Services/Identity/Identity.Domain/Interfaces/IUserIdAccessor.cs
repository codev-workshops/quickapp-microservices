namespace Identity.Domain.Interfaces;

public interface IUserIdAccessor
{
    string? GetCurrentUserId();
}
