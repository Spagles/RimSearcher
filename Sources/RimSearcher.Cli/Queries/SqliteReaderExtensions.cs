using Microsoft.Data.Sqlite;

namespace RimSearcher.Cli.Queries;

internal static class SqliteReaderExtensions
{
    public static string ReadLabel(this SqliteDataReader reader, int nameOrdinal, int labelOrdinal) =>
        reader.IsDBNull(labelOrdinal) ? reader.GetString(nameOrdinal) : reader.GetString(labelOrdinal);

    public static string? ReadOptionalString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
