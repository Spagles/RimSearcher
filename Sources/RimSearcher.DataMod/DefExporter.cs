using RimSearcher.DataMod.Export;
using RimSearcher.DataMod.Reflection;
using RimSearcher.DataMod.Search;
using Verse;

namespace RimSearcher.DataMod;

public static class DefExporter
{
    private const int BatchSize = 500;

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
        var fieldValueInserts = new List<(int DefId, string FieldPath, string FieldValue)>();

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
                    DefFieldExtractor.Extract(def, defId, fieldValueInserts, fieldTexts);
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
}
