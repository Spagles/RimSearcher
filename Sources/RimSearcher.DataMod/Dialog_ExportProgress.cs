using UnityEngine;
using Verse;

namespace RimSearcher.DataMod;

public class Dialog_ExportProgress : Window
{
    private readonly string _dbPath;
    private readonly Thread _thread;
    private readonly long _startTicks;

    private int _current;
    private int _total;
    private string _status = "准备中...";
    private string? _error;
    public override Vector2 InitialSize => new(560f, 330f);

    public Dialog_ExportProgress(string dbPath)
    {
        _dbPath = dbPath;
        _startTicks = DateTime.UtcNow.Ticks;
        doCloseButton = false;
        doCloseX = false;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnAccept = false;
        closeOnCancel = false;
        closeOnClickedOutside = false;

        _thread = new Thread(RunExport)
        {
            IsBackground = true,
            Name = "RimSearcherExport"
        };
        _thread.Start();
    }

    private void RunExport()
    {
        try
        {
            DefExporter.Export(_dbPath,
                log: msg => Verse.Log.Message($"[RimSearcher] {msg}"),
                progress: (current, total, status) =>
                {
                    _current = current;
                    _total = total;
                    _status = status;
                });
            _status = "导出完成!";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _status = "导出失败";
            Verse.Log.Error($"[RimSearcher] 导出失败: {ex}");
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        bool done = !_thread.IsAlive;

        // Consume keyboard events while exporting to prevent key presses leaking to game
        if (!done && Event.current != null && Event.current.isKey)
        {
            Event.current.Use();
        }

        float margin = 20f;
        float w = inRect.width - margin * 2f;
        float y = inRect.y + margin;

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(margin, y, w, 36f), "导出 Def 数据库");
        Text.Font = GameFont.Small;
        y += 48f;

        if (_total > 0)
        {
            float pct = Mathf.Clamp01((float)_current / _total);
            Widgets.Label(new Rect(margin, y, w, 24f), $"{_current:N0} / {_total:N0} ({pct:P0})");
            y += 30f;
            Widgets.FillableBar(new Rect(margin, y, w, 36f), pct);
            y += 44f;

            if (_current > 0)
            {
                var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _startTicks);
                var eta = TimeSpan.FromTicks((long)(elapsed.Ticks / pct - elapsed.Ticks));
                Widgets.Label(new Rect(margin, y, w, 22f), $"已用: {FormatTime(elapsed)}  预计剩余: {FormatTime(eta)}");
                y += 28f;
            }
            else
            {
                y += 6f;
            }
        }

        Widgets.Label(new Rect(margin, y, w, 28f), _status);
        y += 40f;

        if (done)
        {
            if (_error != null)
            {
                Widgets.Label(new Rect(margin, y, w, 24f), $"错误: {_error}");
                y += 32f;
            }

            // Centered close button
            float btnW = 160f;
            float btnX = (inRect.width - btnW) / 2f;
            if (Widgets.ButtonText(new Rect(btnX, y, btnW, 42f), "关闭"))
            {
                Close();
            }
        }
        else
        {
            Widgets.Label(new Rect(margin, y, w, 24f), "正在导出，请勿关闭此窗口...");
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Seconds}s";
    }
}
