using ConsoleAppFramework;
using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Queries;

namespace RimSearcher.Cli.Commands;

internal static class SearchCommands
{
    public static void Register(ConsoleApp.ConsoleAppBuilder app, DefRepository repository, JsonOutput output)
    {
        app.Add("search", ([Argument] string keyword, string? type = null, string? mod = null, int limit = 20, bool count = false) =>
        {
            if (count)
            {
                output.Write(new { count = repository.CountSearchResults(keyword, type, mod) });
                return;
            }
            output.Write(repository.Search(keyword, type, mod, limit));
        });

        app.Add("list", (string? type = null, string? mod = null, int limit = 20, int offset = 0) =>
        {
            output.Write(repository.List(type, mod, limit, offset));
        });
    }
}
