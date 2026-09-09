using Microsoft.AspNetCore.Identity;

namespace Identity.Domain.Entities;

public class ApplicationRole : IdentityRole, IAuditableEntity
{
    public ApplicationRole()
    { }

    public ApplicationRole(string roleName) : base(roleName)
    { }

    public ApplicationRole(string roleName, string description) : base(roleName)
    {
        Description = description;
    }

    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }

    /// <summary>
    /// Navigation property for the users in this role.
    /// </summary>
    public ICollection<IdentityUserRole<string>> Users { get; } = [];

    /// <summary>
    /// Navigation property for claims in this role.
    /// </summary>
    public ICollection<IdentityRoleClaim<string>> Claims { get; } = [];
}
