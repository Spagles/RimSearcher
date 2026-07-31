using System.Collections;
using Verse;

namespace RimSearcher.DataMod.Reflection;

/// <summary>
/// 遍历 Def 对象树并提取可检索的字段值，供 field_values 表与 FTS 检索文本使用。
/// 路径格式：顶层字段名、嵌套用 "." 连接、列表项用 "[i]"、字典项用 ".key"；
/// 深度上限 3、单 Def 上限 5000 条，噪声字段与 modContentPack 前缀被过滤。
/// </summary>
internal static class DefFieldExtractor
{
    private const int MaxDepth = 3;
    private const int MaxValuesPerDef = 5000;

    // 注意：以下名单与 CLI 的 FieldRepository.NoiseFieldNames 内容一致，修改时必须同步两侧。
    // 两侧语义不同：DataMod 按完整路径精确过滤，CLI 按路径末段匹配过滤。
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

    /// <summary>
    /// 提取指定 Def 的全部字段值：写入 inserts 供入库，同时收集到 allTexts 供 FTS 文本构建。
    /// </summary>
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
            else if (item is Def defReference)
            {
                if (!TryAddValue(defId, itemPath, defReference.defName, inserts, allTexts, ref count))
                    return;
            }
            else if (item is ValueType)
            {
                string? scalarText = item.ToString();
                if (scalarText != null
                    && !TryAddValue(defId, itemPath, scalarText, inserts, allTexts, ref count))
                    return;
            }
            else if (item != null && item.GetType().IsClass)
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
            else if (entry.Value is Def defReference)
            {
                if (!TryAddValue(defId, entryPath, defReference.defName, inserts, allTexts, ref count))
                    return;
            }
            else if (entry.Value is ValueType)
            {
                string? scalarText = entry.Value.ToString();
                if (scalarText != null
                    && !TryAddValue(defId, entryPath, scalarText, inserts, allTexts, ref count))
                    return;
            }
            else if (entry.Value != null && entry.Value.GetType().IsClass)
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
