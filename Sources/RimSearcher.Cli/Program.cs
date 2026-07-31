using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;

const string ApplicationName = "RimSearcher";
const string LatestReleaseUrl = "https://github.com/kearril/RimSearcher/releases/latest";
const string ReleaseDownloadUrl = "https://github.com/kearril/RimSearcher/releases/download";

string dbPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "defs.db");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

SqliteConnection OpenDb()
{
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"Error: {dbPath} not found");
        Environment.Exit(1);
    }

    var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    connection.Open();
    return connection;
}

void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));

static string ReadLabel(SqliteDataReader reader, int nameOrdinal, int labelOrdinal) =>
    reader.IsDBNull(labelOrdinal) ? reader.GetString(nameOrdinal) : reader.GetString(labelOrdinal);

static string? ReadOptionalString(SqliteDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

// --- shared helpers ---

var noiseFields = new HashSet<string>
{
    "debugRandomId", "defNameHash", "generated",
    "ignoreConfigErrors", "ignoreIllegalLabelCharacterConfigError",
    "index", "shortHash"
};

bool IsNoiseField(string path)
{
    if (path.StartsWith("modContentPack.", StringComparison.Ordinal)
        || path.Contains(".modContentPack."))
        return true;
    int lastDot = path.LastIndexOf('.');
    int lastBracket = path.LastIndexOf('[');
    int segStart = Math.Max(lastDot, lastBracket) + 1;
    return noiseFields.Contains(path[segStart..]);
}

void AddFilterParams(SqliteCommand cmd, string? type, string? mod)
{
    cmd.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@mod", (object?)mod ?? DBNull.Value);
}

// --- commands ---

var app = ConsoleApp.Create();
app.Add("search", ([Argument] string keyword, string? type = null, string? mod = null, int limit = 20, bool count = false) =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();

    if (count)
    {
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM defs d
            JOIN defs_fts fts ON d.id = fts.rowid
            WHERE defs_fts MATCH @kw
              AND (@type IS NULL OR d.def_type = @type)
              AND (@mod IS NULL OR d.mod_name = @mod)
            """;
        cmd.Parameters.AddWithValue("@kw", keyword);
        AddFilterParams(cmd, type, mod);
        WriteJson(new { count = cmd.ExecuteScalar() });
        return;
    }

    cmd.CommandText = """
        SELECT d.def_name, d.def_type, d.label, d.mod_name, d.package_id, rank
        FROM defs d
        JOIN defs_fts fts ON d.id = fts.rowid
        WHERE defs_fts MATCH @kw
          AND (@type IS NULL OR d.def_type = @type)
          AND (@mod IS NULL OR d.mod_name = @mod)
        ORDER BY rank
        LIMIT @limit
        """;
    cmd.Parameters.AddWithValue("@kw", keyword);
    AddFilterParams(cmd, type, mod);
    cmd.Parameters.AddWithValue("@limit", limit);

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add(new
        {
            def_name = reader.GetString(0),
            def_type = reader.GetString(1),
            label = ReadLabel(reader, 0, 2),
            mod_name = reader.GetString(3),
            package_id = ReadOptionalString(reader, 4),
            rank = reader.GetDouble(5)
        });
    }
    WriteJson(results);
});

app.Add("list", (string? type = null, string? mod = null, int limit = 20, int offset = 0) =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT d.def_name, d.def_type, d.label, d.mod_name, d.package_id
        FROM defs d
        WHERE (@type IS NULL OR d.def_type = @type)
          AND (@mod IS NULL OR d.mod_name = @mod)
        ORDER BY d.def_type, d.def_name
        LIMIT @limit OFFSET @offset
        """;
    AddFilterParams(cmd, type, mod);
    cmd.Parameters.AddWithValue("@limit", limit);
    cmd.Parameters.AddWithValue("@offset", offset);

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add(new
        {
            def_name = reader.GetString(0),
            def_type = reader.GetString(1),
            label = ReadLabel(reader, 0, 2),
            mod_name = reader.GetString(3),
            package_id = ReadOptionalString(reader, 4)
        });
    }
    WriteJson(results);
});

