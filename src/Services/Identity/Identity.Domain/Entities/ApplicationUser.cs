using Microsoft.AspNetCore.Identity;

namespace Identity.Domain.Entities;

public class ApplicationUser : IdentityUser, IAuditableEntity
{
    public virtual string? FriendlyName
    {
        get
        {
            var friendlyName = string.IsNullOrWhiteSpace(FullName) ? UserName : FullName;

            if (!string.IsNullOrWhiteSpace(JobTitle))
                friendlyName = $"{JobTitle} {friendlyName}";

            return friendlyName;
        }
    }

    public string? JobTitle { get; set; }
    public string? FullName { get; set; }
    public string? Configuration { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsLockedOut => LockoutEnabled && LockoutEnd >= DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }

    /// <summary>
    /// Navigation property for the roles this user belongs to.
    /// </summary>
    public ICollection<IdentityUserRole<string>> Roles { get; } = [];

    /// <summary>
    /// Navigation property for the claims this user possesses.
    /// </summary>
    public ICollection<IdentityUserClaim<string>> Claims { get; } = [];
}
