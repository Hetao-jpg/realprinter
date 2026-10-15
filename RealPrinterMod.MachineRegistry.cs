// ═══════════════════════════════════════════════════════════
// ★ 机器注册表 —— 「这台机器是不是我们的」的唯一权威
//
// 【本文件管什么】
//   指针白名单（热路径判据）+ 存档扫描（冷路径认领）。
//
// 【其他部分在哪】（V1.2.0 从 Machine.cs 拆出，逻辑逐字未改）
//   · 裸指针闸门 / SafeId      → RealPrinterMod.PointerSafety.cs
//   · 建机与交互落地            → RealPrinterMod.MachineBuilder.cs
//   · Harmony 交互补丁          → RealPrinterMod.Interact.cs
//
// 【★★★ 最硬的一条约束，改这个文件前必读】
//   `IsOurMachine` 在 `GameItem.MayTarget/CanTarget` 里**每秒被调几千次**。
//   热路径铁律：里面只允许「读指针、比数值」，
//   **一个 IL2CPP 调用都不能有**（V0.4.7 实测：碰一次 IL2CPP 字符串就 AV 猝死）。
//   → 判身份一律走 `_machinePtrs` 查表；需要读 tag / identifier 的判据
//     （`LooksLikeOurMachineByTag`）只准在冷路径用。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// ★★★ 是不是我们的黑印 —— **V0.4.8 改成纯指针白名单，一个字节都不读**。
    ///
    /// 【★★★ V0.4.7 的错法，以及它为什么必然崩】
    ///   V0.4.6/0.4.7 我在热路径（MayTarget/CanTarget/Target，每秒几千次）里
    ///   读 identifier 字段的裸指针，再拿它去问 IL2CPP 要字符串内容。
    ///   实机两次实打实的失败：
    ///     ① V0.4.7 崩在 `IL2CPP.il2cpp_string_length(IntPtr)`
    ///        —— `idp` 这个字段值本身已经是**野指针**（物品被拖动过程销毁），
    ///           把野指针喂给原生函数 → 直接 AV，连 catch 都进不去。
    ///     ② V0.4.7 日志里 `self=<chars?>` 刷满屏
    ///        —— `il2cpp_string_chars` 返回的是字符串内部 `+0x14` 处的 `char*`，
    ///           **天生不是 8 字节对齐**，被我自己的 `LooksLikeHeapPtr` 对齐检查挡掉。
    ///           → 预检写得太严，把合法值也毙了。
    ///   ★★ 教训：热路径里**任何一次间接读**都是雷。判据必须是**读指针，比数值**。
    ///
    /// 【V0.4.8 做法】建机 / 读档认领时把我们的机器指针登记进 `_machinePtrs`，
    ///   热路径只做一次查表 —— **零 IL2CPP 调用、零裸指针读、零 AV 面**。
    ///   指针失效（物品被销毁）时查表命中的是旧值，但后续任何真正读它的地方
    ///   （开窗、读 tag）都有 managed 侧的对象引用兜着，不会在这条路上炸。
    /// </summary>
    /// ★★ V1.2.0：改用 `ConcurrentDictionary` —— **读完全无锁**（代码评审 P1）。
    /// 【判据路径有多热】`IsOurMachine` 在 `MayTarget/CanTarget` 里每秒被调几千次，
    ///   原来每次都要 `lock (_machinePtrsLock)`。
    /// 【性能到底损失多少】说实话**几乎为零**：Unity 主线程单线程，未争用的
    ///   `Monitor.Enter/Exit` 约 20ns/次 → 几千次/秒 ≈ 0.1ms/秒。
    ///   → 换容器的**真实价值不是性能**，而是把"热路径上有一把锁"这个隐患
    ///     从根上消掉：将来谁在锁里加一次 IL2CPP 调用，就会变成每秒几千次的
    ///     原生调用（V0.4.7 的教训：热路径碰 IL2CPP 会猝死）。无锁读 = 无从加起。
    /// 【为什么不换 ImmutableHashSet】写时整体替换引用也行，但登记/清空是
    ///   读档链路上的冷路径，ConcurrentDictionary 的语义更直白、更不容易写错。
    private static readonly ConcurrentDictionary<long, byte> _machinePtrs
        = new ConcurrentDictionary<long, byte>();

    /// <summary>登记一台我们的机器（建机 / 读档认领时调）。</summary>
    internal static void RegisterMachine(GameItem it)
    {
        try
        {
            if (it == null) return;
            long p = ((Il2CppObjectBase)it).Pointer.ToInt64();
            if (p != 0) _machinePtrs[p] = 0;
        }
        catch (Exception ex) { RLog.Swallowed("RegisterMachine", ex); }
    }

    /// <summary>
    /// ★★ V1.3.1：把一台机器从登记表里**注销**。
    ///
    /// 【为什么必须有这个】V1.3.0 实机崩溃（AccessViolation）的根因链：
    ///   摆货失败 → 游戏把那台机器销毁了（"front counter full" 时不抛异常、直接销毁）
    ///   → 但登记表里的指针**没人清** → 之后的购买/拖动链路查表命中
    ///   → 对一块已释放的内存读 `itemFeatures` → **AV 猝死**。
    ///   （MEMORY 铁律 9：WasCollected 对"刚被销毁"一律放行，判据拦不住
    ///    —— 唯一有效防线是**别让死指针留在表里**。）
    /// </summary>
    internal static void UnregisterMachine(GameItem it)
    {
        try
        {
            if (it == null) return;
            long p = ((Il2CppObjectBase)it).Pointer.ToInt64();
            if (p != 0) _machinePtrs.TryRemove(p, out _);
        }
        catch (Exception ex) { RLog.Swallowed("UnregisterMachine", ex); }
    }

    /// <summary>★ V1.3.1：按裸指针注销（物品可能已被游戏销毁、托管引用不可靠时用这个）。</summary>
    internal static void UnregisterPtr(IntPtr p)
    {
        try { if (p != IntPtr.Zero) _machinePtrs.TryRemove(p.ToInt64(), out _); }
        catch (Exception ex) { RLog.Swallowed("UnregisterPtr", ex); }
    }

    /// <summary>清空登记表（重登游戏 / 读档前调用）。</summary>
    internal static void ClearMachineRegistry()
    {
        try { _machinePtrs.Clear(); } catch (Exception ex) { RLog.Swallowed("ClearMachineRegistry", ex); }
    }

    /// <summary>登记表现在有几台（自检用）。</summary>
    internal static int MachineRegistryCount
    {
        get { try { return _machinePtrs.Count; } catch { return 0; } }
    }

    /// <summary>
    /// ★★ 冷路径判据：靠 `state.dict` 里的 tag 认机器。
    ///   会进 IL2CPP，**只能用在对象来源可靠的地方**（存档扫描、建机后校验），
    ///   **绝对不能用在 MayTarget/CanTarget 这类热路径**。
    /// </summary>
    private static bool LooksLikeOurMachineByTag(GameItem it)
    {
        if (it == null) return false;
        try { if (it.state?.dict == null) return false; } catch { return false; }
        try { if (it.state.dict.ContainsKey(OurTag)) return true; } catch (Exception ex) { RLog.Swallowed("LooksLikeOurMachineByTag", ex); }
        try { if (it.state.dict.ContainsKey(ToolTagLabeler) == true
                   && it.identifier == MachineIdentifier) return true; } catch (Exception ex) { RLog.Swallowed("LooksLikeOurMachineByTag", ex); }
        try { if (it.identifier == MachineIdentifier) return true; } catch (Exception ex) { RLog.Swallowed("LooksLikeOurMachineByTag", ex); }
        return false;
    }

    /// <summary>
    /// 热路径判据：只比指针，**不读任何内存**。
    /// ★ 拿不到指针时**直接 false** —— 不再退回读字符串/查 tag，
    ///   因为 V0.4.7 实测证明：热路径上任何一次间接读都可能踩到野指针（AV 抓不住）。
    ///   表为空的极端情况（同一帧建机+使用）由 `BuildMachineRegistryNow()` 兜底。
    /// </summary>
    public static bool IsOurMachine(GameItem it)
    {
        if (it == null) return false;
        try
        {
            long p = ((Il2CppObjectBase)it).Pointer.ToInt64();
            if (p == 0) return false;
            return _machinePtrs.ContainsKey(p);
        }
        catch { return false; }
    }

    /// <summary>
    /// 立刻全量扫存档重建白名单（自检 / 手动强开用）。
    /// 读档后指针全变，RepairMachinesAfterLoad 每次都会调它刷新。
    /// ★ V1.4.14：改调 RebuildAndPruneRegistry —— 重建的同时清掉死指针。
    /// </summary>
    internal static int BuildMachineRegistryNow()
    {
        try { RebuildAndPruneRegistry(); } catch (Exception ex) { RLog.Swallowed("BuildMachineRegistryNow", ex); }
        return MachineRegistryCount;
    }

    /// <summary>
    /// ★★ V1.4.14：全量扫存档重建白名单，并**差集清理**已失效的旧指针。
    ///
    /// 【为什么必须清 —— V1.4.13 的静默误触发根因】
    ///   旧版只 Register 不 Remove：读档后 IL2CPP 指针全变、旧指针全部作废，
    ///   但它们永远留在 `_machinePtrs` 里。IL2CPP 分配器会**复用地址** ——
    ///   放大镜/水桶拿到旧打印机的地址后，热路径 `IsOurMachine` 查表命中
    ///   → `TryClaim` 通过 → May/Can 强制 true 短路原版 → Target 开编辑窗。
    ///   （实机：右键放大镜/水桶触发真实打印机功能。）
    ///
    /// 【差集清理 vs 先清后扫】
    ///   先清后扫：FindOurMachines 抛异常时白名单直接归零，交互全挂。
    ///   差集清理：先扫出"当前确实还在场上的"指针集合，
    ///             再把登记表里不在集合内的条目移除 ——
    ///             扫描失败只会保留旧条目（可能含死指针，但不会凭空丢机器）。
    ///
    /// 【性能】FindOurMachines 本身就是全量扫（RepairMachinesAfterLoad 每秒
    ///   最多跑一次），差集只是多一次 HashSet 构建 + 遍历登记表，
    ///   冷路径，可接受。
    /// </summary>
    internal static int RebuildAndPruneRegistry()
    {
        var machines = FindOurMachines();

        // ① 收集"当前确实还在场上"的指针集合
        var alive = new HashSet<long>();
        for (int i = 0; i < machines.Count; i++)
        {
            try
            {
                long p = ((Il2CppObjectBase)machines[i]).Pointer.ToInt64();
                if (p != 0) alive.Add(p);
            }
            catch (Exception ex) { RLog.Swallowed("RebuildAndPrune.alive", ex); }
        }

        // ② 差集：登记表里不在 alive 集合内的 → 死指针，移除
        var stale = new List<long>();
        foreach (var kv in _machinePtrs)
            if (!alive.Contains(kv.Key)) stale.Add(kv.Key);

        for (int i = 0; i < stale.Count; i++)
            _machinePtrs.TryRemove(stale[i], out _);

        if (stale.Count > 0)
            RLog.Detail("登记表差集清理：移除 " + stale.Count
                       + " 条失效指针（读档/销毁后残留），现存 "
                       + _machinePtrs.Count + " 条");

        return _machinePtrs.Count;
    }

    /// <summary>
    /// 扫描玩家全部物品，找出所有我们的工具。
    ///
    /// 【★★★ V0.4.8 关键修正】这里**不能**调 `IsOurMachine`。
    ///   V0.4.8 把 `IsOurMachine` 改成"纯指针白名单查表"，而白名单要靠这个方法填 ——
    ///   循环依赖 → 首次调用时表是空的 → 一台都认不出 → 读档后功能直接失效。
    ///   → 这里改用**独立的 tag 判据**（读 dict，要进 IL2CPP，但对象来自
    ///     `PlayerStore.FindAllItem`，是游戏保证有效的活对象，不是拖动中的野指针）。
    ///   → 找到后顺手 `RegisterMachine` 把指针填进白名单，供热路径查表。
    /// </summary>
    private static List<GameItem> FindOurMachines()
    {
        var result = new List<GameItem>();
        var seen = new HashSet<long>();
        try
        {
            var store = PlayerStore.Instance;
            if (store == null) return result;
            foreach (bool owned in new[] { true, false })
            {
                var all = store.FindAllItem(owned);
                if (all == null) continue;
                for (int i = 0; i < all.Count; i++)
                {
                    // ★ V1.4.13：**逐件**兜住，而不是只在最外层兜一个 try。
                    //   实机那个 NRE 就发生在这里（all[i] / LooksLikeOurMachineByTag）：
                    //   一件物品出问题就让**整趟扫描**中断，已扫到的结果被丢弃、
                    //   后面的物品全认不出 → 白名单残缺 → 真印静默失去交互能力。
                    //   → 单件失败只记一次限频告警并跳过这一件，不影响其余。
                    GameItem it = null;
                    try { it = all[i]; }
                    catch (Exception e)
                    {
                        RLog.Swallowed("FindOurMachines.all[" + i + "]", e);
                        continue;
                    }
                    if (it == null) continue;
                    try
                    {
                        if (!LooksLikeOurMachineByTag(it)) continue;
                        long p = ((Il2CppObjectBase)it).Pointer.ToInt64();
                        if (p == 0 || !seen.Add(p)) continue;
                        RegisterMachine(it);
                    }
                    catch (Exception e)
                    {
                        RLog.Swallowed("FindOurMachines.item", e);
                        continue;
                    }
                    result.Add(it);
                }
            }
        }
        catch (Exception e)
        {
            // ★ V1.4.13：原先这里是 `MelonLogger.Msg(...)`（普通信息级），
            //   实机日志（2026-10-09）实锤它在启动期抛过：
            //     [真印] FindOurMachines 异常: System.NullReferenceException
            //   而这函数是「找出场上所有我们的机器」的核心 —— 它抛异常时
            //   **返回的是残缺的 result**（已扫到的部分），调用方无从分辨
            //   「扫完了只有 1 台」和「扫到一半炸了只有 1 台」——
            //   这正是 MEMORY 铁律里最忌的「静默失效」。
            //   → ① 升级为 Warning，让它与日常消息区分开；
            //     ② 带上 StackTrace，排障时能直接定位到哪一行炸的。
            RLog.Warning("FindOurMachines 扫描中断（已扫到 " + result.Count
                       + " 台，结果可能不完整）：" + e.GetType().Name + ": " + e.Message
                       + "\n" + e.StackTrace);
        }
        return result;
    }
}
