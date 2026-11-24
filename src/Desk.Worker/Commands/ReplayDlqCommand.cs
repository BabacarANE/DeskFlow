using System.Globalization;
using Desk.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Worker.Commands;

/// <summary>
/// <c>replay-dlq [--max N] [--dry-run]</c>. Exit codes: 0 done, 1 some messages could not be replayed (they stay in the
/// DLQ), 2 bad arguments, 3 broker unreachable or lost during the replay (partial report printed).
/// </summary>
public static class ReplayDlqCommand
{
    public const string Name = "replay-dlq";

    public static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error, IConfiguration? configuration = null)
    {
        if (!TryParse(arguments, out var options, out var problem))
        {
            await error.WriteLineAsync(problem);
            await error.WriteLineAsync("Usage : dotnet Desk.Worker.dll replay-dlq [--max N] [--dry-run]");
            return 2;
        }

        configuration ??= BuildConfiguration(arguments);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(l => l.AddConfiguration(configuration.GetSection("Logging")).AddSimpleConsole(o => o.SingleLine = true));
        services.AddDeskReplay(configuration);
        await using var provider = services.BuildServiceProvider();

        try
        {
            var report = await provider.GetRequiredService<DlqReplayer>().ReplayAsync(options, CancellationToken.None);
            await Print(report, options, output);
            return report.HasProblems ? 1 : 0;
        }
        catch (ReplayInterruptedException ex)
        {
            await Print(ex.Partial, options, output);
            await error.WriteLineAsync("RabbitMQ interrompu pendant le rejeu : " + ex.InnerException?.Message);
            await error.WriteLineAsync("Les messages non confirmés sont restés dans desk.dlq ; un message republié mais dont l'acquittement a été perdu peut être rejoué deux fois (sans effet double : idempotence). Relancez la commande.");
            return 3;
        }
        catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException ex)
        {
            await error.WriteLineAsync("RabbitMQ injoignable : " + ex.Message);
            return 3;
        }
    }

    public static bool TryParse(string[] arguments, out ReplayOptions options, out string problem)
    {
        int? max = null;
        var dryRun = false;
        options = new ReplayOptions();
        problem = "";

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--max":
                    if (i + 1 >= arguments.Length
                        || !int.TryParse(arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                        || n < 1)
                    {
                        problem = "--max attend un entier supérieur ou égal à 1.";
                        return false;
                    }

                    max = n;
                    break;
                default:
                    problem = $"Argument inconnu : {arguments[i]}";
                    return false;
            }
        }

        options = new ReplayOptions(max, dryRun);
        return true;
    }

    private static IConfiguration BuildConfiguration(string[] arguments)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(arguments.Where(a => a.StartsWith("--ConnectionStrings", StringComparison.Ordinal)).ToArray())
            .Build();
    }

    private static async Task Print(ReplayReport report, ReplayOptions options, TextWriter output)
    {
        await output.WriteLineAsync(options.DryRun ? "replay-dlq (simulation, rien n'est modifié)" : "replay-dlq");
        foreach (var entry in report.Entries)
        {
            await output.WriteLineAsync($"  {entry.MessageId}  consommateur={entry.Consumer}  cible={entry.Target}  {entry.Status}");
            if (entry.Reason.Length > 0)
            {
                await output.WriteLineAsync($"    raison de l'échec : {entry.Reason}");
            }
        }

        await output.WriteLineAsync(
            $"Bilan : {report.Read} message(s) lu(s), {report.Replayed} rejoué(s), {report.Skipped} ignoré(s), {report.Failed} en échec.");
    }
}
