// ═══════════════════════════════════════════════════════════
// ★ 真实打印机 RealPrinter —— Mod 入口
//
// 【一句话】只改商品真值的打印机：官方档位目录里有的类别都能改，真值假值一起写。
//
// 【与 BlackPrinter 共存】两者 identifier / 专属 tag 全部错开，
//   可以同时安装、互不抢机器（详见 Core 里的常量说明）。
// ═══════════════════════════════════════════════════════════

using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(RealPrinter.RealPrinterMod), "RealPrinter", RealPrinter.RealPrinterMod.SuiteVersion, "hetao", null)]
// ★★★ MelonGame 的公司名必须**逐字等于**游戏实际值 ——
//   写错的后果不是"报错"，而是**整个 mod 被 MelonLoader 静默拒绝加载**：
//   没有日志、没有异常、cfg 段不会出现、功能一点都没有。
//   （V1.0.0 我写成 "Kinda Brave"，游戏实际是 "Questing Goose Studio"
//     ——串到了别的游戏的名，从没核实过 → 核桃按 F6 毫无反应。）
//   → 正确值已从 BlackPrinter（实机跑通过）核对。
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]
[assembly: MelonColor(76, 175, 160, 255)]

namespace RealPrinter;

public sealed partial class RealPrinterMod : MelonMod
{
    /// <summary>单例（Machine/Interact 里用 Self?.Log 打日志）。</summary>
    internal static RealPrinterMod Self;

    private static bool _patched;

