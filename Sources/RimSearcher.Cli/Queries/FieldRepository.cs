using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Models;

namespace RimSearcher.Cli.Queries;

internal sealed class FieldRepository
{
    private readonly DatabaseConnectionFactory _connections;

    public FieldRepository(DatabaseConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<FieldMatch> Find(string fieldPath, string value, string? type, string? mod, int limit)
    {
        using var connection = _connections.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.def_name, d.def_type, d.label, d.mod_name, d.package_id, fv.field_path, fv.field_value
            FROM defs d
            JOIN field_values fv ON d.id = fv.def_id
            WHERE fv.field_path LIKE '%' || @path
              AND fv.field_value = @value
              AND (@type IS NULL OR d.def_type = @type)
              AND (@mod IS NULL OR d.mod_name = @mod)
            ORDER BY d.def_type, d.def_name
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@path", fieldPath);
        command.Parameters.AddWithValue("@value", value);
        QueryParameters.AddFilters(command, type, mod);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<FieldMatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FieldMatch(
                reader.GetString(0), reader.GetString(1), reader.ReadLabel(0, 2),
                reader.GetString(3), reader.ReadOptionalString(4),
                reader.GetString(5), reader.GetString(6)));
        }
        return results;
    }

    public IReadOnlyList<FieldValue> GetFields(string defName, string type, int limit)
    {
        using var connection = _connections.Open();
        using var command = connection.CreateCommand();
        int sqlLimit = Math.Min(limit * 2, 10000);
        command.CommandText = """
            SELECT fv.field_path, fv.field_value
            FROM field_values fv
            JOIN defs d ON fv.def_id = d.id
            WHERE d.def_name = @name AND d.def_type = @type
            ORDER BY fv.field_path
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@name", defName);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@limit", sqlLimit);

        var results = new List<FieldValue>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (results.Count >= limit)
                break;
            var path = reader.GetString(0);
            if (IsNoiseField(path))
                continue;
            results.Add(new FieldValue(path, reader.GetString(1)));
        }
        return results;
    }

    public IReadOnlyList<string> GetValues(string fieldPath, int limit)
    {
        using var connection = _connections.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT fv.field_value
            FROM field_values fv
            WHERE fv.field_path LIKE '%' || @path
            ORDER BY fv.field_value
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@path", fieldPath);
        command.Parameters.AddWithValue("@limit", limit);

        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static bool IsNoiseField(string path)
    {
        if (path.StartsWith("modContentPack.", StringComparison.Ordinal)
            || path.Contains(".modContentPack.", StringComparison.Ordinal))
            return true;

        int lastDot = path.LastIndexOf('.');
        int lastBracket = path.LastIndexOf('[');
        int segmentStart = Math.Max(lastDot, lastBracket) + 1;
        return NoiseFieldNames.Contains(path[segmentStart..]);
    }

    private static readonly HashSet<string> NoiseFieldNames = new()
    {
        "debugRandomId", "defNameHash", "generated",
        "ignoreConfigErrors", "ignoreIllegalLabelCharacterConfigError",
        "index", "shortHash"
    };
}