app.Add("get", ([Argument] string defName, string? type = null, bool brief = false) =>
{
    using var db = OpenDb();

    if (type == null)
    {
        using var checkCmd = db.CreateCommand();
        checkCmd.CommandText = "SELECT def_type FROM defs WHERE def_name = @name";
        checkCmd.Parameters.AddWithValue("@name", defName);
        var types = new List<string>();
        using var r = checkCmd.ExecuteReader();
        while (r.Read()) types.Add(r.GetString(0));

        if (types.Count == 0)
        {
            Console.Error.WriteLine($"Error: no Def found with defName '{defName}'");
            Environment.Exit(2);
        }
        if (types.Count > 1)
        {
            Console.Error.WriteLine($"Error: '{defName}' matches multiple Def types. Specify --type:");
            foreach (var t in types)
                Console.Error.WriteLine($"  {t}");
            Environment.Exit(2);
        }
        type = types[0];
    }

    if (brief)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT def_name, def_type, label, mod_name, package_id, full_data FROM defs WHERE def_name = @name AND def_type = @type";
        cmd.Parameters.AddWithValue("@name", defName);
        cmd.Parameters.AddWithValue("@type", type);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
            Environment.Exit(2);
        }

        using var doc = JsonDocument.Parse(reader.GetString(5));
        var root = doc.RootElement;

        string? thingClass = null;
        if (root.TryGetProperty("thingClass", out var tc))
            thingClass = tc.GetString();

        var compClasses = new List<string>();
        if (root.TryGetProperty("comps", out var comps) && comps.ValueKind == JsonValueKind.Array)
        {
            foreach (var comp in comps.EnumerateArray())
            {
                if (comp.ValueKind == JsonValueKind.Object && comp.TryGetProperty("compClass", out var cc))
                {
                    var ccStr = cc.GetString();
                    if (ccStr != null)
                        compClasses.Add(ccStr);
                }
            }
        }

        WriteJson(new
        {
            def_name = reader.GetString(0),
            def_type = reader.GetString(1),
            label = ReadLabel(reader, 0, 2),
            mod_name = reader.GetString(3),
            package_id = ReadOptionalString(reader, 4),
            thing_class = thingClass,
            comp_classes = compClasses
        });
        return;
    }

    using var dataCmd = db.CreateCommand();
    dataCmd.CommandText = "SELECT full_data FROM defs WHERE def_name = @name AND def_type = @type";
    dataCmd.Parameters.AddWithValue("@name", defName);
    dataCmd.Parameters.AddWithValue("@type", type);

    var result = dataCmd.ExecuteScalar();
    if (result == null)
    {
        Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
        Environment.Exit(2);
    }
    Console.WriteLine(result.ToString()!);
});

app.Add("find", ([Argument] string fieldPath, [Argument] string value, string? type = null, string? mod = null, int limit = 50) =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
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
    cmd.Parameters.AddWithValue("@path", fieldPath);
    cmd.Parameters.AddWithValue("@value", value);
    AddFilterParams(cmd, type, mod);
    cmd.Parameters.AddWithValue("@limit", limit);

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add(new
        {
            def_name = reader.GetString(0),
            def_type = reader.GetString(1),
            label = ReadLabel(reader, 0, 2),
            mod_name = reader.GetString(3),
            package_id = ReadOptionalString(reader, 4),
            field_path = reader.GetString(5),
            field_value = reader.GetString(6)
        });
    }
    WriteJson(results);
    if (results.Count == 0)
        Console.Error.WriteLine($"Hint: no exact matches. Try fuzzy search: rimsearcher search \"{value}\"");
});

app.Add("fields", ([Argument] string defName, string type, int limit = 1000) =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    var sqlLimit = Math.Min(limit * 2, 10000);
    cmd.CommandText = """
        SELECT fv.field_path, fv.field_value
        FROM field_values fv
        JOIN defs d ON fv.def_id = d.id
        WHERE d.def_name = @name AND d.def_type = @type
        ORDER BY fv.field_path
        LIMIT @limit
        """;
    cmd.Parameters.AddWithValue("@name", defName);
    cmd.Parameters.AddWithValue("@type", type);
    cmd.Parameters.AddWithValue("@limit", sqlLimit);

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        if (results.Count >= limit) break;
        var path = reader.GetString(0);
        if (IsNoiseField(path)) continue;
        results.Add(new
        {
            field_path = path,
            field_value = reader.GetString(1)
        });
    }
    WriteJson(results);
});

