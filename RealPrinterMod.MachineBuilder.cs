// ★★★ 真实打印机建机 —— 严格照抄原版红色标签打印机的机制
//   （2026-10-03 原生反汇编取证，方法体见课题文档，不是猜的）
//
// ═════════════ 取证结论：ToolDirectory.CreateLabeler 到底做了什么 ═════════════
//
// 反汇编 0x835750（CreateLabeler）实际指令序列，翻译成伪 C：
//
//   GameItem CreateLabeler() {
//     var it = <从目录拿 id="labeler" 的成品物品>;      // 不是 CreateEmptyItem！
//     it.SetSpriteAndShape("Items/items_tool", "labeler");   // ★ 用游戏自己的 sprite
//     it.SetName(LocHelper.GetLocalizedItem("item_labeler_name"));
//     it.SetDescription(LocHelper.GetLocalizedItem("item_labeler_desc"));  // 写 +0x1E0
//     it.unitValue = 0x32;                             // 50，★ 写 +0x200
//     it.EnableTag("MAGNIFYING_GLASS", false, false);  // ★ 挂放大镜的 tag（不是 LABELER！）
//     LabelerHelper.InitLabelerItem(it, false);        // ★★ 真正的入口，见下
//     it.EnableTag("IMPORTANT_TAG", false, false);     // ★ "重要物品"标记
//
//     it.maySelectSlotItemFunc  = <b__12_0>;// (item, inv, slot) => true
//     it.canSelectSlotItemFunc  = <b__12_1>;             // 同上（编译器合并成同一 RVA）
//     it.mayItemTargetItemFunc  = <b__12_2>;// (s,t) => GeneralHelper.IsItemOwned(t)
//     it.canItemTargetItemFunc  = <b__12_3>;             // 同上
//     it.onThisTargetItemFunc   = <b__12_4>;             // → LabelerHelper.OnTargetItem(t)
//     return it;
//   }
//
// LabelerHelper.InitLabelerItem(item)   （反汇编 0x4B7FA0）
//   void InitLabelerItem(GameItem item) {
//     if (item == null) throw;
//     item.EnableTag("LABELER_TAG", false, false);       // ★ 只挂这一个 tag，别无其它
//   }
//
// LabelerHelper.OnTargetItem(target)   （反汇编 0x4B8130，尾调用 jmp）
//   void OnTargetItem(GameItem target) {
//     var mgr = LabelerUIManager.Instance;
//     if (mgr == null) throw;
//     mgr.OpenUI(target, false);                // ← 真正干活：打开标签编辑窗
//   }
//
// ═════════════ 我 V1/V2 踩的三个坑（对照上面逐条修正） ═════════════
//
// ❌ 坑 A：我挂的是 ToolDirectory.LABELER = "LABELER"（目录 id），不是 "LABELER_TAG"。
//    全库扫描证明：字符串 "LABELER" 只被 MiscItemDirectory.Dossier 引用（档案菜单），
//    字符串 "LABELER_TAG" 只被 LabelerHelper.InitLabelerItem 引用 —— 挂错tag = 游戏
//    当它是个普通杂物，右键菜单缺项、tooltip 不对、交互不响应。
//
// ❌ 坑 B：我调了 SetGameItemType("TOOL")，原版红色打印机**根本没调**。
//    → tooltip 里多出 "[工具]" 那一行（问题 2 的直接原因）。
//    → 用 RemoveAllGameItemType() 去掉。
//
// ❌ 坑 C：我用 Harmony 拦 ItemBehaviourManager.Target —— 游戏里 Target 遍历的是
//    targetBehaviourList（ItemTargetBehaviour 实例），**不读物品上的委托**。
//    所以我的 Prefix 一次都没被调用（问题 3 的直接原因）。
//    → 正确做法：直接把 onThisTargetItemFunc 赋值上去，走官方正门。
//    → 前缀抄 b__12_4：它就是 `(s,t) => LabelerHelper.OnTargetItem(t, false)`。
//
// ═════════════ 关于"不能继承 IL2CPP 抽象类"的更正 ═════════════
//   上一版我断定"托管无法给 GameItem 挂委托"，理由是 GameItemFunc 是 abstract class。
//   ★ 这个结论只对了一半：**不能继承**（对的），但**它的委托属性全是
//     `public Func<...> xxxFunc { get; set; }` 公开可写**（dump.cs:59050起）——
//     直接赋值即可，完全不需要继承。红色打印机自己就是这么做的。
//
// ═════════════ 「激活」和「切换」菜单项为什么我原来没有 ═════════════
//   它们不是物品级 target 委托，而是**格子级**委托，在 GameInventoryFunc 上：
//     mayActivateSlotItemFunc / onActivateSlotItemFunc
//     mayToggleSlotItemFunc   / onToggleSlotItemFunc
//   签名是 (GameItem, GameInventory, SlotMarker) —— 带 slot 参数。
//   原版 GeneScanner（基因扫描仪）用的就是这一套（<GeneScanner>b__18_0~3）。
//   → 本轮不实现（真实打印机没有"激活/切换"语义，强行加会让菜单出现点了没反应的项）。
//     等核桃需要时再加，加法就是照抄 GeneScanner 的三参数委托。

