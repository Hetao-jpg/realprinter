// ★★★ 真实打印机交互层 —— V0.4.3 Harmony 四补丁（★ 补丁签名纠错 + 短路修正确认）
//
// ═══════════════════════════════════════════════════════════════════════
// ★★★ V0.4.3 变更说明（★ 终于找到"点了没反应"的真正根因）
// ═══════════════════════════════════════════════════════════════════════
//
// 【★★★ 根因一：补丁参数名写错，target 恒为 null —— V0.4.0/V0.4.1 从没接管过任何一次】
//   我 V0.4.1 写的是：
//     private static void Prefix(GameItem __instance, GameItem source, GameItem target, ...)
//   ★★★ **但 GameItem 上这三个方法只有一个参数！** 精确定位所属类后：
//     dump.cs:57796  public abstract class GameItem : ... // TypeDefIndex: 1357
//     dump.cs:58418  public bool MayTarget(GameItem targetItem) { }  // RVA 0x799250
//     dump.cs:58421  public bool CanTarget(GameItem targetItem) { }  // RVA 0x7917F0
//     dump.cs:58424  public void Target   (GameItem targetItem) { }  // RVA 0x79C080
//
//   ★★★ **我之前引用的 dump.cs:54029/54032/54035 根本不在 GameItem 上** ——
//     那三个属于 `public class ItemBehaviourManager`(TypeDefIndex 1292, dump.cs:53979)：
//       public bool MayTarget(GameItem source, GameItem target)
//       public bool CanTarget(GameItem source, GameItem target)
//       public void Target  (GameItem source, GameItem target)
//     两个参数是真的，但**张冠李戴挂错了类**。同名的还有：
//       dump.cs:54065  ItemTargetBehaviour.MayTarget(GameItem, GameItem) →返回 Nullable<bool>
//   —— 我 V0.4.1 把"ItemBehaviourManager 的签名"当成"GameItem 的签名"用了。
//
//   【实机铁证 —— 崩溃栈自己招了】
//     at RealPrinter.BlackPrinterMod.Patch_Target.Prefix(Il2Cpp.GameItem, Il2Cpp.GameItem)
//                                          ↑ 只有 2 个参数，不是 3 个
//     Harmony 按名字绑定：`source` 找不到 source → 按位置 fallback 到 targetItem；
//                        `target` 越界 → **被丢弃，传进来恒为 null**。
//     → TryClaim 第一行 `if (source == null || target == null) return false;`
//     → **补丁一次都没接管过**。判据写得再准也没用，因为根本没走到判据。
//
//   【正确的语义】照抄已跑通的 ContainerSuite.DIYUpgrade（DiyUpgradeMod.Patches.cs:96）：
//     private static bool Prefix(GameItem __instance, GameItem targetItem, ref bool __result)
//   __instance   = **手持工具 / 被作用的物品**（黑印自己）
//   targetItem= **被指向的物品**
//   （与 ItemBehaviourManager 相反 —— 那边 __instance 是 manager）
//
// 【★★★ 根因二：MayTarget/CanTarget 的 Prefix 没短路，__result 被原版覆盖】
//   V0.4.1 的写法：
//     private static void Prefix(..., ref bool __result) { ...; __result = true; }
//   ★★★ **没有 return false → 原版方法照样执行 → 把 __result 覆盖回 false。**
//   已跑通的 DIY 每一个分支都有返回值：`return true`(放行) / `return false`(已接管)。
//   → 不 return false，等于什么都没改。
//
// 【V0.4.1 的错判，V0.4.2 已更正 —— 保留在此避免回退】
//   我曾看到 dump.cs:3002 `public abstract string identifier { set; }` 就断定
//   "identifier 是计算属性、没有 backing field"。那是父类 GameItemFunc 上的抽象声明。
//   事实（GameItem 类内）：`private string <identifier>k__BackingField; // 0x1C0`
//   → V0.4.1 只加 state(0x1B0) 一道闸门 → 半初始化对象照样放行 → 照崩。
//
// 【★★★ 铁律：AccessViolationException 不可捕获】
//   拖动中游戏会拿**半初始化对象**来问 Target，此时读任何托管属性都是读野指针。
//   `try/catch` **抓不住** —— 进程直接死。
//   → 判据必须一次 IL2CPP 都不进（裸指针 + 字节比较）。
//
// 【★ 实机崩溃现场留档 —— V0.4.0/V0.4.1】
//   Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
//   Repeat 2 times:
//    at Il2CppInterop.Runtime.IL2CPP.il2cpp_string_length(IntPtr)
//    at Il2CppInterop.Games.Games.StringHelper.set_identifier(Il2CppSystem.String)
//    at RealPrinter.BlackPrinterMod.IsTargetEditable(Il2Cpp.GameItem)
//    at RealPrinter.BlackPrinterMod.TryClaim(Il2Cpp.GameItem, Il2Cpp.GameItem)
//    at RealPrinter.BlackPrinterMod.Patch_Target.Prefix(Il2Cpp.GameItem, Il2Cpp.GameItem)
//    at DynamicClass.IL2CPP.<EndDrag>d__12.MoveNext()
//
// 【★ 拖动路径偏移】照抄 DIYUpgrade 的 Off 常量（已核对 dump.cs:63815）：
//     dragSource=0x50 / dragTarget=0x88 / dragTargetInv=0x90
//
// 【V0.3.0 委托路线为何被证伪 —— 留档】
//   V0.3.0 给物品挂 6 个托管委托：赋值成功、回读全在、自检全过，**功能 100% 不工作**，
//   日志刷出 2244 次：
//     [ERROR] [Il2CppInterop] During invoking native->managed trampoline
//     System.Reflection.TargetException: Object does not match target type.
//   根因：Il2CppInterop 的 native→managed trampoline 用反射 invoke，参数是 IL2CPP 侧
//   指针，类型对不上。原版红印能用是因为它用 IL2CPP 原生闭包（b__12_0~4），造不出来。
// ═══════════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// 补丁优先级。比 ContainerSuite.DIYUpgrade 的 900 略高，
    /// 保证"确实是黑印在作用"时 ours 先短路；不是我们的活就 return true，不影响别人。
    /// </summary>
    private const int PatchPriority = 990;

    // ────────────────────────────────────────────────────────────────
    // 补丁 ①②③：GameItem.MayTarget / CanTarget / Target
    //
    // ★★★ V0.4.3 签名纠错（dump.cs:58418/58421/58424，所属类 GameItem 从 57796 起）：
    //     public bool MayTarget(GameItem targetItem)   // RVA 0x799250
    //     public bool CanTarget(GameItem targetItem)   // RVA 0x7917F0
    //     public void Target   (GameItem targetItem)   // RVA 0x79C080
    //   —— 都**只有一个显式参数**！
    //   __instance= 手持工具（黑印自己）；targetItem = 被指向的物品。
    //
    // ★★★ 为什么三个必须一起打：
    //   游戏左键点物品时先问 MayTarget/CanTarget 决定"能不能作用"，
    //   返回 false 就直接画红字提示条，**根本不会调 Target**。
    //
    // ★★★ 为什么每个分支都必须有返回值：
    //   Prefix 不 return false → 原版方法照跑 → __result 被覆盖 → 补丁等于没打。
    //
    // ★★★ V0.4.8：热路径日志限流。
    //   实机日志显示 MayTarget/CanTarget 每秒被调**几千次**（游戏对每个物品
    //   每帧都问一遍"能不能作用"）。原来每次接管都打一行 →
    //   单次尝试刷出 900+ 行，把日志文件冲爆、也拖慢游戏。
    //   → 改成"每类补丁每秒最多打 1 行"，要看细节用 F7 详细日志。
    // ────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(GameItem), "MayTarget")]
    private static class Patch_MayTarget
    {
        [HarmonyPriority(PatchPriority)]
        private static bool Prefix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            // 不是我的活 → return true 放行，原版和别人的 mod 照常跑
            if (!TryClaim(__instance, targetItem)) return true;

            __result = true;
            return false;   // ★★★ 必须短路，否则 __result 被原版覆盖
        }
    }

    [HarmonyPatch(typeof(GameItem), "CanTarget")]
    private static class Patch_CanTarget
    {
        [HarmonyPriority(PatchPriority)]
        private static bool Prefix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            if (!TryClaim(__instance, targetItem)) return true;

            __result = true;
            return false;   // ★★★ 必须短路
        }
    }

    [HarmonyPatch(typeof(GameItem), "Target")]
    private static class Patch_Target
    {
        [HarmonyPriority(PatchPriority)]
        private static bool Prefix(GameItem __instance, GameItem targetItem)
        {
            if (!TryClaim(__instance, targetItem)) return true;

            // ★★ V1.4.14：点击级冷复核 —— Target 只在玩家真的点击时触发（不是热路径），
            //   可以安全进 IL2CPP。若 self 的指针已被游戏销毁、被地址复用给了别的物品
            //   （读档/卖掉/垃圾桶后常见），LooksLikeOurMachineByTag 会认不出 →
            //   注销死指针 + 放行原版，避免放大镜/水桶误触发编辑窗。
            if (!LooksLikeOurMachineByTag(__instance))
            {
                RLog.Warning("★ 冷复核失败：self 指针已在白名单但 tag/identifier 对不上"
                           + "（疑似读档/销毁后地址复用）—— 已注销，本次放行原版。"
                           + " ptr=" + SafePtr(__instance));
                UnregisterMachine(__instance);
                return true;
            }

            // ★ 只接管这一次；OnTargetItemFunc 内部会开窗。
            // ★★ V1.4.11：把 `Config.VerboseLog` 判据提到调用点。
            //   `SafeId()` 在命中我方白名单时会做 4 次原生调用
            //   （ReadFieldPtr + il2cpp_string_length + il2cpp_string_chars + Marshal.Copy），
            //   而这些是**实参** —— `RLog.Detail` 内部那句开关拦不住它们，
            //   VerboseLog 关着也照跑。判据写在调用点，实参就只在该分支里求值。
            OnTargetItemFunc(targetItem);
            return false;   // ★★★ 不短路会让原版委托链重复触发
        }
    }

    // ────────────────────────────────────────────────────────────────
    // ★★ V1.4.11：`ItemMouseDragHandler.EndDrag` 补丁**已整类删除**。
    //
    // 【它是什么】V0.4.3~V0.4.7 期间的第 4 个交互补丁。V0.4.8 起逻辑已停用，
    //   但**类壳留着**，Postfix 第一行就 `return`（等于这条路径不存在）。
    //
    // 【V1.4.11 为什么连壳一起删 —— 它不是"无害的空类"，是三重负收益】
    //   ① **每次玩家拖动物品都会被调一次**（EndDrag 是拖动结束的必经回调）。
    //     空 Postfix 一样要走完 Harmony 的 detour：查补丁表 → 解引用委托 → 进方法 → return。
    //   ② **它是 IL2CPP private 无参方法**，Harmony 的定位器与 GameItem 那三个
    //     public 带参方法不同口径 → 自检长期报"⚠ 未挂上"，逼着读代码的人
    //     花时间排一个**根本不该存在的故障**（误报比不报更坏，MEMORY 铁律 10）。
    //   ③ 它是"停用状态"的**唯一证据**。而证据应该是注释，不是常驻补丁 ——
    //     真要恢复 这个功能，判据、坑、写法全在下面这段注释里，代码本身反而是负担。
    //
    // 【恢复时的正确做法（写给将来的自己）】
    //   · **不要复活裸偏移读法**（owner+0x50/0x88/0x90）。EndDrag 是**异步**回调，
    //     此刻 lastItem 可能已被销毁 → 悬空指针 → AccessViolation 且 catch 抓不住
    //     （V0.4.6/V0.4.7 实机崩溃）。要读就走托管属性（Pointer / 仓库遍历）。
    //   · **先确认它到底覆盖不到什么**：功能目前全靠 `GameItem.Target`（补丁③）扛，
    //     V0.4.3 实机日志「★★ 拖动命中」一次都没出现过 —— 也就是说这条路径历史上
    //     从未真正承接过功能。恢复的前提是先有实机证据说明现在漏了什么。
    // ────────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────────
    // 共同判据
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// ★ 这件事是不是该我们管？
    ///   V0.4.3 语义纠正：`self` = 手持工具（黑印），`target` = 被指向的物品。
    ///     照抄已跑通的 ContainerSuite.DIYUpgrade.TryResolve(a=__instance, b=targetItem)。
    ///   条件：① self 确实是我们的黑印
    ///        ② target 成型、且不是我们自己的打印机
    ///   不满足→ false → 补丁 `return true` 放行，原版和别人的 mod 照常跑。
    ///
    /// ★★★ 判据的**顺序**决定生死（V0.4.1 崩在这里）：
    ///   `AccessViolationException 不可捕获` —— 一旦读到半初始化对象就是直接崩。
    ///   所以两边都先过两道裸指针闸门，再谈托管属性。
    /// </summary>
    private static bool TryClaim(GameItem self, GameItem target)
    {
        if (self == null || target == null) return false;
        // ★ 自己作用自己没有意义（拖动/悬停时游戏会问一堆自问自答）
        if (SamePointer(self, target)) return false;
        try
        {
            if (!IsOurMachine(self)) return false;
            if (!IsTargetEditable(target)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 两个 GameItem 是不是同一个原生对象。
    /// 裸指针比较，不进 IL2CPP —— 半初始化对象也安全。
    /// </summary>
    private static bool SamePointer(GameItem a, GameItem b)
    {
        if (ReferenceEquals(a, b)) return true;
        try
        {
            IntPtr pa = ((Il2CppObjectBase)a).Pointer;
            IntPtr pb = ((Il2CppObjectBase)b).Pointer;
            return pa != IntPtr.Zero && pa == pb;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★ V1.2.0：已知类型表（代码评审 P5）。**加类型只改这一处**。
    /// ★★ V1.4.11：`ItemMouseDragHandler` 已移出（EndDrag 补丁整类删除，见上方注释）。
    ///   现在自检只覆盖 GameItem 一个类 —— 表留着是为了"加类型只改一处"这条纪律。
    /// </summary>
    private static readonly (string Name, Type Type)[] KnownTypes =
    {
        ("GameItem", typeof(GameItem)),
    };

    /// <summary>按名字找游戏类型（当前只有 GameItem）。</summary>
    private static Type ResolveType(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < KnownTypes.Length; i++)
        {
            if (string.Equals(KnownTypes[i].Name, name, StringComparison.Ordinal))
                return KnownTypes[i].Type;
        }
        return null;
    }

    /// <summary>
    /// ★★ 找目标方法，**public / non-public 都找**。
    ///   口径必须和 Harmony 的定位器一致，否则自检会误报"未挂上"
    ///   （V0.4.2 踩过：`AccessTools.Method` 只查 public，
    ///     而 `ItemMouseDragHandler.EndDrag` 在 dump.cs 里是 private，
    ///     自检报 3/4 但补丁其实挂上了）。
    ///   ★ V1.4.11：EndDrag 已删，现在剩下的三个都是 GameItem 的 public 实例方法，
    ///     但**保留"翻 private"的口径** —— 游戏一更新，某个方法变 private 是常事，
    ///     到时候自检不能又变成"误报源头"。
    /// </summary>
    private static System.Reflection.MethodInfo FindTargetMethod(Type owner, string name)
    {
        if (owner == null) return null;
        var all = AccessTools.GetDeclaredMethods(owner);       // 含 private / static
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] != null && all[i].Name == name) return all[i];
        }
        return AccessTools.Method(owner, name);                // 兜底走继承链
    }

    /// <summary>
    /// 交互补丁自检（启动时 / F9 时调用）。三个方法任一打不上 → 交互必然失效，必须报错。
    /// ★ 必须是**实例方法**：HarmonyInstance 是 MelonBase 的实例属性。
    ///
    /// ★★★ V0.4.3 新增签名校验：
    ///   光看"挂上了"不够 —— V0.4.0/V0.4.1 四个补丁全挂上了，
    ///   但参数名写错导致 target 恒null，实际一次都没接管过。
    ///   → 现在把目标方法的真实参数个数一起打出来，肉眼可核。
    /// </summary>
    internal void SelfCheckInteractPatches()
    {
        //★★★ V1.4.11：EndDrag 补丁整类删除后，**必需 == 全部 == 3**。
        //   历史上这里长期挂着"4 个目标但只要求 3 个"的错配（V1.0.8 修过一次），
        //   根因是有一个"故意停用的桩"混在必需清单里 —— 现在桩没了，错配不可能再发生。
        string[] targets =
        {
            "GameItem.MayTarget",
            "GameItem.CanTarget",
            "GameItem.Target",
        };
        const int RequiredCount = 3;

        int ok = 0;
        //★★★ V1.4.11 优化：只有**真的有补丁没挂上**时才拼诊断详情。
        //   原来无论成败都 new StringBuilder + 每目标一行 AppendLine，
        //   启动固定刷 4 行。现在正常路径只打一行汇总，异常路径才展开细节 ——
        //   自检的价值是"出问题时给出可排障的信息"，不是"每次启动都报一遍正常"。
        var sb = new System.Text.StringBuilder();
        foreach (var name in targets)
        {
            var parts = name.Split('.');
            Type owner = ResolveType(parts[0]);
            // ★ 用 FindTargetMethod（翻 private），口径与 Harmony 一致
            System.Reflection.MethodInfo mi = FindTargetMethod(owner, parts[1]);
            bool patched = false;
            if (mi != null)
            {
                // Harmony.GetPatchInfo 是静态方法（全局补丁表），
                // 不走 MelonBase.HarmonyInstance（那是已过时的实例属性）。
                var info = HarmonyLib.Harmony.GetPatchInfo(mi);
                patched = info != null
                         && (info.Prefixes != null && info.Prefixes.Count > 0
                             || info.Postfixes != null && info.Postfixes.Count > 0);
            }

            if (patched) { ok++; continue; }

            // ── 只有失败项才进这段（原来是无条件拼行）──
            int argc = 0;
            if (mi != null)
            {
                var ps = mi.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!ps[i].Name.StartsWith("__")) argc++;
                }
            }
            sb.AppendLine("      " + name + " → "
                + (mi == null ? "⚠ 找不到目标方法"
                              : "⚠ 未挂上")
                + "（显式参数 " + argc + " 个）");

            //★★★ V1.0.8 诊断：未挂上时把**为什么**打出来，别只报"未挂上"。
            //   MEMORY 铁律：静默/含糊的失败日志会让排障从"10 秒"变成"半天"。
            if (mi != null)
            {
                sb.AppendLine("          ↳ 目标方法: " + mi.DeclaringType?.FullName
                        + "." + mi.Name + "(" + mi.GetParameters().Length + "参)"
                        + "  static=" + mi.IsStatic
                        + "  IsPublic=" + mi.IsPublic
                        + "  声明Asm=" + (mi.DeclaringType?.Assembly?.GetName().Name ?? "?"));
                // 该类上 Harmony 到底注册了什么？
                try
                {
                    var all2 = AccessTools.GetDeclaredMethods(mi.DeclaringType);
                    int cnt = 0;
                    for (int i = 0; i < all2.Count; i++)
                        if (all2[i] != null && all2[i].Name == mi.Name) cnt++;
                    sb.AppendLine("          ↳ 同名方法数（含重载）= " + cnt);
                }
                catch (Exception ex) { RLog.Swallowed("SelfCheckInteractPatches", ex); }
            }
        }

        // ★ 三个全挂上才算"能交互"。
        MarkPatchesReady(ok >= RequiredCount);

        if (ok >= RequiredCount)
        {
            RLog.Msg("交互补丁 " + ok + "/" + RequiredCount
                   + " 全部就绪 ✓（MayTarget / CanTarget / Target）");
        }
        else
        {
            RLog.Error("★★ 必需交互补丁缺失（"
                          + ok + "/" + RequiredCount + "）—— 左键点物品会没反应。\n" + sb);
        }
    }
}