using Desk.Infrastructure;
using Desk.Worker;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddDeskHealthChecks(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

app.MapDeskHealthChecks();

app.Run();
