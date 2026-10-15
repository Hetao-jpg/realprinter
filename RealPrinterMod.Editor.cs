// ═══════════════════════════════════════════════════════════
// ★ RealPrinter 编辑窗 —— 只渲染真值区块
//
// 【与 BlackPrinter 编辑窗的差别】
//   BlackPrinter：真值区 + tag 属性区（92 条白名单）+ 筛选档位 + 分级定价
//   RealPrinter ：**只有真值区**，别的整块砍掉。
//   → 窗口从520×420 缩到 480×360，玩家看到的就是"选档位"一件事。
//
// 【★ 许可闸门放在最前面】
//   不在许可内 → **根本不开窗**，直接给一句人话说明（License.RejectReason）。
//   ★ V1.5.0：许可已扩到官方档位目录全部 16 类，这条准入逻辑本身不变。
//   其余物品点了会看到提示，但**不会弹出一个空窗**让他猜。
//
// 【★ 加价怎么显示】
//   核桃 2026-10-03 明确："不显示加价"。面向普通玩家，价格由游戏自己结算，
//   在改之前摆加价只会让人分心。档位名一律显示**游戏官方本地化中文**。
//   ★ V1.2.0 微调：候选档位旁补一列「改后 ±N」，主视觉仍是中文名。
//     口径与悬停的「当前加价」**同为官方 modValueDisplay**（两套数字是事故源头）；
//     取不到就不显示 —— **绝不显示 0 假装"这个档位不加价"**。
//
// 【★ 为什么还要留"手填档位名"】
//   候选来自官方目录（启动时自省 56 个工厂），理论上全。
//   万一遇到没收录的档位，手填是唯一出路 —— 但明确告诉玩家"照抄游戏里的值"。
// ═══════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>编辑窗会话。</summary>
    private sealed class EditSession
    {
        public GameItem Tool;// 本mod 的真实打印机
        public GameItem Target;    // 要改真值的商品
        public int Cash;
        /// <summary>★ 行序号：给每行控件生成唯一 id。
        ///   不给唯一 id 时 AddLabel/AddInput 传 null，多行会互相覆盖 → 只显示一行
        ///   （BlackPrinter 实机踩过：4 条建行成功却只显示 1 行）。</summary>
        public int RowSeq;
        /// <summary>★ 当前展开了候选菜单的 feature id（null = 全收起）。</summary>
        public string ExpandedFeatureId;
    }

    private static EditSession _session;

    internal static bool TargetSessionActive => _session != null;

    /// <summary>窗口 id（CustomUIManager 按 id 管窗口）。</summary>
    internal const string EditorWindowId = "real_printer_editor";

    /// <summary>
    /// 打开编辑窗。
    /// ★ 先过许可闸门 —— 不在许可内就不开窗，只给提示。
    /// </summary>
    /// <param name="expandedFeature">
    /// ★★★ 要展开候选菜单的 feature id（null = 全收起）。
    /// 【为什么必须有这个参数】`CustomUIBuilder` 是**一次性绘制**的 ——
    ///   没法在已建好的窗口里"动态插一行"，展开候选只能**重建整个窗口**。
    ///   而重建时**必须把展开状态一起带过来**，否则：
    ///     点「选档位」→ 设了 ExpandedFeatureId → 重建窗口 → 新会话把它初始化成 null
    ///   → 窗口看起来毫无反应（核桃V1.0.1 实机就是这个故障）。
    ///   这是从 BlackPrinter 移植时**漏抄了带 expandedFeature 的重载**导致的。
    /// </param>
    internal static void OpenEditor(GameItem tool, GameItem target,
                                   string expandedFeature = null)
    {
        if (target == null) return;

        // ★★ V1.2.0：准入判定**全部**交给 `TryEnterEditor`（代码评审 P4）。
        //   这里只负责"按结论办事"，不再自己判任何条件。
        var d = TryEnterEditor(target);

        if (d.Verdict == EnterVerdict.RejectSilent)
        {
            // ★ V1.4.11：删掉这里的 `RLog.Detail("跳过开窗：" + d.Reason)`。
            //   这条路径在**拖动时被高频触发**（游戏每秒问几百次"能不能作用"，
            //   每次都走 TryClaim→开窗准入），而它要拼的字符串里没有 SafeId，
            //   却仍要付一次字符串拼接 + 一次字典/方法调用。
            //   ★ 判据：`RejectSilent` 按定义就是"不值得打扰玩家也不值得打扰日志"的情况
            //     （对象不稳定 = 拖动噪声）。真要排障，F3 级的"为什么没开窗"信息
            //     应该在**进入**这个结论的地方打一次，而不是在**每次**得出这个结论时打。
            //     （`RejectWithReason` 那条保留 —— 它对应"玩家真的想改但类别不许可"，
            //      频率是玩家点击级，且要靠 `SafeId` 才能定位到具体物品。）
            return;
        }
        if (d.Verdict == EnterVerdict.RejectWithReason)
        {
            Inform(d.Reason, "yellow");
            if (Config.VerboseLog)
                RLog.Detail("拒绝开窗：" + SafeId(target) + " —— "
                            + (d.Reason ?? "").Replace('\n', ' '));
            return;
        }

        OpenEditorInner(tool, target, d.Licensed, d.RawCount, expandedFeature);
    }

    /// <summary>「这次交互要不要开窗」的三种结论。</summary>
    private enum EnterVerdict
    {
        /// <summary>允许开窗。</summary>
        Open,
        /// <summary>拒绝，且**值得告诉玩家为什么**（他有真值特性，只是类别不许可）。</summary>
        RejectWithReason,
        /// <summary>拒绝，且**不打扰玩家**（拖动噪声 / 对象不稳定 / 压根没有真值特性）。</summary>
        RejectSilent,
    }

    private sealed class EnterDecision
    {
        public EnterVerdict Verdict;
        public List<FeatureRow> Licensed;   // 通过许可的真值项（仅 Verdict=Open 时非空）
        public int RawCount;                // 物品原始 feature 条数（诊断日志用）
        public string Reason;               // 拒绝原因（仅 RejectWithReason 时非空）
    }

    /// <summary>
    /// ★★ V1.2.0：**唯一的开窗准入判定**（代码评审 P4）。
    ///
    /// 【为什么抽出来】改之前，「活体闸 + 许可过滤 + 该不该提示」这段逻辑
    ///   一半在 `OpenEditor`、一半在 `License.RejectReason`（里面还有一道
    ///   `IsSafeTarget`）。**加一道新闸要改两个地方** —— 迟早漏一处，
    ///   漏的那处就变成"提示弹了但窗也开了"这类说不清的行为。
    ///   → 现在"什么时候允许开窗"只有这一处；`OpenEditor` 只按结论办事。
    ///
    /// 【三条结论怎么定的】（V1.0.7 实机反馈，行为未变）
    ///   ① 对象不稳定           → 静默（拖动中游戏自己会调 Target，别打扰玩家）
    ///   ② 有真值特性但类别不许可 → **提示**（玩家是真的想改，值得解释一句）
    ///   ③ 压根没有真值特性     → 静默（多半是拖动噪声，如放大镜、垃圾桶）
    ///
    /// ★【与 `License.RejectReason` 里那道 `IsSafeTarget` 的关系】
    ///   它不是"重复判据"，是**防崩闸**：`RejectReason` 会被别的路径单独调用
    ///   （不只是本方法），它必须先确认对象活着才能去读 itemFeatures
    ///   （AccessViolation 不可捕获，只能事前拦）。两处职责不同，别合并掉。
    /// </summary>
    private static EnterDecision TryEnterEditor(GameItem target)
    {
        if (target == null)
            return new EnterDecision { Verdict = EnterVerdict.RejectSilent, Reason = "目标为空" };

        //★★★ V1.0.6：活体闸（补丁里判过一次，到这里对象仍可能死）。
        if (!IsSafeTarget(target))
            return new EnterDecision
            {
                Verdict = EnterVerdict.RejectSilent,
                Reason = "目标对象状态不稳定（拖动过程中的正常现象）",
            };

        // —— 许可闸门：这是"哪些类别可以改"的唯一实现点——
        var feats = DetectFeatures(target);

        // ★ V1.4.6：先扫一遍数许可项 —— 为 0 就不分配 List。
        //   （复核：TryEnterEditor 不在 MayTarget/CanTarget 热路径上，
        //    这条优化的实际收益有限，但"早退路径零分配"是清晰的纪律。）
        int licensedCount = 0;
        for (int i = 0; i < feats.Count; i++)
        {
            if (License.IsAllowed(feats[i].Category)) licensedCount++;
        }

        if (licensedCount == 0)
        {
            // ★ V1.2.0：把已经算出来的 feature 条数传进去，省一次 IL2CPP 读
            //   （语义完全不变，见 HasAnyCondition 的说明）。
            if (!HasAnyCondition(target, feats.Count))
                return new EnterDecision
                {
                    Verdict = EnterVerdict.RejectSilent,
                    RawCount = 0,
                    Reason = "目标没有任何真值特性（拖动/随手点，非许可范围）",
                };

            return new EnterDecision
            {
                Verdict = EnterVerdict.RejectWithReason,
                RawCount = feats.Count,
                Reason = License.RejectReason(target),
            };
        }

        var licensed = new List<FeatureRow>(licensedCount);
        for (int i = 0; i < feats.Count; i++)
        {
            if (License.IsAllowed(feats[i].Category)) licensed.Add(feats[i]);
        }

        return new EnterDecision
        {
            Verdict = EnterVerdict.Open,
            Licensed = licensed,
            RawCount = feats.Count,
        };
    }

    /// <summary>
    /// ★ V1.0.7：这件物品**到底有没有真值特性**（itemFeatures 非空）。
    ///
    /// 【为什么需要它】区分两种"不许可"：
    ///   ① 有真值特性但类别不支持（如酒）→ 玩家是真的想用它改 → **该提示**；
    ///   ② 压根没有真值特性（如放大镜、垃圾桶）→ 多半是**拖动过程中的噪声** → 不该打扰。
    ///   不分这两者 = 玩家挪一下机器就被弹一次提示。
    /// ★ 读 itemFeatures 是 IL2CPP 调用 —— 调用前已由 IsSafeTarget验过活体。
    ///
    /// ★★ V1.2.0：<paramref name="detectedCount"/> 是 `DetectFeatures` 已算出的条数。
    ///   【为什么不直接用 detectedCount】两者**语义不同**，不能互换：
    ///     · `detectedCount` = 有 realCondition 的 feature 数（DetectFeatures 会跳过
    ///                         `realCondition == null` 的 feature）
    ///     · `itemFeatures`  = 物品身上全部 feature 的条数
    ///   一件"有 feature 但都没有真值"的物品：detectedCount=0、itemFeatures>0。
    ///   若只按 detectedCount 判，这类物品会**既不提示也不开窗** = 静默失效
    ///   （核桃最恨的形态）。
    ///   → 折中：detectedCount>0 时直接返回（常见路径，零 IL2CPP）；
    ///     只有它为 0 时才回退读 itemFeatures，**行为与改动前完全一致**。
    /// </summary>
    private static bool HasAnyCondition(GameItem item, int detectedCount)
    {
        if (detectedCount > 0) return true;
        if (!IsSafeTarget(item)) return false;
        try
        {
            var fl = item.itemFeatures;
            return fl != null && fl.Count > 0;
        }
        catch (Exception ex) { RLog.Swallowed("HasAnyCondition", ex); return false; }
    }

    private static void OpenEditorInner(GameItem tool, GameItem target,
                                       List<FeatureRow> licensed, int rawFeatureCount,
                                       string expandedFeature)
    {
        try
        {
            CloseEditor();     // 关掉上一个会话的窗，避免叠窗

            var sess = new EditSession
            {
                Tool = tool,
                Target = target,
                Cash = Pay.Query(),
                // ★ 展开状态必须在**建会话时就写进去**，渲染才看得到（见方法注释）。
                ExpandedFeatureId = expandedFeature,
            };

            // ★ 用官方静态工厂 `CustomUIManager.Window(id, title, layerId)`——
            //   没有 public 构造函数，只有这一条入口。
            var b = CustomUIManager.Window(EditorWindowId,
                "真实打印机", null);
            if (b == null)
            {
                RLog.Error("★ CustomUIManager.Window 返回 null");
                _session = null;
                return;
            }

            b.SetSize(420, 320);
            b.SetDraggable(true);
            b.SetCloseOnEscape(true);

            //★★ 开窗流水：为什么只列了 licensed.Count 条。
            //   V0.5.x 教训：界面只显示"可改 0 项"而不说原因 = 纯静默失效，
            //   玩家只会以为 mod 坏了。这里把原始 feature 数/ 被许可挡掉的数量都打出来。
            // ★ V1.4.11：把判据提到**调用点**（`Config.VerboseLog`）——
            //   原来靠 `RLog.Detail` 内部的开关，但下面那个 `foreach` 里的
            //   `fr.Id / fr.Category / fr.RealId / fr.FakeId` 全是**字符串拼接的实参**，
            //   VerboseLog 关着时照样逐项拼完再被丢掉。许可项通常 1~4 条，
            //   量不大，但它属于"零收益的分配"，且判据搬上来更符合纪律。
            if (Config.VerboseLog)
            {
                RLog.Detail("编辑窗：" + SafeId(target)
                    + "　原始 feature " + rawFeatureCount + " 条（可改真值 "
                    + licensed.Count + " 条，其余类别不在许可范围）"
                    + "　信用点 " + sess.Cash);
                foreach (var fr in licensed)
                    RLog.Detail("  ★许可真值项: " + fr.Id + "　cat=" + fr.Category
                        + "　现值 REAL=" + fr.RealId + "　假值 FAKE=" + fr.FakeId
                        + "　游戏判可改=" + fr.CanEdit);
            }

            //★ 文案精简（核桃 V1.0.1：名字太长、字体太大、后缀数字和括号删掉）：
            //   物品名只出现一次，档位说明压成一行，费用说明收进悬停。
            // ★ V1.4.6：给信用点加"目前余额"前缀（核桃 2026-10-04）—— 原样
            //   显示"340 点"玩家不知道这是余额还是费用，容易误读。
            b.AddLabel(DisplayNameOf(target)
                       + "　｜" + licensed.Count + " 项真值　｜"
                       + "目前余额 " + sess.Cash + " 点", null);
            ShrinkFont(b);
            b.BeginScroll(200f);

            // ───── 真值区块（唯一的内容）─────
            foreach (var fr in licensed)
                BuildFeatureRow(b, sess, fr);

            b.End();

            //★ 文案按核桃要求改（V1.0.5）：只留费用信息。
            b.AddLabel("★ 每次修改消耗信用点 " + Config.FeatureCost, null);
            ShrinkFont(b);
            b.AddButton("关闭", (Action)(() => CloseEditor()));
            ShrinkFont(b);

            var win = b.Show();
            if (win == null)
            {
                RLog.Error("★ Show() 返回 null");
                _session = null;
                return;
            }

            //★★ 必须挂 onClose：窗口有三个关闭途径（标题栏 / ESC / 自己的关闭按钮），
            //   只有我们自己调的 CloseEditor 会清 _session。
            //   走前两条时 _session 永久残留 → 每次左键都被"编辑窗已开着"挡掉，
            //   表现就是"功能彻底坏了，不重启永远好不了"。
            try { win.onClose = (Action)(() => { _session = null; }); }
            catch (Exception e) { RLog.Warning("挂 onClose 失败: " + e.Message); }

            _session = sess;
        }
        catch (Exception e)
        {
            RLog.Error("★ OpenEditor 异常: " + e);
            try { Inform("开窗失败，详见日志", "yellow"); } catch (Exception ex) { RLog.Swallowed("OpenEditorInner", ex); }
            _session = null;
        }
    }

    /// <summary>重建窗口（改完真值后刷新显示）。</summary>
    private static void RebuildEditor()
    {
        var s = _session;
        if (s == null || s.Target == null)
        {
            RLog.Warning("RebuildEditor：没有会话可重建（忽略）");
            return;
        }
        try
        {
            // ★★ 必须带展开状态，否则点「选档位」后重建就把它丢了（V1.0.1 实机故障）。
            OpenEditor(s.Tool, s.Target, s.ExpandedFeatureId);
        }
        catch (Exception e)
        {
            RLog.Error("★ RebuildEditor 失败: " + e.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ★ 字号：窗口太宽、文字太大，核桃 2026-10-03 要求"字体太大"
    //
    // 【为什么必须自己做】`CustomUIBuilder` **没有字号接口**：
    //   它只有 SetSize / SetPosition / AddLabel / AddButton…
    //   （已在 Il2Cpp/CustomUIBuilder.cs 逐个核对过，没有 fontSize 相关方法）。
    //   → 但 `CustomUIElement.text` 是**公开可读**的 `TextMeshProUGUI`，
    //     那是 Unity 的标准组件，fontSize 可写。
    //   所以做法：AddLabel 之后拿 b.Last，试着把 fontSize 改小。
    //
    // 【为什么套 try】不确定游戏版本的 builder 是否把 Last 暴露出来。
    //   改字号是**纯外观**，失败就保持默认字号 ——
    //   **绝不能让一次外观调整把整个窗口搞挂**（静默失效）。
    // ═══════════════════════════════════════════════════════════

    /// <summary>★ 统一字号。取不到就保持原样，不抛。</summary>
    /// ★ V1.4.6：11 → 11.5（核桃 2026-10-04：整体放大半个字号）。
    private const float FontSizeSmall = 11.5f;

    /// <summary>把刚Add* 出来的元素字号调小（纯外观，失败静默）。</summary>
    private static void ShrinkFont(CustomUIBuilder b)
    {
        try
        {
            var el = b.Last;
            if (el == null) return;
            var tmp = el.text;
            if (tmp == null) return;
            tmp.fontSize = FontSizeSmall;
        }
        catch (Exception ex) { RLog.Swallowed("ShrinkFont", ex); }
    }

    /// <summary>关窗并清会话。</summary>
    internal static void CloseEditor()
    {
        try
        {
            var mgr = CustomUIManager.Instance;
            if (mgr != null) mgr.CloseWindow(EditorWindowId);
        }
        catch (Exception ex) { RLog.Swallowed("CloseEditor", ex); }
        _session = null;
    }

    // ═══════════════════════════════════════════════════════════
    // 真值行渲染
    // ═══════════════════════════════════════════════════════════
    private static void BuildFeatureRow(CustomUIBuilder b, EditSession sess, FeatureRow fr)
    {
        string uid = "f" + (sess.RowSeq++).ToString("X");

        // —— 行首：中文档位名（核桃 V1.0.1：别再显示英文 id + 括号后缀）——
        //★ 显示名优先取**官方中文**，其次真值自带 display，最后才退回内部 id。
        //   （V1.0.1 实机：直接用 fr.Display 显示出来是 `genuineCigarette`，
        //     一眼看不出是"正品还是赝品"。）
        string cur = OfficialDisplay(fr.RealId);
        if (string.IsNullOrEmpty(cur) || string.Equals(cur, fr.RealId, StringComparison.Ordinal))
            cur = fr.Display;
        if (string.IsNullOrEmpty(cur)) cur = fr.RealId;

        string label = "★ " + cur
                     + (fr.CanEdit ? "" : "　（不可改）")
                     + (fr.FakeId != fr.RealId ? "　假值不同" : "");
        b.AddLabel(label, uid + "L");
            ShrinkFont(b);

        // —— 悬停：把 category / 真假值 / 候选全列出来——
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(fr.Id).Append('\n').Append("类别 = ").Append(License.CategoryLabel(fr.Category));
            sb.Append('\n').Append("真值 = ").Append(fr.RealId);
            sb.Append('\n').Append("假值 = ").Append(fr.FakeId);
            sb.Append('\n').Append(fr.CanEdit
                ? "游戏判定：可改（CanEditLabel=True）"
                : "游戏判定：**不可改**（CanEditLabel=False）");
            try
            {
                var rc = fr.Feature == null ? null : fr.Feature.realCondition;
                if (rc != null)
                    sb.Append("\n当前加价 = ").Append(rc.modValueDisplay)
                      .Append("　（与候选档位的「改后 ±N」同一口径：官方 modValueDisplay）");
            }
            catch (Exception ex) { RLog.Swallowed("BuildFeatureRow", ex); }
            if (fr.Options != null && fr.Options.Length > 0)
            {
                sb.Append("\n\n可选档位（中文 ←→ 游戏内部值）：");
                for (int i = 0; i < fr.Options.Length; i++)
                {
                    if (i > 0) sb.Append("\n　");
                    string zh = OfficialDisplay(fr.Options[i]);
                    sb.Append(string.IsNullOrEmpty(zh) || string.Equals(zh, fr.Options[i], StringComparison.Ordinal)
                        ? fr.Options[i] + "　★中文名取不到"
                        : zh + "　← " + fr.Options[i]);
                }
            }
            b.WithTooltip(sb.ToString());
        }
        catch (Exception ex) { RLog.Swallowed("BuildFeatureRow", ex); }

        b.AddButton("选档位", (Action)(() =>
        {
            // 行内展开（红机那种点一下出下拉），不开第二个窗口。
            // ★ 教训（MEMORY）：回调式 UI 撞同步流程 = 永远拿不到结果。
            //   绝不能写成"弹窗 → Show() → 立刻 return 读变量"。
            sess.ExpandedFeatureId = (sess.ExpandedFeatureId == fr.Id) ? null : fr.Id;
            // ★ V1.2.0：删掉这里的 `ShrinkFont(b)` —— 它作用在**即将被丢弃的
            //   旧 builder** 上（`RebuildEditor()` 会重建整个窗口），等于没写。
            //   留着只会误导读者以为"展开时字号要特殊处理"。
            RebuildEditor();
        }), uid + "B");

        // ───── 展开：候选按钮直接列在下面 ─────
        if (sess.ExpandedFeatureId != fr.Id) return;

        if (fr.Options != null && fr.Options.Length > 0)
        {
            for (int oi = 0; oi < fr.Options.Length; oi++)
            {
                string opt = fr.Options[oi];
                bool isCur = string.Equals(opt, fr.RealId, StringComparison.OrdinalIgnoreCase);
                // ★ 显示**官方中文名**，不显示加价（核桃拍板）。
                string zh = OfficialDisplay(opt);
                bool zhOk = !string.IsNullOrEmpty(zh)
                            && !string.Equals(zh, opt, StringComparison.Ordinal);
                // ★ V1.2.0：补上"改到这档会加价多少"（核桃 2026-10-03 拍板的
                //   "按钮上不显示加价"仍然成立 —— 这里只是**多一列信息**，
                //   不改变"一眼看到的是中文档位名"这个主视觉）。
                //   ★ 口径与上面悬停的「当前加价」**完全同一个字段**
                //     （`modValueDisplay`），界面上不会出现两套互相矛盾的数字。
                //   ★ 取不到就不显示 —— 绝不显示 0 假装"这个档位不加价"。
                // ★ V1.4.6：删掉"改后 +N"显示 —— modValue/modValueDisplay 在不同
                //   category 上的填充规则不一致，无法用统一口径给玩家一个可靠数字。
                //   改完真值后的**实际最终价**会在开窗后的提示里给（那里读
                //   GetNegociatedValue，是权威口径）。
                b.AddLabel("　├ " + (zhOk ? zh : opt)
                        + (isCur ? "　← 当前" : "")
                        + (zhOk ? "" : "　（★中文名取不到）"), uid + "L" + oi);
                ShrinkFont(b);
                string cap = opt;
                b.AddButton(isCur ? "✓ 当前" : "选它", (Action)(() =>
                {
                    sess.ExpandedFeatureId = null;
                    ApplyFeatureChange(fr, sess, cap);
                    // ★ V1.2.0：同上，这里的 ShrinkFont(b) 作用在旧 builder 上，无效，删。
                    RebuildEditor();       // 刷新，让行标签显示新值
                }), uid + "O" + oi);
            }
        }
        else
        {
            b.AddLabel("　└ ★没有候选档位 —— 用下面的手填框", uid + "Ln");
            ShrinkFont(b);
        }

        //★★★ V1.0.7：**手填框只在「没有候选档位」时才显示**（核桃 2026-10-03 实机反馈）。
        //   【原设计】候选不全时留个输入框兜底（"手填档位名"）。
        //   【为什么现在要藏】候选来自游戏官方目录，**基本总是有的**——
        //     一旦有候选，下面就是几个「选它」按钮，玩家一眼就知道该点哪个。
        //     这时再摆一个空输入框 = **多条路摆在面前**，玩家不知道填什么
        //     （真值档位是 `street_standard` 这种英文 id，填错 = 改了没反应）。
        //     核桃的原话：「手填挡位名这一个功能在已经有挡位选择的情况下应该不显示」。
        //   → **只有候选为空时才摆出来**（那才是真需要手填的场景）。
        //   ★ 收起按钮始终保留 —— 它是退出展开状态的办法。
        bool hasOptions = fr.Options != null && fr.Options.Length > 0;
        if (!hasOptions)
        {
            b.AddLabel("　└ 手填档位名（照抄游戏里真实的值）:", uid + "Lh");
            ShrinkFont(b);
            b.AddInput("档位名", "", null, uid + "Ih");
            ShrinkFont(b);
            var inp = b.Last;
            b.AddButton("用它", (Action)(() =>
            {
                try
                {
                    var tf = inp.input;
                    string s2 = tf == null ? null : tf.text;
                    if (string.IsNullOrEmpty(s2))
                    {
                        Inform("输入框是空的，没法用", "yellow");
                        return;
                    }
                    sess.ExpandedFeatureId = null;
                    ApplyFeatureChange(fr, sess, s2.Trim());
                    RebuildEditor();
                }
                catch (Exception e) { RLog.Warning("读手填输入框失败: " + e.Message); }
            }), uid + "Bh");
            ShrinkFont(b);
        }

        b.AddButton("收起", (Action)(() =>
        {
            sess.ExpandedFeatureId = null;
            // ★ V1.2.0：同上，删掉作用在旧 builder 上的 ShrinkFont(b)。
            RebuildEditor();
        }), uid + "Bx");
    }

    /// <summary>
    /// 执行一次真值改写。
    /// ★ 顺序：**先判余额 → 先改值 → 再扣费**。
    ///   扣费失败会如实告诉玩家"改动已生效但没扣到钱"（MEMORY 铁律：
    ///   "必须成功"的写入不能静默兜底；反过来也一样，不能悄悄吞掉扣费失败）。
    /// </summary>
    private static void ApplyFeatureChange(FeatureRow fr, EditSession sess, string input)
    {
        if (sess == null)
        {
            Inform("会话已过期，请重新点物品", "yellow");
            return;
        }

        // ★★ V1.4.15：目标档位与当前真值相同 → 早退，不写入不扣费。
        //   【为什么在这里而非 WriteFeature】
        //     WriteFeature 的调用方只看"成功/失败"，看不到"本次无改动"这个语义；
        //     在这里早退最直接 —— 既不进 IL2CPP，也不会让玩家为
        //     "点了一下没变化"付费。
        //   【为什么要拦】选"已经是这个档位"的候选时，旧版会：
        //     ① 建新 ItemCondition 对象
        //     ② 调 SetConditions（把同样的值写一遍）
        //     ③ 调 ExecuteOnActivateAction（**可能重复执行游戏动作，有副作用**）
        //     ④ 扣一次费
        //     —— 玩家会看到"没变化却扣了钱"，是最容易招致差评的体验。
        if (fr != null && !string.IsNullOrEmpty(fr.RealId)
            && string.Equals(fr.RealId, input, StringComparison.OrdinalIgnoreCase))
        {
            string zhSame = OfficialDisplay(input);
            if (string.IsNullOrEmpty(zhSame) || string.Equals(zhSame, input, StringComparison.Ordinal))
                zhSame = input;
            Inform("已经是「" + zhSame + "」了，无需修改", "yellow");
            RLog.Detail("真值改写跳过（无改动）：" + fr.Id + " 已是 " + input);
            return;
        }

        int cost = Config.FeatureCost;
        if (sess.Cash < cost)
        {
            Inform("信用点不足：改一次真值要 " + cost + "，你只有 " + sess.Cash, "yellow");
            RLog.Detail("真值改写被拒（余额不足）: " + fr.Id);
            return;
        }

        // ★ V1.2.0：**写入前**先读一次商品价值 —— 改完要跟改后值对比着给玩家看。
        //   【为什么】"改成功了"和"售价真的变了"是两件事（V1.0.4 就栽在这：
        //     标签全对、回读断言全绿，但商品价格纹丝不动）。
        //     → 把 before → after 直接打在提示里，一眼就能看出有没有真的变。
        long valBefore = ReadUnitValue(sess.Target);

        RLog.Detail("▶ 改真值: " + fr.Id + "　" + fr.RealId + " → " + input
            + "　费用 " + cost + "　改前 unitValue=" + valBefore);
        // ★ 传target：WriteFeature 要用它调 ExecuteOnActivateAction
        //   （只贴标签不执行动作 = 游戏里的实际状态不变，售价不会跟着变）。
        bool ok = WriteFeature(fr.Feature, input, out string msg, sess.Target);
        if (!ok)
        {
            Inform("★ 没能改成：" + msg, "yellow");
            RLog.Detail("★ 改真值失败: " + fr.Id + " → " + msg);
            return;
        }

        if (!Pay.TryCharge(cost, out string why))
        {
            Inform("扣费失败（" + why + "）。改动已生效但没扣到钱 —— 请报我。", "yellow");
            RLog.Error("★★ 真值写入成功但扣费失败: " + fr.Id + "　" + why);
        }
        else
        {
            sess.Cash = Pay.Query();
        }

        // ★★ V1.0.5 提示文案精简（核桃要求）：只要「真品/赝品」+「商品价值」。
        //   之前把整个 msg（真值链、显示、加价、动作、单位价值）塞进提示，
        //   一行字糊满屏幕顶部，玩家根本读不到重点。
        //   档位名取**官方中文**（正品/赝品），取不到才退回内部 id。
        string tierZh = OfficialDisplay(input);
        if (string.IsNullOrEmpty(tierZh) || string.Equals(tierZh, input, StringComparison.Ordinal))
            tierZh = input;
        long val = ReadUnitValue(sess.Target);

        // ★ V1.2.0：提示改成「改前 → 改后」。
        //   ★★ 两个值一样时**明确标出来**（"★没变"）—— 这正是售价验证
        //      最需要的一行：以前只显示改后的数，玩家（和我）根本看不出
        //      "改了，但价格没动"和"改了，价格也动了"的区别。
        string arrow = valBefore + " → " + val;
        if (valBefore == val) arrow += "　★没变";
        Inform("★ " + tierZh + "　价值 " + arrow, "green");
        RLog.Detail("✓ 真值已改: " + fr.Id + "　" + msg
            + "　（扣费 " + cost + "，余额 " + sess.Cash + "）");
        PlayRatchet();
    }

    /// <summary>
    /// 读商品的**最终议价**（玩家在 tooltip / 商店里看到的"预估价值"）。
    /// ★ V1.4.6：从 `unitValue` 改为 `GetNegociatedValue()`（核桃 2026-10-04）。
    ///
    /// 【为什么必须改】原读 `GameItem.unitValue` 是**基础值**（层 ①），
    ///   它**不随真值档位变** —— 改真值改的是 `ItemCondition.modValue`
    ///   （档位加价，层 ②），两者不是一回事。
    ///   后果：玩家改完化学品/香烟/金属锭，提示显示"36 → 36 ★没变"，
    ///   但游戏 tooltip 里的"预估价值"其实变了 → **提示在说谎**。
    ///   （注射器之所以"看着对"，是因为它某些档位的动作恰好会动 unitValue，
    ///    属特例，不能作为基准。）
    ///
    /// 【口径依据】`GetNegociatedValue()` 是游戏自己算最终价的方法
    ///   （`SupplierStockOne` 里打印供货商机器售价用的就是它）。
    ///   → 玩家看到什么，我们就显示什么。
    ///
    /// 【回退】`GetNegociatedValue` 拿不到时退回 `unitValue` —— 宁可显示基础值，
    ///   也不显示 0（"读不到"和"真的是 0"必须区分开，MEMORY 铁律）。
    /// </summary>
    private static long ReadUnitValue(GameItem item)
    {
        if (item == null) return 0;
        try { return item.GetNegociatedValue(); }
        catch (Exception ex) { RLog.Swallowed("ReadUnitValue", ex); }
        try { return item.unitValue; }
        catch (Exception ex) { RLog.Swallowed("ReadUnitValue", ex); return 0; }
    }

}