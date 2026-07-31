using System.Collections;
using Verse;

namespace RimSearcher.DataMod.Reflection;

internal static class DefFieldExtractor
{
    private const int MaxDepth = 3;
    private const int MaxValuesPerDef = 5000;

    private static readonly HashSet<string> SkipFieldNames = new()
    {
        "debugRandomId", "defNameHash", "generated",
        "ignoreConfigErrors", "ignoreIllegalLabelCharacterConfigError",
        "index", "shortHash"
    };

    private static readonly HashSet<string> SkipFieldPrefixes = new()
    {
        "modContentPack."
    };

    public static void Extract(
        Def def,
        int defId,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts)
    {
        var visited = new HashSet<object>();
        int count = 0;
        ExtractRecursive(def, defId, string.Empty, inserts, allTexts, visited, 0, ref count);
    }

    private static void ExtractRecursive(
        object? value,
        int defId,
        string pathPrefix,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts,
        HashSet<object> visited,
        int depth,
        ref int count)
    {
        if (value == null || depth > MaxDepth || count >= MaxValuesPerDef)
            return;

        Type type = value.GetType();
        if (!type.IsValueType)
        {
            if (visited.Contains(value))
                return;
            visited.Add(value);
        }

        try
        {
            if (value is IList list)
            {
                ExtractList(list, defId, pathPrefix, inserts, allTexts, visited, depth, ref count);
                return;
            }

            if (value is IDictionary dictionary)
            {
                ExtractDictionary(dictionary, defId, pathPrefix, inserts, allTexts, visited, depth, ref count);
                return;
            }

            if (ReflectionTraversalPolicy.IsExcludedNamespace(type))
                return;

            ExtractObjectFields(value, type, defId, pathPrefix, inserts, allTexts, visited, depth, ref count);
        }
        finally
        {
            if (!type.IsValueType)
                visited.Remove(value);
        }
    }

    private static void ExtractList(
        IList list,
        int defId,
        string pathPrefix,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts,
        HashSet<object> visited,
        int depth,
        ref int count)
    {
        for (int index = 0; index < list.Count && count < MaxValuesPerDef; index++)
        {
            string itemPath = string.IsNullOrEmpty(pathPrefix)
                ? $"[{index}]"
                : $"{pathPrefix}[{index}]";
            var item = list[index];

            if (item is string text)
            {
                if (!TryAddValue(defId, itemPath, text, inserts, allTexts, ref count))
                    return;
            }
            else if (item is Type itemType)
            {
                if (!TryAddValue(defId, itemPath, itemType.FullName ?? itemType.Name, inserts, allTexts, ref count))
                    return;
            }
            else if (item != null && item.GetType().IsClass && !(item is ValueType))
            {
                ExtractRecursive(item, defId, itemPath, inserts, allTexts, visited, depth + 1, ref count);
            }
        }
    }

    private static void ExtractDictionary(
        IDictionary dictionary,
        int defId,
        string pathPrefix,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts,
        HashSet<object> visited,
        int depth,
        ref int count)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count >= MaxValuesPerDef)
                return;

            string key = entry.Key?.ToString() ?? string.Empty;
            string entryPath = string.IsNullOrEmpty(pathPrefix)
                ? key
                : $"{pathPrefix}.{key}";

            if (entry.Value is string text)
            {
                if (!TryAddValue(defId, entryPath, text, inserts, allTexts, ref count))
                    return;
            }
            else if (entry.Value is Type valueType)
            {
                if (!TryAddValue(defId, entryPath, valueType.FullName ?? valueType.Name, inserts, allTexts, ref count))
                    return;
            }
            else if (entry.Value != null && entry.Value.GetType().IsClass && !(entry.Value is ValueType))
            {
                ExtractRecursive(entry.Value, defId, entryPath, inserts, allTexts, visited, depth + 1, ref count);
            }
        }
    }

    private static void ExtractObjectFields(
        object value,
        Type type,
        int defId,
        string pathPrefix,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts,
        HashSet<object> visited,
        int depth,
        ref int count)
    {
        foreach (var field in PublicFieldCache.Get(type))
        {
            if (count >= MaxValuesPerDef)
                return;
            if (field.Name.StartsWith("<", StringComparison.Ordinal))
                continue;

            string fieldPath = string.IsNullOrEmpty(pathPrefix)
                ? field.Name
                : $"{pathPrefix}.{field.Name}";

            object? fieldValue;
            try { fieldValue = field.GetValue(value); }
            catch { continue; }

            if (fieldValue == null)
                continue;

            if (fieldValue is string text)
            {
                if (!TryAddValue(defId, fieldPath, text, inserts, allTexts, ref count))
                    return;
            }
            else if (fieldValue is Type fieldType)
            {
                if (!TryAddValue(defId, fieldPath, fieldType.FullName ?? fieldType.Name, inserts, allTexts, ref count))
                    return;
            }
            else if (fieldValue is ValueType)
            {
                string? scalarText = fieldValue.ToString();
                if (scalarText != null
                    && !TryAddValue(defId, fieldPath, scalarText, inserts, allTexts, ref count))
                    return;
            }
            else if (fieldValue is Def defReference)
            {
                if (!TryAddValue(defId, fieldPath, defReference.defName, inserts, allTexts, ref count))
                    return;
            }
            else if (fieldValue.GetType().IsEnum)
            {
                string? enumText = fieldValue.ToString();
                if (enumText != null
                    && !TryAddValue(defId, fieldPath, enumText, inserts, allTexts, ref count))
                    return;
            }
            else
            {
                ExtractRecursive(fieldValue, defId, fieldPath, inserts, allTexts, visited, depth + 1, ref count);
            }
        }
    }

    private static bool TryAddValue(
        int defId,
        string fieldPath,
        string fieldValue,
        List<(int DefId, string FieldPath, string FieldValue)> inserts,
        List<string> allTexts,
        ref int count)
    {
        if (count >= MaxValuesPerDef)
            return false;
        if (string.IsNullOrEmpty(fieldValue))
            return true;
        if (SkipFieldNames.Contains(fieldPath))
            return true;

        foreach (var prefix in SkipFieldPrefixes)
        {
            if (fieldPath.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        allTexts.Add(fieldValue);
        inserts.Add((defId, fieldPath, fieldValue));
        count++;
        return true;
    }
}
