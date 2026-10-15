// ═══════════════════════════════════════════════════════════
// ★ 指针安全层 —— 裸指针闸门 / 偏移自检 / 安全取标识
//
// 【本文件管什么】
//   「这个对象现在能不能安全地读」这一件事。不建机、不做业务判定。
//
// 【其他部分在哪】（V1.2.0 从 Machine.cs 拆出，逻辑逐字未改）
//   · 指针白名单与存档扫描  → RealPrinterMod.MachineRegistry.cs
//   · 建机与交互落地        → RealPrinterMod.MachineBuilder.cs
//   · 官方档位目录与写入    → RealPrinterMod.ConditionCatalog.cs / FeatureWriter.cs
//   · Harmony 交互补丁      → RealPrinterMod.Interact.cs
//
// 【为什么单独成文件】这几条函数是全项目**唯一会摸裸内存**的地方，
//   也是最容易被"顺手优化"掉防线的代码。单独放 = 改它的人一眼看到上下文。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// ★★ 裸指针闸门（V0.4.2，照抄 ContainerSuite.DIYUpgrade 的 IdFieldNull）。
    ///
    /// 【为什么必须有】AccessViolationException 是**不可捕获**的 ——
    ///   try/catch 抓不住它，进程直接死。所以判据必须**完全不进 IL2CPP**。
    ///
    /// 【★★★ V0.4.1 的错判 —— 我读错了 dump.cs】
    ///   我看到 dump.cs:3002 `public abstract string identifier { set; }` 就断定
    ///   "identifier 是计算属性、没有 backing field"，于是改用 state 当探针。
    ///   ★★★ **那是父类 `GameItemFunc` 上的抽象声明，不是 GameItem 的实现。**
    ///   事实（dump.cs:57814，GameItem 类从 57796 起）：
    ///     private string <identifier>k__BackingField;      // 0x1C0
    ///     private string <identifierName>k__BackingField;   // 0x1C8
    ///     public string get_identifier()                // dump.cs:58018
    ///   → V0.4.1 用 state(0x1B0) 当探针 → **半初始化对象 state 也有值 → 照样放行 → 照崩**。
    ///
    /// 【正确做法】判据只读裸指针 + 只做字节比较，**一次 IL2CPP 都不进**。
    /// </summary>
    private const int IdOffset = 0x1C0;      // private string <identifier>k__BackingField
    private const int StateOffset = 0x1B0;    // private TagSystem <state>k__BackingField

    /// ★★ V1.4.15：偏移自检"拿不到探针"是否已报警过 —— 首次 Warning，后续 Detail。
    ///   详见 SelfCheckOffsets 里 probe==null 分支的说明。
    private static bool _offsetProbeWarned;

    // ═══════════════════════════════════════════════════════════
    // ★★★ 硬编码偏移量自检（代码评审 P0-2，我认为这是全项目**最有价值**的一条）
    //
    // 【问题】IdOffset = 0x1C0 / StateOffset = 0x1B0 是**按当前游戏版本**的 dump
    //   写死的。游戏一更新、字段顺序变了 → 读出来的是别的字段
    //   → **静默失效**（MEMORY 铁律最怕的那类：不报错、不崩溃、功能直接没了）。
    //
    // 【为什么必须做】这个偏移是**唯一一处**"游戏升级就会断"的地方
    //   （其余 56 个官方档位工厂、许可 category 都是按**名字**取的游戏自己的数据，
    //     版本变了照样能用）。
    //   → 游戏升级后如果没人发现，mod 会安静地不工作，玩家只会以为坏了。
    //
    // 【校验怎么做到"零猜测"】
    //   不去猜"0x1C0 现在是什么字段"，而是**反过来验证**：
    //     ① 用**托管属性**写/读 `it.identifier`（权威答案，一定对）
    //     ② 用**裸偏移**读 IdOffset 处的字符串
    //     ③ 两个字符串**逐字符比对**
    //   一致 → 偏移仍然正确；不一致 → **游戏变了，立刻报出来**。
    //   ★ 这个校验不需要"知道新偏移是多少"，所以游戏更新后依然有效。
    //
    // 【★★★ V1.4.13 实机翻车：原来这里根本跑不起来】
    //   旧版拿「玩家存档里已造的真印」当样本，而自检是在 `OnInitializeMelon`
    //   里调的 —— 那一刻玩家**通常一台机器都没有**（新档、或还没按过 F6）。
    //   实机日志（2026-10-09）实锤：
    //     [真印] 偏移契约发现没找到实例，放弃验证（真印的 F6 送出一台不会自动漂移）
    //   ⇒ 自检恒定跳过，等于**形同虚设**：恰恰是它要防的"游戏更新 → 静默损坏"
    //   场景下毫无保护。
    //
    // 【★ V1.4.13 第一次尝试也失败了 —— 换成 CreateEmptyItem() 造探针】
    //   实机日志（2026-10-09 20:54）实锤：
    //     [WARNING] [真印] 偏移: 偏移自检异常: Il2CppException: NullReferenceException
    //   原因：`ItemDirectory.CreateEmptyItem()` **依赖游戏目录（ModItemDirectory）就绪**，
    //   而本自检排在 `OnInitializeMelon` 的早期 —— 那时目录还没初始化完
    //   （`MachineBuilder` 里同一个 API 是**建机时**才调的，那时目录已就绪；
    //   它前面紧挨着一条 "目录里没有 labeler" 的 Warning 就是这个前提的证据）。
    //   ⇒ **换了个来源，换了个时序依赖，并没有解决问题。**
    //
    // 【★ V1.4.13 第二次尝试（现在这版）：拿游戏目录里的物品当探针】
    //   改用 `DirectoryMaster.Item(<官方目录里必然存在的 id>, true)` 取一个**游戏自己的**
    //   物品，直接读它的 identifier —— **不写入任何字段**。
    //   为什么这样才对：
    //     ① `DirectoryMaster.Item` 是**读**目录（与建机同一个 API，但建机是在游戏跑起来后
    //        才调；自检在启动早期仍可能早于目录构建）。—— 为此加了两级候选：
    //        先试目录里的物品，再退到"游戏必然持有的物品"，都失败才报跳过（见下方实现）。
    //     ② **不写 identifier** —— 写字段需要 setter 正常，而空物品上 setter 可能抛
    //        NRE（这正是第一次尝试崩的地方）。只读就完全避开这一类风险。
    //     ③ 基准照样确定：官方物品的 id 是我们已知的常量（`save_bag` 等），
    //        拿托管属性读出来、再用裸偏移读同一个字符串比对 —— 逻辑与原设计完全一致，
    //        仍然是"不需要知道新偏移是多少"的自证。
    // ═══════════════════════════════════════════════════════════
    internal static List<string> SelfCheckOffsets()
    {
        var bad = new List<string>();

        // 探针候选：按"最可能拿得到"排序。都是游戏自己的物品，我们**只读不写**。
        //   save_bag —— 本 mod 本身就依赖它（存档袋），几乎必然先于我们初始化完成。
        //   toilet_paper —— 目录里最基础的消耗品，跨版本极稳定。
        //   labeler —— 建机模板，同一处 DirectoryMaster 调用，兜底。
        string[] probeIds = { "save_bag", "toilet_paper", TemplateItemId };

        GameItem probe = null;
        string usedId = null;
        foreach (var pid in probeIds)
        {
            if (string.IsNullOrEmpty(pid)) continue;
            try
            {
                var cand = DirectoryMaster.Item(pid, true);
                if (cand == null) continue;
                probe = cand;
                usedId = pid;
                break;
            }
            catch (Exception e) { RLog.Swallowed("SelfCheckOffsets.pick." + pid, e); }
        }

        if (probe == null)
        {
            // ★★ V1.4.15：从 Detail 升级为"首次 Warning + 后续 Detail"。
            //   【为什么不能一直 Detail】偏移是全项目**唯一一处"游戏升级就会断"**
            //     的地方。旧版 Detail 静默 → 游戏更新后本自检**每局都跳过**，
            //     日志里一行告警都没有，玩家只看到"功能莫名失效"，
            //     正是核桃铁律最忌的静默失效。
            //   【为什么不能每次都 Warning】每次启动都报 Warning 也会变噪音。
            //     折中：**首次**报 Warning（明确说"当前游戏偏移未校验"），
            //     后续同类静默为 Detail。
            //   【恢复方法】玩家进一次存档 / 按一次 F6 后，探针就存在了 ——
            //     届时重启游戏即可看到"偏移自检通过"。
            if (!_offsetProbeWarned)
            {
                _offsetProbeWarned = true;
                RLog.Warning("偏移自检**本次未验证**：启动早期拿不到探针物品（"
                          + string.Join("/", probeIds) + " 都没取到）。"
                          + "这不代表偏移失效，但**当前游戏版本的偏移未经校验**。"
                          + "若功能异常，请进一次存档后重启游戏，届时会自动复查。");
            }
            else
            {
                RLog.Detail("偏移自检跳过（本次仍未取到探针，已提示过一次）。");
            }
            return bad;
        }

        try
        {
            // ① 托管属性读出的权威值（官方物品的 id，内容确定 → 基准可靠）
            string truth = null;
            try { truth = probe.identifier; } catch (Exception ex) { RLog.Swallowed("SelfCheckOffsets", ex); }
            if (string.IsNullOrEmpty(truth))
            {
                RLog.Detail("偏移自检跳过：探针「" + usedId + "」读不到 identifier（对象异常），本次未验证。");
                return bad;
            }

            // ② 裸偏移读 IdOffset 处的字符串
            string raw = ReadIl2CppStringAt(probe, IdOffset);

            // ③ 逐字符比对 —— 这一步才是真正的结论
            if (raw == null)
            {
                bad.Add("★★ IdOffset=0x" + IdOffset.ToString("X")
                      + " 读不出字符串 —— **偏移已失效**（游戏更新过？）。"
                      + " 探针「" + usedId + "」的托管属性读到的是「" + truth + "」，裸偏移却读不到。"
                      + " 请重新 dump GameItem 确认 <identifier>k__BackingField 的偏移。");
            }
            else if (!string.Equals(raw, truth, StringComparison.Ordinal))
            {
                bad.Add("★★ IdOffset=0x" + IdOffset.ToString("X") + " **偏移已失效**："
                      + "探针「" + usedId + "」裸偏移读到「" + raw + "」，但托管属性是「" + truth + "」。"
                      + " 游戏更新过字段顺序，请重新 dump GameItem 确认偏移。");
            }
            else if (Config.VerboseLog)
            {
                // 通过：默认不打（每次启动都跑，不该占常态日志一行）。
                RLog.Detail("偏移自检通过：IdOffset=0x" + IdOffset.ToString("X")
                          + " 在探针「" + usedId + "」上读到「" + truth + "」，与托管属性一致 ✓");
            }

            // StateOffset 只验"能不能读出一个像样的对象指针"（不比对内容）：
            //   TagSystem 没有等价的托管读法，且我们并不依赖它的内容，只判非空。
            var sp = ReadFieldPtr(probe, StateOffset);
            if (sp == IntPtr.Zero)
            {
                bad.Add("★ StateOffset=0x" + StateOffset.ToString("X")
                      + " 读回空 —— 该字段可能已移位（TagSystem 读不到）。"
                      + " 不影响主流程，但相关功能可能失效。");
            }
        }
        catch (Exception e)
        {
            bad.Add("偏移自检异常: " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            // 探针是游戏目录里的物品，**不是我们造的东西** —— 不能销毁、不该改字段，
            // 这里只断开托管引用，避免它被后续逻辑误当成"我们的机器"。
            probe = null;
        }
        return bad;
    }

    /// <summary>
    /// ★ V1.0.9：按裸偏移读一个 IL2CPP string（不手写布局）。
    /// 【为什么必须用官方 API】MEMORY 铁律：
    ///   IL2CPP string 布局是 klass(8) monitor(8) length(4)@0x10 **chars@0x14**，
    ///   末尾 null(2)。chars 写 0x18 会**整体右移一个字符**，
    ///   日志里出现 `ack_printer T` 而功能照样能跑 = 最隐蔽的静默偏差。
    ///   → 一律 `il2cpp_string_length` + `il2cpp_string_chars`。
    /// </summary>
    private static string ReadIl2CppStringAt(GameItem it, int offset)
    {
        IntPtr sp = ReadFieldPtr(it, offset);
        if (sp == IntPtr.Zero) return null;
        try
        {
            int len = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_length(sp);
            if (len <= 0 || len > 512) return null;
            // ★ il2cpp_string_chars 返回的是 **char\***（不是 IntPtr），
            //   且它指向 UTF-16 字符数组 —— 必须用 unsafe 取址再转 IntPtr。
            unsafe
            {
                char* ch = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_chars(sp);
                if (ch == null) return null;
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    new IntPtr(ch), len);
            }
        }
        catch (Exception ex) { RLog.Swallowed("ReadIl2CppStringAt", ex); return null; }
    }

    /// <summary>
    /// ★★ 裸指针读一个字段。
    ///
    /// 【★★★ V0.4.7 重写 —— 原版这里就是崩溃点】
    ///   V0.4.6 的实现只有 `Marshal.ReadIntPtr(owner, offset)` 一句，
    ///   实机在"打印老鼠夹"时 AV：
    ///     Fatal error. System.AccessViolationException
    ///       at ... Marshal_ReadInt32 / ReadFieldPtr / IsTargetEditable
    ///       at ... TryClaim / Patch_Target.Prefix / &lt;EndDrag&gt;d__12.MoveNext
    ///
    ///   【为什么 catch 抓不住】`AccessViolationException` 在 .NET Core 里
    ///   **不作为托管异常传播**（historically 不可捕获，除非启用
    ///   `HandleCorruptedStateExceptions`+LegacyCorruptedStateExceptionsPolicy，
    ///   MelonLoader 没开）→ 进程直接猝死，日志连最后一行都写不完。
    ///   ★★ 所以判据必须**在进 Marshal 之前**把野指针挡掉。
    ///
    /// 【V0.4.7 三道预检】全在纯托管里算，一次 IL2CPP 都不进：
    ///   ① 8 字节对齐 —— IL2CPP 对象头按 8 对齐，未对齐必是野值
    ///   ② 落在进程地址空间内（Windows 用户态 0x10000 ~ 0x7FFFFFFFFFF）
    ///   ③ owner + offset + 8 不溢出、不越界
    /// 拦不住"已释放但地址仍在合法区间"的悬空指针（那只能靠上游判活），
    /// 但那类在实机上表现为读出垃圾指针 → 下一层 IdFieldNull 会挡住。
    /// </summary>
    private static IntPtr ReadFieldPtr(GameItem it, int offset)
    {
        if (it == null) return IntPtr.Zero;
        if (offset < 0 || offset > 0x4000) return IntPtr.Zero;   // 越界偏移直接拒

        IntPtr owner;
        // Pointer getter 走 il2cpp_gchandle_get_target，对象被 GC 会抛
        // ObjectCollectedException（这个是能 catch 的）。
        try
        {
            if (((Il2CppObjectBase)it).WasCollected) return IntPtr.Zero;
            owner = ((Il2CppObjectBase)it).Pointer;
        }
        catch { return IntPtr.Zero; }
        if (!LooksLikeHeapPtr(owner)) return IntPtr.Zero;         // ★ 新增：指针形态预检

        // 不溢出 + 末字节在进程内
        long end = owner.ToInt64() + offset + 8L;
        if (end <= 0 || end > 0x7FFFFFFFFFFL) return IntPtr.Zero;

        try { return Marshal.ReadIntPtr(owner, offset); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 指针形态预检：像不像一个 IL2CPP **对象**指针。
    ///   ① 非空
    ///   ② 落在 Windows 用户态地址范围内（0x10000 起，48 位地址上限）
    ///   ③ 不在"保留页"区间（0xFFFFxxxx 附近的内核/陷阱地址）
    ///
    /// 【★★★ V0.4.8 修正 —— V0.4.7 我加了 8 字节对齐，结果把合法值也毙了】
    ///   `IL2CPP.il2cpp_string_chars()` 返回的是**字符串内部 +0x14 处的 char\***，
    ///   它指向 UTF-16 字符数组，**天生只保证 2 字节对齐**。
    ///   V0.4.7 拿 8 字节对齐去卡它 → 实机日志 `self=<chars?>` 刷满屏，
    ///   合法 id 全被判成"指针可疑"。
    ///   → 现在**只做范围检查，不做对齐检查**：对齐是分配器的性质，
    ///     而野指针靠范围检查已经能挡掉绝大多数（空页/内核地址/超范围）。
    ///     真正要防的"已释放但地址合法"的悬空指针，靠**上游不读**（见 IsTargetEditable），
    ///     不靠这里的启发式。
    ///
    /// ★ V1.4.3：安全模式**固定开启**，不再有可关分支（原 SafePointerMode 开关已删）。
    /// </summary>
    private static bool LooksLikeHeapPtr(IntPtr p)
    {
        if (p == IntPtr.Zero) return false;
        long v = p.ToInt64();
        if (v < 0x10000) return false;                 // 空页/垃圾
        if (v > 0x7FFFFFFFFFFL) return false;          // 超出 48 位用户态
        if ((v >> 40) == 0xFFFF) return false;         // 内核空间/陷阱页
        return true;
    }

    /// <summary>
    /// ★★ V0.4.8 起：本文件**不再有任何"读 IL2CPP string 来判身份"的函数**。
    ///
    ///   保留在这里留档，因为 V0.4.6/0.4.7 的血泪值得记住：
    ///   · V0.4.6 手写 string 布局 `length@0x10 / chars@0x18` —— **chars 真实是 0x14**，
    ///     读 0x18 整体右移一个 UTF-16 字符，日志里 `black_printer` 打成
    ///     `ack_printer\x00T`（hexdump 实锤），而**功能照样能跑**（tag 判据兜住了），
    ///     属最隐蔽的静默偏差。
    ///   · V0.4.7 改走官方 API 后崩在 `IL2CPP.il2cpp_string_length(IntPtr)` ——
    ///     喂进去的 `idp` 本身已是野指针（拖动中物品被销毁）。
    ///   ★★ 结论：**热路径（MayTarget/CanTarget，每秒几千次）里绝不能碰 IL2CPP 字符串**。
    ///     身份判据只能是"比指针"。见 MachineRegistry.cs 的 `_machinePtrs`。
    /// </summary>

    /// <summary>
    /// 安全取 identifier —— ★★ V0.4.8 起**只在确定安全时**才真解字符串。
    ///
    /// 【为什么收紧】这函数被 MayTarget/CanTarget 的日志调用，而那两个补丁
    ///   每秒跑几千次。V0.4.7 实测：为了打一行日志去调 `il2cpp_string_chars`，
    ///   ① 野指针 → AV（`il2cpp_string_length` 崩点）
    ///   ② 合法 char* 被我的对齐检查误判 → 日志刷满 `self=<chars?>`
    ///   日志路径**绝不能成为新的崩溃点**。
    ///
    /// 【V0.4.8 规则】只有命中我们自己的指针白名单（那台机器一定是活对象）
    ///   才真读字符串；其他一律返回 `0x?` 形式的指针摘要 —— 不碰任何 IL2CPP。
    /// </summary>
    private static string SafeId(GameItem it)
    {
        try
        {
            if (it == null) return "<null>";
            long p;
            try
            {
                if (((Il2CppObjectBase)it).WasCollected) return "<collected>";
                p = ((Il2CppObjectBase)it).Pointer.ToInt64();
            }
            catch { return "<err>"; }
            if (p == 0) return "<no-ptr>";

            // ★ V1.2.0：无锁读（_machinePtrs 已是 ConcurrentDictionary）
            if (!_machinePtrs.ContainsKey(p))
            {
                // ★ 非我方物品：只回指针摘要，不进 IL2CPP（热路径大头全在这）
                return "0x" + p.ToString("X");
            }

            // 我方机器：确定是活对象，可以放心解字符串
            IntPtr idp = ReadFieldPtr(it, IdOffset);
            if (idp == IntPtr.Zero) return "<no-id>";
            int len = IL2CPP.il2cpp_string_length(idp);
            if (len <= 0 || len > 512) return "<len" + len + ">";
            IntPtr chars;
            unsafe { chars = (IntPtr)IL2CPP.il2cpp_string_chars(idp); }
            byte[] buf = new byte[len * 2];
            Marshal.Copy(chars, buf, 0, len * 2);
            return System.Text.Encoding.Unicode.GetString(buf);
        }
        catch { return "<err>"; }
    }

    /// <summary>安全取指针（十六进制），用于日志里比对同一个物品。</summary>
    private static string SafePtr(GameItem it)
    {
        try { return it == null ? "-" : "0x" + it.Pointer.ToString("X"); } catch { return "<err>"; }
    }
}
