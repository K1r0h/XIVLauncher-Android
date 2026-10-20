using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XlaBoot;

/// <summary>
/// Per-GPU defaults, the way GameNative's BestConfigService picks a driver per Adreno family.
///
/// Applied once, and only over settings the player never touched: after that the player's own choice
/// stands, including going back to the setting this replaced.
/// </summary>
public static class BestConfig
{
    /// <summary>
    /// Records which revision of the recommendations has been applied. Bump <see cref="Revision"/> when a
    /// recommendation changes, so existing installs pick it up.
    /// </summary>
    private const string AppliedKey = "best_config_applied";

    private const string Revision = "1";

    private const string GpuModelPath = "/sys/class/kgsl/kgsl-3d0/gpu_model";

    /// <summary>Adreno model by SoC code, for when the kgsl node cannot be read.</summary>
    private static readonly Dictionary<string, int> AdrenoBySoc = new()
    {
        ["SM8850"] = 840,
        ["SM8750"] = 830,
    };

    /// <summary>The Adreno model number ("Adreno840v2" gives 840), or 0 when it cannot be told.</summary>
    public static int AdrenoModel()
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(GpuModelPath), @"Adreno\D*(\d{3})", RegexOptions.IgnoreCase);
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }
        catch (Exception)
        {
            // Not an Adreno, or the node is not readable from the app on this device.
        }
        return AdrenoBySoc.TryGetValue(AppHost.SocModel.Trim().ToUpperInvariant(), out var model) ? model : 0;
    }

    /// <summary>
    /// The bundled driver this GPU should start on. Turnip Gen8 is built for the Adreno 830; on the
    /// 8 Elite Gen 5's Adreno 84x it breaks the game's auto-exposure, so bloom pulses bright and dark.
    /// GameNative ships Mr_Purple's build as that family's default for the same reason.
    /// </summary>
    public static string RecommendedDriver(int adreno) =>
        adreno is >= 840 and < 860 ? GraphicsDrivers.MrPurple : GraphicsDrivers.DefaultBundled;

    /// <summary>Applies the recommendations once the runtime is installed. Cheap enough for every app start.</summary>
    public static void Apply()
    {
        if (AppHost.Get(AppliedKey, "") == Revision)
            return;

        var adreno = AdrenoModel();
        var recommended = RecommendedDriver(adreno);
        if (GraphicsDrivers.IsDefaultSelected() && recommended != GraphicsDrivers.DefaultBundled)
        {
            var driver = GraphicsDrivers.All().FirstOrDefault(d => d.IsBundled && d.Id == recommended);
            // Not unpacked yet means the runtime predates it: try again after the update, rather than
            // recording that this device was handled.
            if (driver == null)
                return;
            GraphicsDrivers.Select(driver);
            AppLog.Note($"Adreno {adreno}: switched the driver to {driver.Label}");
        }
        AppHost.Set(AppliedKey, Revision);
    }
}
