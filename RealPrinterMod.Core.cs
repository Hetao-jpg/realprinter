// ═══════════════════════════════════════════════════════════
// ★ 真实打印机 RealPrinter —— 面向用户版内核
//
// 【与 BlackPrinter 的关系】
//   RealPrinter 是从 BlackPrinter（测试/开发版）裁出来的**精简分支**。
//   BlackPrinter 保留全部功能继续跑（92 条 tag 白名单 + 恢复链 + 12 个排障热键），
//   **本项目一个字都不改**，两边可同时装、互不干扰。
//
// 【RealPrinter 只做一件事：改真值】
//   BlackPrinter 的编辑窗里有两块：
//     ① tag 属性区（重量/耐久/浓度…）→ 走 TagSystem 92 条白名单
//     ② ★真值区（验真/纯度）        → 走 itemFeatures + 官方档位目录
//   **RealPrinter 只保留 ②**，① 整块砍掉。
//   → 连带砍掉：白名单表 92 条、永久黑名单 343 条、汉化词典 1169 行、
//     恢复链、裸指针安全模式、派生值监视、筛选档位、F1~F12 共 10 个排障热键。
//
// 【许可：开放范围】
//   V1.4.x：只对四类开放（香烟 / 化学品（含毒药）/ 金属锭 / 免疫宁注射器）。
//   ★★ V1.5.0（核桃要求"能改红色标签打印机支持改的商品"）：改成
//       **游戏官方档位目录里存在的全部类别（16 类）**，是红机范围的超集。
//   【红机真实口径（反汇编 LabelerHelper.CanEditLabel @ RVA 0x4B78D0）】
//       category ∈ {INGOT_PURITY, EQUIPMENT_CONDITION, WINE_QUALITY} → 不给改，其余放行。
//       → 本机取超集（连金属锭一起给），不砍老功能。
//   【仍然只认 category，不写死物品名单】
//     - 好处：游戏加新物品自动支持，永不漏；不用核桃报 id。
//     - 依据：category 由游戏自己在运行时给出（ItemConditionList 16 张静态表自省），
//       与物品名字/模型无关 —— 换个皮同款物品照样认得出。
//   ⚠ 游戏真值体系**只有 16 个 category，没有"毒药"这一类**
//     → 毒药按"身上有没有化学品纯度真值"判定（核桃拍板：归入化学品）。
//
// 【只留三个热键】（V1.2.0 更正：早期注释写"只留 F6"，V1.1.x 加了外观热键）
//   F6 领取机器（沿用 BlackPrinter）
//   F3 切换外观档位（切完自动刷新已存在的机器）
//   F4 手动刷新外观（F3 没生效时的兜底）
//   —— F9「重建窗口」那版**未采纳**（用户明确不要）。
//     卡窗口的问题在裁掉排障热键后概率极低，不值得占一个键。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2Cpp;
using UnityEngine;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>cfg 句柄（面板规则见 §Config 说明）。</summary>
    public sealed class Cfg
    {
        public MelonPreferences_Entry<int> FeatureCostEntry;
        public MelonPreferences_Entry<bool> VerboseLogEntry;
        public MelonPreferences_Entry<bool> SupplierStockEntry;
        // ★ V1.4.12：恢复 F6 直领 cfg 开关
        public MelonPreferences_Entry<bool> EnableF6GiveEntry;
        // ★ V1.5.0：拖动触发开关
        public MelonPreferences_Entry<bool> DragToUseEntry;
        // （其余 Entry 字段保留，走硬编码默认值）
        public MelonPreferences_Entry<int> DealerPriceEntry;
        public MelonPreferences_Entry<int> GiveMachineHotkeyEntry;
    }

    /// ★ V1.4.6 修：改为声明时初始化 —— 原来写成 `= null` 且 InitPreferences()
    ///   里**没有 `_cfg = new Cfg();`**，导致 InitPreferences 第一行就 NPE：
    ///   所有 CreateEntry 全没跑 → cfg 段里一条都没有 → 面板看不到任何开关，
    ///   而 Config 走的是硬编码默认值（"看起来能跑"但配置全失效）。
    ///   ★ 保留"判空兜默认值"语义：热键读取仍用 `_cfg?.xxxEntry`，
    ///     这里 new 出来只保证 InitPreferences 不再抛。
    private static Cfg _cfg = new Cfg();

    /// <summary>配置门面。全代码统一读这里，值由 InitPreferences() 从 cfg 写入。</summary>
    public static class Config
    {
        /// <summary>机器本身的基础价值。对齐原版红色打印机（反汇编 CreateLabeler
        /// 0x8358EF：`mov qword ptr [rdi+0x200], 0x32` → 50）。别乱改。</summary>
        public const int BaseMachineValue = 50;

        /// <summary>★ 改一次真值收多少信用点。核桃 2026-10-03 拍板：**固定 20**。
        /// 【为什么不分档】BlackPrinter 的按等级/分档定价是为 92 条 tag 准备的，
        ///   RealPrinter 只剩"改真值"一个动作 —— 分档定价失去意义，只会让人算不清。
        /// 【为什么不免费】红机免费会让黑印毫无存在感；20 相当于跑一趟的零头，
        ///   够形成"这台机器是稀缺工具"的感觉，又不会挡住正常使用。</summary>
        public static int FeatureCost = 20;

        /// <summary>
        /// ★★ V1.4.0：供货商上货（**新方向，接替已停用的同行带货**）。
        ///
        /// 【机制（反汇编取证）】游戏里"供货商摆货"只有一个收敛点：
        ///   `StoreClientList.PlaceSupplierInventory()`（静态）——
        ///   它内部就是一串
        ///   `PlayerStore.Instance.AddDirectSellingItemToTable(造的货, isOwend:false, ...)`。
        ///   → 我在它**执行完之后**（Harmony Postfix）追加一台真实打印机，
        ///     走**完全相同的官方 API** —— 与供货商自己的货同一条路、同一种"未拥有"状态。
        ///
        /// 【为什么这次不算"污染"】V1.3.0 的错是在**收购型客户**（同行，不带货）
        ///   在场时往共享柜台塞东西，柜台/交易状态属于当时在场的剧情 NPC。
        ///   这次挂在**供货商自己的补货函数**上：谁在摆供货商的货、什么时候摆，
        ///   全由游戏自己决定 —— 我只是在他的货里**追加一件**，时机与归属都对。
        ///
        /// 【"优先上货"怎么体现】每次供货商摆货**必定**追加（100% 出现，不随机缺席）；
        ///   原生货架摆完后我最后追加 —— 若柜台满，游戏会静默销毁，回读断言会喊出来
        ///   并把死机器从登记表注销（V1.3.1 防线）。
        /// </summary>
        public static bool SupplierStock = true;

        // （V1.4.0 起真实打印机改走供货商商品池；同行带货死代码已删除，此项不再需要。）

        /// <summary>
        /// ★★ V1.4.12：F6 直接领取。**cfg 已暴露开关**，玩家可在面板切换。
        ///   · 打开 → 按 F6 领取一台价值 0 的机器（防刷钱）
        ///   · 关闭 → 按 F6 无任何反应（不弹提示，核桃拍板）
        /// 【为什么价值 0】F6 是调试/应急通道。若价值 100，玩家可无限刷机器
        ///   再卖给商人套利 → 价值 0 从根上断绝这条经济链。
        /// 【正式获取途径仍是供货商上货】开关默认为关。
        /// </summary>
        public static bool EnableF6Give = false;

        /// <summary>
        /// ★★ V1.5.0：左键**拖动**机器到商品上是否触发改值窗（与原版红机一致）。
        ///
        /// 【为什么做成开关】拖动落点是全项目唯一有过 AccessViolation 实机记录的
        ///   路径（V1.1.9 之前靠 `IsDraggingNow()` 整条短路来防）。
        ///   V1.5.0 改成"压到拖动结束后的下一帧再开窗"来同时满足「能拖」和「不崩」，
        ///   但真要是在某台机器上仍然出状况，玩家可以**面板一键关掉**，
        ///   不用卸载 mod —— 关掉后退回 V1.4.x 行为（拖动不触发，先装备再左键点）。
        /// </summary>
        public static bool DragToUse = true;

        /// <summary>
        /// ★★ V1.3.0：同行柜台上那台机器的**售价**（核桃 2026-10-03 拍板：**100**）。
        /// 【口径】直接写进机器的 `unitValue` / `unitBaseValue`，让游戏自己算买价
        ///   （`PlayerStore.CanBuyItemFromTable` 内部用的是 `GameItem.GetNegociatedValue()`）。
        ///   → 不自己加价、不打折，机制上跟原版"供应商补货"完全同一条路。
        /// 【为什么不是 50】50 是原版红色打印机的自身价值（F6 直领沿用）；
        ///   同行是黑市渠道，100 才算"稀缺工具"，也让"买不买"变成一个真选择。
        /// </summary>
        public const int DealerPrice = 100;


        /// ★★ V1.0.5 新增：**详细日志开关，默认关**（核桃 2026-10-03 要求）。
        /// 【为什么不给玩家看全部日志】默认状态下只保留：
        ///   ① 启动横幅（版本/许可/费用/按键）
        ///   ② 启动自检结论（官方档位、许可类别、交互补丁）
        ///   ③ **报错**（Error / Warning 一律照打，永不静默）
        /// 关闭的是「每次操作都打」的流水（开窗、改值、回读、接管…）。
        /// 【为什么不直接删掉那些日志】出问题时还得靠它排障 ——
        ///   所以留一个面板开关，需要时勾上即可全开。
        public static bool VerboseLog = false;
    }

    /// <summary>面板规则（改显示名/段名前必读）：
    ///   ① cfg 段名必须 = DLL 文件名（只留字母数字）→ 这里叫 "RealPrinter"
    ///   ② 面板只显示 bool / 数字 / *Hotkey 结尾项
    ///   ③ 字符串项不显示</summary>
    public void InitPreferences()
    {
        var cat = MelonPreferences.CreateCategory("RealPrinter", "真实打印机");

        // ★ V1.4.6：cfg 只暴露 3 个可改项（核桃 2026-10-04 拍板）：
        //   ① 改真值费用　② 详细日志　③ 供货商上货
        //
        //   EnableF6Give / DealerPrice / GiveMachineHotkey **不再注册** ——
        //   它们不会出现在 MelonPreferences.cfg 里、也不会出现在 ModManager 面板上。
        //   ★ 三个字段本身（Config.EnableF6Give / Config.DealerPrice / HotkeyGive）
        //     全部保留，功能不变；只是值改由代码里的硬编码默认值决定。
        //   ★ Cfg 类里对应的 Entry 字段也保留（不删）—— 万一将来要恢复 cfg 输出，
        //     只需把对应的 CreateEntry 一行加回来，不用改类型定义。
        _cfg.FeatureCostEntry = cat.CreateEntry("FeatureCost",
            Config.FeatureCost, "改一次真值（验真/纯度）消耗多少信用点。");
        _cfg.VerboseLogEntry = cat.CreateEntry("VerboseLog",
            Config.VerboseLog, "详细日志。默认关；排障时勾上它看流水。");
        _cfg.SupplierStockEntry = cat.CreateEntry("SupplierStock",
            Config.SupplierStock, "供货商每次摆库存时，追加一台真实打印机（可购买）。");
        // ★ V1.4.12：F6 直领开关（默认关）。打开后按 F6 领取价值 0 的机器。
        _cfg.EnableF6GiveEntry = cat.CreateEntry("EnableF6Give",
            Config.EnableF6Give,
            "F6 直领：打开后按 F6 领取一台价值 0 的真实打印机（防刷钱）。关闭时不响应。");
        // ★ V1.5.0：拖动触发开关（默认开）。
        _cfg.DragToUseEntry = cat.CreateEntry("DragToUse",
            Config.DragToUse,
            "拖动触发：打开后，把真实打印机拖到商品上即可开窗（与原版红机一致）。"
            + "关闭则退回旧用法：先使用/装备，再左键点商品。");

        // ───── 全部注册完毕，统一读取 ─────
        Config.FeatureCost = _cfg.FeatureCostEntry.Value;
        Config.VerboseLog = _cfg.VerboseLogEntry.Value;
        Config.SupplierStock = ReadFlag(_cfg.SupplierStockEntry, Config.SupplierStock);
        // ★ V1.4.12：读 F6 开关
        Config.EnableF6Give = ReadFlag(_cfg.EnableF6GiveEntry, Config.EnableF6Give);
        // ★ V1.5.0：读拖动触发开关
        Config.DragToUse = ReadFlag(_cfg.DragToUseEntry, Config.DragToUse);
        // ★ V1.4.6：Config.EnableF6Give / Config.DealerPrice / HotkeyGive
        //   不再从 cfg 读取 —— Cfg 类里对应的 Entry 字段恒为 null，
        //   直接保留 Config 类里的硬编码默认值。

        // ★★ V1.4.13：cfg 热更新 —— 面板改值后**立刻**同步到静态字段。
        //   【为什么必须挂】MelonPreferences_Entry.Value 变了不会自动推到
        //     我们的 Config.xxx 静态字段，面板改了得重启才生效。
        //   【安全性】回调在 Unity 主线程触发，与游戏逻辑同线程，不存在竞态。
        try
        {
            _cfg.FeatureCostEntry.OnEntryValueChanged.Subscribe(
                (oldV, newV) => { Config.FeatureCost = newV; });
            _cfg.VerboseLogEntry.OnEntryValueChanged.Subscribe(
                (oldV, newV) => { Config.VerboseLog = newV; });
            _cfg.SupplierStockEntry.OnEntryValueChanged.Subscribe(
                (oldV, newV) => { Config.SupplierStock = newV; });
            _cfg.EnableF6GiveEntry.OnEntryValueChanged.Subscribe(
                (oldV, newV) => { Config.EnableF6Give = newV; });
            _cfg.DragToUseEntry.OnEntryValueChanged.Subscribe(
                (oldV, newV) => { Config.DragToUse = newV; });
        }
        catch (Exception ex) { RLog.Swallowed("CfgHotReload", ex); }

        // ★★ V1.4.1：注册完**立刻写盘**。
        //   【为什么必须加】V1.4.0 实机（核桃 2026-10-04）：cfg 里只有
        //   只有寥寥几条旧条目，V1.3.x 起新增的开关全都不在 ——
        //   本工程此前**从未调用 MelonPreferences.Save()**，新条目只活在内存，
        //   全靠别的机制代写；而 ModManager 面板重写 cfg 时会抹掉它不认识的条目
        //   （MEMORY：面板保存重写整个 cfg）→ "cfg 缺条目 + 开关看不见"。
        try { MelonPreferences.Save(); }
        catch (Exception ex) { RLog.Warning("cfg 写盘失败: " + ex.Message); }

        // ★★ V1.4.1：cfg 完整性自检 —— 条目数对不上就明说。
        //   "cfg 缺条目"和"功能没生效"是两回事，必须分开报。
        try
        {
            const int expected = 5;    // V1.5.0：拖动触发开关 → 5 项
            int actual = cat.Entries.Count;
            if (actual < expected)
                RLog.Error("★★ cfg 条目不完整（" + actual + "/" + expected
                           + "）—— 旧 cfg 残留或被面板重写过。已重新写盘；"
                           + "若面板仍缺开关，请把 UserData/MelonPreferences.cfg 的 [RealPrinter] 段"
                           + "删掉后重启游戏一次。");
            else
                RLog.Detail("cfg 自检通过（" + actual + " 条全部注册并写盘）。");
        }
        catch (Exception ex) { RLog.Swallowed("CfgSelfCheck", ex); }
    }

    /// <summary>读 bool 配置：读不到就用 fallback（永不让"读配置失败"改变玩家已选的行为）。</summary>
    private static bool ReadFlag(MelonPreferences_Entry<bool> entry, bool fallback)
    {
        try { if (entry != null) return entry.Value; } catch (Exception ex) { RLog.Swallowed("ReadFlag", ex); }
        return fallback;
    }

    /// <summary>读 int 配置：读不到就用 fallback。</summary>
    private static int ReadHotkey(MelonPreferences_Entry<int> entry, int fallback)
    {
        try { if (entry != null) return entry.Value; } catch (Exception ex) { RLog.Swallowed("ReadHotkey", ex); }
        return fallback;
    }

    /// <summary>★ F6 直领的键位（值恒为 F6，cfg 里看不到它 —— Entry 恒为 null 走 fallback）。
    /// ★★ V1.4.11：F6 的每帧轮询已删，本属性现在**没有调用者**。
    ///   它保留是因为恢复 F6 时会用到（见 Runtime.cs 末尾的恢复说明），
    ///   性质属于"停用功能的开关"，不是死代码 —— 删了就得多写一遍类型和 fallback。
    /// </summary>
    internal static int HotkeyGive => ReadHotkey(_cfg?.GiveMachineHotkeyEntry, (int)KeyCode.F6);

    // ★ V1.4.11：删掉 `ReadInt`（无调用者）。原来 cfg 有 4 个 int 项，
    //   精简到只剩 HotkeyGive 一个，而它走的是 ReadHotkey（键位要能容错回退）。


    // ═══════════════════════════════════════════════════════════
    // 机器常量
    //
    // ★★★ 为什么 identifier / OurTag 必须和 BlackPrinter 不同★★★
    //   两个 mod 的机器都挂 `LABELER_TAG` / `IMPORTANT_TAG` / `MAGNIFYING_GLASS`
    //   （这三个是原版 CreateLabeler 的机制，不能改，否则交互不上）。
    //   真正区分身份的是 **identifier + OurTag**：
    //     BlackPrinter → "black_printer" / "BLACK_PRINTER_TAG"
    //     RealPrinter  → "real_printer"  / "REAL_PRINTER_TAG"
    //   若沿用同一组，两个 mod 会互相把对方的机器认成自己的
    //   → 点对方机器触发自己窗口、或同台机器被两个 mod 重复认领。
    //   ★ 用户可能同时装着两个（对比测试），所以这里必须错开。
    // ═══════════════════════════════════════════════════════════

    /// <summary>本 mod 机器的 identifier（存档里的唯一身份）。</summary>
    public const string MachineIdentifier = "real_printer";

    /// <summary>本 mod 专属 tag —— 认机器的冷路径判据之一。</summary>
    public const string OurTag = "REAL_PRINTER_TAG";

    /// <summary>以下三个是**原版红色打印机的机制，必须沿用**，不能改。
    /// 挂错就交互不上（BlackPrinter 已实证：挂成 "LABELER" 会导致右键菜单缺项、
    /// tooltip 不对、交互无响应三连）。</summary>
    public const string ToolTagLabeler = "LABELER_TAG";
    public const string ImportantTag = "IMPORTANT_TAG";
    public const string MagnifierTag = "MAGNIFYING_GLASS";

    /// <summary>
    /// ★★ V1.5.0：工具箱入箱 tag。
    /// 【取证】`ToolboxHelper.<InitToolbox>b__2`（RVA 0x5182F0，槽位过滤）与
    ///   `ToolboxHelper.IsToolboxCompatible`（RVA 0x514010，UI 提示）共用同一张白名单：
    ///     IsGameItemType("TOOL") || IsGameItemType("MELEE_WEAPON")
    ///     || IsTag("CAN_USE_ON_NON_OWNED_TAG") || IsTag("TOOLBOX_ITEM_TAG")
    ///     || identifier == "labeler" || identifier == "labeler_blue"
    ///   原版红机能进工具箱靠的是 `identifier == "labeler"` 这条 id 特判；
    ///   本机 identifier 是 "real_printer"，走不通 → 必须自己挂 TOOLBOX_ITEM_TAG。
    ///   ★ 为什么选它而不是 CAN_USE_ON_NON_OWNED_TAG：后者还带"可对非自有物品
    ///     使用"的语义（放大镜用它），会**改变交互口径**；TOOLBOX_ITEM_TAG 语义
    ///     就是"工具箱里装的工具"，是最小影响面的一条。
    /// </summary>
    public const string ToolboxItemTag = "TOOLBOX_ITEM_TAG";

    /// <summary>
    /// ★ V1.5.0：容器通用标记（`CONTAINER_TAG`）。
    /// 【取证】`ContainerHelper.InitContainerItem`（RVA 0x8AC920）与
    ///   `InitContainerItemTagOnly`（0x8AC8B0）都会给容器打它；
    ///   `ToolboxHelper.InitToolbox`（0x513970）也打。
    ///   → 拖动时碰到带它的目标，本机**不接管**，把交互让回游戏的放置逻辑
    ///     （否则打印机永远放不进工具箱/档案箱，只会被当成"对它使用"）。
    /// </summary>
    public const string ContainerTag = "CONTAINER_TAG";

    /// <summary>
    /// ★★ V1.5.0：档案箱（dossier）入箱 tag —— **这条是靠 MAGNIFYING_GLASS**。
    /// 【取证】`MiscItemDirectory.Dossier`（RVA 0x70DFB0）末尾调用
    ///   `ContainerHelper.AllowOnlyTaggedItemsOr(inv, tags, ownedOnly:true)`，
    ///   tags 四个元素经 str xref 逐条核对为：
    ///     DOCUMENT / MAGNIFYING_GLASS / LABELER / LABELER_BLUE
    ///   红机身上没有 "LABELER" tag（只有 LABELER_TAG），**它靠的是
    ///   MAGNIFYING_GLASS 进的档案箱** —— 放大镜同理。
    ///   本机 V1.4.x 建机时漏挂了 MAGNIFYING_GLASS → 档案箱拒收。
    ///   → 挂回它即恢复。★ 注意：全库 xref 显示没有任何
    ///     `IsTag("MAGNIFYING_GLASS")` 的运行时闸门，它只当"族谱标记/白名单"用，
    ///     挂回去不会改变任何交互语义（不会让本机变成放大镜）。
    /// </summary>
    public const string DossierTag = MagnifierTag;

    /// <summary>建机用的目录模板 id（原版红机同款，借它的 sprite）。</summary>
    public const string TemplateItemId = "labeler";

    /// <summary>工具图集路径（借原版红机的 sprite，所以用它的图集）。</summary>
    public const string ToolAtlasPath = "Items/items_tool";

    /// <summary>本 mod 版本号（横幅与排障日志都要打，核桃靠它确认跑的是哪一版）。</summary>
    public const string SuiteVersion = "1.5.1";

    // ═══════════════════════════════════════════════════════════
    // 日志：所有输出走这里，方便排障时统一关掉。
    // ★ RealPrinter 是面向用户版 → 日志量比 BlackPrinter 克制得多，
    //   只打「为什么没反应」，不打「一切正常」的常规流水。
    // ═══════════════════════════════════════════════════════════
    public static class RLog
    {
        /// ★★ 操作流水日志：**默认静默**，仅当面板勾上 VerboseLog 才打。
        /// 【铁律】Error / Warning **不走这里**，永远照打 ——
        ///   报错被静默是比日志过多严重得多的事故。
        ///
        /// ★★★ V1.4.11 新增铁律：**开关在函数里面，参数求值在函数外面。**
        ///   `Detail("…" + SafeId(x))` 里的 `SafeId(x)` 是**实参**，
        ///   C# 会在**进入函数之前**就把它求值完。所以：
        ///     · 纯托管字段（fr.Id、cost）→ 无所谓，关着也只是白拼一次字符串；
        ///     · ★ **任何 IL2CPP 调用**（SafeId / Pay.Query / DisplayNameOf /
        ///       ReadUnitValue / IsOnFrontCounter …）→ VerboseLog 关着时
        ///       **照样会跑**。看着 gated、实际没 gated。
        ///   → 正确写法是**把判据搬到调用点**：
        ///       if (Config.VerboseLog) RLog.Detail("…" + SafeId(x));
        ///   本项目 V1.4.11 已按此清理：横幅的 `Pay.Query()`、
        ///   `Patch_Target` 的两处 `SafeId`、`OnTargetItemFunc` 的 `SafeId`、
        ///   编辑窗的 `SafeId` + 逐项 foreach。
        ///   （本函数里的 `if (!Config.VerboseLog) return;` **保留** ——
        ///     它挡的是"打出去"这一步，两者互补，不冲突。）
        internal static void Detail(string m)
        {
            if (!Config.VerboseLog) return;
            MelonLogger.Msg(TAG + m);
        }

        private static readonly string TAG = "[真印] ";

        /// ★★ 被吞掉的非致命异常（代码评审 P0-1）。
        ///
        /// 【为什么不能只 catch { }】
        ///   `AccessViolationException` 在 .NET Core + MelonLoader 下**不可捕获**
        ///   （MEMORY 铁律），所以"加 try 就安全"是**假防护**。
        ///   但反过来的害处也真实存在：**其他**异常（NullReference / InvalidCast /
        ///   IndexOutOfRange）被同一个空 catch 吞掉 → 逻辑 bug 全被藏起来，
        ///   排障只能靠猜。
        ///   → 折中：**区分"预期内的降级"与"意料外的异常"**：
        ///     · 裸指针形态预检失败（野指针/已回收）→ 预期内，Detail 即可；
        ///     · 其它一切异常 → 走 Warning，**永远可见**。
        ///
        /// 【为什么不让它在默认日志里刷屏】
        ///   `IsSafeTarget` / `ReadFieldPtr` 这类判据在**拖动时每秒会被调几十次**
        ///   （MEMORY：热路径铁律），若每次都打 Warning 就是日志炸弹。
        ///   → 只有**第一次**以及**每累计 100 次**才打一行，既不刷屏又不丢失。
        private static int _swallowedCount;
        private const int SwallowReportEvery = 100;

        /// <summary>
        /// 记录一次被降级处理的异常。
        /// </summary>
        /// <param name="where">出问题的位置（简短，如 "IsSafeTarget"）。</param>
        /// <param name="e">捕获到的异常。</param>
        internal static void Swallowed(string where, Exception e)
        {
            if (e == null) return;
            _swallowedCount++;
            // ★ AccessViolation 走到这里说明**它可捕获**（与 MEMORY 记录不符）——
            //   那就必须报出来，这种事本身就不该发生。
            string kind = e.GetType().Name;
            bool av = kind.IndexOf("AccessViolation", StringComparison.OrdinalIgnoreCase) >= 0;
            if (av)
            {
                MelonLogger.Error(TAG + "★★ " + where + " 捕获到 AccessViolation —— "
                               + "MEMORY 记录它不可捕获，说明前提变了，必须查！"
                               + " 累计吞掉 " + _swallowedCount + " 次");
                _swallowedCount = 0;
                return;
            }
            if (_swallowedCount == 1 || _swallowedCount % SwallowReportEvery == 0)
                MelonLogger.Warning(TAG + "降级：" + where + " — " + kind + ": " + e.Message
                               + (_swallowedCount > 1 ? "（累计 " + _swallowedCount + " 次）" : ""));
        }

        /// <summary>被降级处理的异常总数（横幅用，让"静默失效"可见）。</summary>
        internal static int SwallowedCount { get { return _swallowedCount; } }

        public static void Msg(string m) { MelonLogger.Msg(TAG + m); }
        public static void Warning(string m) { MelonLogger.Warning(TAG + m); }
        public static void Error(string m) { MelonLogger.Error(TAG + m); }
    }

    // ═══════════════════════════════════════════════════════════
    // 信用点
    //
    // ★ 移植自 BlackPrinter.PriceTable.CashBackend，**回读确认那一步必须留着**：
    //   扣完立刻 GetCash 复核，对不上就**补回去并报错** ——
    //   宁可让玩家知道"扣费出问题了"，也不能白花钱且无提示。
    //
    // ★ 一处**有意的改动**：原版 `amount <= 0` 时写 `failReason = "费用非正数（内部错误）"`
    //   并返回 false —— 但 FeatureCost=0（免费改真值）是合法配置，
    //   于是日志里一直刷"费用非正数"噪声。RealPrinter 里 0 = 免费，直接放行。
    // ═══════════════════════════════════════════════════════════
    public static class Pay
    {
        public static int Query()
        {
            try { return PlayerStore.Instance?.GetCash() ?? 0; }
            catch { return 0; }
        }

        public static bool TryCharge(int amount, out string failReason)
        {
            failReason = null;
            if (amount <= 0) return true;   // 免费（合法配置，不报错）

            try
            {
                var store = PlayerStore.Instance;
                if (store == null) { failReason = "PlayerStore 不可用（不在存档内？）"; return false; }

                int have = store.GetCash();
                if (have < amount)
                {
                    failReason = "信用点不足：需要 " + amount + "，只有 " + have;
                    return false;
                }

                store.ModCash(-amount, false);
                int after = store.GetCash();
                if (after != have - amount)
                {
                    // ★ 扣费没按预期生效：立刻补回去并报错，不能让玩家白花钱
                    try { store.ModCash(amount, false); } catch (Exception ex) { RLog.Swallowed("TryCharge", ex); }
                    failReason = "扣费未生效（期望 " + (have - amount) + "，实际 " + after + "），已退回";
                    RLog.Error("⚠ 扣费异常已回滚：" + failReason);
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                failReason = "扣费异常：" + e.Message;
                return false;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 小工具
    // ═══════════════════════════════════════════════════════════

    /// <summary>★ 读 IL2CPP 字段可能抛 —— 包一层，绝不让一处异常打断整个开窗流程。</summary>
    private static string SafeStr(Func<string> get)
    {
        try { return get(); } catch { return null; }
    }

    /// <summary>
    /// 判断一个字符串长得像不像内存指针（0x/0X 开头的十六进制）。
    /// ★ 用途：SafeId 在拿不到 id 时会回退成指针形态的字符串，
    ///   那种东西**绝不能**直接显示给玩家（V1.0.1 实机把`0X2921ADCO0000`
    ///   当商品名显示了，玩家一脸问号）。
    /// </summary>
    private static bool IsPointerLike(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length <3) return false;
        if (s[0] != '0') return false;
        if (s[1] != 'x' && s[1] != 'X') return false;
        for (int i = 2; i < s.Length; i++)
        {
            char c = s[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    // ★ SafeId 定义在 Machine.cs：它额外处理了「指针已被回收 / 无指针」的情况，
    //   热路径日志全都靠它。Core 不重复定义，避免两份口径不一致。

    /// <summary>
    /// ★ 物品显示名。这里只读裸字段 / 安全标识，拿不到才退回 id。
    /// 【为什么不走词典】RealPrinter 砍掉了 1169 行汉化词典：
    ///   真值相关的名称全部来自**游戏官方档位对象的 display**（已本地化），
    ///   这里只需要显示"这是什么东西"。
    /// </summary>
    private static string DisplayNameOf(GameItem item)
    {
        string id = SafeId(item);
        if (item != null)
        {
            //★★★ V1.0.6 重大修正：**这里一个 IL2CPP 调用都不能有**。
            //
            // 【实机崩溃（核桃 2026-10-03 18:47，拖动打印机时）】
            //   AccessViolationException @ IL2CPP.il2cpp_runtime_invoke
            //   堆栈（自下而上）：
            //     Patch_Target.Prefix ← managed_EndDrag
            //       → OnTargetItemFunc → OpenEditor → License.RejectReason
            //         → DisplayNameOf → SafeStr → GetDisplayName  ←崩在这
            //
            // 【为什么 try/catch 拦不住】
            //   `AccessViolationException` 在 .NET Core + MelonLoader 下
            //   **不作为托管异常传播**（进程猝死，日志最后一行都写不完）——
            //   MEMORY 早已记过这条铁律，我却又用 try 包了一层就当防护。
            //
            // 【根因】**拖动过程中**游戏在 `ItemMouseDragHandler.EndDrag` 内部
            //   自己去调 `GameItem.Target`（这条重复路径 MEMORY 也记过），
            //   此时传进来的 target 可能**正在被销毁**。
            //   `IsTargetEditable` 只验了「指针非零 + 像堆指针 + 不是自己机器」，
            //   这对一个半死对象**完全不够** → 进去调 GetDisplayName 就是野指针喂原生函数。
            //
            // 【修法】DisplayNameOf **只允许读裸字段**：
            //   - 删掉 `GetDisplayName()`（它是 il2cpp_runtime_invoke，最危险）
            //   - 删掉 `shortDescription`（property getter，同样走 invoke）
            //   - 只用 `SafeId()`（纯裸指针读，且对非我方物品只回指针摘要，不进 IL2CPP）
            //   - 显示层要中文名 → **放到开窗后的稳态阶段**去取（那时对象已确定存活），
            //     而不是在这条随时可能崩的路径上取。
            // ★ id 不是指针形态 → 说明 SafeId 真的解出了可读标识，直接用。
            if (!string.IsNullOrEmpty(id) && !IsPointerLike(id)) return id;
        }

        //★ 兜底：**绝不能返回指针**。指针形态是 0x/0X 开头的十六进制串，
        //   一旦显示出来玩家会以为程序坏了（V1.0.1 实机就是这样）。
        // ★ V1.4.6：同时排除 <null> / <err> / <no-ptr> 等 SafeId 占位符
        //   （item==null 时原代码会把这些当商品名返回给玩家）。
        if (id != null && !IsPointerLike(id) && id[0] != '<') return id;
        return "该商品";
    }

    // ═══════════════════════════════════════════════════════════
    // 玩家提示通道
    //
    //★★★ V1.0.2 修：**弹窗 → 轻提示**（核桃实机截图：F6 领取后弹出一个
    //   「确认 / 取消」对话框，玩家要点一次 Cancel 才能继续 —— 完全多余）
    //
    //【为什么之前会弹窗】我用的是 `OverlayHandler.ShowConfirmation(text)`，
    //   它的真签名是：
    //     ShowConfirmation(title, richText, yesAction, noAction,
    //                      canYes, showYes, yes="Confirm", no="Cancel", ...)
    //   —— **它是个「是/否」确认对话框**，第一个参数还是标题。
    //   我把提示文案塞进title、又没传 yes/no 回调 →
    //   弹出一个空动作的确认框，玩家必须手动关掉。
    //   ★ MEMORY 早就记过「玩家提示通道首选 ShowConfirmation」—— **那条是错的**，
    //     我没核实签名就照抄了。**"通知"和"确认"是两个完全不同的语义。**
    //
    //【正解】`StoreUIManager.Notify(message, color="default")` ——
    //   游戏自己用的轻提示，一行字、自动消失、颜色可选
    //   （default / yellow / green，签名见 Il2Cpp/StoreUIManager.cs:3582）。
    //   ★ MEMORY 记的"不在店内大概率 null"经实测**在店内可用**（F6 就是在店里按的）。
    //   → 三级降级：Notify → 落日志。**任何一级都不许弹窗。**
    // ═══════════════════════════════════════════════════════════

    /// <summary>颜色参数：与游戏 Notify 的约定一致（方法内用字符串字面量传色）。</summary>
    private const string ColorDefault = "default";

    /// <summary>
    /// 给玩家一行提示（**绝不弹窗**）。
    /// </summary>
    /// <param name="color">"default" / "yellow" / "green"。</param>
    private static void Inform(string text, string color = ColorDefault)
    {
        if (string.IsNullOrEmpty(text)) return;

        // ① 首选：游戏自己的轻提示
        try
        {
            var ui = StoreUIManager.Instance;
            if (ui != null) { ui.Notify(text, color); return; }
        }
        catch (Exception ex) { RLog.Swallowed("Inform", ex); }

        // ② 退路：落日志（玩家看不到，但排障能查）
        RLog.Msg("提示（无 UI 通道）: " + text);
    }

    // ═══════════════════════════════════════════════════════════
    // 许可：开放"游戏官方档位目录里有档位"的全部类别（V1.5.0 起 16 类）
    // ═══════════════════════════════════════════════════════════
    public static class License
    {
        /// <summary>
        /// ★ 唯一许可判据：**category 白名单**。
        ///
        /// 【为什么不认物品 id 名单】
        ///   - 不写死名单 → 游戏加新物品自动支持，永不漏；核桃不用报 id。
        ///   - 抗换皮：同款物品换个名字/模型，category 不变，照样认得出。
        ///
        /// ★★★【为什么不写死 category 字符串】★★★
        ///   我原来写的是 `CATEGORY_CHEMICAL_PURITY` 这种**手打字符串**，
        ///   但那些 category 值是**工厂对象自己报的字段**，我打错一个字、
        ///   或游戏改名，这台机器就默默开不了窗 —— 而日志会显示一切正常。
        ///   （这与 BlackPrinter V0.5.18「手写档位名全是错的」是同一类错误。）
        ///   → 正解：启动时先读游戏自己的**分类档位表**
        ///     （`ItemConditionList.conditionChemicalPurity` 等 16 个静态列表），
        ///     从里面取**第一条的 category 字段**当这个类的真名。
        ///     拿到的字符串 100% 与游戏一致，我一个字符都不用猜。
        ///     手写串只作为"读不到时的兜底"，且自检会报警。
        /// </summary>
        // ★★ V1.5.0：从"手写 4 类"改成 **游戏 16 张分类档位表全取**。
        //
        // 【为什么改】核桃要"能改红色标签打印机支持改的商品"。
        //   反汇编 `LabelerHelper.CanEditLabel`（RVA 0x4B78D0）得到红机的口径：
        //       category ∈ {CATEGORY_INGOT_PURITY, CATEGORY_EQUIPMENT_CONDITION,
        //                   CATEGORY_WINE_QUALITY} → false（红机不给改）
        //       其余一律 true
        //   → 红机放行的类别是"除这三类之外的全部"。
        //   【本机取**超集**，不砍金属锭】金属锭是本机老功能（README 头条），
        //     核桃只说"增加"，没说移除 → 保留。于是本机许可 = 全部 16 类。
        //   【为什么不手写这 16 个字符串】手写 = 打错一个字就静默开不了窗
        //     （V0.5.18 同款事故）。这里一张表都不写死，全从游戏静态表读。
        private static readonly Dictionary<string, Func<Il2CppSystem.Collections.Generic.List<ItemCondition>>> _catListAccessors
            = new Dictionary<string, Func<Il2CppSystem.Collections.Generic.List<ItemCondition>>>(StringComparer.OrdinalIgnoreCase)
        {
            // 键 = 语义标签（我自己的，只用于给玩家看的中文名，不参与游戏判定）
            // 值 = 游戏自己的分类档位表（dump.cs:40775 `ItemConditionList` 全部 16 张）
            { "香烟",     () => ItemConditionList.conditionGenuineCigarette },
            { "化学品",   () => ItemConditionList.conditionChemicalPurity },
            { "金属锭",   () => ItemConditionList.conditionIngotPurity },
            { "注射器",   () => ItemConditionList.conditionGenuineInjector },
            { "印花票",   () => ItemConditionList.conditionGenuineStamp },
            { "效期",     () => ItemConditionList.conditionExpired },
            { "水",       () => ItemConditionList.conditionWaterPurity },
            { "酒",       () => ItemConditionList.conditionWineQuality },
            { "装备成色", () => ItemConditionList.conditionEquipmentCondition },
            { "新鲜度",   () => ItemConditionList.conditionFoodFreshness },
            { "凹陷",     () => ItemConditionList.conditionDented },
            { "生锈",     () => ItemConditionList.conditionRusted },
            { "已开封",   () => ItemConditionList.conditionOpenBox },
            { "仿制品",   () => ItemConditionList.conditionReplica },
            { "赠品",     () => ItemConditionList.conditionFree },
            { "模块卡死", () => ItemConditionList.conditionModuleStuck },
        };

        /// <summary>语义标签 → 游戏真实 category 名（启动时填充）。</summary>
        private static readonly Dictionary<string, string> _catRealNames
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>兜底用的手写 category 名（仅在读游戏表失败时使用，自检会报警）。</summary>
        private static readonly Dictionary<string, string> _catFallback
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "香烟",     "CATEGORY_GENUINE_CIGARETTE" },
            { "化学品",   "CATEGORY_CHEMICAL_PURITY" },
            { "金属锭",   "CATEGORY_INGOT_PURITY" },
            { "注射器",   "CATEGORY_GENUINE_INJECTOR" },
            { "印花票",   "CATEGORY_GENUINE_STAMP" },
            { "效期",     "CATEGORY_EXPIRED" },
            { "水",       "CATEGORY_WATER_PURITY" },
            { "酒",       "CATEGORY_WINE_QUALITY" },
            { "装备成色", "CATEGORY_EQUIPMENT_CONDITION" },
            { "新鲜度",   "CATEGORY_FRESHNESS" },
            { "凹陷",     "CATEGORY_DENTED" },
            { "生锈",     "CATEGORY_RUSTED" },
            { "已开封",   "CATEGORY_OPEN_BOX" },
            { "仿制品",   "CATEGORY_REPLICA" },
            { "赠品",     "CATEGORY_FREE" },
            { "模块卡死", "CATEGORY_MODULE_STUCK" },
        };

        // ★★ V1.4.15：`_catResolved` 语义改为「已确定」——
        //   只有**成功拿到游戏表**或**用尽重试退回手写兜底**时才置 true。
        //   重试期间保持 false，让每次调用都能重新探测游戏静态表。
        private static bool _catResolved;
        private static int _catRetry;      // 已重试次数（用尽 3 次后不再试，退回手写兜底）

        /// ★★ V2：哪些语义标签的值是**手写兜底**（不是从游戏表读回来的）。
        ///   【为什么要有这张表】自检要报"哪些中文名是猜的"就得能**列名字** ——
        ///   光报"x/16"下次还是同样的盲区（不知道缺的是哪几类、为什么缺）。
        private static readonly HashSet<string> _catGuessed
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// ★ 从游戏自己的分类档位表解析出每个语义标签的真实 category 名。
        ///   必须在**官方档位目录建好之后**调（那些静态列表由 .cctor 填充）。
        ///
        /// ★★ V1.4.15：改成**有限次重试**（最多 3 次）。
        ///   【为什么】旧版第一行就 `_catResolved = true` —— 一旦启动早期
        ///     游戏 .cctor 还没跑完、读到空表，就**永久锁死**成手写兜底，
        ///     再也不重试。这与 `AllowCatalogRetry` 是同款静默失效。
        ///   【为什么限 3 次】无限重试 = 每次开窗刷异常日志；3 次足够跨启动窗口。
        /// </summary>
        /// <returns>true = 已确定（成功 或 已退回手写兜底）；false = 本次失败、允许下次重试。</returns>
        private static bool ResolveCategoryNames()
        {
            if (_catResolved) return true;

            // ★ 每次都先清空重扫 —— 避免上次失败残留污染本次结果
            _catRealNames.Clear();
            _catGuessed.Clear();
            foreach (var kv in _catListAccessors)
            {
                try
                {
                    var lst = kv.Value();
                    if (lst == null || lst.Count == 0) continue;
                    string cat = null;
                    try { cat = lst[0].category; } catch (Exception ex) { RLog.Swallowed("ResolveCategoryNames", ex); }
                    if (string.IsNullOrEmpty(cat)) continue;
                    _catRealNames[kv.Key] = cat;
                }
                catch (Exception ex) { RLog.Swallowed("ResolveCategoryNames", ex); }
            }

            // ★★ V2：判据从"读到 ≥1 条"改成"**16 张表全部**读回真名"。
            //   【为什么必须改】旧判据只要读到 1 条就 `_catResolved = true`（**永久锁定**），
            //     而 `_catRetry` 只在"一条都没读到"时才递增 —— 于是"部分解析"
            //     既不会重试、也不会自我修正，被固化成 x/16，且每次启动都在自检里
            //     报一条降级。部分解析和零条**本质是同一件事**（游戏 .cctor 还没跑完），
            //     必须走同一条重试路径。
            if (_catRealNames.Count == _catListAccessors.Count)
            {
                _catResolved = true;
                foreach (var kv in _catRealNames)
                    RLog.Detail("许可类别 " + kv.Key + " → 游戏真实 category = " + kv.Value);
                return true;
            }

            // ★ 未读全（含一个字都没读到）—— 允许有限次重试
            if (_catRetry < 3)
            {
                _catRetry++;
                RLog.Detail("许可类别名未读全（" + _catRealNames.Count + "/"
                            + _catListAccessors.Count + "，第 " + _catRetry + " 次尝试），稍后重试。");
                return false;
            }

            // 用尽重试 → 缺的那些用手写兜底补齐。
            // ★ 已读到的**保留**（真名比手写准），只补缺口 —— 旧版是整表覆盖，
            //   等于把已经读回来的真名又换成手写猜测，纯亏。
            var guessedNames = new List<string>();
            foreach (var kv in _catFallback)
            {
                if (_catRealNames.ContainsKey(kv.Key)) continue;
                _catRealNames[kv.Key] = kv.Value;
                _catGuessed.Add(kv.Key);
                guessedNames.Add(kv.Key);
            }
            _catResolved = true;

            if (guessedNames.Count == _catListAccessors.Count)
                RLog.Warning("⚠ 许可类别名读取失败（已重试 " + _catRetry
                             + " 次）→ 全部回退到**手写字符串**（若机器开不了窗，多半是这个原因）。");
            else
                RLog.Warning("⚠ 许可类别名只读回 "
                             + (_catListAccessors.Count - guessedNames.Count) + "/"
                             + _catListAccessors.Count + " 条；以下 " + guessedNames.Count
                             + " 条由**手写字符串**兜底（界面可能显示英文原名，功能不受影响）："
                             + string.Join(" / ", guessedNames.ToArray()));
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // ★★ V1.5.0：许可判据改成「官方档位目录里有没有这一类的档位」
        //
        // 【为什么不能再拿 _catRealNames 当许可集合】_catRealNames 是**为显示中文名**
        //   才去读的 16 张分类静态表；它和 56 个工厂产出的 category 是**两套数据源**，
        //   只要任何一处命名对不上（比如静态表叫 CATEGORY_FOOD_FRESHNESS、
        //   工厂产出 CATEGORY_FRESHNESS），许可集合就会漏掉一整类 —— 而
        //   `SelfCheckLicense` 还会为此在每次启动时刷一条红字。
        //
        // 【改成什么】许可 = `_byCat.Keys`（官方档位目录的类别键）。
        //   ① **与候选同源**：能许可 ⇒ 一定有候选；不可能出现"开窗是空的"。
        //   ② **自检不可能误报**：集合由目录构造而来，逐项反查必然命中。
        //   ③ **更快**：一次 HashSet 查找，不需要先解析 16 张静态表。
        //   ④ **更准的语义**：本机真正的边界就是"游戏给这类东西配没配档位"。
        // ═══════════════════════════════════════════════════════════
        private static HashSet<string> _allowed;

        // ★ V2：水主动排除（核桃拍板）—— 改水的价格容易破坏游戏经济平衡。
        //   保持"许可 = 官方档位目录的键"，只在末尾扣掉这个排除集。
        private static readonly string[] _excludedSemanticLabels = { "水" };

        /// <summary>许可集合（官方档位目录的类别键，扣掉排除集）。目录没建好时返回 null 且不缓存，允许下次重试。</summary>
        private static HashSet<string> AllowedSet()
        {
            var s = _allowed;
            if (s != null) return s;

            EnsureCatalog();
            if (_byCat == null || _byCat.Count == 0) return null;

            if (!ResolveCategoryNames()) return null;

            s = new HashSet<string>(_byCat.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var label in _excludedSemanticLabels)
            {
                string cat;
                if (_catRealNames.TryGetValue(label, out cat) && !string.IsNullOrEmpty(cat))
                    s.Remove(cat);
                if (_catFallback.TryGetValue(label, out cat) && !string.IsNullOrEmpty(cat))
                    s.Remove(cat);
            }

            _allowed = s;
            return s;
        }

        /// <summary>
        /// ★ 许可自检（启动时跑一次）。★★ V2：返回值**分两级**。
        ///
        /// 【查两件事】
        ///   ① 每个许可 category 在官方档位目录里**必须真的有档位** → **功能故障**。
        ///      V1.5.0 起许可集合就是目录的键，这条按构造必然成立 ——
        ///      留着是防将来有人又把许可换成手写名单（那正是 V0.5.18 的事故形态）。
        ///   ② 16 张分类静态表**读回来几个真名** → **显示降级**（只影响 UI 上
        ///      显示中文还是 CATEGORY_ 英文，功能一点不受影响）。
        ///
        /// 【★★ V2 为什么必须拆】
        ///   旧版把 ② 也塞进 `bad`，调用点统一打成 `RLog.Error`（启动一条红字）→
        ///   玩家看到"每次启动都报错"，以为 mod 坏了。**误报比不报更坏**：
        ///   它让人去查一个不存在的故障（这与 MEMORY 铁律同源）。
        /// 【★★ V2 为什么还要"列名字"】
        ///   旧文案只有"只解析出 x/16 条"—— 无从下手。列出**缺的是哪几类**，
        ///   下次进游戏可直接核对（多半是那个游戏版本本来就没有这类内容）。
        /// 【★★ V2 为什么只在"已定案"后才报】
        ///   重试还开着（`_catResolved == false`）就不该报 —— 那不是降级，
        ///   只是"还没读完"，此刻报出去就是纯噪音，而且每次启动都会报一条。
        /// </summary>
        internal static void SelfCheckLicense(out List<string> functional, out List<string> cosmetic)
        {
            functional = new List<string>();
            cosmetic = new List<string>();

            // ★ 启动时就把类别真名表定下来（中文名要用），别等到第一次开窗
            ResolveCategoryNames();

            var allowed = AllAllowed();
            for (int i = 0; i < allowed.Length; i++)
            {
                string cat = allowed[i];
                var opts = OfficialIdsForCategory(cat);
                if (opts == null || opts.Length == 0)
                    functional.Add("许可类别 " + cat
                            + " 在游戏官方档位目录里没有对应档位 → 这类商品开窗会是空的。");
            }

            // ② 显示降级：只影响"中文名从哪来"，不影响能不能改
            if (_catResolved && _catGuessed.Count > 0)
            {
                if (_catGuessed.Count == _catListAccessors.Count)
                    cosmetic.Add("类别中文名**全部**来自手写兜底（" + _catListAccessors.Count
                                 + " 张游戏分类表一张都没读回）"
                                 + "—— 界面可能显示 CATEGORY_ 英文原名（功能不受影响）。");
                else
                {
                    var names = new List<string>(_catGuessed);
                    cosmetic.Add("类别中文名有 " + names.Count + "/" + _catListAccessors.Count
                                 + " 条来自手写兜底（游戏分类表没读到）："
                                 + string.Join(" / ", names.ToArray())
                                 + " —— 这几类会显示英文原名（功能不受影响）。");
                }
            }
        }

        // ── 类别中文名兜底表（代码评审 P1-5：switch → Dictionary）──
        // ★ V1.5.0：16 类已全部由 _catRealNames 自动填好（读游戏分类档位表，零猜测），
        //   这张表现在只兜"游戏新增的、还没进 16 张静态表"的类别。
        //   用途不变：玩家点了个没法改的东西，我要用人话告诉他那是什么。
        // ★ 用 Dictionary 而不是 switch：加一条只要写一行，且不会漏 default 分支。
        private static readonly Dictionary<string, string> _labelByCat
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CATEGORY_GENUINE_STAMP",       "印花票" },
            { "CATEGORY_EXPIRED",             "效期" },
            { "CATEGORY_WATER_PURITY",        "水" },
            { "CATEGORY_WINE_QUALITY",        "酒" },
            { "CATEGORY_EQUIPMENT_CONDITION", "装备成色" },
            { "CATEGORY_FRESHNESS",           "新鲜度" },
            { "CATEGORY_DENTED",              "凹陷" },
            { "CATEGORY_RUSTED",              "生锈" },
            { "CATEGORY_OPEN_BOX",            "已开封" },
            { "CATEGORY_REPLICA",             "仿制品" },
            { "CATEGORY_FREE",                "赠品" },
            { "CATEGORY_MODULE_STUCK",        "模块卡死" },
        };

        /// <summary>中文类别名（提示语用，别给玩家看一串 CATEGORY_ 英文）。</summary>
        public static string CategoryLabel(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return "未知类别";

            // ★ V1.5.0：许可判据已改走官方档位目录（AllowedSet），不再顺带触发
            //   类别名解析 —— 这里必须**自己**确保表已填好，否则中文名永远查不到
            //   （又一个"两个功能共用一次初始化"的隐性耦合，拆开了就得各自保证）。
            ResolveCategoryNames();

            // ① 优先用**从游戏表读回来的真实名**反查（16 类走这条）
            foreach (var kv in _catRealNames)
            {
                if (string.Equals(kv.Value, cat, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
            // ② 其余用静态表；查不到就把英文原样返回（不编造中文）
            string label;
            return _labelByCat.TryGetValue(cat, out label) ? label : cat;
        }

        public static bool IsAllowed(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            var s = AllowedSet();
            return s != null && s.Contains(cat);
        }

        /// <summary>全部许可 category 名（横幅/自检用）。</summary>
        public static string[] AllAllowed()
        {
            var s = AllowedSet();
            if (s == null) return Array.Empty<string>();
            var a = new string[s.Count];
            s.CopyTo(a);
            return a;
        }

        /// <summary>
        /// 玩家点了不支持的物品时，给一句人话说明。★ 必须说清"为什么"，
        /// 否则就是"点了没反应"（用户最讨厌的静默失效）。
        /// </summary>
        public static string RejectReason(GameItem target)
        {
            var cats = new List<string>();
            int rawCount = 0;

            //★★★ V1.0.6：**先验活，再碰 IL2CPP**。
            //   崩溃那次的调用链就是 OpenEditor → RejectReason，
            //   所以这里同样必须先确认对象还活着 —— 判据复用 IsTargetEditable
            //   （含 WasCollected / 堆指针形态 / 非我方物品三道闸）。
            //   拖动中传进来的常常是半死对象，活都不活都不知道就进去读 = 猝死。
            //   try/catch 对 AccessViolation **无效**（MEMORY 铁律）。
            if (!IsSafeTarget(target))
                return "「该商品」无法处理（对象状态不稳定）。请松手后重新点一次。";

            try
            {
                var fl = target.itemFeatures;
                if (fl != null)
                {
                    rawCount = fl.Count;
                    for (int i = 0; i < fl.Count; i++)
                    {
                        string c = null;
                        try { c = fl[i].category; } catch { break; }
                        if (!string.IsNullOrEmpty(c) && !cats.Contains(c)) cats.Add(c);
                    }
                }
            }
            catch (Exception ex) { RLog.Swallowed("RejectReason", ex); }

            if (rawCount == 0)
                return "「" + DisplayNameOf(target) + "」身上没有任何可验证的特性"
                     + "（这台打印机只改真值：验真、纯度、可不可用、成色…）。";

            if (cats.Count == 0)
                return "「" + DisplayNameOf(target) + "」的真值特性无法识别（类别为空），无法处理。";

            // ★ V1.5.0：许可已扩到"游戏官方档位目录里有的全部类别"（16 类），
            //   所以走到这里的唯一原因是：**这个 category 在官方目录里没有档位**
            //   —— 不是"我不让你改"，是"游戏自己没给这类东西配档位"。
            //   → 文案必须说准，否则玩家会以为是权限问题（MEMORY：静默/含糊最坏）。
            var parts = new List<string>();
            foreach (var c in cats) parts.Add(CategoryLabel(c));
            return "「" + DisplayNameOf(target) + "」的真值属于「" + string.Join(" / ", parts.ToArray())
                 + "」—— 游戏官方档位目录里没有这一类的档位，本机无从改写。";
        }
    }
}