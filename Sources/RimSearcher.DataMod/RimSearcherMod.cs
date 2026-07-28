using System.Diagnostics;
using UnityEngine;
using Verse;

namespace RimSearcher.DataMod;

public class RimSearcherMod : Mod
{
    private string _exportPath = "";

    public RimSearcherMod(ModContentPack content) : base(content)
    {
        _exportPath = Path.Combine(content.RootDir, "defs.db");
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        float y = inRect.y;
        float btnW = 200f;
        float btnH = 36f;

        Widgets.Label(new Rect(0f, y, inRect.width, 24f), "导出路径:");
        y += 26f;

        _exportPath = Widgets.TextField(new Rect(0f, y, inRect.width, 28f), _exportPath);
        y += 42f;

        if (Widgets.ButtonText(new Rect(0f, y, btnW, btnH), "在资源管理器中打开"))
            OpenInExplorer();
        y += btnH + 6f;

        if (Widgets.ButtonText(new Rect(0f, y, btnW, btnH), "导出 Def 数据库"))
        {
            var path = _exportPath;
            if (Directory.Exists(path))
                path = Path.Combine(path, "defs.db");
            Find.WindowStack.Add(new Dialog_ExportProgress(path));
        }
        y += btnH + 16f;
    }

    private void OpenInExplorer()
    {
        try
        {
            var dir = Path.GetDirectoryName(_exportPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", "/select,\"" + _exportPath + "\"");
            else if (Directory.Exists(_exportPath))
                Process.Start("explorer.exe", _exportPath);
        }
        catch (Exception ex)
        {
            Verse.Log.Error($"[RimSearcher] 打开资源管理器失败: {ex}");
        }
    }

    public override string SettingsCategory() => "RimSearcher";
}
