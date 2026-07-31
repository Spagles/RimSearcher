using RimSearcher.DataMod.Export;
using RimSearcher.DataMod.Reflection;
using RimSearcher.DataMod.Search;
using Verse;

namespace RimSearcher.DataMod;

public static class DefExporter
{
    private const int MaxFieldDepth = 3;
    private const int BatchSize = 500;
    private const int MaxFieldValuesPerDef = 5000;

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
    /// Exports every currently loaded RimWorld definition to a searchable SQLite database.
    /// </summary>
    public static void Export(string dbPath, Action<string>? log = null, Action<int, int, string>? progress = null)
    {
        void Log(string msg) => log?.Invoke(msg);

        Log($"开始导出 Def 数据库到: {dbPath}");

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
            Log("已删除旧数据库文件");
        }

        using var conn = ExportDatabase.Open(dbPath, Log);
        ExportSchema.Create(conn);
        Log("数据库 schema 已创建");

        var defTypes = GenDefDatabase.AllDefTypesWithDatabases().ToList();
        Log($"发现 {defTypes.Count} 个 Def 类型");

        int estimatedTotal = CountDefs(defTypes);
        Log($"预估总数: {estimatedTotal} 个 Def");
        progress?.Invoke(0, estimatedTotal, "开始处理...");

        int totalDefs = 0;
        int defId = 0;
        var fieldValueInserts = new List<(int defId, string fieldPath, string fieldValue)>();

        using var tx = conn.BeginTransaction();

