using ConsoleAppFramework;
using Microsoft.Data.Sqlite;

namespace RimSearcher.Cli.Infrastructure;

/// <summary>
/// 命令执行异常的统一出口：错误消息写入 stderr、退出码置 1，不向用户泄漏堆栈。
/// 参数解析类错误由框架顶层处理，经重定向的 <see cref="ConsoleApp.LogError"/> 同样写入 stderr。
/// </summary>
internal sealed class CliExceptionFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 用户中断（如 Ctrl+C），保持框架默认行为，不输出错误。
        }
        catch (SqliteException exception)
        {
            // 查询 SQL 均为固定模板，唯一接受用户输入的是 FTS MATCH 表达式，
            // 因此执行期的 SQLite 错误基本来自 FTS 语法。
            Console.Error.WriteLine($"FTS 查询语法错误: {exception.Message}");
            Environment.ExitCode = ExitCodes.Error;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误: {exception.Message}");
            Environment.ExitCode = ExitCodes.Error;
        }
    }
}
