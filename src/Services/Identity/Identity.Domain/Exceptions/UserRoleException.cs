namespace Identity.Domain.Exceptions;

/// <summary>
/// Represents errors that occur with user role related operations.
/// </summary>
public class UserRoleException : Exception
{
    public UserRoleException() : base("A User Role Exception has occurred.")
    { }

    public UserRoleException(string? message) : base(message)
    { }

    public UserRoleException(string? message, Exception? innerException) : base(message, innerException)
    { }
}
