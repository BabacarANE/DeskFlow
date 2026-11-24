using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Desk.Api.Auth;
using Desk.Api.Contracts;
using Desk.Api.Endpoints;
using Desk.Api.Http;
using Desk.Infrastructure;
using Desk.Infrastructure.Bootstrap;
using Desk.Infrastructure.Messaging;
using FluentValidation;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddDeskHealthChecks(builder.Configuration);
builder.Services.AddDeskIdentity(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddDeskAuthentication(builder.Configuration);
// Outbox writer and publisher; the topology is declared in the background (a broker that is down must not stop the API).
builder.Services.AddDeskMessaging(builder.Configuration);
builder.Services.AddDeskTopologyStartup();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidatorsFromAssemblyContaining<CreateTenantRequestValidator>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddScoped<Desk.Api.Tickets.TicketService>();
builder.Services.AddOpenApi();

// CORS: closed by default (same-site deployment behind nginx); origins are listed in configuration.
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Rate limiting on everything under /auth: the API endpoints and the OAuth callbacks.
var authPermits = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 30);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var path = http.Request.Path;
        var isAuth = path.StartsWithSegments("/api/v1/auth") || path.StartsWithSegments("/signin-google") || path.StartsWithSegments("/signin-github");
        return isAuth
            ? RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("none");
    });
    o.OnRejected = async (context, _) =>
        await Results.Problem(statusCode: 429, title: "Trop de requêtes", detail: "Réessayez dans une minute.")
            .ExecuteAsync(context.HttpContext);
});

var app = builder.Build();

if (DatabaseInitializer.ShouldRun(app.Configuration, app.Environment.IsDevelopment()))
{
    await DatabaseInitializer.RunAsync(app.Services);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapDeskHealthChecks();
app.MapOpenApi();
app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapTenantEndpoints();
app.MapUserEndpoints();
app.MapInvitationEndpoints();
app.MapCategoryEndpoints();
app.MapTicketEndpoints();

app.Run();

public partial class Program;
