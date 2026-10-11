// ═══════════════════════════════════════════════════════════
// ★ RealPrinter 运行期运行时 —— 领取机器 / 读档重挂 / 热键
//
// 【从 BlackPrinter 提取的三个函数】
//   RepairMachinesAfterLoad / GiveOneManually / TryGiveToWarehouse / TryPlace
//   —— 原样保留，**不做任何"顺手优化"**（MEMORY 铁律：移植时逐字比对，
//      少一行都可能踩到指针/存档相关的深坑）。
//
// 【砍掉的东西】
//   F1 排障总闸 / F4 全扫 / F5 筛选档位 / F7 详细日志 / F8 派生值监视 /
//   F9 重建窗口 / F10 dump / F11 裸指针安全模式 / F12 拖动路径 ——
//   全部是 BlackPrinter 排障设施，面向用户版不需要。
//   ★ 核桃 2026-10-03 拍板：**只留 F6 领取机器**。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using MelonLoader;
using Il2Cpp;
using UnityEngine;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    private float _lastRepairTime;
    // ★ V1.4.12：F6 恢复后加回
    private float _nextHotkeyTime;

    /// <summary>
    /// 读档后修复。
    ///
    /// ★★★ 为什么必须有（V0.4.8 实机踩过的最隐蔽的静默失效）：
    ///   读档后 **IL2CPP 对象指针全变了**，而热路径 `IsOurMachine`
    ///   只认白名单里的指针。**不重建的话，读完档左键点物品就完全没反应**
    ///   —— 不报错、不掉帧、玩家只会以为 mod 坏了。
    ///   `FindOurMachines()` 内部会顺手 RegisterMachine，这里只做检查与报警。
    /// </summary>
    private void RepairMachinesAfterLoad()
    {
        try
        {
            if (Time.unscaledTime < _lastRepairTime + 1f) return;
            _lastRepairTime = Time.unscaledTime;

            // ★★ V1.4.1 进店门禁：主菜单 / 还没进存档时，PlayerStore 还不存在，
            //   下面 FindOurMachines 扫存档就是纯空转（还会刷"读档重挂异常"警告）。
            //   → 静态单例判空是读一个指针，不 invoke 任何方法，主菜单零开销。
            bool inStore = false;
            try { inStore = PlayerStore.Instance != null; }
            catch { inStore = false; }
            if (!inStore) return;


            var machines = FindOurMachines();
            if (machines.Count == 0)
            {
                RLog.Detail("读档检查：存档里没有真实打印机（按 F6 领一台）。");
                return;
            }

            int registered = MachineRegistryCount;
            if (registered == 0)
                RLog.Error("★★ 指针白名单重建后仍是空的 —— 交互必然失效（左键点物品没反应）。请重启游戏。");

            //逐台确认物品状态正常（identifier / tag 读得到 = 还认得出来）
            int ok = 0, bad = 0;
            for (int i = 0; i < machines.Count; i++)
            {
                try { if (IsOurMachine(machines[i])) ok++; else bad++; }
                catch { bad++; }
            }

            RLog.Detail("读档检查完成：共 " + machines.Count + " 台真实打印机"
                    + "（认得出来 " + ok + "，认不出 " + bad + "）· 指针白名单 "
                    + registered + " 条");
            if (bad > 0)
                RLog.Error("★★ 有 " + bad + " 台打印机读档后认不出来了，详见上面异常行。");
        }
        catch (Exception e) { RLog.Warning("读档重挂异常: " + e.Message); }
    }

    /// <summary>
    /// F6：领一台真实打印机（直接进主仓库）。
    /// ★ 不发测试品 —— 面向用户版不该塞一堆莫名其妙的东西进玩家仓库。
    ///   （BlackPrinter 那边会发一批，那是给核桃测试用的。）
    /// ★★ V1.3.0：正式获取改成"从同行处购买"，所以这个入口**默认关**。
    ///   → 关的时候按键必须**明确告诉玩家为什么没反应**，绝不能静默无响应
    ///     （MEMORY 铁律：静默失效是最难排查的故障）。
    /// </summary>
    /// <summary>
    /// F6：领一台真实打印机（直接进主仓库）。
    /// ★ V1.4.12：价值固定为 0（防刷钱），cfg 开关已在 OnUpdate 前置判过。
    /// </summary>
    private static void GiveOneManually()
    {
        try
        {
            RLog.Detail("===== F6 领取（价值 0）=====");

            // ★ 显式传 0 → unitValue/unitBaseValue = 0
            var tool = CreateRealPrinter(0);
            if (tool == null)
            {
                Inform("创建真实打印机返回 null（详见日志）", "yellow");
                return;
            }

            if (!TryGiveToWarehouse(tool, "真实打印机"))
                Inform("真实打印机进主仓库失败，详见日志", "yellow");
        }
        catch (Exception e)
        {
            RLog.Msg("领取异常: " + e.Message);
            try { Inform("领取异常：" + e.Message, "yellow"); }
            catch (Exception ex) { RLog.Swallowed("GiveOneManually", ex); }
        }
    }

    /// <summary>
    /// ★ V1.3.0：玩家**真的买下了**柜台上那台机器（由 OnItemBought 后置补丁调进来）。
    /// 【为什么单独记一笔】"摆上柜台"和"买成"是两件事；不记的话只能靠
    ///   "仓库里有没有"去猜。买成的那一刻必须有一条明摆着的日志。
    /// </summary>
    internal static void NoteMachineBought(GameItem item, int cost)
    {
        try
        {
            if (item == null) return;
            RegisterMachine(item);        // 幂等：指针没变，但确保白名单里有它
            RLog.Msg("✓ 已买下真实打印机（花了 " + cost + " 信用点）—— 现在归你了。");
            Inform("已买下真实打印机（-" + cost + "）");
        }
        catch (Exception e) { RLog.Warning("记录购买结果时异常: " + e.Message); }
    }

    /// <summary>
    /// 直接把物品放进玩家主仓库。
    /// ★ 铁律：写完**回读断言** —— 不做静默兜底（放进去了不等于真放进去）。
    /// </summary>
    private static bool TryGiveToWarehouse(GameItem item, string what)
    {
        if (item == null) return false;

        GameInventory warehouse = null;
        try
        {
            var emp = EmporiumEntry.Instance;
            if (emp != null) warehouse = emp.invElement;
        }
        catch (Exception e) { RLog.Msg("取主仓库异常: " + e.Message); }

        if (warehouse == null)
        {
            RLog.Error("✘ 主仓库不可用（EmporiumEntry.Instance / invElement 为空）—— "
                      + what + " 没有发放，请回店里再试。");
            // ★ V1.3.1：销毁前先注销 —— 否则死指针留在登记表里，购买链路查表
            //   命中后读 itemFeatures 就是 AV（V1.3.0 实机崩溃根因）。
            UnregisterMachine(item);
            try { item.Destroy(); } catch (Exception ex) { RLog.Swallowed("TryGiveToWarehouse", ex); }
            return false;
        }

        if (!TryPlace(item, warehouse))
        {
            RLog.Error("✘ " + what + " 放不进主仓库（格子满？形状不匹配？）");
            UnregisterMachine(item);   // ★ V1.3.1：同上
            try { item.Destroy(); } catch (Exception ex) { RLog.Swallowed("TryGiveToWarehouse", ex); }
            return false;
        }

        // ★ 回读断言：进没进主仓库要看得见，不能"以为成功了"
        bool landed = false;
        try
        {
            var all = warehouse.childItems;   // ★ GameInventory 没 AllItems，childItems 才是
            if (all != null)
            {
                IntPtr p = item.Pointer;
                for (int i = 0; i < all.Count; i++)
                {
                    try { if (all[i] != null && all[i].Pointer == p) { landed = true; break; } }
                    catch (Exception ex) { RLog.Swallowed("TryGiveToWarehouse", ex); }
                }
            }
        }
        catch (Exception e) { RLog.Msg("回读主仓库异常: " + e.Message); }

        if (!landed)
        {
            RLog.Error("✘ " + what + " 放入后回读主仓库没找到它 —— 判定为发放失败。");
            // ★ V1.3.1：状态不明的机器绝不能留在登记表里 ——
            //   它可能已被游戏收走/销毁，指针随时悬空。注销 + 销毁，两头都做。
            UnregisterMachine(item);
            try { item.Destroy(); } catch (Exception ex) { RLog.Swallowed("TryGiveToWarehouse", ex); }
            return false;
        }

        RLog.Detail("✓ 已放主仓库（" + what + "）。");
        // ★ 轻提示（不是弹窗）。核桃 V1.0.1 反馈：原来那个「确认/取消」对话框
        //   完全多余 —— 领取结果成功与否，日志里有、白提示也有，不必再问一次。
        Inform("「" + what + "」已放主仓库");
        return true;
    }

    /// <summary>
    /// 放置：走官方的 `TryFindOneValidInventorySlot` + `slot.item=` + `AcceptUnchecked()`。
    /// ★ 绝不拿 UncheckedAccept 兜底（满格时它会强塞物品）。
    ///   容器**没有公开 AddItem**（只有 AddParent），这是唯一正解。
    /// </summary>
    private static bool TryPlace(GameItem item, GameInventory inv)
    {
        if (item == null || inv == null) return false;
        try
        {
            SlotMarker slot = null;
            try
            {
                if (inv is GameGridInventory grid) slot = grid.TryFindOneValidInventorySlot(item, false);
                else slot = inv.TryFindOneValidInventorySlot(item);
            }
            catch (Exception ex) { RLog.Swallowed("TryPlace", ex); }
            if (slot == null) return false;
            try { slot.item = item; return slot.AcceptUnchecked() > 0; }
            catch { return false; }
        }
        catch { return false; }
    }

    // ────────────────────────────────────────────────────────────────
    // ★ V1.4.12：F6 热键轮询恢复。与 V1.4.11 删除时相比，行为不同：
    //   · 开关**暴露到 cfg**（EnableF6Give），玩家可切换
    //   · 关闭时**无任何反应**（核桃拍板：不弹提示）
    //   · 领取的机器**价值 0**（防刷钱）
    //   · 每帧只有一次 `if (!Config.EnableF6Give) return;` —— 关着时零开销
    // ────────────────────────────────────────────────────────────────

    /// <summary>F6 直领轮询（只在 cfg 打开时被 OnUpdate 调用）。</summary>
    private void PollHotkeys()
    {
        try
        {
            if (Time.unscaledTime < _nextHotkeyTime) return;
            // ★ 直接使用 KeyCode.F6（编译期常量）—— 原 HotkeyGive 属性每次走
            //   ReadHotkey 有 try/catch + null 检查，每帧调用不值。
            if (!Input.GetKeyDown(KeyCode.F6)) return;
            _nextHotkeyTime = Time.unscaledTime + 0.4f;   // 防连按
            GiveOneManually();
        }
        catch (Exception ex) { RLog.Swallowed("PollHotkeys", ex); }
    }
}