using System.Diagnostics;
using System.Reflection;

namespace RimSearcher.Cli.Maintenance;

internal static class ReleaseUpdater
{
    private const string ApplicationName = "RimSearcher";
    private const string LatestReleaseUrl = "https://github.com/kearril/RimSearcher/releases/latest";
    private const string ReleaseDownloadUrl = "https://github.com/kearril/RimSearcher/releases/download";

    public static void Update()
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"无法检查更新: {exception.Message}");
            Environment.Exit(1);
        }

        var latestVersion = tag.StartsWith('v') ? tag[1..] : tag;
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version!;
        var currentVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";

        if (new Version(latestVersion) <= new Version(currentVersion))
        {
            Console.WriteLine($"rimsearcher 已是最新 ({currentVersion})");
            return;
        }

        var downloadUrl = $"{ReleaseDownloadUrl}/{tag}/rimsearcher.exe";
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var newExecutablePath = Path.Combine(executableDirectory, "rimsearcher.new.exe");

        try
        {
            using var downloader = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            downloader.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationName);
            using var stream = downloader.GetStreamAsync(downloadUrl).Result;
            using var file = File.Create(newExecutablePath);
            stream.CopyTo(file);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"下载失败: {exception.Message}");
            TryDelete(newExecutablePath);
            Environment.Exit(1);
        }

        var batchPath = Path.Combine(executableDirectory, "rimsearcher.update.bat");
        File.WriteAllText(batchPath, $"@echo off\r\ntimeout /t 2 /nobreak > nul\r\nmove /y \"{newExecutablePath}\" \"{Environment.ProcessPath}\"\r\ndel \"%~f0\"\r\n");

        try
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c \"{batchPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"更新脚本启动失败: {exception.Message}");
            Console.WriteLine($"新版本已下载到: {newExecutablePath}");
            Environment.Exit(1);
        }

        Console.WriteLine($"已下载 {latestVersion}，正在安装...");
        Environment.Exit(0);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
