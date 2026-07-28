using UnityEngine;
using Verse;

namespace RimSearcher.DataMod;

public class RimSearcherMod : Mod
{
    private string _lastExportPath = "";

    public RimSearcherMod(ModContentPack content) : base(content)
    {
        _lastExportPath = System.IO.Path.Combine(content.RootDir, "defs.db");
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);

        listing.Label($"导出路径: {_lastExportPath}");

        if (listing.ButtonText("导出 Def 数据库"))
        {
            var dialog = new Dialog_ExportProgress(_lastExportPath);
            Find.WindowStack.Add(dialog);
        }

        listing.End();
    }

    public override string SettingsCategory()
    {
        return "RimSearcher";
    }
}
