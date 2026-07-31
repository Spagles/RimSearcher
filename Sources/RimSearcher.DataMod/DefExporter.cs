using System.Data.SQLite;
using System.Runtime.InteropServices;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

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

        var connStr = $"Data Source={dbPath};Version=3;";
        using var conn = new SQLiteConnection(connStr);
        conn.Open();
        LoadFtsExtension(conn, Log);
        ConfigureConnection(conn);

        CreateSchema(conn);
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

        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = @"
                INSERT INTO defs (id, def_name, def_type, label, description, mod_name, package_id, source_file, full_data)
                VALUES (@id, @dn, @dt, @lbl, @desc, @mod, @pkg, @src, @data)";
            var pId = insertCmd.Parameters.Add("@id", System.Data.DbType.Int32);
            var pDn = insertCmd.Parameters.Add("@dn", System.Data.DbType.String);
            var pDt = insertCmd.Parameters.Add("@dt", System.Data.DbType.String);
            var pLbl = insertCmd.Parameters.Add("@lbl", System.Data.DbType.String);
            var pDesc = insertCmd.Parameters.Add("@desc", System.Data.DbType.String);
            var pMod = insertCmd.Parameters.Add("@mod", System.Data.DbType.String);
            var pPkg = insertCmd.Parameters.Add("@pkg", System.Data.DbType.String);
            var pSrc = insertCmd.Parameters.Add("@src", System.Data.DbType.String);
            var pData = insertCmd.Parameters.Add("@data", System.Data.DbType.String);

            // FTS5 insert command
            using var ftsInsertCmd = conn.CreateCommand();
            ftsInsertCmd.CommandText = "INSERT INTO defs_fts(rowid, def_name, label, description, full_text) VALUES (@rid, @fdn, @flbl, @fdesc, @ftxt)";
            var pFtsRowid = ftsInsertCmd.Parameters.Add("@rid", System.Data.DbType.Int32);
            var pFtsDn = ftsInsertCmd.Parameters.Add("@fdn", System.Data.DbType.String);
            var pFtsLbl = ftsInsertCmd.Parameters.Add("@flbl", System.Data.DbType.String);
            var pFtsDesc = ftsInsertCmd.Parameters.Add("@fdesc", System.Data.DbType.String);
            var pFtsTxt = ftsInsertCmd.Parameters.Add("@ftxt", System.Data.DbType.String);

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

                    pId.Value = defId;
                    pDn.Value = def.defName ?? "";
                    pDt.Value = typeName;
                    pLbl.Value = (object?)label ?? DBNull.Value;
                    pDesc.Value = (object?)description ?? DBNull.Value;
                    pMod.Value = modName;
                    pPkg.Value = (object?)packageId ?? DBNull.Value;
                    pSrc.Value = (object?)sourceFile ?? DBNull.Value;
                    pData.Value = json;

                    insertCmd.ExecuteNonQuery();

                    // Build FTS text and insert into FTS5 index
                    var fieldTexts = new List<string>();
                    ExtractFieldValues(def, defId, fieldValueInserts, fieldTexts);
                    var ftsText = SearchTextBuilder.Build(def.defName, label, description, fieldTexts);

                    pFtsRowid.Value = defId;
                    pFtsDn.Value = def.defName ?? "";
                    pFtsLbl.Value = (object?)label ?? DBNull.Value;
                    pFtsDesc.Value = (object?)description ?? DBNull.Value;
                    pFtsTxt.Value = ftsText;
                    ftsInsertCmd.ExecuteNonQuery();

                    if (fieldValueInserts.Count >= BatchSize)
                    {
                        FlushFieldValues(conn, fieldValueInserts);
                    }

                    if (totalDefs % BatchSize == 0)
                    {
                        Log($"已处理 {totalDefs} 个 Def...");
                    }
                }
            }
        }

        FlushFieldValues(conn, fieldValueInserts);

        tx.Commit();
        Log($"已写入 {totalDefs} 个 Def");

        conn.Close();
        Log($"导出完成: {dbPath} ({new FileInfo(dbPath).Length / 1024 / 1024} MB)");
    }

    private static void LoadFtsExtension(SQLiteConnection conn, Action<string> log)
    {
        conn.EnableExtensions(true);
        var architecture = IntPtr.Size == 8 ? "x64" : "x86";
        var assemblyDirectory = Path.GetDirectoryName(typeof(DefExporter).Assembly.Location)!;
        var interopPath = Path.Combine(assemblyDirectory, architecture, "SQLite.Interop.dll");
        log($"尝试加载 FTS5 扩展: {interopPath} (exists={File.Exists(interopPath)})");

        // Pre-load the interop DLL so sqlite3_load_extension finds it already loaded.
        var handle = LoadLibrary(interopPath);
        log($"预加载结果: 0x{handle.ToInt64():X}");

        conn.LoadExtension(interopPath, "sqlite3_fts5_init");
        log("已加载 FTS5 扩展");
    }

    private static void ConfigureConnection(SQLiteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = @"
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                PRAGMA cache_size=-20000;
                PRAGMA mmap_size=268435456;
                PRAGMA temp_store=MEMORY;
                PRAGMA page_size=8192;
            ";
        command.ExecuteNonQuery();
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

    private static void CreateSchema(SQLiteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA encoding='UTF-8'";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE TABLE defs (
                id          INTEGER PRIMARY KEY,
                def_name    TEXT NOT NULL,
                def_type    TEXT NOT NULL,
                label       TEXT,
                description TEXT,
                mod_name    TEXT NOT NULL,
                package_id  TEXT,
                source_file TEXT,
                full_data   TEXT NOT NULL
            );

            CREATE UNIQUE INDEX idx_defs_name_type ON defs(def_name, def_type);
            CREATE INDEX idx_defs_type ON defs(def_type);
            CREATE INDEX idx_defs_mod ON defs(mod_name);

            CREATE TABLE field_values (
                def_id      INTEGER NOT NULL REFERENCES defs(id),
                field_path  TEXT NOT NULL,
                field_value TEXT NOT NULL
            );

            CREATE INDEX idx_fv_def_id ON field_values(def_id);
            CREATE INDEX idx_fv_path ON field_values(field_path);
            CREATE INDEX idx_fv_value ON field_values(field_value);

            CREATE VIRTUAL TABLE defs_fts USING fts5(
                def_name,
                label,
                description,
                full_text,
                tokenize='unicode61'
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private static void FlushFieldValues(SQLiteConnection conn, List<(int, string, string)> inserts)
    {

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO field_values (def_id, field_path, field_value) VALUES (@did, @fp, @fv)";
        var pDid = cmd.Parameters.Add("@did", System.Data.DbType.Int32);
        var pFp = cmd.Parameters.Add("@fp", System.Data.DbType.String);
        var pFv = cmd.Parameters.Add("@fv", System.Data.DbType.String);

        foreach (var (defId, fieldPath, fieldValue) in inserts)
        {
            pDid.Value = defId;
            pFp.Value = fieldPath;
            pFv.Value = fieldValue;
            cmd.ExecuteNonQuery();
        }

        inserts.Clear();
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
