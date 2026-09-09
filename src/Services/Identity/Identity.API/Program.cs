using Identity.API.Authorization;
using Identity.API.Authorization.Requirements;
using Identity.API.Configuration;
using Identity.API.Services;
using Identity.Domain.Entities;
using Identity.Domain.Interfaces;
using Identity.Domain.Services;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Outbox;
using Identity.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Logging;
using Microsoft.OpenApi.Models;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

/************* DATA *************/

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
    throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<IdentityDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName));
    options.UseOpenIddict();
});

/************* IDENTITY *************/

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
    .AddEntityFrameworkStores<IdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<IdentityOptions>(options =>
{
    options.User.RequireUniqueEmail = true;

    // Map ASP.NET Identity claim types onto the OpenIddict claim names (same as the monolith)
    options.ClaimsIdentity.UserNameClaimType = Claims.Name;
    options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
    options.ClaimsIdentity.RoleClaimType = Claims.Role;
    options.ClaimsIdentity.EmailClaimType = Claims.Email;
});

/************* OPENIDDICT *************/

var oidcCertificate = OidcCertificateLoader.Load(builder.Configuration, builder.Environment,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("OidcCertificateLoader"));

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<IdentityDbContext>();
    })
    .AddServer(options =>
    {
        options.SetTokenEndpointUris("connect/token");

        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow();

        options.RegisterScopes(
            Scopes.Profile,
            Scopes.Email,
            Scopes.Address,
            Scopes.Phone,
            Scopes.Roles);

        // Behind the gateway the public issuer must be identical on both apps for tokens to cross-validate.
        var issuer = builder.Configuration["OIDC:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
            options.SetIssuer(new Uri(issuer));

        if (oidcCertificate is not null)
        {
            options.AddEncryptionCertificate(oidcCertificate)
                   .AddSigningCertificate(oidcCertificate);
        }
        else
        {
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
        }

        // TLS is terminated at the API gateway; service-to-service traffic inside the compose network is plain HTTP.
        options.UseAspNetCore()
               .EnableTokenEndpointPassthrough()
               .DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

/************* AUTHORIZATION *************/

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.ViewAllUsersPolicy,
        policy => policy.RequireClaim(CustomClaims.Permission, ApplicationPermissions.ViewUsers))
    .AddPolicy(AuthPolicies.ManageAllUsersPolicy,
        policy => policy.RequireClaim(CustomClaims.Permission, ApplicationPermissions.ManageUsers))
    .AddPolicy(AuthPolicies.ViewAllRolesPolicy,
        policy => policy.RequireClaim(CustomClaims.Permission, ApplicationPermissions.ViewRoles))
    .AddPolicy(AuthPolicies.ViewRoleByRoleNamePolicy,
        policy => policy.Requirements.Add(new ViewRoleAuthorizationRequirement()))
    .AddPolicy(AuthPolicies.ManageAllRolesPolicy,
        policy => policy.RequireClaim(CustomClaims.Permission, ApplicationPermissions.ManageRoles))
    .AddPolicy(AuthPolicies.AssignAllowedRolesPolicy,
        policy => policy.Requirements.Add(new AssignRolesAuthorizationRequirement()));

builder.Services.AddSingleton<IAuthorizationHandler, ViewUserAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, ManageUserAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, ViewRoleAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, AssignRolesAuthorizationHandler>();

/************* API *************/

builder.Services.AddCors();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = OidcServerConfig.ServerName, Version = "v1" });
    c.OperationFilter<SwaggerAuthorizeOperationFilter>();
    c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new OpenApiOAuthFlows
        {
            Password = new OpenApiOAuthFlow { TokenUrl = new Uri("/connect/token", UriKind.Relative) }
        }
    });
});
builder.Services.AddHealthChecks();
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<IUserAccountService, UserAccountService>();
builder.Services.AddScoped<IUserRoleService, UserRoleService>();
builder.Services.AddScoped<IUserIdAccessor, UserIdAccessor>();
builder.Services.AddTransient<DatabaseSeeder>();

/************* REVERSE SYNC (identitydb -> monolith) *************/

builder.Services.Configure<ReverseSyncOptions>(builder.Configuration.GetSection(ReverseSyncOptions.SectionName));
builder.Services.PostConfigure<ReverseSyncOptions>(o =>
    o.MonolithConnectionString ??= builder.Configuration.GetConnectionString("MonolithConnection"));
builder.Services.AddHostedService<ReverseSyncPublisher>();

var app = builder.Build();

/************* PIPELINE *************/

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.DocumentTitle = "Swagger UI - Identity";
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{OidcServerConfig.ServerName} V1");
        c.OAuthClientId(OidcServerConfig.SwaggerClientID);
    });

    IdentityModelEventSource.ShowPII = true;
}

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/healthz");

/************* MIGRATE + SEED *************/

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
    await OidcServerConfig.RegisterClientApplicationsAsync(scope.ServiceProvider);
}

app.Run();
