using System.Text.Json;
using ConsoleAppFramework;
using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Models;
using RimSearcher.Cli.Queries;

namespace RimSearcher.Cli.Commands;

internal static class DefCommands
{
    public static void Register(ConsoleApp.ConsoleAppBuilder app, DefRepository repository, JsonOutput output)
    {
        app.Add("get", ([Argument] string defName, string? type = null, bool brief = false) =>
        {
            if (type == null)
            {
                var types = repository.FindTypes(defName);
                if (types.Count == 0)
                {
                    Console.Error.WriteLine($"Error: no Def found with defName '{defName}'");
                    Environment.Exit(ExitCodes.NotFound);
                }
                if (types.Count > 1)
                {
                    Console.Error.WriteLine($"Error: '{defName}' matches multiple Def types. Specify --type:");
                    foreach (var candidateType in types)
                        Console.Error.WriteLine($"  {candidateType}");
                    Environment.Exit(ExitCodes.NotFound);
                }
                type = types[0];
            }

            if (brief)
            {
                var source = repository.GetBriefSource(defName, type!);
                if (source == null)
                {
                    Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
                    Environment.Exit(ExitCodes.NotFound);
                }

                using var document = JsonDocument.Parse(source.FullData);
                var root = document.RootElement;
                string? thingClass = root.TryGetProperty("thingClass", out var thingClassElement)
                    ? thingClassElement.GetString()
                    : null;
                var compClasses = new List<string>();
                if (root.TryGetProperty("comps", out var comps)
                    && comps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var comp in comps.EnumerateArray())
                    {
                        if (comp.ValueKind == JsonValueKind.Object
                            && comp.TryGetProperty("compClass", out var componentClass))
                        {
                            var className = componentClass.GetString();
                            if (className != null)
                                compClasses.Add(className);
                        }
                    }
                }

                output.Write(new BriefDef(
                    source.DefName, source.DefType, source.Label, source.ModName,
                    source.PackageId, thingClass, compClasses));
                return;
            }

            var fullData = repository.GetFullData(defName, type!);
            if (fullData == null)
            {
                Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
                Environment.Exit(ExitCodes.NotFound);
            }
            Console.WriteLine(fullData);
        });
    }
}
