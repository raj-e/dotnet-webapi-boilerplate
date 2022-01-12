using System.Data.SqlClient;
using System.Security.Claims;
using FSH.WebAPI.Domain.Multitenancy;
using FSH.WebAPI.Infrastructure.Auth.Permissions;
using FSH.WebAPI.Infrastructure.Common;
using FSH.WebAPI.Infrastructure.Common.Extensions;
using FSH.WebAPI.Infrastructure.Identity;
using FSH.WebAPI.Infrastructure.Persistence.Context;
using FSH.WebAPI.Infrastructure.Seeding;
using FSH.WebAPI.Shared.Authorization;
using FSH.WebAPI.Shared.Multitenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using Serilog;

namespace FSH.WebAPI.Infrastructure.Multitenancy;

public class TenantBootstrapper
{
    private static readonly ILogger _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

    public static void Initialize(ApplicationDbContext appContext, string dbProvider, string rootConnectionString, Tenant tenant, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, IList<IDatabaseSeeder> seeders)
    {
        string? connectionString = string.IsNullOrEmpty(tenant.ConnectionString) ? rootConnectionString : tenant.ConnectionString;
        if (string.IsNullOrEmpty(connectionString))
            return;

        if (TryValidateConnectionString(dbProvider, connectionString, tenant.Key))
        {
            appContext.Database.SetConnectionString(connectionString);
            if (appContext.Database.GetMigrations().Any())
            {
                if (appContext.Database.GetPendingMigrations().Any())
                {
                    _logger.Information($"Applying Migrations for '{tenant.Key}' tenant.");
                    appContext.Database.Migrate();
                }

                if (appContext.Database.CanConnect())
                {
                    _logger.Information($"Connection to {tenant.Key}'s Database Succeeded.");
                    SeedRolesAsync(tenant, roleManager, appContext).GetAwaiter().GetResult();
                    SeedTenantAdminAsync(tenant, userManager, roleManager, appContext).GetAwaiter().GetResult();
                }

                foreach (var seeder in seeders)
                {
                    seeder.Initialize(tenant);
                }
            }
        }
    }

    public static bool TryValidateConnectionString(string dbProvider, string connectionString, string? key)
    {
        try
        {
            switch (dbProvider.ToLowerInvariant())
            {
                case DbProviderKeys.Npgsql:
                    var postgresqlcs = new NpgsqlConnectionStringBuilder(connectionString);
                    break;

                case DbProviderKeys.MySql:
                    var mysqlcs = new MySqlConnectionStringBuilder(connectionString);
                    break;

                case DbProviderKeys.SqlServer:
                    var mssqlcs = new SqlConnectionStringBuilder(connectionString);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"{key} Connection String Exception : {ex.Message}");
            return false;
        }
    }

    private static async Task SeedRolesAsync(Tenant tenant, RoleManager<ApplicationRole> roleManager, ApplicationDbContext applicationDbContext)
    {
        foreach (string roleName in RoleService.DefaultRoles)
        {
            var roleStore = new RoleStore<ApplicationRole>(applicationDbContext);

            var role = new ApplicationRole(roleName, tenant.Key, $"{roleName} Role for {tenant.Key} Tenant");
            if (!await applicationDbContext.Roles.IgnoreQueryFilters().AnyAsync(r => r.Name == roleName && r.Tenant == tenant.Key))
            {
                await roleStore.CreateAsync(role);
                _logger.Information($"Seeding {roleName} Role for '{tenant.Key}' Tenant.");
            }

            if (roleName == FSHRoles.Basic)
            {
                var basicRole = await roleManager.Roles.IgnoreQueryFilters()
                    .Where(a => a.NormalizedName == FSHRoles.Basic.ToUpperInvariant() && a.Tenant == tenant.Key)
                    .FirstOrDefaultAsync();
                if (basicRole is null)
                    continue;
                var basicClaims = await roleManager.GetClaimsAsync(basicRole);
                foreach (string permission in DefaultPermissions.Basics)
                {
                    if (!basicClaims.Any(a => a.Type == FSHClaims.Permission && a.Value == permission))
                    {
                        await roleManager.AddClaimAsync(basicRole, new Claim(FSHClaims.Permission, permission));
                        _logger.Information($"Seeding Basic Permission '{permission}' for '{tenant.Key}' Tenant.");
                    }
                }
            }
        }
    }

