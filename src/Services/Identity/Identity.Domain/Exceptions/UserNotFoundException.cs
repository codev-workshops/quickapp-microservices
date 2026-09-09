namespace Identity.Domain.Exceptions;

/// <summary>
/// The exception that is thrown when an attempt to access a particular User Account fails.
/// </summary>
public class UserNotFoundException : UserAccountException
{
    public UserNotFoundException() : base("Unable to find the requested User.")
    { }

    public UserNotFoundException(string? message) : base(message)
    { }

    public UserNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    { }
}
