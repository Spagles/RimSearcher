using System.Collections;
using System.Globalization;
using System.Text;
using Verse;

namespace RimSearcher.DataMod.Reflection;

internal static class DefJsonSerializer
{
    private const int MaxDepth = 10;

    public static string Serialize(Def def)
    {
        var builder = new StringBuilder();
        var visited = new HashSet<object>();
        SerializeValue(def, builder, visited, 0);
        return builder.ToString();
    }

    private static void SerializeValue(
        object? value,
        StringBuilder builder,
        HashSet<object> visited,
        int depth)
    {
        if (value == null)
        {
            builder.Append("null");
            return;
        }

        if (depth > MaxDepth)
        {
            builder.Append("\"...\"");
            return;
        }

        Type type = value.GetType();
        if (TrySerializeSimpleValue(value, type, builder))
            return;

        if (!type.IsValueType)
        {
            if (visited.Contains(value))
            {
                builder.Append("\"$cyclic_ref\"");
                return;
            }

            visited.Add(value);
        }

        try
        {
            if (depth > 0 && value is Def defReference)
            {
                AppendQuoted(builder, defReference.defName);
                return;
            }

            if (value is IList list)
            {
                SerializeList(list, builder, visited, depth);
                return;
            }

            if (value is IDictionary dictionary)
            {
                SerializeDictionary(dictionary, builder, visited, depth);
                return;
            }

            if (value is Type typeReference)
            {
                AppendQuoted(builder, typeReference.FullName ?? typeReference.Name);
                return;
            }

            if (ReflectionTraversalPolicy.IsExcludedNamespace(type))
            {
                builder.Append("{}");
                return;
            }

            SerializeObject(value, type, builder, visited, depth);
        }
        finally
        {
            if (!type.IsValueType)
                visited.Remove(value);
        }
    }

    private static bool TrySerializeSimpleValue(object value, Type type, StringBuilder builder)
    {
        switch (value)
        {
            case string text:
                AppendQuoted(builder, text);
                return true;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return true;
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                builder.Append(value);
                return true;
            case float single:
                builder.Append(single.ToString("G", CultureInfo.InvariantCulture));
                return true;
            case double number:
                builder.Append(number.ToString("G", CultureInfo.InvariantCulture));
                return true;
            case decimal decimalValue:
                builder.Append(decimalValue.ToString("G", CultureInfo.InvariantCulture));
                return true;
        }

        if (!type.IsEnum)
            return false;

        AppendQuoted(builder, value.ToString());
        return true;
    }

    private static void SerializeList(IList list, StringBuilder builder, HashSet<object> visited, int depth)
    {
        builder.Append('[');
        for (int index = 0; index < list.Count; index++)
        {
            if (index > 0)
                builder.Append(',');
            SerializeValue(list[index], builder, visited, depth + 1);
        }
        builder.Append(']');
    }

    private static void SerializeDictionary(IDictionary dictionary, StringBuilder builder, HashSet<object> visited, int depth)
    {
        builder.Append('{');
        bool first = true;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (!first)
                builder.Append(',');
            first = false;
            SerializeValue(entry.Key, builder, visited, depth + 1);
            builder.Append(':');
            SerializeValue(entry.Value, builder, visited, depth + 1);
        }
        builder.Append('}');
    }

    private static void SerializeObject(
        object value,
        Type type,
        StringBuilder builder,
        HashSet<object> visited,
        int depth)
    {
        builder.Append('{');
        bool first = true;
        foreach (var field in PublicFieldCache.Get(type))
        {
            if (field.Name.StartsWith("<", StringComparison.Ordinal))
                continue;

            if (!first)
                builder.Append(',');
            first = false;
            AppendQuoted(builder, field.Name);
            builder.Append(':');

            try
            {
                SerializeValue(field.GetValue(value), builder, visited, depth + 1);
            }
            catch
            {
                builder.Append("null");
            }
        }
        builder.Append('}');
    }

    private static void AppendQuoted(StringBuilder builder, string? value)
    {
        builder.Append('"');
        builder.Append(Escape(value));
        builder.Append('"');
    }

    private static string Escape(string? value)
    {
        if (value == null)
            return string.Empty;

        var builder = new StringBuilder(value.Length + 4);
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (character < 0x20)
                        builder.Append($"\\u{(int)character:X4}");
                    else
                        builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
