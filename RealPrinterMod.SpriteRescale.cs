// ═══════════════════════════════════════════════════════════
// ★ 精灵缩放（Sprite Rescale）—— V1.4.12 方案 D-lite
//
// 【与 V1.4.9~V1.4.11 旧方案的区别】
//   旧方案：读源像素 → 双线性/最近邻重采样 → 建新 Texture2D → 建新 Sprite。
//     开销大；且 32→16 / 48→32 不是整数倍，无论怎么采样都损质量（"糊"）。
//   新方案：**复用原纹理，只改 pixelsPerUnit** —— 让 Unity 渲染时缩小。
//     · 零像素操作：不读源像素、不建纹理、不做重采样
//     · 零额外显存：纹理是同一张
//     · 视觉质量取决于源图集 filterMode：
//         Point → 最近邻采样，像素画锐利
//         Bilinear → 双线性采样，略有模糊但比旧方案好
//
// 【物理限制 —— 为什么不能完美匹配 1×2 格】
//   2×3 → 1×2 是**各向异性**缩放（宽缩 1/2，高缩 2/3）。
//   而 pixelsPerUnit 是**各向同性**的，只能整体等比缩放。
//   → 只能选一方向匹配。这里选**横向匹配**（图宽 = 格宽），
//     纵向会略短（原图宽高比 2:3，格子宽高比 1:2）。
//   若要完美匹配，只能回到"重建纹理"旧方案——而旧方案的画质问题
//   正是本次要规避的，所以不回去。
//
// 【为什么用 Sprite.Create 而不直接改 __result.pixelsPerUnit】
//   Unity 的 `Sprite.pixelsPerUnit` 是 **get-only**（IL2CPP 绑定同样只读）。
//   要改只能通过 Sprite.Create 重建一个 Sprite 包装对象——
//   **只复制元数据，不复制纹理**，开销极小。
//
// 【历史教训留档（旧方案踩过的坑，本方案天然绕过）】
//   · V1.4.8 启动预热 → 图集未加载 → `_tried=true` 永久锁 → 图永远不缩。
//   · V1.4.10 双线性重采样 → 像素画糊。
//   · 旧方案还要处理 DXT/ASTC 压缩纹理的 Blit 绕道。
//   → 本方案**不读像素、不建纹理**，以上问题全部不存在。
// ═══════════════════════════════════════════════════════════

using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace RealPrinter
{

    internal static class RealPrinterSpriteRescale
    {
        /// <summary>原图占几格（用于算 ppu 缩放倍数；不写死像素）。</summary>
        private const float SrcCellW = 2f;
        private const float SrcCellH = 3f;

        /// <summary>目标格子数（必须与 ShrinkShape 的 ShrinkW/ShrinkH 一致）。</summary>
        private const float TargetCellW = RealPrinterMod.ShrinkWF;
        private const float TargetCellH = RealPrinterMod.ShrinkHF;

        // ★ V1.4.12a：改用**纵向匹配**（图高对齐格高）。
        //   【为什么改】横向匹配（ppu×2）实机结果 = 16×24 px = 1×1.5 格，
        //     整体偏小、视觉上像"只占一格"。
        //   纵向匹配 = 图 21.3×32 px ≈ 1.33×2 格：纵向填满，横向略溢 1/3。
        private const float PpuScale = SrcCellH / TargetCellH;   // = 1.5

        private static Sprite _scaled;
        private static int _scaledFromId;
        private static bool _reported;

        /// <summary>取 ppu 放大后的 sprite（复用原纹理，只改元数据）。</summary>
        internal static Sprite GetShrunk(Sprite sourceSprite)
        {
            if (sourceSprite == null) return null;

            if (_scaled != null && _scaledFromId == sourceSprite.GetInstanceID())
                return _scaled;                          // 命中缓存

            try
            {
                // ★ pivot 转换：Sprite.pivot 是**像素坐标**（相对 rect 左下），
                //   而 Sprite.Create 的 pivot 参数要**归一化坐标**（0~1 相对 rect）。
                //   直接相除即可；有些 IL2CPP 版本可能已经是归一化的，
                //   但当前游戏实测（V1.4.9 旧方案）走的是像素口径。
                var rect = sourceSprite.rect;
                Vector2 pivotNorm = new Vector2(
                    rect.width > 0f ? sourceSprite.pivot.x / rect.width : 0.5f,
                    rect.height > 0f ? sourceSprite.pivot.y / rect.height : 0.5f);

                // ★ 复用原纹理、原 rect —— 只把 ppu 放大。
                var s = Sprite.Create(
                    sourceSprite.texture,
                    rect,
                    pivotNorm,
                    sourceSprite.pixelsPerUnit * PpuScale,
                    0u,                            // extrude
                    SpriteMeshType.FullRect,       // 机器图不需要 Tight 网格
                    Vector4.zero);                 // border（机器图无 9-slice）

                if (s == null) return null;
                _scaled = s;
                _scaledFromId = sourceSprite.GetInstanceID();
                if (!_reported)
                {
                    var fm = "?";
                    try { fm = sourceSprite.texture.filterMode.ToString(); } catch { }
                    RealPrinterMod.RLog.Msg("[缩图] ✓ 机器图已按 ppu×" + PpuScale
                        + " 缩放（纹理复用，采样=" + fm + "，宽 "
                        + rect.width + "×" + rect.height + " px）");
                    _reported = true;
                }
                return _scaled;
            }
            catch (Exception e)
            {
                RealPrinterMod.RLog.Warning("[缩图] 缩放失败（保持原图）: "
                    + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>自检/横幅用。</summary>
        internal static string Report()
        {
            return _scaled != null
                ? "已缩放 ppu×" + PpuScale + "（纹理复用，无重采样）"
                : "未生成";
        }
    }

    // ────────────────────────────────────────────────────────────
    // Harmony 补丁：元素层替换 sprite（双门槛判据与旧方案完全一致）
    //   · 门槛① 归属：__instance.identifier == 我们机器的 identifier
    //   · 门槛② 名字：name == __instance.spritePath（防配件层"按名字取别的图"被误换）
    // ────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.ResolveSpriteByName))]
    internal static class RealPrinter_RescaleSpritePatch
    {
        private static void Postfix(GameItemElement __instance, string name, ref Sprite __result)
        {
            try
            {
                if (__result == null || __instance == null) return;

                // 门槛 ①：归属
                string id = null;
                try { id = __instance.identifier; } catch { return; }
                if (!string.Equals(id, RealPrinterMod.MachineIdentifier,
                                   StringComparison.Ordinal)) return;

                // 门槛 ②：名字
                string myPath = null;
                try { myPath = __instance.spritePath; } catch { return; }
                if (myPath == null || !string.Equals(name, myPath, StringComparison.Ordinal)) return;

                Sprite small = RealPrinterSpriteRescale.GetShrunk(__result);
                if (small != null) __result = small;
            }
            catch (Exception e)
            {
                RealPrinterMod.RLog.Warning("[缩图] Postfix 异常: " + e.Message);
            }
        }
    }

} // namespace RealPrinter