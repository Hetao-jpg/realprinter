// ═══════════════════════════════════════════════════════════
// ★ 形状压缩 —— 把机器从 2×3 压到 1×2（★ 正式功能，V1.4.7 起）
//
// 【为什么必须压】实机取证结论（V1.4.6 诊断）：
//   柜台 18×4 = 72 格。供货商品占满后剩下的空位往往不足 6 格且不连续，
//   而原版形状是 2×3 = 6 格 → TryAcceptAllOrDestroy **静默销毁**我们的机器
//   （不抛异常、不报错，只有一条"没找到这台机器"的日志）。
//     快照#1：占 47/72，剩 25 格 → 够 → 上架成功
//     快照#2：占 69/72，剩  3 格 → 差 3 格 → 被销毁
//   → 压到 1×2（2 格）后实机成功上架。
//
// 【★★ data 必须填 1，否则网格预览消失（踩过的坑）】
//   反汇编 GridShapeBuilder：
//     .ctor(w,h)          0x766AA0 → 只写 width/height + `new byte[w*h]`，
//                                     **一个字节都不填**
//     SetDataFill(byte b) 0x766200 → `Array.Fill(data, b)`
//     SetData(data,w)     0x766330 → 写入外部数组
//   原版 SetSpriteAndShape 走 SetData（逐像素扫 alpha 的结果），不透明格写 1。
//   若只 new 不填 → data 全 0 → UI 认为这物品不占任何格子
//   → **不绘制半透明占位网格**（用户实机截图确认过的现象）。
//   → 正确写法：`.ctor(w,h).SetDataFill(1)`，一格都不能少。
//
// 【为什么不能碰 shape 以外的字段】
//   反汇编 0x79BA00 `ValidateShapeState` 只写 0x1A0/0x1A8/0x1AC/0x1B8，
//   **从不碰 0x198(shape)** → 压完不会被游戏改回去（实机回读已确认）。
//
// 【V1.4.11 精简】原文件 RealPrinterMod.Diagnostic.cs（682 行）里
//   只有本函数是正式功能，其余全是排障探针（ASCII 现场快照 / 逐件元凶排查 /
//   两个 Harmony 探针），诊断使命在 V1.4.6 就已完成 → 整体删除。
//   本函数剥离出来独立成文件，**去掉全部 DiagOn 分支与回读日志**
//   （原来每次建机都要 StringBuilder 拼一整段回读，只为 VerboseLog 开着时看）。
// ═══════════════════════════════════════════════════════════

using System;
using Il2Cpp;

namespace RealPrinter;

public sealed partial class RealPrinterMod
{
    /// <summary>压缩后的形状：1×2 = 2 格（替代原版 2×3 = 6 格）。</summary>
    internal const int ShrinkW = 1;
    internal const int ShrinkH = 2;

    /// <summary>同上的 float 版 —— 缩图那边按格子比例算像素要用（int 不能整除 0.667）。</summary>
    internal const float ShrinkWF = 1f;
    internal const float ShrinkHF = 2f;

    /// <summary>
    /// 把机器形状压到 1×2 并填满占位数据。**无条件生效**。
    /// 失败时保持原形状继续跑 —— 宁可占 6 格放不上，也不能造不出机器。
    /// </summary>
    internal static void ShrinkShape(GameItem it)
    {
        if (it == null) return;
        try
        {
            // ★★ 顺序要紧：先 .ctor 建尺寸（分配 byte[w*h]），
            //   再 SetDataFill 把每个字节填 1（= 该格被占用）。
            //   少任何一步 → data 全 0 → 网格预览消失。
            var nb = new GridShapeBuilder(ShrinkW, ShrinkH);
            nb.SetDataFill((byte)1);
            // ★ 必须取 `.shape` —— Il2CppInterop 把 IL2CPP 的
            //   "GridShapeBuilder : GridShape"（类继承）**拍平成了两个并列类**
            //   （实测 GridShapeBuilder 的基类是 Il2CppSystem.Object，不是 GridShape），
            //   托管侧直接传会 CS1503 编译不过。
            //   `GridShapeBuilder.shape` getter 返回 GridShape，
            //   就是游戏自己预留的转换点（1000 处调用，官方自己也在用）。
            it.SetShape(nb.shape);
        }
        catch (Exception e)
        {
            // 压失败不是致命错误 —— 保持原形状，柜台还有空间时照样能上架
            RLog.Warning("[真印] 形状压缩失败（保持原 2×3）: "
                + e.GetType().Name + ": " + e.Message);
        }
    }
}
