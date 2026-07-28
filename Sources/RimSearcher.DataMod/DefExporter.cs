using System.Data.SQLite;
using System.Reflection;
using System.Text;
using Verse;

namespace RimSearcher.DataMod;

using System.Runtime.InteropServices;

public static class DefExporter
{
    private static readonly HashSet<string> ExcludedNamespaces = new()
    {
        "UnityEngine",
        "UnityEditor",
        "Microsoft.",
        "Mono."
    };

    private const int MaxFieldDepth = 3;
    private const int MaxJsonDepth = 10;
    private const int BatchSize = 500;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public static void Export(string dbPath, Action<string>? log = null)
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
        conn.EnableExtensions(true);
        var arch = IntPtr.Size == 8 ? "x64" : "x86";
        var interopPath = Path.Combine(Path.GetDirectoryName(typeof(DefExporter).Assembly.Location)!, arch, "SQLite.Interop.dll");
        Log($"尝试加载 FTS5 扩展: {interopPath} (exists={File.Exists(interopPath)})");

        // Pre-load the interop DLL so sqlite3_load_extension finds it already loaded
        var handle = LoadLibrary(interopPath);
        Log($"预加载结果: 0x{handle.ToInt64():X}");

        conn.LoadExtension(interopPath, "sqlite3_fts5_init");
        Log("已加载 FTS5 扩展");

        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;";
            pragmaCmd.ExecuteNonQuery();
        }

        CreateSchema(conn);
        Log("数据库 schema 已创建");

        var defTypes = GenDefDatabase.AllDefTypesWithDatabases().ToList();
        Log($"发现 {defTypes.Count} 个 Def 类型");

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
                        json = SerializeToJson(def);
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
                    var ftsText = BuildSearchText(def.defName, label, description, fieldTexts);

                    pFtsRowid.Value = defId;
                    pFtsDn.Value = def.defName ?? "";
                    pFtsLbl.Value = (object?)label ?? DBNull.Value;
                    pFtsDesc.Value = (object?)description ?? DBNull.Value;
                    pFtsTxt.Value = ftsText;
                    ftsInsertCmd.ExecuteNonQuery();

                    if (fieldValueInserts.Count >= BatchSize)
                    {
                        FlushFieldValues(conn, tx, fieldValueInserts);
                    }

                    if (totalDefs % 500 == 0)
                    {
                        Log($"已处理 {totalDefs} 个 Def...");
                    }
                }
            }
        }

        FlushFieldValues(conn, tx, fieldValueInserts);

        tx.Commit();
        Log($"已写入 {totalDefs} 个 Def");

        conn.Close();
        Log($"导出完成: {dbPath} ({new FileInfo(dbPath).Length / 1024 / 1024} MB)");
    }

    private static void CreateSchema(SQLiteConnection conn)
    {
        using var cmd = conn.CreateCommand();
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
            CREATE INDEX idx_defs_label ON defs(label);

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
                full_text
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private static void FlushFieldValues(SQLiteConnection conn, SQLiteTransaction tx, List<(int, string, string)> inserts)
    {
        if (inserts.Count == 0) return;

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

    private static string BuildSearchText(string? defName, string? label, string? description, List<string> fieldTexts)

    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(defName)) sb.Append(defName).Append(' ');
        if (!string.IsNullOrWhiteSpace(label)) sb.Append(label).Append(' ');
        if (!string.IsNullOrWhiteSpace(description)) sb.Append(description).Append(' ');
        foreach (var t in fieldTexts)
        {
            sb.Append(t).Append(' ');
        }
        return sb.ToString().Trim();
    }

    #region JSON Serialization

    private static string SerializeToJson(Def def)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<object>();
        SerializeValue(def, sb, visited, 0);
        return sb.ToString();
    }

    private static void SerializeValue(object? value, StringBuilder sb, HashSet<object> visited, int depth)
    {
        if (value == null) { sb.Append("null"); return; }
        if (depth > MaxJsonDepth) { sb.Append("\"...\""); return; }

        Type t = value.GetType();

        // Primitives
        if (value is string s) { sb.Append('"'); sb.Append(EscapeJson(s)); sb.Append('"'); return; }
        if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
        if (value is int or long or short or byte or sbyte or uint or ulong or ushort) { sb.Append(value.ToString()); return; }
        if (value is float f) { sb.Append(f.ToString("G", System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (value is double d) { sb.Append(d.ToString("G", System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (value is decimal dec) { sb.Append(dec.ToString("G", System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (t.IsEnum) { sb.Append('"'); sb.Append(EscapeJson(value.ToString()!)); sb.Append('"'); return; }

        // Cycle detection
        if (!t.IsValueType)
        {
            if (visited.Contains(value)) { sb.Append("\"$cyclic_ref\""); return; }
            visited.Add(value);
        }

        try
        {
            // Nested Def references — defName only (top-level Def at depth 0 is fully serialized)
            if (depth > 0 && value is Def defRef)
            {
                sb.Append('"'); sb.Append(EscapeJson(defRef.defName)); sb.Append('"');
                return;
            }

            // Collections (IList covers List<T>, arrays, etc.) — BEFORE namespace check
            if (value is System.Collections.IList list)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    SerializeValue(list[i], sb, visited, depth + 1);
                }
                sb.Append(']');
                return;
            }

            // Dictionaries — BEFORE namespace check
            if (value is System.Collections.IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    SerializeValue(entry.Key, sb, visited, depth + 1);
                    sb.Append(':');
                    SerializeValue(entry.Value, sb, visited, depth + 1);
                }
                sb.Append('}');
                return;
            }

            // Type references — output FullName (must come before namespace exclusion)
            if (value is Type typeVal)
            {
                sb.Append('"'); sb.Append(EscapeJson(typeVal.FullName ?? typeVal.Name)); sb.Append('"');
                return;
            }

            // Skip excluded namespaces for general objects (AFTER IList/IDict/Type checks)
            string? ns = t.Namespace;
            if (ns != null)
            {
                foreach (var excluded in ExcludedNamespaces)
                {
                    if (ns.StartsWith(excluded, StringComparison.Ordinal)) { sb.Append("{}"); return; }
                }
            }

            // General objects: serialize public instance fields
            sb.Append('{');
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            bool firstField = true;
            foreach (var field in fields)
            {
                if (field.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                if (!firstField) sb.Append(',');
                firstField = false;
                sb.Append('"'); sb.Append(EscapeJson(field.Name)); sb.Append('"'); sb.Append(':');
                try
                {
                    SerializeValue(field.GetValue(value), sb, visited, depth + 1);
                }
                catch { sb.Append("null"); }
            }
            sb.Append('}');
        }
        finally
        {
            if (!t.IsValueType) visited.Remove(value);
        }
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    #endregion

    #region Field Values Extraction

    private static void ExtractFieldValues(Def def, int defId, List<(int, string, string)> inserts, List<string> allTexts)
    {
        var visited = new HashSet<object>();
        ExtractFieldValuesRecursive(def, defId, "", inserts, allTexts, visited, 0);
    }

    private static void ExtractFieldValuesRecursive(object? obj, int defId, string pathPrefix, List<(int, string, string)> inserts, List<string> allTexts, HashSet<object> visited, int depth)
    {
        if (obj == null) return;
        if (depth > MaxFieldDepth) return;

        Type t = obj.GetType();

        if (!t.IsValueType)
        {
            if (visited.Contains(obj)) return;
            visited.Add(obj);
        }

        try
        {
            // Collections — BEFORE namespace check
            if (obj is System.Collections.IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string itemPath = string.IsNullOrEmpty(pathPrefix) ? $"[{i}]" : $"{pathPrefix}[{i}]";
                    var item = list[i];
                    if (item is string strVal)
                    {
                        allTexts.Add(strVal);
                        if (strVal.Length > 0) inserts.Add((defId, itemPath, strVal));
                    }
                    else if (item is Type typeVal)
                    {
                        allTexts.Add(typeVal.FullName ?? typeVal.Name);
                        inserts.Add((defId, itemPath, typeVal.FullName ?? typeVal.Name));
                    }
                    else if (item != null && item.GetType().IsClass && !(item is ValueType))
                    {
                        ExtractFieldValuesRecursive(item, defId, itemPath, inserts, allTexts, visited, depth + 1);
                    }
                }
                return;
            }

            // Dictionaries — BEFORE namespace check
            if (obj is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    string keyStr = entry.Key?.ToString() ?? "";
                    string entryPath = string.IsNullOrEmpty(pathPrefix) ? keyStr : $"{pathPrefix}.{keyStr}";
                    if (entry.Value is string dictVal)
                    {
                        allTexts.Add(dictVal);
                        if (dictVal.Length > 0) inserts.Add((defId, entryPath, dictVal));
                    }
                    else if (entry.Value is Type typeVal)
                    {
                        allTexts.Add(typeVal.FullName ?? typeVal.Name);
                        inserts.Add((defId, entryPath, typeVal.FullName ?? typeVal.Name));
                    }
                    else if (entry.Value != null && entry.Value.GetType().IsClass && !(entry.Value is ValueType))
                    {
                        ExtractFieldValuesRecursive(entry.Value, defId, entryPath, inserts, allTexts, visited, depth + 1);
                    }
                }
                return;
            }

            // Skip excluded namespaces for general objects (AFTER IList/IDict)
            string? ns = t.Namespace;
            if (ns != null)
            {
                foreach (var excluded in ExcludedNamespaces)
                {
                    if (ns.StartsWith(excluded, StringComparison.Ordinal)) return;
                }
            }

            // General object: iterate public instance fields
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.Name.StartsWith("<", StringComparison.Ordinal)) continue;

                string fieldPath = string.IsNullOrEmpty(pathPrefix) ? field.Name : $"{pathPrefix}.{field.Name}";

                object? fieldValue;
                try { fieldValue = field.GetValue(obj); }
                catch { continue; }

                if (fieldValue == null) continue;

                if (fieldValue is string strField)
                {
                    allTexts.Add(strField);
                    if (strField.Length > 0) inserts.Add((defId, fieldPath, strField));
                }
                else if (fieldValue is Type typeField)
                {
                    string typeName = typeField.FullName ?? typeField.Name;
                    allTexts.Add(typeName);
                    inserts.Add((defId, fieldPath, typeName));
                }
                else if (fieldValue is ValueType)
                {
                    string? valStr = fieldValue.ToString();
                    if (valStr != null) { allTexts.Add(valStr); if (valStr.Length > 0) inserts.Add((defId, fieldPath, valStr)); }
                }
                else if (fieldValue is Def defRef)
                {
                    allTexts.Add(defRef.defName);
                    if (!string.IsNullOrEmpty(defRef.defName)) inserts.Add((defId, fieldPath, defRef.defName));
                }
                else if (fieldValue.GetType().IsEnum)
                {
                    string? enumStr = fieldValue.ToString();
                    if (enumStr != null) { allTexts.Add(enumStr); if (enumStr.Length > 0) inserts.Add((defId, fieldPath, enumStr)); }
                }
                else
                {
                    ExtractFieldValuesRecursive(fieldValue, defId, fieldPath, inserts, allTexts, visited, depth + 1);
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
