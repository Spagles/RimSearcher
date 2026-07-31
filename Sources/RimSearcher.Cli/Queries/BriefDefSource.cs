using RimSearcher.Cli.Models;

namespace RimSearcher.Cli.Queries;

internal sealed record BriefDefSource(
    string DefName,
    string DefType,
    string Label,
    string ModName,
    string? PackageId,
    string FullData);
