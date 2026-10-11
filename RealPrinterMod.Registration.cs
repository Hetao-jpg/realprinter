// ═══════════════════════════════════════════════════════════
// ★ 物品池注册 —— 让 real_printer 成为游戏目录里的一等公民
//
// 【参考】同门 CustomRatPrinter 的 RatPrinterMod.Registration.cs，
//   以及 JacksonPerks 里的 GuMachineSystem / CustomStorageContainer。
//
// 【做了什么】
//   钩 ModItemDirectory.InitDirectory 的 Postfix，把自己 Add 进
//   Directory<GameItem>，id = MachineIdentifier。
//   之后游戏里任何 `DirectoryMaster.Item("real_printer", ...)` 都能拿到。
//
// 【工厂返回什么版本的机器】
//   **标准版 100（Config.DealerPrice）**，不是 F6 那台价值 0 的。
//   【为什么】注册工厂是**游戏自己**（读档重建 / 未来可能的掉落 / 任务奖励）
//     造物品时走的路径。若返回 0，玩家会通过游戏自身机制白拿到"卖 0 元"的
//     机器，反而更乱。价值 0 只在 F6 直领这一条**显式**路径上出现。
//
// 【和供货商上货 / F6 直领的关系】
//   两条路径都**不走注册工厂** —— 它们是 `CreateRealPrinter(...)` 直接造
//   再 `AddDirectSellingItemToTable` / `TryGiveToWarehouse`。注册只是让
//   "按 id 取机器"这条**第三路径**也能用。
// ═══════════════════════════════════════════════════════════

using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    private static Il2CppSystem.Func<GameItem> _printerFactory;

    [HarmonyPatch(typeof(ModItemDirectory), "InitDirectory")]
    internal static class Patch_ModItemDirectoryInit
    {
        private static void Postfix(ItemDirectory __instance) => TryRegister(__instance);
    }

    private static void TryRegister(ItemDirectory dir)
    {
        if (dir == null) return;
        try
        {
            if (((Directory<GameItem>)dir).Has(MachineIdentifier)) return;

            if (_printerFactory == null)
            {
                // ★ 工厂返回"标准版"（100）—— 详见文件头说明
                System.Func<GameItem> sysFunc = () => CreateRealPrinter(Config.DealerPrice);
                _printerFactory = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<GameItem>>(sysFunc);
            }

            bool ok = ((Directory<GameItem>)dir).Add(MachineIdentifier, _printerFactory);
            RLog.Msg((ok ? "★ 已注册 " : "⚠ 注册失败 ")
                + MachineIdentifier + " → " + dir.GetType().Name);
        }
        catch (Exception e)
        {
            RLog.Warning("注册 " + MachineIdentifier + " 异常: " + e.Message);
        }
    }
}