    public override void OnInitializeMelon()
    {
        Self = this;

        // ★ cfg 必须先挂：后面费用表 / 许可判定都读它
        try { InitPreferences(); }
        catch (Exception e) { RLog.Error("cfg 挂载异常（用默认值继续）: " + e.Message); }

        // —— 官方档位自检 / 许可自检（V1.5.2 起★★ 延迟 ★★执行，不在这里跑）——
        //★★★ 2026-10-16 事故（实机实锤）：这两段自检原来就在这里（启动期）跑，
        //   而它们（`BuildConditionCatalogOnce` 的 56 个工厂 + `SelfCheckLicense`
        //   读 16 张分类静态表）都会触碰游戏 `ItemConditionList` —— 于是本 mod
        //   成了**全场第一个**触发该类 .cctor 的代码。而该 .cctor 内部要取
        //   本地化文本（LocHelper.Get → Addressables），启动早期本地化未就绪
        //   → cctor 抛 `invalid operation handle` 且**构造失败被永久缓存**
        //   （同 .NET 语义，TypeInitializationException）→ 本地化 / 物品系统
        //   连锁瘫痪：主菜单变英文、读档失败。
        //   【为什么不能靠"失败重试"救】类型构造失败在 il2cpp 里是一次性的，
        //   本场无法补救 —— 唯一修法就是**不去当第一个**。
        //   【搬去哪了】两段自检整体搬进 `ConditionCatalog.EnsureCatalog` 的
        //   ★懒建成功路径★（`RunPostBuildSelfChecksOnce`）：首次建表发生在玩家
        //   进游戏后第一次触发许可/开窗，那时游戏早已自己初始化完该类。
        //   日志内容一字未改（①官方档位自检 ②许可类别自检，Error/Warning
        //   分级照旧），只是从"启动日志末尾"挪到"目录首次就绪之后"。

        // —— 拖动采样机制（V2-7）——
        // ★ "这套机制到底有没有在跑"必须可查：拖动采样一旦静默失效，
        //   拖动让路与延迟开窗会**一起**失效，而且一行日志都没有（最难查的那类故障）。
        // ★★ 实机教训（2026-10-16）：原来这里在启动时打一行 Detail —— 结果是
        //   "启动时 VerboseLog 还关着"就整行消失（核桃是进游戏后才在面板里打开的）。
        //   → 那行**搬进 SampleDragState**：第一次成功采到拖动状态、且日志真的开着时打。
        //     晚一点报，总比永远不报强。

        // —— 硬编码偏移量自检（代码评审 P0-2）——
        // ★ 这是全项目**唯一一处"游戏升级就会断"的地方**：
        //   IdOffset=0x1C0 是按当前 dump 写死的裸偏移。
        //   游戏一更新 → 读出别的字段 → 静默失效（不报错、不崩溃、功能直接没了）。
        //   → 拿游戏目录里的物品当探针：托管属性 vs 裸偏移，两个字符串逐字符比。
        //     不需要"知道新偏移是多少"，所以游戏更新后依然有效。
        // ★ V1.4.13：SelfCheckOffsets 返回的每一项都是**结论**，不是"没验成"。
        //   拿不到探针的情况它内部已降级为 Detail（静默），不再混进这个列表 ——
        //   否则"这次没验成"会被打成 Warning，看着像故障，天天刷。
        try
        {
            var offBad = SelfCheckOffsets();
            foreach (var b in offBad) RLog.Warning("偏移: " + b);
        }
        catch (Exception e) { RLog.Error("★★ 偏移自检异常: " + e.Message); }

        // —— 交互补丁自检 ——
        //★★★ V1.0.2 修：**这一行在裁剪时被漏掉了**，
        //   导致 `MarkPatchesReady()` 从没被调用 → `_patchesReady` 恒为 false
        //   → 建机时报"★★ 交互补丁没挂上 —— 左键点物品不会有任何反应"。
        //   而补丁其实**挂得好好的**（HarmonyInstance.PatchAll 已在上面执行），
        //   纯属**误报**。误报比不报更坏：玩家会去查一个不存在的故障。
        try { SelfCheckInteractPatches(); }
        catch (Exception e) { RLog.Error("交互补丁自检异常: " + e.Message); }


        // —— Harmony 补丁 ——
        // ★ 教训（BlackPrinter 首跑）：[HarmonyPatch] 写在**类**上时
        //   AccessTools.GetDeclaredMethods 挖不到 → 必须用 PatchAll() 无参全量扫描。
        // ★★ V1.1.2 修：**这一块必须排在横幅之前**。原来横幅在前、PatchAll 在后
        //   → 横幅读 `_patched` 时它还是 false → **每次启动都误报"Harmony 补丁未挂"**；
        //   同时外观探针的"劫持命中 N 次"永远是 0（Postfix 还没挂上）。
        //   → 顺序铁律：数据准备 → **打补丁** → 横幅（横幅是总结论，必须在事实之后）。
        try
        {
            HarmonyInstance.PatchAll();
            _patched = true;
            RLog.Msg("Harmony 补丁已挂 ✓");

            // ★★ V1.4.0：供货商上货补丁自检 —— "挂没挂"必须明摆着（同上，防静默跳过）。
            // ★★★ V1.4.11 修文案：原来这里写「改用 F6 直领」，
            //   但 F6 直领从 V1.4.6 起就默认关闭且 cfg 里已无开关 —— 玩家按了也没反应。
            //   告诉他一条**真的能走的路**（MEMORY 铁律：报错必须给出路，
            //   指一条死路比不指更糟 —— 玩家会以为是自己的问题）。
            try
            {
                var mi = AccessTools.Method(typeof(StoreClientList), nameof(StoreClientList.PlaceSupplierInventory));
                if (mi == null)
                    RLog.Error("★★ 供货商上货：找不到 StoreClientList.PlaceSupplierInventory（游戏版本变了？）——"
                             + "**这个版本没法自动获得打印机**。请把游戏退回原版本，"
                             + "或手动改存档放入一台真实打印机。");
                else
                {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mi);
                    int n = pi == null ? 0 : pi.Postfixes.Count;
                    if (n > 0)
                        RLog.Msg("供货商上货补丁已挂 ✓（PlaceSupplierInventory Postfix × " + n
                               + "）—— 供货商每次摆货都会带一台打印机"
                               + (Config.SupplierStock ? "" : "　【当前 cfg 已关】"));
                    else
                        RLog.Error("★★ 供货商上货补丁没挂上 —— 供货商不会带打印机，"
                                 + "也就没有正常获取途径。"
                                 + "（多半是补丁类漏了 [HarmonyPatch] 特性）");
                }
            }
            catch (Exception e) { RLog.Warning("供货商上货自检异常: " + e.Message); }

            // ★★ V1.4.12：物品池注册补丁自检 —— 与鼠打印机同款机制。
            //   注册失败不影响 F6 直领 / 供货商上货（它们走自己造物品的路径），
            //   但会让"通过游戏目录按 id 取机器"的路径失效，必须报警。
            try
            {
                var mi = AccessTools.Method(typeof(ModItemDirectory), "InitDirectory");
                if (mi == null)
                    RLog.Warning("物品池注册：找不到 ModItemDirectory.InitDirectory"
                               + "（游戏版本变了？）—— real_printer 无法注册进物品池。");
                else
                {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mi);
                    int n = pi == null ? 0 : pi.Postfixes.Count;
                    if (n > 0)
                        RLog.Msg("物品池注册补丁已挂 ✓（ModItemDirectory.InitDirectory Postfix × " + n + "）");
                    else
                        RLog.Warning("⚠ 物品池注册补丁没挂上 —— 游戏目录里不会有 real_printer。");
                }
            }
            catch (Exception e) { RLog.Warning("物品池注册自检异常: " + e.Message); }

