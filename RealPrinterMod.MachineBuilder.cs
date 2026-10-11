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
    ///         ② sprite 用游戏自己的 atlas（临时借一个原版图，样式后续换）
    ///         ③ 挂 LABELER_TAG（不是 "LABELER"）
    ///         ④ 不调 SetGameItemType —— 否则 tooltip 多出"[工具]"行
    ///         ⑤ 直接给 onThisTargetItemFunc 赋值，走官方交互正门
    ///  ⑩ ★ V1.3.0：`valueOverride` > 0 时用它当机器价值（供货商上货卖 100，
    ///     F6 直领沿用原版红机的固定 50）。不传（0）就用固定 50。
    /// </summary>
    /// <summary>
    /// ★ V1.4.12：`valueOverride` 语义改清楚：
    ///   · `< 0`（默认）→ 用 `Config.BaseMachineValue`（原版红机口径 50）
    ///   · `>= 0`      → 显式使用传入值（0 = F6 直领防刷钱版；100 = 供货商售价版）
    /// 【为什么改】原逻辑 `> 0 ? ... : default` 无法区分"没传"和"传 0"——
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
            // ★ V1.4.6：文案明确排除"拖动"用法（核桃 2026-10-04 拍板）。
            //   【为什么必须写清】拖动路径在 OnTargetItemFunc 里被 IsDraggingNow 短路
            //     （V1.1.9 防拖动期 AccessViolation），拖动**不会**开窗。
            //     原版红机支持拖动，玩家会自然地把本机也拖过去 → 一定失败。
            //     → tooltip 里明说"拖动无效"，把玩家引导到正确用法。
            it.shortDescription = "用法：装备或使用本机后，左键点目标商品即可（拖动无效）。";
            it.longDescription =
                "真实打印机：能改写四类商品真实品质的机器 ——"
                + "香烟的真伪、化学品的纯度、金属锭的纯度、注射器的真伪与可用性。"
                + "\n★ 正确用法：先「使用 / 装备」本机，再左键点击目标商品 → 选择档位 → 扣费写入。"
                + "\n★ 直接拖到商品上无效（与原版红机不同，拖动不会触发）。"
                + "\n所有权 / 任务进度 / 存档相关属性不开放。";
            // ★ 价值：默认对齐原版打印机的 50（红色打印机 unitValue = 0x32）。
            //   原来写 1500 是我自己定的，导致 tooltip 里"预估价值"比原版高 30 倍，不对齐。
            // ★ V1.3.0：同行渠道传 valueOverride=100（核桃拍板售价口径）。
            int value;
            if (valueOverride >= 0) value = valueOverride;
            else value = Config.BaseMachineValue;
            it.unitValue = (long)value;
            it.unitBaseValue = (long)value;

            // ⑤ ★★ tag：照抄原版，三个都不能少
            //    ⑤-1「重要物品」标记 —— 缺了它右键菜单会少一档
            try { it.EnableTag(ImportantTag, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-2 ★ 这才是真正的"标签打印机"标记（原版 InitLabelerItem 挂的就是它）
            try { it.EnableTag(ToolTagLabeler, true); } catch (Exception ex) { RLog.Swallowed("CreateRealPrinter", ex); }
            //    ⑤-3 我们自己的识别 tag
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
        return true;
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

    /// ★ 官方交互入口的落地实现（由 Harmony 的 GameItem.Target补丁调用）。
    ///   对应原版 LabelerHelper.OnTargetItem → LabelerUIManager.OpenUI(target)。
    ///
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
    /// </summary>
    private static bool IsDraggingNow()
    {
        try
        {
            var h = ItemMouseDragHandler.current;
            if (h == null) return false;
            return h.IsDraggingItem;
        }
        catch (Exception ex) { RLog.Swallowed("IsDraggingNow", ex); return false; }
    }

    /// <summary>
    /// ★ 官方交互入口的落地实现（由 Harmony 的 GameItem.Target 补丁调用）。
    ///   对应原版 LabelerHelper.OnTargetItem → LabelerUIManager.OpenUI(target)。
    /// </summary>
    internal static void OnTargetItemFunc(GameItem target)
    {
        //★★★ V1.1.9 **根治拖动期崩溃**（AccessViolation @ il2cpp_object_get_class）。
        //   【实机堆栈（核桃 2026-10-03 21:59）】
        //     Patch_Target.Prefix ← managed_EndDrag → OnTargetItemFunc
        //       → OpenEditor → DetectFeatures
        //         → Il2CppObjectPool.Get → il2cpp_object_get_class← AccessViolation
        //   ★ 与外观无关：拖动时游戏在 `EndDrag` 内部自己调 `GameItem.Target`，
        //     传入的 target **正在被销毁**。`IsSafeTarget` 挡不住它
        //     （`WasCollected` 只在 GC 回收后才为真，Unity 原生对象销毁时未必置位）。
        //
        //   【正解：用游戏自己提供的"正在拖动"标志整个跳过这一路】
        //     `ItemMouseDragHandler.current.IsDraggingItem`（dump.cs:63859，
        //     内部读 `isDragging` 字段 0x68）—— **一个bool，不碰任何对象**。
        //   → 拖动期间玩家本来就不该开窗（他只是在挪位置），
        //     静默跳过**既安全又符合预期**。
        //   ★ 这是"事前不进 IL2CPP"，而不是"加 try"（MEMORY 铁律：AV 不可捕获）。
        if (IsDraggingNow()) return;

        try
        {
            if (target == null) return;
            // ★ V1.4.11：删掉这里的 `RLog.Detail("交互被触发：target=" + SafeId(target))`。
            //   【为什么】两处理由，第二条是硬的：
            //     ① **重复**：`Patch_Target.Prefix` 上一行已经打了
            //        「交互被触发：self=… → target=…」，信息完全覆盖。
            //     ② ★★ **`SafeId` 是有代价的**（MEMORY 铁律 8② 的同类问题）：
            //        它在"命中我方白名单"时会 `ReadFieldPtr` + `il2cpp_string_length`
            //        + `il2cpp_string_chars` + `Marshal.Copy` —— 四次原生调用加一次
            //        内存拷贝。而 `RLog.Detail` 的开关在**函数里面**，
            //        `SafeId(target)` 这个**实参**在 VerboseLog 关着时照样求值。
            //        → 看着 gated、实际没 gated 的典型形态（横幅那行同病，已一起修）。
            //   ★ RealPrinter：编辑器入口换成只渲染真值的 OpenEditor。
            //     许可闸门在里面（不在四类许可内 → 不开窗，只给一句人话提示）。
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
