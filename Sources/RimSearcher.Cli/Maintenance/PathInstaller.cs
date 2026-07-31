namespace RimSearcher.Cli.Maintenance;

internal static class PathInstaller
{
    public static void Install()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;

        if (currentPath.Split(';').Any(path => path.Equals(executableDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("rimsearcher 已在 PATH 中。");
            return;
        }

        Environment.SetEnvironmentVariable(
            "Path",
            currentPath.TrimEnd(';') + ";" + executableDirectory,
            EnvironmentVariableTarget.User);

        Console.WriteLine($"rimsearcher 已加入用户 PATH。\n路径: {executableDirectory}\n重启终端后全局可用。");
    }
}