            // ★★ V1.4.11：诊断探针自检整段删除（连同两个 Harmony 探针补丁）。
            //   【为什么能删】V1.4.6 的诊断使命已完成 —— 根因查明
            //   （柜台 18×4=72 格，货占满后剩的空位不足 6 格且不连续，
            //     而原版形状 2×3=6 格 → TryAcceptAllOrDestroy 静默销毁），
            //     对策（压到 1×2）已实机验证成功。
            //   【为什么删比留好 —— 性能】
            //     两个探针是**常驻 Harmony 补丁**，永不卸载：
            //       · AddDirectSellingItemToTable Prefix  ← 供货商每次摆货都过
            //       · TryAcceptAllOrDestroy Postfix       ← 每件货都过
            //     即使 VerboseLog 关着，Harmony 仍要做一次委托解引用 + 进方法。
            //     且探针为拿柜台状态会调 MayHaveValidInventorySlot / TryFindOneValidInventorySlot
            //     / CheckShapePlacement（还带"逐件忽略现有货"的重试，最多 N×M 次）
            //     —— 那才是真正的开销来源。
            //   → 诊断是一次性的，留着就是永久税。

            // ★★ V1.4.7 精灵缩放补丁自检 —— 这是**正式功能**（图要缩到和格子匹配），
            //   不是探针，所以**没挂上就是故障，必须报警**。
            try
            {
                var mi = AccessTools.Method(typeof(GameItemElement),
                                           nameof(GameItemElement.ResolveSpriteByName));
                if (mi == null)
                    RLog.Error("★★ 精灵缩放：找不到 GameItemElement.ResolveSpriteByName（游戏版本变了？）——"
                             + "机器图会比格子大（显示溢出），摆货本身不受影响。");
                else
                {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(mi);
                    int n = pi == null ? 0 : pi.Postfixes.Count;
                    if (n > 0)
                        RLog.Msg("精灵缩放补丁已挂 ✓（ResolveSpriteByName Postfix × " + n
                               + "）—— 机器图 " + "2×3 → 1×2，与占的格子一致");
                    else
                        RLog.Error("★★ 精灵缩放补丁没挂上 —— 机器图会比格子大（显示溢出）。"
                                 + "（多半是补丁类漏了 [HarmonyPatch] 特性）");
                }
            }
            catch (Exception e) { RLog.Warning("精灵缩放自检异常: " + e.Message); }

