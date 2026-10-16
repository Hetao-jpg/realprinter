// ═══════════════════════════════════════════════════════════
// ★ RealPrinterMod.DealerSupply.cs —— 柜台 / 购买相关工具（V1.4.0+ 重命名）
//
// 当前职责：
//   ① 购买成功的可观测性补丁（OnItemBought Patch）—— 买没买成明摆着，防静默失效。
//   ② 共享柜台工具函数（TryGetInventory / CountChildren / IsOnFrontCounter）
//      —— 供货商上货流程（Supplier.cs）在用。
//   ★ V1.4.3：F7 回收残留功能（CleanupStaleCounterStock）已删除。
//
// 【历史】V1.3.0 的“同行带货”方案（往共享客户柜台塞机器）实机证实会污染剧情
//   NPC 商品池 + 崩溃，已在 2026-10-04 回调，改走供货商商品池（Supplier.cs）。
//   相关死代码（PollDealerVisit / PlacePrinterOnCounter / DiagnoseCannotBuy / DealerStatusLine）已删除。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>取柜台：front = 客户商品区（前柜台）；back = 玩家自己的柜台。</summary>
    private static GameInventory TryGetInventory(bool front)
    {
        try
        {
            var emp = EmporiumEntry.Instance;
            if (emp == null) return null;
            return front ? (GameInventory)emp.frontInvinvElement : (GameInventory)emp.backInvinvElement;
        }
        catch (Exception ex) { RLog.Swallowed("TryGetInventory", ex); }
        return null;
    }

    private static int CountChildren(GameInventory inv)
    {
        if (inv == null) return -1;
        try { return inv.childItems?.Count ?? (-1); }
        catch (Exception e) { RLog.Swallowed("CountChildren", e); }
        return -1;
    }

    private static bool IsOnFrontCounter(GameItem item)
    {
        return IsInside(TryGetInventory(front: true), item);
    }

    /// <summary>
    /// ★ V2：这件物品是不是躺在**主仓库**（`EmporiumEntry.invElement`）里。
    ///   用途：拖动落点取证（V2-4）—— 判"目标已经躺在某个库存里"时，
    ///   游戏的落点优先级究竟是"放进去"还是"对目标使用"。
    ///   与 IsOnFrontCounter 同构、同用 <see cref="IsInside"/>；只在
    ///   VerboseLog 的落点日志里调（每次落点 1 次），不进热路径。
    /// </summary>
    private static bool IsInWarehouse(GameItem item)
    {
        GameInventory warehouse = null;
        try
        {
            var emp = EmporiumEntry.Instance;
            if (emp != null) warehouse = emp.invElement;
        }
        catch (Exception ex) { RLog.Swallowed("IsInWarehouse", ex); }
        return IsInside(warehouse, item);
    }

    /// <summary>★ V2：一个裸指针，在不在某个库存的 childItems 里（纯指针比较，不进 IL2CPP 读目标字段）。</summary>
    private static bool IsInside(GameInventory inv, GameItem item)
    {
        if (inv == null || item == null) return false;
        IntPtr pointer;
        try { pointer = ((Il2CppObjectBase)item).Pointer; }
        catch (Exception e) { RLog.Swallowed("IsInside", e); return false; }
        if (pointer == IntPtr.Zero) return false;
        try
        {
            var childItems = inv.childItems;
            if (childItems == null) return false;
            for (int i = 0; i < childItems.Count; i++)
            {
                GameItem val = childItems[i];
                if (val == null) continue;
                try { if (((Il2CppObjectBase)val).Pointer == pointer) return true; }
                catch (Exception e2) { RLog.Swallowed("IsInside", e2); }
            }
        }
        catch (Exception e3) { RLog.Swallowed("IsInside", e3); }
        return false;
    }
}

[HarmonyPatch(typeof(PlayerStore), "OnItemBought")]
internal static class RealPrinter_OnItemBoughtPatch
{
    private static void Postfix(GameItem gameItem, int cost)
    {
        try
        {
            if (gameItem == null) return;
            string text = null;
            try { text = gameItem.identifier; }
            catch { return; }
            if (string.Equals(text, RealPrinterMod.MachineIdentifier, StringComparison.Ordinal))
                RealPrinterMod.NoteMachineBought(gameItem, cost);
        }
        catch (Exception ex)
        {
            RealPrinterMod.RLog.Warning("OnItemBought 后置处理异常: " + ex.Message);
        }
    }
}
