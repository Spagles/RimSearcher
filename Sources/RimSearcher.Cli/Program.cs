using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using RimSearcher.Cli.Infrastructure;
using RimSearcher.Cli.Models;
using RimSearcher.Cli.Queries;

Console.OutputEncoding = Encoding.UTF8;

const string ApplicationName = "RimSearcher";
const string LatestReleaseUrl = "https://github.com/kearril/RimSearcher/releases/latest";
const string ReleaseDownloadUrl = "https://github.com/kearril/RimSearcher/releases/download";

string dbPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "defs.db");

var connectionFactory = new DatabaseConnectionFactory(dbPath);
var jsonOutput = new JsonOutput();
var defRepository = new DefRepository(connectionFactory);
var fieldRepository = new FieldRepository(connectionFactory);
var statisticsRepository = new StatisticsRepository(connectionFactory);

void WriteJson(object value) => jsonOutput.Write(value);


// --- commands ---

var app = ConsoleApp.Create();
app.Add("search", ([Argument] string keyword, string? type = null, string? mod = null, int limit = 20, bool count = false) =>
{
    if (count)
    {
        WriteJson(new { count = defRepository.CountSearchResults(keyword, type, mod) });
        return;
    }

    WriteJson(defRepository.Search(keyword, type, mod, limit));
});

app.Add("list", (string? type = null, string? mod = null, int limit = 20, int offset = 0) =>
{
    WriteJson(defRepository.List(type, mod, limit, offset));
});

app.Add("get", ([Argument] string defName, string? type = null, bool brief = false) =>
{
    if (type == null)
    {
        var types = defRepository.FindTypes(defName);
        if (types.Count == 0)
        {
            Console.Error.WriteLine($"Error: no Def found with defName '{defName}'");
            Environment.Exit(2);
        }
        if (types.Count > 1)
        {
            Console.Error.WriteLine($"Error: '{defName}' matches multiple Def types. Specify --type:");
            foreach (var candidateType in types)
                Console.Error.WriteLine($"  {candidateType}");
            Environment.Exit(2);
        }
        type = types[0];
    }

    if (brief)
    {
        var source = defRepository.GetBriefSource(defName, type!);
        if (source == null)
        {
            Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
            Environment.Exit(2);
        }

        using var document = JsonDocument.Parse(source.FullData);
        var root = document.RootElement;
        string? thingClass = root.TryGetProperty("thingClass", out var thingClassElement)
            ? thingClassElement.GetString()
            : null;
        var compClasses = new List<string>();
        if (root.TryGetProperty("comps", out var comps)
            && comps.ValueKind == JsonValueKind.Array)
        {
            foreach (var comp in comps.EnumerateArray())
            {
                if (comp.ValueKind == JsonValueKind.Object
                    && comp.TryGetProperty("compClass", out var componentClass))
                {
                    var className = componentClass.GetString();
                    if (className != null)
                        compClasses.Add(className);
                }
            }
        }

        WriteJson(new BriefDef(
            source.DefName, source.DefType, source.Label, source.ModName,
            source.PackageId, thingClass, compClasses));
        return;
    }

    var fullData = defRepository.GetFullData(defName, type!);
    if (fullData == null)
    {
        Console.Error.WriteLine($"Error: no Def found with defName '{defName}' and type '{type}'");
        Environment.Exit(2);
    }
    Console.WriteLine(fullData);
});

app.Add("find", ([Argument] string fieldPath, [Argument] string value, string? type = null, string? mod = null, int limit = 50) =>
{
    var results = fieldRepository.Find(fieldPath, value, type, mod, limit);
    WriteJson(results);
    if (results.Count == 0)
        Console.Error.WriteLine($"Hint: no exact matches. Try fuzzy search: rimsearcher search \"{value}\"");
});

app.Add("fields", ([Argument] string defName, string type, int limit = 1000) =>
{
    WriteJson(fieldRepository.GetFields(defName, type, limit));
});

app.Add("values", ([Argument] string fieldPath, int limit = 200) =>
{
    WriteJson(fieldRepository.GetValues(fieldPath, limit));
});

app.Add("types", () =>
{
    WriteJson(statisticsRepository.GetTypes());
});

app.Add("mods", () =>
{
    WriteJson(statisticsRepository.GetMods());
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