        using (var defWriter = new DefRecordWriter(conn))
        using (var searchWriter = new SearchIndexWriter(conn))
        {
            foreach (var defType in defTypes)
            {
                IEnumerable<Def> defs;
                try
                {
                    defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType);
                }
                catch (Exception ex)
                {
                    Log($"跳过类型 {defType.Name}: {ex.Message}");
                    continue;
                }

                string typeName = defType.Name;

                foreach (var def in defs)
                {
                    defId++;
                    totalDefs++;

                    string json;
                    try
                    {
                        json = DefJsonSerializer.Serialize(def);
                    }
                    catch (Exception ex)
                    {
                        Log($"序列化失败 {typeName}/{def.defName}: {ex.Message}");
                        json = "{}";
                    }

                    string? label = null;
                    try { label = def.label; } catch { }
                    string? description = null;
                    try { description = def.description; } catch { }
                    string modName = def.modContentPack?.Name ?? "Unknown";
                    string? packageId = null;
                    try { packageId = def.modContentPack?.PackageId; } catch { }
                    string? sourceFile = null;
                    try { sourceFile = def.fileName; } catch { }

                    defWriter.Write(
                        defId,
                        def.defName ?? "",
                        typeName,
                        label,
                        description,
                        modName,
                        packageId,
                        sourceFile,
                        json);

                    // Build FTS text and insert into FTS5 index
                    var fieldTexts = new List<string>();
                    ExtractFieldValues(def, defId, fieldValueInserts, fieldTexts);
                    var ftsText = SearchTextBuilder.Build(def.defName, label, description, fieldTexts);

                    searchWriter.Write(defId, def.defName ?? "", label, description, ftsText);

                    if (fieldValueInserts.Count >= BatchSize)
                    {
                        FieldValueWriter.Flush(conn, fieldValueInserts);
                    }

                    if (totalDefs % BatchSize == 0)
                    {
                        Log($"已处理 {totalDefs} 个 Def...");
                    }
                }
            }
        }

        FieldValueWriter.Flush(conn, fieldValueInserts);

        tx.Commit();
        Log($"已写入 {totalDefs} 个 Def");

        conn.Close();
        Log($"导出完成: {dbPath} ({new FileInfo(dbPath).Length / 1024 / 1024} MB)");
    }


    private static int CountDefs(IEnumerable<Type> defTypes)
    {
        int total = 0;
        foreach (var defType in defTypes)
        {
            try
            {
                total += GenDefDatabase.GetAllDefsInDatabaseForDef(defType).Count();
            }
            catch
            {
                // Some third-party Def databases may fail enumeration. Export still continues.
            }
        }

        return total;
    }




    #region Field Values Extraction

    private static void ExtractFieldValues(Def def, int defId, List<(int, string, string)> inserts, List<string> allTexts)
    {
        var visited = new HashSet<object>();
        int count = 0;
        ExtractFieldValuesRecursive(def, defId, "", inserts, allTexts, visited, 0, ref count);
    }
    private static bool TryInsertFieldValue(int defId, string fieldPath, string fieldValue, List<(int, string, string)> inserts, List<string> allTexts, ref int count)
    {
        if (count >= MaxFieldValuesPerDef) return false;
        if (string.IsNullOrEmpty(fieldValue)) return true;
        if (SkipFieldNames.Contains(fieldPath)) return true;
        foreach (var prefix in SkipFieldPrefixes)
            if (fieldPath.StartsWith(prefix, StringComparison.Ordinal)) return true;
        allTexts.Add(fieldValue);
        inserts.Add((defId, fieldPath, fieldValue));
        count++;
        return true;
    }

    private static void ExtractFieldValuesRecursive(object? obj, int defId, string pathPrefix, List<(int, string, string)> inserts, List<string> allTexts, HashSet<object> visited, int depth, ref int count)
    {
        if (obj == null) return;
        if (depth > MaxFieldDepth) return;
        if (count >= MaxFieldValuesPerDef) return;

        Type t = obj.GetType();

        if (!t.IsValueType)
        {
            if (visited.Contains(obj)) return;
            visited.Add(obj);
        }

        try
        {
            // Collections
            if (obj is System.Collections.IList list)
            {
                for (int i = 0; i < list.Count && count < MaxFieldValuesPerDef; i++)
                {
                    string itemPath = string.IsNullOrEmpty(pathPrefix) ? $"[{i}]" : $"{pathPrefix}[{i}]";
                    var item = list[i];
                    if (item is string strVal)
                    {
                        if (!TryInsertFieldValue(defId, itemPath, strVal, inserts, allTexts, ref count)) return;
                    }
                    else if (item is Type typeVal)
                    {
                        if (!TryInsertFieldValue(defId, itemPath, typeVal.FullName ?? typeVal.Name, inserts, allTexts, ref count)) return;
                    }
                    else if (item != null && item.GetType().IsClass && !(item is ValueType))
                    {
                        ExtractFieldValuesRecursive(item, defId, itemPath, inserts, allTexts, visited, depth + 1, ref count);
                    }
                }
                return;
            }

            // Dictionaries
            if (obj is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (count >= MaxFieldValuesPerDef) return;
                    string keyStr = entry.Key?.ToString() ?? "";
                    string entryPath = string.IsNullOrEmpty(pathPrefix) ? keyStr : $"{pathPrefix}.{keyStr}";
                    if (entry.Value is string dictVal)
                    {
                        if (!TryInsertFieldValue(defId, entryPath, dictVal, inserts, allTexts, ref count)) return;
                    }
                    else if (entry.Value is Type typeVal)
                    {
                        if (!TryInsertFieldValue(defId, entryPath, typeVal.FullName ?? typeVal.Name, inserts, allTexts, ref count)) return;
                    }
                    else if (entry.Value != null && entry.Value.GetType().IsClass && !(entry.Value is ValueType))
                    {
                        ExtractFieldValuesRecursive(entry.Value, defId, entryPath, inserts, allTexts, visited, depth + 1, ref count);
                    }
                }
                return;
            }

            // Skip excluded namespaces for general objects
            string? ns = t.Namespace;
            if (ns != null)
            {
                if (ReflectionTraversalPolicy.IsExcludedNamespace(t))
                    return;
            }

            // General object: iterate public instance fields
            var fields = PublicFieldCache.Get(t);
            foreach (var field in fields)
            {
                if (count >= MaxFieldValuesPerDef) return;
                if (field.Name.StartsWith("<", StringComparison.Ordinal)) continue;

                string fieldPath = string.IsNullOrEmpty(pathPrefix) ? field.Name : $"{pathPrefix}.{field.Name}";

                object? fieldValue;
                try { fieldValue = field.GetValue(obj); }
                catch { continue; }

                if (fieldValue == null) continue;

                if (fieldValue is string strField)
                {
                    if (!TryInsertFieldValue(defId, fieldPath, strField, inserts, allTexts, ref count)) return;
                }
                else if (fieldValue is Type typeField)
                {
                    if (!TryInsertFieldValue(defId, fieldPath, typeField.FullName ?? typeField.Name, inserts, allTexts, ref count)) return;
                }
                else if (fieldValue is ValueType)
                {
                    string? valStr = fieldValue.ToString();
                    if (valStr != null)
                    {
                        if (!TryInsertFieldValue(defId, fieldPath, valStr, inserts, allTexts, ref count)) return;
                    }
                }
                else if (fieldValue is Def defRef)
                {
                    if (!TryInsertFieldValue(defId, fieldPath, defRef.defName, inserts, allTexts, ref count)) return;
                }
                else if (fieldValue.GetType().IsEnum)
                {
                    string? enumStr = fieldValue.ToString();
                    if (enumStr != null)
                    {
                        if (!TryInsertFieldValue(defId, fieldPath, enumStr, inserts, allTexts, ref count)) return;
                    }
                }
                else
                {
                    ExtractFieldValuesRecursive(fieldValue, defId, fieldPath, inserts, allTexts, visited, depth + 1, ref count);
                }
            }
        }
        finally
        {
            if (!t.IsValueType) visited.Remove(obj);
        }
    }

    #endregion
}
