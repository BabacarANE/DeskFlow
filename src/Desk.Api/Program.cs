using Desk.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddDeskHealthChecks(builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapDeskHealthChecks();

app.Run();

public partial class Program;
