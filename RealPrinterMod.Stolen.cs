using System;
using Il2Cpp;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// 改真值成功后给目标打赃物标记。
    /// 分档依据：改后价值 GetNegociatedValue()；为 0 时用 MinStolenHeat 兜底。
    /// </summary>
    internal static bool MarkItemStolen(GameItem target)
    {
        if (target == null) return false;
        if (!IsSafeTarget(target)) return false;

        // 1. 先让原版跑（建立 STOLEN_TAG + 其内部副作用）
        try { StolenHelper.InitStolenItem(target, -1); }
        catch (Exception e) { RLog.Warning("★ InitStolenItem 异常（继续走我方重算）: " + e.Message); }

        // 2. 读原版结果
        int curHeat = -1;
        try { curHeat = target.state?.GetTag("STOLEN_VALUE_INT")?.GetInt() ?? 0; } catch { }
        bool hasStolenTag = false;
        try { hasStolenTag = target.IsTag("STOLEN_TAG"); } catch { }

        // 3. 读改后价值 → 算我方热度
        long val = ReadUnitValue(target);
        int ourHeat = CalcHeatByValue(val);
        if (ourHeat <= 0) ourHeat = Config.MinStolenHeat;

        // 4. 原版已给且更高 → 保留原版
        if (curHeat >= ourHeat)
        {
            if (Config.VerboseLog)
                RLog.Detail("  标记：STOLEN_TAG, STOLEN_VALUE_INT=" + curHeat + "（保留原版）");
            return true;
        }

        // 5. 我方更高或原版没打 → 覆盖
        try
        {
            if (!hasStolenTag)
                target.EnableTag("STOLEN_TAG", skipValidate: true);
            if (!target.IsTag("STOLEN_VALUE_INT"))
                target.EnableTag("STOLEN_VALUE_INT", skipValidate: true);

            target.ModifyTag("STOLEN_VALUE_INT",
                (Il2CppSystem.Action<TagState>)(st => st.SetInt(ourHeat)),
                skipValidate: true);

            // 6. 覆盖后回读确认
            int after = -1;
            try { after = target.state?.GetTag("STOLEN_VALUE_INT")?.GetInt() ?? -1; } catch { }

            if (Config.VerboseLog)
                RLog.Detail("  标记：STOLEN_TAG, STOLEN_VALUE_INT=" + after
                            + (after == ourHeat ? "" : "（★覆盖失败）"));
            return after == ourHeat;
        }
        catch (Exception e)
        {
            RLog.Warning("★ 覆盖 STOLEN_VALUE_INT 失败: " + e.Message);
            return false;
        }
    }

    /// <summary>按价值分档（分界点照抄原版 InitStolenItem）。</summary>
    private static int CalcHeatByValue(long v)
    {
        if (v <= 17) return 0;
        if (v <= 49) return 25;
        if (v <= 84) return 50;
        if (v <= 139) return 75;
        return 100;
    }
}