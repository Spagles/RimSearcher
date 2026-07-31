using ConsoleAppFramework;
using RimSearcher.Cli.Maintenance;

namespace RimSearcher.Cli.Commands;

internal static class MaintenanceCommands
{
    public static void Register(ConsoleApp.ConsoleAppBuilder app)
    {
        app.Add("install", PathInstaller.Install);
        app.Add("update", ReleaseUpdater.Update);
    }
}