            // ★★ V1.4.9 删掉了这里的 GetShrunk() 预热调用。
            //   【为什么删 —— V1.4.8 缩图完全没生效的根因】
            //     预热在这一刻跑，图集**还没加载完** → 拿不到原图 → null；
            //     而 GetShrunk 里 `_tried = true` 又是**永久锁** →
            //     之后所有调用都直接 return null，图永远不缩。
            //   （这是我亲手写的静默失效：日志一行都没有，功能就是不生效。）
            //   【现在的做法】缩图只在 Postfix 里**按需**触发 ——
            //     那一刻游戏自己刚把原图解析出来（__result 非 null），
            //     时序天然正确；失败也只 Warning + 3 秒后重试，不锁死。
        }
        catch (Exception e)
        {
            RLog.Error("Harmony 补丁挂载异常（左键点物品不会有反应）: " + e.Message);
        }

        // —— 横幅（**必须排在目录建立 + 补丁挂载之后**）——
        // ★ 教训①：横幅要打"许可类别"，而许可名读游戏静态表（.cctor 填充）
        //   → 早于目录建立会读到空表，横幅显示"0 类"，玩家以为许可没生效。
        // ★ 教训②（V1.1.2）：也必须晚于 PatchAll，否则 `_patched` 还是 false
        //   → 每次都误报"Harmony 补丁未挂"，外观探针命中数也永远是 0。
        //   **横幅是"总结论"，必须排在所有事实之后。**
        try { LogBanner(); }
        catch (Exception e) { RLog.Error("横幅异常: " + e.Message); }
        // —— 读档重挂钩子 ——
        //★ 必须挂：读档后 IL2CPP 对象指针全变，不重挂白名单就会
        //   "读档后左键点物品完全没反应"（静默失效，BlackPrinter 踩过）。
        try
        {
            var m = AccessTools.Method(typeof(PlayerStore), "LoadGame");
            if (m != null)
            {
                HarmonyInstance.Patch(m,
                    postfix: new HarmonyMethod(typeof(RealPrinterMod), nameof(OnStoreLoadedPostfix)));
                RLog.Msg("读档重挂钩子已挂。");
            }
            else RLog.Warning("⚠ 未找到 PlayerStore.LoadGame —— 读档后交互会失效，请重启游戏。");
        }
        catch (Exception e) { RLog.Warning("读档钩子异常: " + e.Message); }
    }

    /// <summary>读档后：重建指针白名单。</summary>
    private static void OnStoreLoadedPostfix()
    {
        try { Self?.RepairMachinesAfterLoad(); }
        catch (Exception e) { RLog.Warning("读档修复异常: " + e.Message); }
    }

    /// <summary>
    /// ★★ V1.4.12：恢复 F6 直领热键轮询。
    ///   关着 cfg 时 —— **首行 return，零开销**（只读一次静态 bool）。
    ///   关闭时按 F6 无反应且不弹提示（核桃 2026-10-05 拍板）。
    ///
    /// 【历史】V1.4.6~V1.4.11 期间本覆写曾被删除（那段时间 F6 直领停用，
    ///   PollHotkeys 空转）。V1.4.12 恢复 F6 + cfg 开关后重新启用。
    ///   V1.4.15 修正：删除下方与本覆写矛盾的过时注释（它说"已删除"，
    ///   代码却挂着 —— 读者会以为覆写是意外残留）。
    /// </summary>
    public override void OnUpdate()
    {
        // ★★ V1.5.0：F6 与拖动落点两条每帧路径**各自独立判空**。
        //   【为什么不能像 V1.4.x 那样 `if (!Config.EnableF6Give) return;` 在第一行】
        //     那会让"拖动落点队列"在 F6 关着（默认就是关）时**永远得不到执行**
        //     → 拖动功能整体失效。两条路径互不依赖，必须分开判。
        //   【性能】两道闸都只是读静态 bool；两个功能都关着时每帧开销 = 两次 bool 读。
        if (Config.EnableF6Give)
        {
            try { PollHotkeys(); }
            catch (Exception ex) { RLog.Swallowed("OnUpdate", ex); }
        }

        // ★ 拖动状态每帧采样一次（必须排在 TickPendingDrop 之前，见其注释）
        try { SampleDragState(); }
        catch (Exception ex) { RLog.Swallowed("OnUpdate", ex); }

        // 拖动落点：内部 `_pendingDrop == null` 时立即返回（常态零开销）
        try { TickPendingDrop(); }
        catch (Exception ex) { RLog.Swallowed("OnUpdate", ex); }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        try { RepairMachinesAfterLoad(); }
        catch (Exception ex) { RLog.Swallowed("OnStoreLoadedPostfix", ex); }
    }

    /// <summary>★ 打包时补齐指针白名单。读档切换场景后指针会变。</summary>
    public override void OnApplicationQuit() { try { ClearMachineRegistry(); } catch { } }

    /// ★★ V1.0.5 横幅精简（核桃要求：只保留基本的初始加载）。
    ///   之前 7 行、含余额/机器价值/键位数值等细节；现在压到 5 行，
    ///   只留玩家**真需要知道**的：版本、支持哪几类、怎么用、报错。
    ///   ★ 详细配置一律去 ModManager 面板看，不占日志。
    private void LogBanner()
    {
        RLog.Msg("══════ 真实打印机 RealPrinter v" + SuiteVersion + " ══════");

        // ★ V1.5.0：许可从 4 类扩到 16 类 —— 再把 16 个中文名全拼进一行横幅
        //   就成噪音了（核桃要求"少输出工作日志"）。
        //   → 横幅只报**结论**（几类、怎么触发）；完整清单压进 Detail（排障才开）。
        // ★★ V1.5.2：横幅**不许再现场读许可目录**（`License.AllAllowed` →
        //   EnsureCatalog → 游戏 ItemConditionList）—— 横幅跑在启动期，读了就是
        //   2026-10-16 事故的第二个触发点（详见 OnInitializeMelon 顶部注释）。
        //   → 改成静态文案；类别计数/明细由延迟自检在目录首次就绪后报。
        RLog.Msg("　支持：官方档位目录里的全部类别"
                + "（香烟 / 化学品 / 金属锭 / 注射器 / 印花票 / 效期 / 酒 …，毒药按化学品算；水不支持）");

        RLog.Msg("　触发：" + (Config.DragToUse
                    ? "拖到商品上（开）＋ 装备后左键点（开）"
                    : "装备后左键点（拖动已关）"));
        RLog.Msg("　获取：" + (Config.SupplierStock ? "供货商上货（开）" : "供货商上货（关）"));
        RLog.Msg("　F6 直领：" + (Config.EnableF6Give ? "开（按 F6 领取价值 0 的机器）" : "关"));
        if (Config.SupplierStock)
            RLog.Msg("　★ 供货商上货：开（供货商每次摆库存都会带一台，售价 "
                     + Config.DealerPrice + "）");
        // ★ V1.4.11：删掉「键位值 / 当前余额」这一行。
        //   【为什么删 —— 它有两个问题，第一个是性能】
        //     ① `Pay.Query()` 是**条件求值的实参**：C# 会在调用 `Detail` **之前**
        //        先把 `"键位值 " + HotkeyGive + … + Pay.Query()` 整条拼好。
        //        也就是说 `RLog.Detail` 内部那句 `if (!Config.VerboseLog) return;`
        //        **拦不住它** —— `PlayerStore.Instance.GetCash()` 这次 IL2CPP 调用
        //        照样发生。开关在函数**里面**，可拼串和实参求值在函数**外面**。
        //        ★ 这是"日志开关"最常见的漏洞形态：看起来 gated，实际没 gated。
        //     ② 内容已无意义：F6 直领 V1.4.6 起停用，这行只是报一个没人会按的键值。
        //     （`KeyName()` 仍保留 —— 另两处在用，且 MEMORY 铁律要求键名绝不手算。）
        //   ★ 需要临时查键值/余额时，临时把下面这行加回来（**不要**无脑加回原版）：
        //       if (Config.VerboseLog) RLog.Detail("键位值 " + HotkeyGive + " 余额=" + Pay.Query());
        //     —— 判据写在**调用点**，参数就只在这条分支里求值。

        // ★ V1.4.8：机器形状压成 1×2，图也跟着缩到 1×2（否则格子小、图大，视觉对不上）。
        // ★ V1.4.9：这一行**改成 Msg**（不受 VerboseLog 控制）。
        //   原因：缩图是"看得见"的功能，玩家会直接问"怎么还是 3×2 的大图"；
        //   如果只在 VerboseLog 里说"未生成"，玩家永远不知道自己看到的是降级状态。
        //   缩图成功时横幅会打"已生成"（第一次成功另有 Msg），失败时报原因。
        RLog.Msg("　机器外观：" + RealPrinterSpriteRescale.Report()
            + "（形状 1×2 格 = 2 格，柜台放得下）");

        RLog.Msg("　用法："
            + (Config.DragToUse
                ? "把机器拖到商品上（或装备后左键点）→ 选档位"
                : "手持机器 → 左键点商品 → 选档位"));
        if (!_patched) RLog.Error("★★ Harmony 补丁未挂 —— 左键点物品不会有任何反应。");
        // ★ 被降级处理的异常：默认不打（否则拖动时会刷屏），
        //   但**一旦累计到值得警惕的数量就报出来** —— 让"静默失效"变可见。
        if (RLog.SwallowedCount >= 100)
            RLog.Warning("运行中已有 " + RLog.SwallowedCount
                       + " 次异常被降级处理 —— 功能可能不完整，勾上 VerboseLog 看详情。");
        // 只在开着的状态下提醒一句，**不要**每次都打"详细日志已关"。
        if (Config.VerboseLog) RLog.Msg("　（详细日志已开，操作流水会一并显示）");
    }

    // ★★★ V1.4.11：`KeyName(int)` 辅助函数已删除（它原本只有两个调用点，
    //   都在这次清理里一起没了 —— 一个没有调用者的函数就是死代码）。
    //   【知识没有丢，只是搬了地方】要显示键名时**绝不手算键值、绝不手写
    //     F1~F12 的 switch**（MEMORY 铁律：294 曾被算成 F13，导致 F10 热键
    //     静默失灵、核桃白等一轮）。正确写法就一句：
    //       string name = ((KeyCode)key).ToString();
//     Unity 的 KeyCode 枚举自带名字（F1..F12 / Alpha0 / Mouse0 …），
    //     手写 switch 既冗余又必漏（数字键、小键盘、方向键全都没有）。
    //     拿到纯数字（如 "291"）或空 = 键值非法，照实显示数值，**绝不猜**。
}
