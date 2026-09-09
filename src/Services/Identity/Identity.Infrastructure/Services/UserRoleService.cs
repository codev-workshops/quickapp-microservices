using Identity.Domain.Entities;
using Identity.Domain.Interfaces;
using Identity.Domain.Services;
using Identity.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Identity.Infrastructure.Services;

public class UserRoleService(IdentityDbContext context, RoleManager<ApplicationRole> roleManager) : IUserRoleService
{
    public async Task<ApplicationRole?> GetRoleByIdAsync(string roleId)
    {
        return await roleManager.FindByIdAsync(roleId);
    }

    public async Task<ApplicationRole?> GetRoleByNameAsync(string roleName)
    {
        return await roleManager.FindByNameAsync(roleName);
    }

    public async Task<ApplicationRole?> GetRoleLoadRelatedAsync(string roleName)
    {
        return await context.Roles
            .Include(r => r.Claims)
            .Include(r => r.Users)
            .AsSingleQuery()
            .Where(r => r.Name == roleName)
            .SingleOrDefaultAsync();
    }

    public async Task<List<ApplicationRole>> GetRolesLoadRelatedAsync(int page, int pageSize)
    {
        IQueryable<ApplicationRole> rolesQuery = context.Roles
            .Include(r => r.Claims)
            .Include(r => r.Users)
            .AsSingleQuery()
            .OrderBy(r => r.Name);

        if (page != -1)
            rolesQuery = rolesQuery.Skip((page - 1) * pageSize);

        if (pageSize != -1)
            rolesQuery = rolesQuery.Take(pageSize);

        return await rolesQuery.ToListAsync();
    }

    public async Task<(bool Succeeded, string[] Errors)> CreateRoleAsync(ApplicationRole role, IEnumerable<string> claims)
    {
        var invalidClaims = claims.Where(c => ApplicationPermissions.GetPermissionByValue(c) == null).ToArray();
        if (invalidClaims.Length != 0)
            return (false, [$"The following claim types are invalid: {string.Join(", ", invalidClaims)}"]);

        var result = await roleManager.CreateAsync(role);
        if (!result.Succeeded)
            return (false, result.Errors.Select(e => e.Description).ToArray());

        role = (await roleManager.FindByNameAsync(role.Name!))!;

        foreach (var claim in claims.Distinct())
        {
            result = await roleManager.AddClaimAsync(role,
                new Claim(CustomClaims.Permission, ApplicationPermissions.GetPermissionByValue(claim)!));

            if (!result.Succeeded)
            {
                await DeleteRoleAsync(role);
                return (false, result.Errors.Select(e => e.Description).ToArray());
            }
        }

        return (true, []);
    }

    public async Task<(bool Succeeded, string[] Errors)> UpdateRoleAsync(ApplicationRole role, IEnumerable<string>? claims)
    {
        if (claims != null)
        {
            var invalidClaims = claims.Where(c => ApplicationPermissions.GetPermissionByValue(c) == null).ToArray();
            if (invalidClaims.Length != 0)
                return (false, [$"The following claim types are invalid: {string.Join(", ", invalidClaims)}"]);
        }

        var result = await roleManager.UpdateAsync(role);
        if (!result.Succeeded)
            return (false, result.Errors.Select(e => e.Description).ToArray());

        if (claims != null)
        {
            var roleClaims = (await roleManager.GetClaimsAsync(role))
                .Where(c => c.Type == CustomClaims.Permission);
            var roleClaimValues = roleClaims.Select(c => c.Value).ToArray();

            var claimsToRemove = roleClaimValues.Except(claims).ToArray();
            var claimsToAdd = claims.Except(roleClaimValues).Distinct().ToArray();

            foreach (var claim in claimsToRemove)
            {
                result = await roleManager.RemoveClaimAsync(role, roleClaims.First(c => c.Value == claim));
                if (!result.Succeeded)
                    return (false, result.Errors.Select(e => e.Description).ToArray());
            }

            foreach (var claim in claimsToAdd)
            {
                result = await roleManager.AddClaimAsync(role, new Claim(CustomClaims.Permission,
                    ApplicationPermissions.GetPermissionByValue(claim)!));
                if (!result.Succeeded)
                    return (false, result.Errors.Select(e => e.Description).ToArray());
            }
        }

        return (true, []);
    }

    public async Task<(bool Success, string[] Errors)> TestCanDeleteRoleAsync(string roleId)
    {
        var errors = new List<string>();

        if (await context.UserRoles.Where(r => r.RoleId == roleId).AnyAsync())
            errors.Add("Role has associated users");

        return (errors.Count == 0, errors.ToArray());
    }

    public async Task<(bool Succeeded, string[] Errors)> DeleteRoleAsync(string roleName)
    {
        var role = await roleManager.FindByNameAsync(roleName);

        if (role != null)
            return await DeleteRoleAsync(role);

        return (true, []);
    }

    public async Task<(bool Succeeded, string[] Errors)> DeleteRoleAsync(ApplicationRole role)
    {
        var result = await roleManager.DeleteAsync(role);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }
}
