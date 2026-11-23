using System.Security.Cryptography;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Auth;
using Desk.Infrastructure.Email;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>EF Core (PostgreSQL) with tenant isolation: shared by the API and the Worker.</summary>
    public static IServiceCollection AddDeskPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddDbContext<DeskDbContext>((sp, options) => options
            .UseNpgsql(connection)
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));
        return services;
    }

    /// <summary>SMTP sender (MailKit), shared by the API (invitations) and the Worker (notifications).</summary>
    public static IServiceCollection AddDeskEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection("Smtp"));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }

    /// <summary>EF Core (PostgreSQL), tenant isolation, token services, e-mail.</summary>
    public static IServiceCollection AddDeskIdentity(
        this IServiceCollection services, IConfiguration configuration, bool isProduction)
    {
        services.AddDeskPersistence(configuration);

        var jwt = new JwtOptions();
        configuration.GetSection("Jwt").Bind(jwt);
        if (string.IsNullOrWhiteSpace(jwt.Key))
        {
            // Never a hard-coded key: outside production a random per-process key is enough.
            jwt.Key = isProduction
                ? throw new InvalidOperationException("Jwt:Key is not configured (set the Jwt__Key environment variable, 32 characters minimum).")
                : Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }

        JwtKeyPolicy.Validate(jwt.Key, isProduction);

        services.AddSingleton(jwt);
        services.AddScoped<JwtTokenService>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<ExternalLoginService>();

        services.AddDeskEmail(configuration);
        services.AddScoped<InvitationService>();
        return services;
    }
}