// ═══════════════════════════════════════════════════════════
// 【本文件管什么】
//   造一台机器 + 「这个交互该不该我们接」的判据 + 接住之后的落地。
//
// 【其他部分在哪】（V1.2.0 从 Machine.cs 拆出，逻辑逐字未改）
//   · 裸指针闸门 / SafeId        → RealPrinterMod.PointerSafety.cs
//   · 指针白名单与存档扫描        → RealPrinterMod.MachineRegistry.cs
//   · Harmony 交互补丁            → RealPrinterMod.Interact.cs
//   · 编辑窗                      → RealPrinterMod.Editor.cs
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
    /// 造一台真实打印机。
    /// ★★ 完全照抄 ToolDirectory.CreateLabeler 的流程（反汇编取证，见文件头）。
    ///   关键点：① 用目录成品物品，不是 CreateEmptyItem
    ///         ② sprite 用游戏自己的图集（原版就是借用的）
    ///         ③ 挂 LABELER_TAG（不是 "LABELER"）
    ///         ④ 不调 SetGameItemType —— 否则 tooltip 多出"[工具]"行
    ///         ⑤ 交互走 Harmony 打 GameItem.MayTarget / CanTarget / Target
    ///            （"给物品挂托管委托"那条路已证伪，见 Interact.cs 文件头）
    ///
    /// ★ V1.4.12：`valueOverride` 语义：
    ///   · `< 0`（默认）→ 用 `Config.BaseMachineValue`（原版红机口径 50）
    ///   · `>= 0`      → 显式使用传入值（0 = F6 直领防刷钱版；100 = 供货商售价版）
    /// 【为什么不是 `> 0 ? ... : default`】它分不清"没传"和"传 0"——
    ///   而 F6 直领恰恰需要"传 0 就真的是 0"。
    /// </summary>
    private static GameItem CreateRealPrinter(int valueOverride = -1)
    {
        try
        {
            // ① 拿一个"有完整目录数据"的成品物品当底子。
            //   ★ 与红色打印机等价：它也是从目录拿 id="labeler" 的成品。
            //   借用一个真实工具的目录条目最稳（sprite/形状/描述字段都有默认值），
            //   然后把所有字段覆写成我们的。
            GameItem it = null;
            try
            {
                // 借红色打印机的目录条目当模板 —— 和原型同源，字段结构完全一致
                it = DirectoryMaster.Item(TemplateItemId, true);
                if (it == null)
                {
                    // 目录里没有就退回空壳（会缺 sprite，所以下面手动设）
                    it = ItemDirectory.CreateEmptyItem();
                    RLog.Warning("目录里没有 " + TemplateItemId
                        + "，退回CreateEmptyItem —— sprite 必须手动设，否则显示蓝格子。");
                }
            }
            catch (Exception e)
            {
                RLog.Detail("取模板物品异常: " + e.Message);
                try { it = ItemDirectory.CreateEmptyItem(); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            }

            if (it == null)
            {
                RLog.Msg("✘ CreateRealPrinter：模板与空壳都拿不到，放弃创建。");
                return null;
            }

            // ② identifier —— ★ 必须先有 id，后面 IsOurMachine 才认得出
            //   ★ V1.2.0：公开 setter 已经够用，**不再额外反射写 backing field**
            //     （MEMORY 铁律：机器 id 走公开属性 `GameItem.identifier`，绝不反射写）。
            try { it.identifier = MachineIdentifier; } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }

            // ③ sprite：借游戏自己的工具图集（★ 不自建了 —— 原版就是借用的，
            //    自建会踩RenderHandler.LoadFromAtlas 那条链，且样式不统一）
            try
            {
                it.SetSpriteAndShape(ToolAtlasPath, TemplateItemId);   // ★ 借用红色打印机的图
                RLog.Detail("sprite 借用原版工具图（Items/items_tool / labeler）。");
            }
            catch (Exception e) { RLog.Msg("SetSpriteAndShape 异常: " + e.Message); }

            // ③-2 ★★ [V1.4.7 正式功能] 压小形状 2×3 → 1×2
            //   【为什么】实机取证：柜台 18×4=72 格，供货商商品占满后
            //     剩下的空位常常不足 6 格且不连续 → 2×3 的机器放不上，
            //     TryAcceptAllOrDestroy **静默销毁**（不抛异常）。
            //     实测占 69/72 只剩 3 格时机器消失，占 47/72 时正常。
            //   【位置】必须在 SetSpriteAndShape **之后** —— shape 是它
            //     逐像素扫 sprite alpha 推导出来的（反汇编 0x79A480），
            //     不先建好没得压。
            //   【注意】压完必须 SetDataFill 填 1，否则 data 全 0 →
            //     UI 不画占位网格（用户实机截图确认过这个现象）。
            ShrinkShape(it);

            // ④ 名称 / 描述 / 价值 —— 对齐原版写法
            // ★ V1.2.0：**全部字段改走公开 setter**，反射写法已彻底移除。
            //   编译期实测：name / shortDescription / longDescription /
            //   unitValue / unitBaseValue **都有 public setter**，一个都不需要反射。
            //   原先"公开 setter 与反射各写一遍"是历史遗留 —— 两份写法并存
            //   会让人以为"哪个成算哪个"，实际上冗余且更难维护
            //   （字段名字符串写错 = 编译不报、实机静默失效）。
            // ★ V1.4.11：删掉上面那句 `it.SetName("真实打印机")` —— 它和紧接着的
            //   `it.name = "真实打印机"` **设的是同一个字段**（`SetName` 是 name 的
            //   setter 包装）。原版 CreateLabeler 走 SetName 是因为它只有本地化 key；
            //   我们写的是字面量，用公开 setter 就够。两次赋值 = 两次 IL2CPP 调用 + 一个
            //   "到底哪个生效"的疑问，删掉后者中的前者毫无信息损失。
            it.name = "真实打印机";
            // ★ V1.4.1 文案（核桃 2026-10-04）：把"能改什么"写进 tooltip，
            //   不再让玩家猜"真实属性"是什么。
            // ★★ V1.5.0 改写：拖动能用了（与原版红机一致），支持范围也不再是四类。
            //   旧文案里那句"拖动无效"是 V1.4.6 因为拖动会被
            //   `IsDraggingNow()` 短路才写的 —— 现在**事实变了，文案必须跟着变**，
            //   否则玩家照着 tooltip 用会以为功能坏了（比不写更糟）。
            it.shortDescription = "把本机拖到商品上，或装备后左键点商品 → 选档位 → 真值真的改。";
            it.longDescription =
                "真实打印机：改写商品**真值**的机器 ——"
                + "香烟/印花票的真伪、化学品与金属锭的纯度、注射器的真伪与可用性、"
                + "水的纯净度、酒的品质、装备成色、新鲜度、效期、凹陷/生锈/开封、"
                + "仿制品、赠品、模块状态 —— 游戏官方档位里有的类别都可改。"
                + "\n★ 用法一：把本机**拖到**目标商品上（与原版红色标签打印机一致）。"
                + "\n★ 用法二：先「使用 / 装备」本机，再左键点击目标商品。"
                + "\n★ 真值与假值一起改，并执行档位的官方动作 —— 算价、验真、鉴定助手读到的都是同一个值。"
                + "\n所有权 / 任务进度 / 存档相关属性不开放。";
            // ★ 价值：默认对齐原版打印机的 50（红色打印机 unitValue = 0x32）。
            //   原来写 1500 是我自己定的，导致 tooltip 里"预估价值"比原版高 30 倍，不对齐。
            // ★ V1.3.0：同行渠道传 valueOverride=100（核桃拍板售价口径）。
            int value;
            if (valueOverride >= 0) value = valueOverride;
            else value = Config.BaseMachineValue;
            it.unitValue = (long)value;
            it.unitBaseValue = (long)value;

            // ⑤ ★★ tag：照抄原版**三个**，另加两条本机专用
            //    ⑤-1「重要物品」标记 —— 缺了它右键菜单会少一档
            try { it.EnableTag(ImportantTag, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-2 ★ 这才是真正的"标签打印机"标记（原版 InitLabelerItem 挂的就是它）
            try { it.EnableTag(ToolTagLabeler, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-3 ★★ V1.5.0 补回：原版 CreateLabeler 的**第一条** EnableTag 就是它，
            //       V1.4.x 裁成面向用户版时漏挂了。它是档案箱（dossier）白名单
            //       `AllowOnlyTaggedItemsOr{DOCUMENT, MAGNIFYING_GLASS, LABELER, LABELER_BLUE}`
            //       里本机**唯一能命中**的一条 → 漏了就放不进档案箱。
            //       详见 Core.cs 里 DossierTag 的取证注释。
            try { it.EnableTag(DossierTag, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-4 ★★ V1.5.0：工具箱入箱 tag。工具箱白名单认 identifier=="labeler"，
            //       本机 id 是 real_printer 走不通 → 挂 TOOLBOX_ITEM_TAG 补位。
            try { it.EnableTag(ToolboxItemTag, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-5 我们自己的识别 tag
            try { it.EnableTag(OurTag, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }

            // ⑥ ★★ 不要 SetGameItemType("TOOL")！
            //    原版红色打印机从头到尾没调过它（反汇编里无此调用）。
            //    调了之后 tooltip 会多出 "[工具]" 那一行 —— 就是问题 2。
            //    如果之前版本已经写进去了，这里显式清掉。
            try { it.RemoveAllGameItemType(); }
            catch (Exception e) { RLog.Msg("RemoveAllGameItemType 异常: " + e.Message); }

            // ⑦ owned=true
            try { GeneralHelper.SetItemOwned(it, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }

            // ⑧ ★★★ 交互：走 Harmony 打 GameItem.MayTarget/CanTarget/Target（见方法注释）
            if (!SelfCheckInteractionPatchesReady())
                RLog.Error("★★ 建机时挂交互委托失败 —— 这台打印机左键点物品不会有任何反应。");

            // ⑨ ★★ V0.4.8：登记指针进白名单（热路径 IsOurMachine 只查这张表）
            RegisterMachine(it);

            RLog.Detail("已创建真实打印机（照抄 CreateLabeler 机制）。");
            return it;
        }
        catch (Exception e)
        {
            RLog.Msg("创建工具失败: " + e.Message);
            return null;
        }
    }
    /// <summary>
    /// ★★★ V0.4.0：这里**不再挂任何委托**。
    ///
    /// 【为什么推翻 V0.3.0】—— 实机证据（2026-10-03 01:26 日志）：
    ///   委托赋值成功、回读 6 个全在，但左键点了没反应，同时日志刷出 2244 次：
    ///     [ERROR] [Il2CppInterop] During invoking native->managed trampoline
    ///     System.Reflection.TargetException: Object does not match target type.
    ///       at System.Reflection.RuntimeMethodInfo.Invoke (...)
    ///       at GameItem.MayTarget (GameItem targetItem)
    ///
    ///   翻译：游戏确实读到了我挂的委托、也确实调了，但 **Il2CppInterop 的蹦床
    ///   无法把 IL2CPP 的调用转回我的托管 lambda** —— 委托参数是 IL2CPP 侧的
    ///   GameItem 指针，反射 invoke 时类型对不上，抛 TargetException。
    ///
    ///   ★★★ 这条路在 MelonLoader 0.7.4 + Il2CppInterop 上是**走不通的**，
    ///      不是写法不对，是 trampoline 机制不支持（红色打印机能用是因为
    ///      它的委托是 IL2CPP 原生闭包，不是托管 lambda）。
    ///
    /// 【正解 —— 照抄生态里已验证的两个 mod】
    ///   ContainerSuite.DIYUpgrade/DiyUpgradeMod.Patches.cs
    ///   XIAOWOTradePerks/BayExpansionTargetPatch.cs
    ///   它们都用 Harmony 打 **GameItem 的实例方法**：
    ///     GameItem.MayTarget / GameItem.CanTarget / GameItem.Target
    ///     ItemBehaviourManager.Target（否决别人的）
    ///     ItemMouseDragHandler.EndDrag（拖动路径）
    ///   这些是**普通方法**，Harmony 能正常重写 —— 没有任何 trampoline 问题。
    ///
    /// 【反汇编补充证据】GameItem.Target(0x79C080) 里读的正是
    ///   [rbx+0xC8](onItemTargetItemFunc) / [rbx+0xE0](onThisTargetItemFunc)
    ///   [rbx+0xD0](mayThisTargetItemFunc)，
    ///   说明委托就是在这个方法里被调的 —— 它是物品的官方交互入口，
    ///   Harmony 打它等价于"在官方入口接管"，语义上比拦 ItemBehaviourManager 更准。
    ///
    /// ★ 本方法**只做补丁就绪自检，不写任何字段**。
    /// </summary>
    private static bool SelfCheckInteractionPatchesReady()
    {
        // ★ V1.4.6：删除 `_attachCheckedAt` 死集合（V1.3.0 前还挂托管委托时的遗留，
        //   只被 Remove、从没被 Add）。同时去掉无用的 `it` 参数。
        // ★ 现在的自检：3 个 Harmony 交互补丁挂上没挂上，才决定能不能用。
        bool ok = HasPatchesReady();
        if (!ok)
        {
            //★★ V1.0.8：措辞改准 —— 缺的是**必需**补丁（MayTarget/CanTarget/Target）。
            //   ★ V1.4.11：EndDrag 补丁已整类删除，"四个补丁"的说法作废。
            RLog.Error("★★ 必需的交互补丁缺失 —— 左键点物品不会有任何反应。");
        }
        // ★ V1.4.11：删掉 ok 分支里的 Detail。
        //   它原本写「… + EndDrag」，而 EndDrag 补丁 V1.4.11 已删 → 这句话本身就是错的。
        //   而且这个函数**每次建机都调**，供货商每次摆货调一次 —— 一条开机时的
        //   "补丁已挂"流水，重复打几十次毫无信息量（启动横幅与自检已经报过一次）。
        return ok;
    }

    /// <summary>交互补丁是否已挂载（由 Interact.cs 的自检设置）。</summary>
    private static bool HasPatchesReady() => _patchesReady;
    private static bool _patchesReady;

    /// <summary>由 Interact.cs 在补丁自检时调用。</summary>
    internal static void MarkPatchesReady(bool ready) => _patchesReady = ready;


    /// <summary>
    /// 目标物品能不能改。
    /// ★ 与原版 b__12_2/b__12_3 的差别：
    ///   原版用 GeneralHelper.IsItemOwned(t)（只允许改玩家已拥有的）。
    ///   我们放宽到"不是我们自己的打印机" —— 因为黑印是测试期工具，
    ///   玩家会想改货架上的、别人的装备。**是否 owned 不影响能不能改，只影响价格。**
    ///   （原版那条限制是游戏平衡设计，不是技术限制。）
    /// </summary>
    internal static bool IsTargetEditable(GameItem target)
    {
        if (target == null) return false;
        //★★★ V0.4.8：这里**不再读任何裸指针字段**。
        //   V0.4.6/0.4.7 的 `IdFieldNull(target)` 会去读 target+0x1C0，
        //   而拖动中 target 可能是**已被销毁的物品**（V0.4.7 实测崩在
        //   `il2cpp_string_length`，就是这条路的野指针喂进了原生函数）。
        //   → 判据改成纯指针：能不能拿到指针 + 是不是我们自己那台。
        // ★★ V1.0.6：抽成 IsSafeTarget —— 崩溃那次的漏洞就在这条路上。
        //   原来这里逐字重复三道闸，但**和后面的 IsOurMachine 一样**：
        //   全都只看"指针长得像不像"，**没有拦住"正在被销毁"的对象**
        //   （WasCollected 只在 GC 回收后才为真，Unity 原生对象被销毁时未必立刻置位）。
        //   → 统一走一处判据，将来加固只改一个地方。
        if (!IsSafeTarget(target)) return false;
        // 目标不能是我们自己的打印机
        try { if (IsOurMachine(target)) return false; } catch { return false; }

        // ★★ V1.5.0：**拖动中**遇到容器类目标一律不接管 —— 让游戏走它自己的"放进去"。
        //
        // 【为什么必须有这道闸】`MayTarget` 返回 true 时，游戏认为"这是对目标使用工具"，
        //   于是**不再走放置分支** —— 玩家把打印机拖进工具箱/档案箱会"没反应"
        //   （我们接管了，但箱子没有真值特性 → 静默什么都不发生）。
        //   这正是"能放进档案箱和工具箱"这条需求背后真正的阻碍：光补 tag 不够，
        //   还得在拖动时不抢这次交互。
        //
        // 【判据】`CONTAINER_TAG` —— 容器通用标记。工具箱（InitToolbox）、
        //   档案箱（InitContainerItemTagOnly）、背包（InitBackpackItem）都会打它，
        //   一条就够，不用逐个列 id。
        //
        // 【为什么只在拖动中生效】非拖动时（装备后左键点）不涉及放置，
        //   加这道闸只会把"点了没反应"变成"弹红字"，是**行为变更**；
        //   限制在拖动中 = 只在真正需要让路的场景让路，影响面最小。
        //
        // ★★ V2：判据改走 `IsContainerLikeCached`（原来是裸 `IsContainerLike`）。
        //   【为什么必须缓存】这里是热路径（MayTarget/CanTarget 每秒 900+ 次），
        //     而拖动中游戏**每帧对可见的每个物品都问一遍** —— 同一帧里的目标是
        //     一整排**不同**物品，不是同一个。所以缓存必须按指针多槽；
        //     单槽会被下一个物品顶掉，等于没缓存（详见 IsContainerLikeCached）。
        if (IsDraggingNow() && IsContainerLikeCached(target)) return false;

        return true;
    }

    /// <summary>
    /// ★ V1.5.0：这是不是个容器（工具箱 / 档案箱 / 背包 / 箱子…）。
    /// ★★ V2：**只剩冷路径在用**（拖动落点日志那一行；每次拖动 1~几次）。
    ///   热路径（IsTargetEditable）走 <see cref="IsContainerLikeCached"/>。
    ///   失败返回 false（宁可多接管一次，也不让路给错误目标）。
    /// </summary>
    private static bool IsContainerLike(GameItem target)
    {
        try { return target.IsTag(ContainerTag); }
        catch (Exception ex) { RLog.Swallowed("IsContainerLike", ex); return false; }
    }

    // ────────────────────────────────────────────────────────────
    // ★★ V2：拖动中容器判据的**多槽缓存**（按指针）。
    //
    // 【为什么不是单槽】上一版想用单槽的理由是"拖动期间落点唯一（_pendingDrop 只有一个）"
    //   —— 那个前提对 **落点队列**成立，对**这个判据**不成立：
    //   判据挂在 IsTargetEditable 上，游戏每帧对**可见的每个物品**都问一遍
    //   （N 个不同指针）。单槽每帧都被下一个物品顶掉 → N 个物品 × 每帧重算
    //   = **一次都没省**。
    // 【为什么可以长驻整次拖动】容器 tag 在一次拖动里不会变；
    //   拖动结束时整体清空（清空点在 SampleDragState 的下降沿）。
    // 【为什么缓存键用裸指针】不进 IL2CPP，半死对象也安全（同活体闸纪律）；
    //   指针被复用（极罕见）最坏让"让路判据"错一次，不崩。
    // ────────────────────────────────────────────────────────────
    private const int ContainerCacheMax = 512;
    private static readonly Dictionary<long, bool> _containerCache = new Dictionary<long, bool>();

    private static bool IsContainerLikeCached(GameItem target)
    {
        long p;
        try { p = ((Il2CppObjectBase)target).Pointer.ToInt64(); }
        catch { return false; }
        if (p == 0) return false;

        bool v;
        if (_containerCache.TryGetValue(p, out v)) return v;

        // 上限保护：异常路径（指针狂变）下也不能无界增长
        if (_containerCache.Count >= ContainerCacheMax) _containerCache.Clear();

        try { v = target.IsTag(ContainerTag); }
        catch (Exception ex) { RLog.Swallowed("IsContainerLike", ex); v = false; }

        _containerCache[p] = v;
        return v;
    }

    /// <summary>
    /// ★★ V1.0.6：**纯内存安全**判据 —— 现在是否可以安全地进 IL2CPP。
    ///
    /// 【为什么与 IsTargetEditable 分开】
    ///   `IsTargetEditable` 验的是「能不能作为交互目标」，含"不是我方机器"这类
    ///   **业务条件**；这里验的是**纯内存安全** —— 对象本身还活着吗。
    ///
    /// 【实机崩溃（核桃 2026-10-03 18:47 拖动打印机）】
    ///   AccessViolationException @ IL2CPP.il2cpp_runtime_invoke，
    ///   堆栈：Patch_Target.Prefix ← managed_EndDrag → OnTargetItemFunc
    ///        → OpenEditor → License.RejectReason → DisplayNameOf
    ///        → SafeStr → GetDisplayName ←崩在这。
    ///   拖动时游戏在 `ItemMouseDragHandler.EndDrag` 内部自己调 `GameItem.Target`
    ///   （这条重复路径 MEMORY 已记），此时传入的 target 可能**正在被销毁**：
    ///   指针非零、也像堆指针，**但已经不是活对象** —— 现有三道闸全都放行了。
    ///   → 进去调 GetDisplayName（il2cpp_runtime_invoke）就是野指针喂原生函数。
    ///   ★ `try/catch` **拦不住** AccessViolation（MEMORY 铁律），只能事前拦。
    internal static bool IsSafeTarget(GameItem t)
    {
        if (t == null) return false;
        try
        {
            if (((Il2CppObjectBase)t).WasCollected) return false;
            long p = ((Il2CppObjectBase)t).Pointer.ToInt64();
            if (p == 0) return false;
            return LooksLikeHeapPtr(new IntPtr(p));
        }
        catch { return false; }
    }

    /// <summary>
    /// ★★ 游戏当前是否**正在拖动物品**。
    ///
    ///【为什么用它】拖动时游戏会在 `ItemMouseDragHandler.EndDrag` 内部
    ///   **自己**调 `GameItem.Target`（这条重复路径 MEMORY 早就记过），
    ///   此时传进来的 target 可能**正在被销毁** ——
    ///   指针非零、也像堆指针，但已经不是活对象，
    ///   连`IsSafeTarget` 都放行（`WasCollected` 未必及时置位）。
    ///   然后任何 IL2CPP 调用（读 identifier / itemFeatures）都会
    ///   **AccessViolation 猝死**（try/catch 拦不住）。
    ///
    /// 【为什么这个判据安全】它只读一个 `bool`：
    ///   `ItemMouseDragHandler.current`（静态）→ `isDragging`（bool 字段 0x68）。
    ///   **不碰任何目标对象** → 即使目标是半死的也不会崩。
    ///   （`current` 本身可能是 null，判空即可。）
    ///
    /// ★★★ V1.5.0：**改成读每帧采样一次的缓存**，不再每次实时读。
    ///   【为什么必须改】V1.5.0 把这道判据挂进了 `IsTargetEditable`，
    ///     而它在 `MayTarget`/`CanTarget` 上 —— 实机日志显示那条路径
    ///     **每秒被调 900+ 次**（游戏对每个物品每帧都问一遍"能不能作用"）。
    ///     实时读 = 每秒几千次 `ItemMouseDragHandler.current` + `IsDraggingItem`
    ///     两次 IL2CPP 调用，纯属为了拿到一个几乎不变的值。
    ///   【为什么缓存一帧是安全的】
    ///     · `isDragging` 由输入事件驱动，一次真实拖动必然跨多帧（拖动阈值 10px）
    ///       → 采样滞后一帧不会漏判"正在拖动"；
    ///     · 反方向（缓存 false 而实际在拖）只在"同一帧内开始并结束拖动"时发生，
    ///       物理上不可能。
    ///   【兜底】采样失败时置 false —— 宁可走点击路径，也不卡在"永远以为在拖动"。
    /// </summary>
    private static bool IsDraggingNow()
    {
        return _dragging;
    }

    /// <summary>
    /// ★★ V2：**实时**读拖动状态 —— 只给 QueueDrop 做落点归类用（落点级频率）。
    /// 【为什么不复用 IsDraggingNow】那是热路径（IsTargetEditable，每秒数千次）的缓存版；
    ///   落点这一处要的是"此刻"，多读两次 IL2CPP 属性完全付得起。
    /// 【关于可靠性】它只能回答"此刻 isDragging 是不是 true"，**回答不了**
    ///   "EndDrag 有没有跑完" —— 所以它**不承担安全职责**（安全由"一律压后一帧"保证，
    ///   见 OnTargetItemFunc）。判错的最坏后果只是"关着拖动触发时多开一次窗"，不崩。
    /// </summary>
    private static bool IsReallyDraggingNow()
    {
        try
        {
            var h = ItemMouseDragHandler.current;
            return h != null && h.IsDraggingItem;
        }
        catch (Exception ex) { RLog.Swallowed("IsReallyDraggingNow", ex); return false; }
    }

    private static bool _dragging;
    private static bool _draggingPrev;        // 上一帧的拖动状态（只为抓"结束"下降沿）
    private static bool _dragProbeOkLogged;   // "机制正常"只报一次
    private static bool _dragProbeWarned;     // "首次异常"只报一次

    /// <summary>
    /// ★ 每帧采样一次"是否正在拖动"（由 OnUpdate 调用，顺序必须早于 TickPendingDrop）。
    ///
    /// ★★ V2 两处新增：
    ///   ① **下降沿清容器缓存**。
    ///      【为什么必须在这里】看上去该在 TickPendingDrop 里清，但那条路在
    ///        `_pendingDrop == null` 时**首行就 return** —— 而"拖动中让路给箱子"
    ///        这条路恰恰**不会产生落点** → 缓存永远清不掉：两次拖动之间指针被复用
    ///        就会留下错误结论，而且会无界增长。这里是每帧都跑的，唯一可靠的位置。
    ///   ② **可见化**：首次读到 handler / 首次异常各报一次。
    ///      【为什么两个标志要分开】合成一个的话，首次采样一旦抛异常，
    ///        之后就算恢复正常也永远不会再打"机制正常"。
    ///      【为什么只报一次】本机制每帧都跑，报多了就是刷屏；
    ///        它是"静默失效最难查"的那一类（OnUpdate 没挂 / 静态字段改名 /
    ///        handler 拿不到 → 让路与延迟开窗会**一起**失效且毫无日志）。
    /// </summary>
    private static void SampleDragState()
    {
        try
        {
            var h = ItemMouseDragHandler.current;
            _dragging = h != null && h.IsDraggingItem;

            // 拖动结束的下降沿 → 清容器缓存（见上）
            if (_draggingPrev && !_dragging) _containerCache.Clear();
            _draggingPrev = _dragging;

            // ★ V2-7：首次读到 handler 且**能打印时**报一次（含机制是否启用）。
            //   【为什么必须带 `Config.VerboseLog` 才是对的】实测踩到：第一版把标志
            //     无条件置位、只让 RLog.Detail 自己吞掉输出 —— 于是"第一次拖动
            //     发生在 VerboseLog 还没打开的时候"就把这一次机会用掉了，
            //     之后永远看不到这行（2026-10-16 实机日志：一次都没出现）。
            //   → 标志只在**真的打出去**的时候置位，晚一点打总比永远不打强。
            //   【失败方向不受影响】异常那条走 RLog.Warning，与 VerboseLog 无关。
            if (!_dragProbeOkLogged && h != null && Config.VerboseLog)
            {
                _dragProbeOkLogged = true;
                RLog.Detail("拖动采样机制正常：ItemMouseDragHandler.current 可读（DragToUse="
                            + (Config.DragToUse ? "开" : "关") + "）。");
            }
        }
        catch (Exception ex)
        {
            RLog.Swallowed("SampleDragState", ex);
            _dragging = false;

            if (!_dragProbeWarned)
            {
                _dragProbeWarned = true;
                RLog.Warning("⚠ 拖动采样异常 —— 拖动让路与延迟开窗可能失效。原因："
                           + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// ★ 官方交互入口的落地实现（由 Harmony 的 GameItem.Target 补丁调用）。
    ///   对应原版 LabelerHelper.OnTargetItem → LabelerUIManager.OpenUI(target)。
    ///
    /// ★★ V1.5.0：拖动**不再短路**，改成"压后一帧"。
    /// ★★★ V2：**无条件压后一帧**，且延迟不再受任何 cfg 影响（见方法体内说明）。
    /// </summary>
    internal static void OnTargetItemFunc(GameItem target)
    {
        // ★★ V1.5.0：拖动期**不立刻开窗**，把落点压到"拖动结束后的下一帧"。
        //
        // 【V1.1.9~V1.4.x 的旧做法】第一行 `if (IsDraggingNow()) return;`
        //   —— 整条拖动路径短路。代价是**拖动永远不触发**，与原版红机不一致。
        //
        // 【为什么不能直接删掉那一行】它挡的是真实存在的事故：
        //   AccessViolationException @ il2cpp_object_get_class，实机堆栈
        //     Patch_Target.Prefix ← managed_EndDrag → OnTargetItemFunc
        //       → OpenEditor → DetectFeatures → Il2CppObjectPool.Get
        //   根因：游戏在 `ItemMouseDragHandler.EndDrag` 内部自己调 `GameItem.Target`，
        //     此刻传进来的 target 可能**正在被销毁** —— 指针非零、像堆指针，
        //     但已经不是活对象（`IsSafeTarget` 的 WasCollected 拦不住）。
        //     而 AccessViolation **不可捕获**（MEMORY 铁律），只能事前不进去。
        //
        // 【V1.5.0 的折中：既满足"能拖"，又不站在崩溃窗口里】
        //   拖动中：只记一个**指针 + 托管引用**，一个 IL2CPP 调用都不做。
        //   拖动结束后（OnUpdate 里 IsDraggingItem 已为 false，游戏 EndDrag 已跑完、
        //   状态已稳定）才真正开窗，并在开窗前复核指针没漂。
        //   → 把"读 IL2CPP"从最危险的时刻挪到最安全的时刻。
        //
        // ═══════════════════════════════════════════════════════════
        // ★★★ V2 修正：**不再在这里判拖动，一律入队**。
        //
        // 【原来写的是】`if (Config.DragToUse && IsDraggingNow()) { QueueDrop(target); return; }`
        //   它有两个问题：
        //   ① **安全机制被功能开关绑死**：DragToUse 一关（README 恰好教玩家
        //      "拖机会闪退就把拖动关掉"），延迟整条消失 → 落点直接
        //      RunTargetItemFunc → OpenEditor → DetectFeatures，
        //      正好回到上面那段实机事故的窗口里。**关掉功能反而更容易崩**。
        //      而且同一处旧注释写的"cfg 关掉就退回 V1.4.x 行为：拖动不触发"
        //      与代码不符（V1.4.x 那行是**无条件**短路）：既触发、又不延迟。
        //   ② **判据在最需要它的那一瞬间答不准**：`IsDraggingNow()` 读的是**上一帧的采样**，
        //      而 EndDrag 那一刻 isDragging 已经是 false、本帧采样又未必跑过；
        //      实时读也只能回答"此刻是不是在拖"，回答不了"EndDrag 跑完没有" ——
        //      而危险窗口恰恰就是"isDragging 已 false、EndDrag 还没跑完"那一段。
        //
        // 【V2 的结构性解法】不判了：**所有 Target 调用一律压后一帧**。
        //   · 点击路径同样只晚一帧（≈16ms，玩家无感）；
        //   · 换来的是"开窗永远不会发生在 EndDrag 的调用栈里" ——
        //     不依赖缓存准不准、不依赖实时读拿到什么、不依赖游戏哪个版本先置 false；
        //   · DragToUse 退化成**纯功能开关**，由 TickPendingDrop 在安全时机处理。
        // ═══════════════════════════════════════════════════════════
        QueueDrop(target);
    }

    /// <summary>★ 真正落地开窗（拖动路径与点击路径共用）。</summary>
    private static void RunTargetItemFunc(GameItem target)
    {
        try
        {
            if (target == null) return;
            // ★ RealPrinter：编辑器入口换成只渲染真值的 OpenEditor。
            //   许可闸门在里面（不在官方档位目录内 → 不开窗，只给一句人话提示）。
            OpenEditor(null, target);
        }
        catch (Exception e)
        {
            RLog.Error("★ OnTargetItemFunc 异常: " + e.Message);
            // 栈也走 RLog —— 与上面同一出口，日志前缀才统一（RLog 会自动加 [真印]）。
            try { RLog.Error(e.StackTrace); } catch (Exception ex) { RLog.Swallowed("OnTargetItemFunc", ex); }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ★★ V1.5.0：拖动落点的延迟开窗队列
    //
    // 【为什么是"一帧之后"而不是"立刻"】见 OnTargetItemFunc 里的实机事故记录。
    // 【为什么只留最后一个】一次拖动里游戏可能多次调 Target（悬停高亮也会问），
    //   后到的覆盖先到的 —— 真正要开窗的是**松手那一刻**的落点。
    // ★★ V2：点击路径也走这里（见 OnTargetItemFunc），所以本队列现在
    //   **同时服务"点击"和"拖动落点"**，两者的差别只剩 `FromDrag` 一个标记。
    // ═══════════════════════════════════════════════════════════
    private sealed class PendingDrop
    {
        public GameItem Target;
        public IntPtr Ptr;
        public float QueuedAt;
        /// <summary>★ V2：这次落点是不是拖动来的（决定 DragToUse 关着时要不要丢弃）。</summary>
        public bool FromDrag;
    }

    private static PendingDrop _pendingDrop;

    /// <summary>
    /// 记下落点。**只做指针级操作，绝不进 IL2CPP 读字段。**
    ///
    /// ★★ V2：多了一件事 —— 给落点打"是不是拖动来的"标记，并（VerboseLog 下）
    ///   打一行落点日志。日志**只在这一处**：它同时服务"拖动路径可见化"（V2-7）
    ///   与"主仓库落点取证"（V2-4 的 PR-B），不另写第二份。
    ///   频率 = 每次点击/落点 1 行，不在热路径上。
    /// </summary>
    private static void QueueDrop(GameItem target)
    {
        if (target == null) return;
        IntPtr p;
        try { p = ((Il2CppObjectBase)target).Pointer; }
        catch { return; }
        if (p == IntPtr.Zero) return;

        // 尽力而为的归类。判错的**最坏后果**只是"关着拖动触发时多开一次窗"
        // （不是崩溃 —— 安全由"一律压后一帧"保证，与这个标记无关）。
        bool fromDrag = IsDraggingNow() || IsReallyDraggingNow();

        _pendingDrop = new PendingDrop
        {
            Target = target,
            Ptr = p,
            QueuedAt = Time.unscaledTime,
            FromDrag = fromDrag,
        };

        // ★ V2-7 / V2-4：落点日志（冷路径）。三个布尔就是"主仓库 tag"那张取证明细：
        //   ContainerTag  = 目标自己是不是容器（现在的让路判据）
        //   在柜台/在主仓库 = 目标是不是已经躺在某个库存里（V2-4 要判的东西）
        if (Config.VerboseLog)
            RLog.Detail("拖动落点: id=" + SafeId(target)
                        + " 拖动来源=" + fromDrag
                        + " 目标带ContainerTag=" + IsContainerLike(target)
                        + " 在柜台=" + IsOnFrontCounter(target)
                        + " 在主仓库=" + IsInWarehouse(target));
    }

    /// <summary>
    /// 每帧检查落点队列。★ 队列为空时**只是一次静态字段读**（性能铁律：
    /// 常态路径零开销），有落点才去读 `IsDraggingItem` 那个 bool。
    ///
    /// ★★ V2：多了一道"拖动触发关着 → 丢弃拖动落点"的闸（见下 ①）。
    /// </summary>
    private static void TickPendingDrop()
    {
        var pd = _pendingDrop;
        if (pd == null) return;

        // ① ★★ V2：拖动触发关着（DragToUse=false）→ 拖动来的落点直接丢弃，
        //    回到 V1.4.x 的"拖动不触发"。
        //    【为什么放在这里而不是 OnTargetItemFunc】那里不能判——见该方法的说明：
        //      入队是无条件的安全动作，**要不要开窗**才是功能开关的事，
        //      而"要不要开窗"必须等到状态稳定（本方法）才有资格判断。
        //    【为什么判 DragToUse 而不是 cfg 变更】玩家可能在拖动途中改配置，
        //      以开窗时刻的配置为准更符合直觉。
        if (pd.FromDrag && !Config.DragToUse)
        {
            _pendingDrop = null;
            return;
        }

        // ② 拖动还没真正结束 → 继续等。
        // ★ 超时保护：万一 isDragging 卡住（异常中断拖动），不能在玩家不知情时
        //   憋一个幽灵窗口等下去 —— 2 秒后丢弃。
        if (IsDraggingNow())
        {
            if (Time.unscaledTime - pd.QueuedAt < 2f) return;
            _pendingDrop = null;
            RLog.Detail("拖动落点等待超时（isDragging 未复位），已丢弃本次落点。");
            return;
        }

        _pendingDrop = null;
        var t = pd.Target;
        try
        {
            if (t == null) return;
            // ① 活体闸（纯指针，不进 IL2CPP）
            if (!IsSafeTarget(t)) return;
            // ② 指针漂移闸：对象被回收/地址复用给了别的东西 → 放弃。
            //    这正是"读档后/卖掉后旧指针被认成自己人"那类事故的同款防线。
            IntPtr now;
            try { now = ((Il2CppObjectBase)t).Pointer; } catch { return; }
            if (now != pd.Ptr) return;

            RunTargetItemFunc(t);
        }
        catch (Exception e)
        {
            RLog.Error("★ 拖动落点开窗异常: " + e.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 【V1.2.0 已删除】`SetPrivateField(object, string fieldName, object)` —— 反射写
    //   `<xxx>k__BackingField` 的那套工具。
    //   【为什么删】建机用到的全部字段（identifier / name / shortDescription /
    //     longDescription / unitValue / unitBaseValue）**都有公开 setter**（编译期实测），
    //     反射是纯冗余 —— 而且字段名是字符串，写错不报错、只会实机静默失效。
    //   【恢复方法】若将来某个字段变成只读（getter only）导致编译不过：
    //     ① 加回 `using System.Reflection;`
    //     ② 写一个同名方法，内部 `GetField(name, Instance|NonPublic)` → `SetValue`
    //     ③ **只在那个字段上用**，并在调用点注明"为什么这里不能用公开 setter"。
    //     历史实现见 `RealPrinter_V1.1.11_前.bak/RealPrinterMod.Machine.cs`。
    // ═══════════════════════════════════════════════════════════

    /// <summary>播放"咔哒"音效（复用游戏内置）。</summary>
    private static void PlayRatchet()
    {
        try
        {
            var audio = EmporiumAudioPlayer.Instance;
            if (audio != null && audio.ratchet != null) audio.ratchet.Play();
        }
        catch (Exception ex) { RLog.Swallowed("PlayRatchet", ex); }
    }
}
