using Identity.Domain.Entities;
using Identity.Domain.Interfaces;
using Identity.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Identity.Infrastructure.Data;

/// <summary>
/// Mirrors the monolith's DatabaseSeeder for the Identity bounded context. Only seeds when the
/// users table is empty so it never fights the backfill / forward CDC during the strangler window.
/// </summary>
public class DatabaseSeeder(
    IdentityDbContext dbContext,
    ILogger<DatabaseSeeder> logger,
    IUserAccountService userAccountService,
    IUserRoleService userRoleService)
{
    public async Task SeedAsync()
    {
        await dbContext.Database.MigrateAsync();
        await SeedDefaultUsersAsync();
    }

    private async Task SeedDefaultUsersAsync()
    {
        if (await dbContext.Users.AnyAsync())
            return;

        logger.LogInformation("Generating inbuilt accounts");

        const string adminRoleName = "administrator";
        const string userRoleName = "user";

        await EnsureRoleAsync(adminRoleName, "Default administrator", ApplicationPermissions.GetAllPermissionValues());
        await EnsureRoleAsync(userRoleName, "Default user", []);

        await CreateUserAsync("admin", "tempP@ss123", "Inbuilt Administrator", "admin@ebenmonney.com", "+1 (123) 000-0000", [adminRoleName]);
        await CreateUserAsync("user", "tempP@ss123", "Inbuilt Standard User", "user@ebenmonney.com", "+1 (123) 000-0001", [userRoleName]);

        logger.LogInformation("Inbuilt account generation completed");
    }

    private async Task EnsureRoleAsync(string roleName, string description, string[] claims)
    {
        if (await userRoleService.GetRoleByNameAsync(roleName) != null)
            return;

        logger.LogInformation("Generating default role: {roleName}", roleName);

        var applicationRole = new ApplicationRole(roleName, description);
        var result = await userRoleService.CreateRoleAsync(applicationRole, claims);

        if (!result.Succeeded)
            throw new Exception($"Seeding \"{description}\" role failed. Errors: {string.Join(Environment.NewLine, result.Errors)}");
    }

    private async Task CreateUserAsync(string userName, string password, string fullName, string email, string phoneNumber, string[] roles)
    {
        logger.LogInformation("Generating default user: {userName}", userName);

        var applicationUser = new ApplicationUser
        {
            UserName = userName,
            FullName = fullName,
            Email = email,
            PhoneNumber = phoneNumber,
            EmailConfirmed = true,
            IsEnabled = true
        };

        var result = await userAccountService.CreateUserAsync(applicationUser, roles, password);

        if (!result.Succeeded)
            throw new Exception($"Seeding \"{userName}\" user failed. Errors: {string.Join(Environment.NewLine, result.Errors)}");
    }
}
