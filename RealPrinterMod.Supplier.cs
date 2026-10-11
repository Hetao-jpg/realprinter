// ═══════════════════════════════════════════════════════════
// ★ V1.4.0：供货商上货 —— 真实打印机的**正式获取方式**（接替已停用的同行带货）
//
// 【需求（核桃 2026-10-04）】
//   "下一步的方向是放到供货商的商品池，保证我们的真实打印机的优先上货。"
//
// 【取证结论（反汇编 + dump.cs，见当日日志）】
//   ① `StoreClient` 本身**没有商品池字段** —— 客户带的货是到访时动态造的。
//   ② "供货商摆货"的唯一收敛点 = `StoreClientList.PlaceSupplierInventory()`
//      （public static void，托管绑定已确认存在）。
//      内部就是一串：
//          PlayerStore.Instance.AddDirectSellingItemToTable(
//              DirectoryMaster.Item("wine_yeast" / "bandage_item" / ...), false, ...)
//      —— 与 V1.3.0 用的是**同一个官方 API**，差别只在**挂在哪里**。
//   ③ 它的直接 call 调用者 = 0（被 OnArrival 闭包/委托调用）——
//      对 Harmony 无影响：detour 挂在方法入口，不管谁调它都会命中。
//
// 【为什么这次不是重蹈 V1.3.0 的"污染"】
//   V1.3.0 错在：往**收购型客户**（同行，自己不带任何货）在场的共享柜台塞东西，
//   柜台当时在服务剧情 NPC → 数据串池。
//   这次挂在**供货商自己的补货函数**上：函数被谁调、什么时候调，全由游戏自己决定
//   —— 我只是在供货商摆完自己的货之后**追加一件**，时机与归属都正确。
//
// 【"优先上货"】每次供货商摆货**必定**追加（100% 出现，不随机缺席）。
//   若柜台满，游戏会静默销毁新来的物品 → 回读断言会喊出来，
//   并把死机器从登记表注销（V1.3.1 防线，绝不让悬空指针过夜）。
//
// 【未验证（实机待验）】
//   · 供货商的来访频率（游戏内多久来一次）没查 —— 频率太低的话再加别的途径。
//   · 摆完之后游戏会不会对柜台做"去重/清理"导致机器被移走 —— 靠回读断言暴露。
// ═══════════════════════════════════════════════════════════

using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// ★ 供货商摆完库存之后，追加一台真实打印机上柜台（未拥有，玩家付款买下）。
    /// ★ 写完**必须回读断言**（MEMORY 铁律 4）；失败必须注销白名单（V1.3.1 防线）。
    /// </summary>
    internal static void SupplierStockOne()
    {
        var m = CreateRealPrinter(Config.DealerPrice);
        if (m == null)
        {
            RLog.Error("★★ 造机器失败 —— 这次供货商没带打印机（详见上面的创建日志）。");
            return;
        }

        PlayerStore store = null;
        try { store = PlayerStore.Instance; }
        catch (Exception ex) { RLog.Swallowed("SupplierStockOne", ex); }
        if (store == null)
        {
            RLog.Error("★★ PlayerStore 不可用 —— 供货商没带打印机。");
            UnregisterMachine(m);
            try { m.Destroy(); } catch (Exception ex) { RLog.Swallowed("SupplierStockOne", ex); }
            return;
        }

        // ★ 与供货商自己的货走**完全相同**的官方摆货 API（isOwend=false → 未拥有、可购买）
        try { store.AddDirectSellingItemToTable(m, false, false, false, 0); }
        catch (Exception e)
        {
            // ★ V1.4.6：这里刻意**不 Destroy** —— AddDirectSellingItemToTable 抛异常时，
            //   我们**不知道**物品是否已挂到柜台：
            //     · 已挂上 → Destroy 会让柜台持悬空指针（比泄漏危险得多）
            //     · 未挂上 → 只泄漏一个对象（无害）
            //   两害相权取轻：只注销白名单（防死指针被热路径命中），不销毁。
            //   ★ 若该机器其实已挂上柜台，下次读档扫存档时会被 LooksLikeOurMachineByTag
            //     重新认回（identifier + tag 都在）→ 白名单自动恢复，玩家仍能用它。
            RLog.Error("★★ AddDirectSellingItemToTable 异常: " + e.Message
                     + "　—— 该机器状态未知，已从登记表注销；"
                     + "若它已挂上柜台，下次读档会被自动重新认回。");
            UnregisterMachine(m);
            return;
        }

        // ★ 回读断言：摆不上去时游戏会**静默销毁**刚摆的物品，不回读 = 静默失效。
        //   【V1.4.6 取证】柜台 18×4=72 格，失败那次 24 件货已占 69 格只剩 3 格，
        //     而机器原形状 2×3=6 格 —— 差 3 格，是格子确实不够。
        //   【V1.4.7】机器形状已压到 1×2=2 格，理论上 3 格空位足够。
        //     → 走到这个分支说明**连 2 格都放不下**（空位是 1 格的孤立洞），
        //       这已经是极端情况，报出来是为了留证据，不是预期路径。
        int front = CountChildren(TryGetInventory(true));
        if (!IsOnFrontCounter(m))
        {
            RLog.Error("★★ 摆货后回读前柜台没找到这台机器（柜台现有 " + front
                       + " 件）—— 被游戏销毁。机器已压到 1×2=2 格仍放不下，"
                       + "说明柜台剩下的空位是 1 格的孤立洞、拼不出 2 格。"
                       + "已从登记表注销。");
            UnregisterMachine(m);
            return;
        }

        long price = -1;
        try { price = m.GetNegociatedValue(); } catch (Exception ex) { RLog.Swallowed("SupplierStockOne", ex); }
        RLog.Detail("[供货商] ✓ 打印机已上货架 · 售价 " + price + "（柜台现有 " + front + " 件）");
        Inform("供货商带来了一台真实打印机（" + (price > 0 ? price + " 信用点" : "待定价") + "）");
    }
}

// ═══════════════════════════════════════════════════════════
// ★ Harmony 补丁：供货商补货收敛点
//   `PlaceSupplierInventory` 是 static void 无参 —— Postfix 也无参，最简形态。
//   ★ 只追加、不改参数、不写 __result —— 纯"搭车"，原方法行为零改动。
// ═══════════════════════════════════════════════════════════
[HarmonyPatch(typeof(StoreClientList), nameof(StoreClientList.PlaceSupplierInventory))]
internal static class RealPrinter_SupplierStockPatch
{
    private static void Postfix()
    {
        try
        {
            if (!RealPrinterMod.Config.SupplierStock) return;
            RealPrinterMod.SupplierStockOne();
        }
        catch (Exception e)
        {
            RealPrinterMod.RLog.Warning("供货商上货后置处理异常: " + e.Message);
        }
    }
}
