// ═══════════════════════════════════════════════════════════
// ★ 真值探测器 —— 「这件物品身上有哪些可改的真值」
//
// 【本文件管什么】
//   枚举 itemFeatures → FeatureRow 列表（探测 + 候选档位）。**不写入任何东西。**
//
// 【其他部分在哪】（V1.2.0 由 FeatureEngine.cs 改名 + 拆出，逻辑逐字未改）
//   · 官方档位目录 / 中文名 / 加价 → RealPrinterMod.ConditionCatalog.cs
//   · WriteFeature（真值写入）      → RealPrinterMod.FeatureWriter.cs
//   · 编辑窗                        → RealPrinterMod.Editor.cs
//
// 【★ 为什么探测与写入分开】
//   V0.5.2~0.5.4 连续猜错三次，根因是数据在两套系统里：
//     ① TagSystem（state/modifiedState）—— 重量/耐久/尼古丁值/烟盒封条…
//     ② itemFeatures（List<ItemFeature>，0x2A8）—— ★真伪、纯度、可否使用
//   我们一直只读①。核桃要的功能全在②。
//   → 探测（读②）与写入（改②）分开后，"读出来的是什么"和"写进去的是什么"
//     可以各自验证，不会再混成一处说不清。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using MelonLoader;
using Il2Cpp;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>一条可改的 itemFeature（真值层）。</summary>
    internal sealed class FeatureRow
    {
        public ItemFeature Feature;      // 游戏原对象
        public string Id;                // feature.identifier，如 chemical_purity
        public string Category;          // feature.category，如 CATEGORY_CHEMICAL_PURITY
        public string RealId;            // ★ 现在的真值档位（改之前）
        public string FakeId;            // ★ 现在的假值档位
        public string Display;           // 真值的显示名（中文）
        public string[] Options;         // 合法档位候选（null = 自由输入）
        public bool CanEdit;             // 游戏自己判的能不能改
    }

    ///<summary>
    /// 枚举这件物品身上**游戏认定可改**的 itemFeature。
    /// ★ 判据用 <see cref="LabelerHelper.CanEditLabel"/> —— 这是游戏红机自己用的那条，
    ///   不自己另立标准（核桃铁律 #1：借鉴成功案例）。
    /// ★ **不按许可类别过滤** —— 过滤放在 Editor.OpenEditor 里做，
    ///   那里需要"未通过许可"的信息来给玩家提示；这里保持原始数据。
    /// </summary>
    internal static List<FeatureRow> DetectFeatures(GameItem item)
    {
        var rows = new List<FeatureRow>();
        if (item == null) return rows;

        // ★ itemFeatures 是 Il2Cpp 的 List，**不能**赋给托管 List（CS0029）→
        //   直接用 var 拿Il2Cpp 列表，按下标取。托管 List 只装我们自己的行。
        var list = item.itemFeatures;
        if (list == null || list.Count == 0) return rows;

        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            if (f == null) continue;

            bool can;
            try { can = LabelerHelper.CanEditLabel(f); }
            catch { can = false; }

            var real = f.realCondition;
            var fake = f.fakeCondition;
            if (real == null) continue;         // 没有真值 = 没东西可改

            rows.Add(new FeatureRow
            {
                Feature = f,
                Id = SafeStr(() => f.identifier),
                Category = SafeStr(() => f.category),
                RealId = CondId(real),
                FakeId = CondId(fake),
                Display = SafeStr(() => real.display),
                Options = OptionsFor(real),
                CanEdit = can,
            });
        }
        return rows;
    }

    /// <summary>
    /// 按 feature 的 category 找候选档位。
    /// ★ 依据：实机跑出的真值（`chemical_purity` → `street_standard`）
    ///   + stringliteral 里的 `mech_condition_*` / `wine_quality_*` 全家族。
    /// </summary>
    private static string[] OptionsFor(ItemCondition c)
    {
        string cat;
        try { cat = c == null ? null : c.category; } catch { return null; }
        if (string.IsNullOrEmpty(cat)) return null;
        return OptionsForCategory(cat);
    }

    private static string[] OptionsForCategory(string cat)
    {
        if (string.IsNullOrEmpty(cat)) return null;

        //★★★★★★ V0.5.19：**候选列表改从官方目录取**（不再手写字符串数组）。
        //
        // 【为什么】手写候选有两个致命问题：
        //   ① **和真实档位不同名** —— 我手写的 `perfect_purity` / `fair_purity` 等
        //      在游戏里根本不是 identifier（游戏叫 `lab_pure` / `street_standard`…）。
        //      选一个写进去 = 游戏读不到分支 = **改了没反应**（静默失效）。
        //   ② **和写入路径不同源** —— 候选能选、但 `ResolveOfficialCondition` 拿不到
        //      对象 → 点了就报错。
        //   → 改成"官方目录里有什么就列什么"，候选与写入**同一份数据**，
        //     天然自洽：列出来的必然写得进去。
        string[] official = OfficialIdsForCategory(cat);
        if (official != null && official.Length > 0) return official;

        // —— category 没收录：把**全库官方档位**摆出来（唯一可靠的兜底）——
        //   比手写一数组猜的值靠谱：玩家能照着游戏真实档位名填。
        EnsureCatalog();
        if (_byId != null && _byId.Count > 0)
        {
            var all = new List<string>(_byId.Keys);
            all.Sort(StringComparer.OrdinalIgnoreCase);
            RLog.Detail("category「" + cat + "」未收录 → 列出全库 "
                + all.Count + " 个官方档位供手填");
            return all.ToArray();
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //
    // 【为什么用"按钮点选"而不是输入框】
    //   档位值是 snake_case 英文（`street_standard` / `perfect_purity`），
    //   让玩家手打必然打错 → 填错游戏读不到分支 = 静默失效（核桃最恨这个）。
    //   → 候选全摆成按钮，**点一下就选中**，一个字都不用打。
    //   → 仍保留一个输入框兜底（category 未收录、候选不全时用）。
    //
    // 【★ 三条关闭途径都要清状态】窗口有标题栏 closeButton / ESC / 自己的取消按钮，
    //   只有自己调的会清（MEMORY 已记的坑）→ 挂 onClose。
    // ═══════════════════════════════════════════════════════════


    // （V0.5.12 的「选档位弹窗」已移除：V0.5.13 起改成**行内展开菜单**，
    //   不再需要第二个窗口。选档位走 BuildFeatureRow 里的行内按钮。
    //   保留这段说明是为了记住教训：**回调式 UI 撞同步流程 = 永远拿不到结果**。）
}
