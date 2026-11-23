using Desk.Infrastructure;
using Desk.Infrastructure.Messaging;
using Desk.Worker.Commands;
using Serilog;

// "dotnet Desk.Worker.dll replay-dlq [--max N] [--dry-run]": a one-shot command, no web host, no background service.
if (args.Length > 0 && args[0] == ReplayDlqCommand.Name)
{
    return await ReplayDlqCommand.RunAsync(args[1..], Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddDeskHealthChecks(builder.Configuration);

// Persistence, e-mail, messaging, the Worker's consumers (EmailAckConsumer...) and the outbox dispatcher.
// A new consumer is a class with [Consumer(...)] in Desk.Infrastructure: AddDeskConsumers picks it up.
builder.Services.AddDeskWorker(builder.Configuration);

var app = builder.Build();

app.MapDeskHealthChecks();

app.Run();
return 0;
