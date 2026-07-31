using ConsoleAppFramework;
using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Queries;

namespace RimSearcher.Cli.Commands;

internal static class StatisticsCommands
{
    public static void Register(ConsoleApp.ConsoleAppBuilder app, StatisticsRepository repository, JsonOutput output)
    {
        app.Add("types", () => output.Write(repository.GetTypes()));
        app.Add("mods", () => output.Write(repository.GetMods()));
    }
}
