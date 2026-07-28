using UnityEngine;
using Verse;
using RimWorld;

namespace RimSearcher.DataMod;

public class RimSearcherMod : Mod
{
    private string _lastExportPath = "";
    private string _exportMessage = "";
    private bool _isExporting;

    public RimSearcherMod(ModContentPack content) : base(content)
    {
        _lastExportPath = System.IO.Path.Combine(content.RootDir, "defs.db");
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);

        listing.Label($"导出路径: {_lastExportPath}");

        if (!_isExporting)
        {
            if (listing.ButtonText("导出 Def 数据库"))
            {
                _isExporting = true;
                _exportMessage = "正在导出...";
                LongEventHandler.QueueLongEvent(() =>
                {
                    try
                    {
                        DefExporter.Export(_lastExportPath, msg =>
                        {
                            _exportMessage = msg;
                            Verse.Log.Message($"[RimSearcher] {msg}");
                        });
                        _exportMessage = "导出完成!";
                        Messages.Message("Def 数据库导出完成: " + _lastExportPath, MessageTypeDefOf.TaskCompletion, false);
                    }
                    catch (Exception ex)
                    {
                        _exportMessage = $"导出失败: {ex.Message}";
                        Verse.Log.Error($"[RimSearcher] 导出失败: {ex}");
                    }
                    finally
                    {
                        _isExporting = false;
                    }
                }, "RimSearcherDefExport", true, null, false, false, null);
            }
        }

        if (!string.IsNullOrEmpty(_exportMessage))
        {
            listing.Label(_exportMessage);
        }

        listing.End();
    }

    public override string SettingsCategory()
    {
        return "RimSearcher";
    }
}
