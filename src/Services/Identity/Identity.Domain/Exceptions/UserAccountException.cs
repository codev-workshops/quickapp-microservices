namespace Identity.Domain.Exceptions;

/// <summary>
/// Represents errors that occur with user account related operations.
/// </summary>
public class UserAccountException : Exception
{
    public UserAccountException() : base("A User Account Exception has occurred.")
    { }

    public UserAccountException(string? message) : base(message)
    { }

    public UserAccountException(string? message, Exception? innerException) : base(message, innerException)
    { }
}