app.Add("values", ([Argument] string fieldPath, int limit = 200) =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT DISTINCT fv.field_value
        FROM field_values fv
        WHERE fv.field_path LIKE '%' || @path
        ORDER BY fv.field_value
        LIMIT @limit
        """;
    cmd.Parameters.AddWithValue("@path", fieldPath);
    cmd.Parameters.AddWithValue("@limit", limit);

    var values = new List<string>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        values.Add(reader.GetString(0));
    WriteJson(values);
});

app.Add("types", () =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT def_type, COUNT(*) FROM defs GROUP BY 1 ORDER BY 2 DESC";

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add(new
        {
            def_type = reader.GetString(0),
            count = reader.GetInt32(1)
        });
    }
    WriteJson(results);
});

app.Add("mods", () =>
{
    using var db = OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT mod_name, package_id, COUNT(*) FROM defs GROUP BY 1, 2 ORDER BY 3 DESC";

    var results = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add(new
        {
            mod_name = reader.GetString(0),
            package_id = reader.IsDBNull(1) ? null : reader.GetString(1),
            def_count = reader.GetInt32(2)
        });
    }
    WriteJson(results);
});

app.Add("install", () =>
{
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
    var currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";

    if (currentPath.Split(';').Any(p => p.Equals(exeDir, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("rimsearcher 已在 PATH 中。");
        return;
    }

    Environment.SetEnvironmentVariable("Path",
        currentPath.TrimEnd(';') + ";" + exeDir,
        EnvironmentVariableTarget.User);

    Console.WriteLine($"rimsearcher 已加入用户 PATH。\n路径: {exeDir}\n重启终端后全局可用。");
});

app.Add("update", () =>
{
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    http.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationName);

    string tag = null!;
    try
    {
        var response = http.GetAsync(LatestReleaseUrl).Result;
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
            throw new Exception($"Unexpected status: {(int)response.StatusCode}");
        var location = response.Headers.Location?.ToString()
            ?? throw new Exception("No Location header in redirect");
        tag = location[(location.LastIndexOf('/') + 1)..];
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"无法检查更新: {ex.Message}");
        Environment.Exit(1);
    }

    var latestVer = tag.StartsWith('v') ? tag[1..] : tag;
    var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
    var currentVer = $"{v.Major}.{v.Minor}.{v.Build}";

    if (new Version(latestVer) <= new Version(currentVer))
    {
        Console.WriteLine($"rimsearcher 已是最新 ({currentVer})");
        return;
    }

    var downloadUrl = $"{ReleaseDownloadUrl}/{tag}/rimsearcher.exe";

    var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
    var newPath = Path.Combine(exeDir, "rimsearcher.new.exe");

    try
    {
        using var dl = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        dl.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationName);
        using var stream = dl.GetStreamAsync(downloadUrl).Result;
        using var file = File.Create(newPath);
        stream.CopyTo(file);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"下载失败: {ex.Message}");
        TryDelete(newPath);
        Environment.Exit(1);
    }

    var batPath = Path.Combine(exeDir, "rimsearcher.update.bat");
    File.WriteAllText(batPath, $"@echo off\r\ntimeout /t 2 /nobreak > nul\r\nmove /y \"{newPath}\" \"{Environment.ProcessPath}\"\r\ndel \"%~f0\"\r\n");

    try
    {
        Process.Start(new ProcessStartInfo("cmd", $"/c \"{batPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"更新脚本启动失败: {ex.Message}");
        Console.WriteLine($"新版本已下载到: {newPath}");
        Environment.Exit(1);
    }

    Console.WriteLine($"已下载 {latestVer}，正在安装...");
    Environment.Exit(0);
});

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { }
}

app.Run(args);

