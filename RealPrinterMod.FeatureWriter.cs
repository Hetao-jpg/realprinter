// ═══════════════════════════════════════════════════════════
// ★ 真值写入引擎 —— 把"贴标签"变成"游戏状态真的变了"
//
// 【本文件管什么】
//   `WriteFeature` 一件事：把一个 feature 的真值改成指定档位，并证明它真改了。
//
// 【其他部分在哪】（V1.2.0 从 FeatureEngine.cs 拆出，逻辑逐字未改）
//   · FeatureRow / DetectFeatures → RealPrinterMod.FeatureDetector.cs
//   · 官方档位目录 / 中文名 / 加价 → RealPrinterMod.ConditionCatalog.cs
//   · 编辑窗                      → RealPrinterMod.Editor.cs
//
// ═══════════════════════════════════════════════════════════
// ★★ V0.5.5 验真 / 纯度真值引擎 —— 把红机的"假装"变成"真改"
//
// 【红机"假装"的机制】（V0.5.4 实机 F10 dump 实证）
//   ItemFeature { id=chemical_purity  cat=CATEGORY_CHEMICAL_PURITY
//                 ★FAKE = {id=street_standard, display=街头标准}← 红机只改这个
//                 ★REAL = {id=street_standard, display=街头标准}← 真值，红机不动 }
//   `ItemFeature` 公开方法 `SetConditions(ItemCondition fake, ItemCondition real)`
//   → 红机调它时只填 fake。**黑印两个都填，并额外保证 fake==real**，
//     这样玩家看到什么就是什么、验真逻辑读的也是同一个值 = 真的改了。
//
// 【★ 铁律：写完必须回读断言】
//   `ItemCondition` 是**托管对象**（不是 IL2CPP 指针），字段走生成的 property，
//   安全；但游戏可能在下一帧重算覆盖它（同派生值陷阱）。
//   → 写完立刻回读比对，不一致就**报出来**，不静默。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using MelonLoader;
using Il2Cpp;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>
    /// ★<paramref name="item"/> 是**必填参数**（故意不给默认值）。
    /// 【为什么不给默认值】给默认值 = 编译器不强制调用点传 =
    ///   漏传时静默走"缺 item 引用"分支 → 又是一次静默失效（V1.0.4 实机踩过）。
    ///   去掉默认值后，漏传**编译不过**，问题在编译期暴露。
    /// </summary>
    /// <summary>
    /// ★★ 核心：把一个 feature 的**真值**（realCondition）改成指定档位。
    ///
    /// 【为什么 fake 和 real 都要改】
    ///   只改 real → 玩家看到的（fake）还是旧值 → 界面与实际不一致 = 半吊子。
    ///   只改 fake → 就是红机的"假装"，真值没动。
    ///   两个都改成同一个值 → 玩家看到什么、验真逻辑读到什么，**完全一致**。
    ///
    /// 【写完回读断言】不靠"调了SetConditions 就当成功"，而是把两个字段
    ///   重新读出来比对，不一致立刻报出来（核桃铁律 #4）。
    /// </summary>
    private static bool WriteFeature(ItemFeature f, string newId, out string message,
                                    GameItem item)
    {
        message = "";
        if (f == null) { message = "feature 为空"; return false; }

        var real = f.realCondition;
        var fake = f.fakeCondition;
        if (real == null) { message = "这件物品的 feature 没有真值（realCondition 为空）"; return false; }

        string oldReal = CondId(real);
        string oldFake = CondId(fake);

        // ——— ① 拿到官方档位对象 ———
        //   ★ V0.5.19：走 ItemConditionList.Create*()，不再自己 clone。
        //   拿不到（映射表缺 / 官方方法抛异常）→ **明确失败**，绝不退化成
        //   "自己造一个 display=英文、modValue=0 的半成品"（那正是 V0.5.18 两个 bug 的来源）。
        var target = ResolveOfficialCondition(newId);
        if (target == null)
        {
            message = "找不到官方档位对象：" + newId
                      + "　→ 这档没在 ItemConditionList.Create* 映射表里。"
                      + "不做降级处理（降级会造成价格不动 + 英文显示）。"
                      + "把这行字和上面的候选列表给我，我照单补映射。";
            RLog.Warning("★ 无法解析官方档位: " + newId);
            return false;
        }

        // ——— ② 调游戏自己的 SetConditions（★ 正门，不反射写字段）———
        try
        {
            f.SetConditions(target, target);
        }
        catch (Exception e)
        {
            message = "SetConditions 调用失败: " + e.Message;
            RLog.Error("★ SetConditions 异常: " + e);
            return false;
        }

        // ═══════════════════════════════════════════════════════════
        //★★★ V1.0.4 关键修正：SetConditions 之后**必须调 ExecuteOnActivateAction**
        //
        //【实机证据（核桃 2026-10-03 16:56）】
        //   改真值成功、回读四项全对（display=赝品 / modValue=0 / compareValue=-50，
        //   日志还打了"价格✓ 显示✓"），**但商品价格纹丝不动**：
        //   tooltip 仍显示「预估价值 115, 基础价值 100」、商店仍按正品报价。
        //
        //【根因】`ItemCondition` 有三个方法（dump.cs:40676起）：
        //   SetConditions(fake, real)—— 只是**贴标签**
        //   HasOnActivateAction()     —— 这个档位带不带后续动作
        //   ExecuteOnActivateAction(item) —— **真正改商品**的那一步
        //   赝品/正品的 onActivateActionId 分别是
        //   `counterfeitCigarette` / `genuineCigarette`，注册在
        //   `ItemConditionActionList.ItemConditionActionDict`，
        //   对应 `CounterfeitCigaretteAction(GameItem)` 等方法。
        //   → 只贴标签不执行动作 = **游戏里的实际状态根本没变**。
        //   这与V0.5.8 那个「只写 modValue 不改档位名」是**镜像故障**：
        //   那次改了标签没改价，这次改了标签没**执行**。
        //
        //【为什么之前没发现】我把"display 与加价是否与官方一致"当成成功判据，
        //   但那只能证明**标签**写对了，证明不了**游戏状态**变了。
        //   → 现在断言升级为：动作执行完，回读商品的 tag/价值，看有没有跟着变。
        // ═══════════════════════════════════════════════════════════
        string actionMsg = "";
        try
        {
            if (item != null && target != null)
            {
                bool hasAction = false;
                try { hasAction = target.HasOnActivateAction(); } catch (Exception ex) { RLog.Swallowed("WriteFeature", ex); }
                if (hasAction)
                {
                    target.ExecuteOnActivateAction(item);
                    actionMsg = "　已执行档位动作";
                    if (Config.VerboseLog)
                        RLog.Detail("▶ 已执行档位动作: " + newId
                            + "（onActivateActionId=" + SafeStr(() => target.onActivateActionId) + "）");
                }
                else
                {
                    RLog.Detail("· 该档位无onActivateAction（纯标签档位）: " + newId);
                }
            }
            else
            {
                RLog.Warning("★ 缺 item 引用，无法执行档位动作 —— 价格可能不会变");
                actionMsg = "　★未执行动作（缺物品引用）";
            }
        }
        catch (Exception e)
        {
            RLog.Error("★ ExecuteOnActivateAction 异常: " + e.Message);
            actionMsg = "　★动作执行异常：" + e.Message;
        }

        // ═══════════════════════════════════════════════════════════
        //★★★ 动作执行后：**回读商品自身的价格字段**，证明动作真的生效了。
        //
        //【为什么必须加这一层】V1.0.4 实机教训：
        //   "标签写对了"（display/加价都✓）**不能证明**游戏状态变了。
        //   必须回读**商品那边**的数值（unitValue / compareValue 之类），
        //   看它到底动没动。日志打出来，核桃一眼就知道有没有生效。
        // ═══════════════════════════════════════════════════════════
        if (item != null)
        {
            try
            {
                //★ 只回读 GameItem **真实存在**的字段（unitValue）。
                //   （GameItem 没有 compareValue —— 已核对Il2Cpp/GameItem.cs：
                //   只有 `public long unitValue`。别照ItemCondition 的字段名硬套。）
                // ★ V1.4.6：同时回读基础值（unitValue）与最终价（GetNegociatedValue）。
                //   【为什么两个都要打】改真值动的是"档位加价"，最终价会变，
                //     但基础值通常不变 —— 只打一个会误导排障。
                //   排查"改完没生效"时，看**最终价**有没有动。
                long uv = 0; long nv = 0;
                try { uv = item.unitValue; } catch (Exception ex) { RLog.Swallowed("WriteFeature", ex); }
                try { nv = item.GetNegociatedValue(); } catch (Exception ex) { RLog.Swallowed("WriteFeature", ex); }
                if (Config.VerboseLog)
                    RLog.Detail("商品侧回读: " + SafeId(item)
                        + "　基础 unitValue=" + uv
                        + "　最终 GetNegociatedValue=" + nv
                        + "　（最终价才是玩家看到的价值；基础值一般不随档位变）");
                actionMsg += "｜最终价=" + nv;
            }
            catch (Exception e)
            {
                RLog.Warning("商品侧回读失败: " + e.Message);
            }
        }

        // ——— ③ 回读断言：值可能立刻被游戏重算覆盖（同派生值陷阱）———
        try
        {
            string backReal = CondId(f.realCondition);
            string backFake = CondId(f.fakeCondition);
            if (!string.Equals(backReal, newId, StringComparison.OrdinalIgnoreCase))
            {
                message = "写入后回读不一致：期望 " + newId + "，实读 " + backReal
                          + "　→ 这个档位是**派生值**，游戏立刻重算覆盖了它。"
                          + "（老值 " + oldReal + "）";
                RLog.Warning("⚠ feature 真值回读不一致: " + f.identifier
                    + " 期望=" + newId + " 实读=" + backReal);
                return false;
            }

            //★★★★★★ V0.5.19：加价与显示名**也要断言**。
            //   问题1「品质价格没同步」和问题2「标签是英文」都是
            //   "identifier 写对了、但其它字段还是旧的/空的" —— 光断言 identifier
            //   抓不到（V0.5.18 就是这么漏过去的）。所以这里逐字段比对官方对象。
            string backDisplay = null; int backMod = 0, backCmp = 0;
            try
            {
                backDisplay = f.realCondition == null ? null : f.realCondition.display;
                backMod = f.realCondition == null ? 0 : f.realCondition.modValue;
                backCmp = f.realCondition == null ? 0 : f.realCondition.compareValue;
            }
            catch (Exception ex) { RLog.Swallowed("WriteFeature", ex); }

            string wantDisplay = SafeStr(() => target.display);
            bool priceOk = backMod == target.modValue && backCmp == target.compareValue;
            bool dispOk = string.Equals(backDisplay, wantDisplay, StringComparison.Ordinal);

            if (Config.VerboseLog)
                RLog.Detail("真值写入回读: " + f.identifier
                    + " id=" + backReal
                    + " display=" + (backDisplay ?? "<空>")
                    + " modValue=" + backMod + "(官方 " + target.modValue + ")"
                    + " compareValue=" + backCmp + "(官方 " + target.compareValue + ")"
                    + "　" + (priceOk ? "价格✓" : "★★价格不符")
                    + (dispOk ? " 显示✓" : " ★显示与官方不一致"));

            message = "真值 " + oldReal + " → " + backReal
                      + (string.Equals(backFake, newId, StringComparison.OrdinalIgnoreCase)
                          ? "（假值已同步）" : "　★假值未能同步：" + backFake)
                      + "｜显示=" + (backDisplay ?? "<空>")
                      + "｜加价=" + backMod
                      + (priceOk ? "" : "　★★加价与官方档位不符（售价不会同步）")
                      + actionMsg;
            return true;
        }
        catch (Exception e)
        {
            message = "回读异常: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// 取 ItemCondition 的档位名（identifier，空则退回 display）。
    /// ★ 原在 BlackPrinter.TagEngine.cs —— 那份文件是"通用 tag 写入"用的，
    ///   RealPrinter 整份砍掉，CondId 也跟着搬到这里（真值引擎唯一的读法）。
    /// ★ V1.2.0：搬进 FeatureWriter（主要使用方在这里，DetectFeatures 也用它）。
    /// </summary>
    private static string CondId(ItemCondition c)
    {
        if (c == null) return "(null)";
        try
        {
            string id = c.identifier;
            if (!string.IsNullOrEmpty(id)) return id;
            id = c.display;
            return string.IsNullOrEmpty(id) ? "(空)" : id;
        }
        catch { return "(读取失败)"; }
    }
}