    private static async Task SeedTenantAdminAsync(Tenant tenant, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, ApplicationDbContext applicationDbContext)
    {
        if (string.IsNullOrEmpty(tenant.Key) || string.IsNullOrEmpty(tenant.AdminEmail))
        {
            return;
        }

        string adminUserName = $"{tenant.Key.Trim()}.{FSHRoles.Admin}".ToLowerInvariant();
        var superUser = new ApplicationUser
        {
            FirstName = tenant.Key.Trim().ToLowerInvariant(),
            LastName = FSHRoles.Admin,
            Email = tenant.AdminEmail,
            UserName = adminUserName,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = tenant.AdminEmail?.ToUpperInvariant(),
            NormalizedUserName = adminUserName.ToUpperInvariant(),
            IsActive = true,
            Tenant = tenant.Key.Trim().ToLowerInvariant()
        };
        if (!applicationDbContext.Users.IgnoreQueryFilters().Any(u => u.Email == tenant.AdminEmail))
        {
            var password = new PasswordHasher<ApplicationUser>();
            superUser.PasswordHash = password.HashPassword(superUser, MultitenancyConstants.DefaultPassword);
            var userStore = new UserStore<ApplicationUser>(applicationDbContext);
            await userStore.CreateAsync(superUser);
            _logger.Information($"Seeding Default Admin User for '{tenant.Key}' Tenant.");
        }

        await AssignAdminRoleAsync(superUser.Email, tenant.Key, applicationDbContext, userManager, roleManager);
    }

    private static async Task AssignAdminRoleAsync(string email, string tenant, ApplicationDbContext applicationDbContext, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager)
    {
        var user = await userManager.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email.Equals(email));
        if (user == null) return;
        var roleRecord = await roleManager.Roles.IgnoreQueryFilters()
            .Where(a => a.NormalizedName == FSHRoles.Admin.ToUpperInvariant() && a.Tenant == tenant)
            .FirstOrDefaultAsync();
        if (roleRecord == null) return;
        bool isUserInRole = await applicationDbContext.UserRoles.AnyAsync(a => a.UserId == user.Id && a.RoleId == roleRecord.Id);
        if (!isUserInRole)
        {
            applicationDbContext.UserRoles.Add(new IdentityUserRole<string>() { RoleId = roleRecord.Id, UserId = user.Id });
            await applicationDbContext.SaveChangesAsync();
            _logger.Information($"Assigning Admin Permissions for '{tenant}' Tenant.");
        }

        var allClaims = await roleManager.GetClaimsAsync(roleRecord);
        foreach (string permission in typeof(FSHPermissions).GetNestedClassesStaticStringValues())
        {
            if (!allClaims.Any(a => a.Type == FSHClaims.Permission && a.Value == permission))
            {
                await roleManager.AddClaimAsync(roleRecord, new Claim(FSHClaims.Permission, permission));
            }
        }

        if (tenant == MultitenancyConstants.Root.Key && email == MultitenancyConstants.Root.EmailAddress)
        {
            foreach (string rootPermission in typeof(FSHRootPermissions).GetNestedClassesStaticStringValues())
            {
                if (!allClaims.Any(a => a.Type == FSHClaims.Permission && a.Value == rootPermission))
                {
                    await roleManager.AddClaimAsync(roleRecord, new Claim(FSHClaims.Permission, rootPermission));
                }
            }
        }

        await applicationDbContext.SaveChangesAsync();
    }
}