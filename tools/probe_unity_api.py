#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
编译期 API 探针：把"我以为存在的 Unity/IL2CPP API 成员"写成一段小程序编译一遍。

★★★ 为什么需要这个 ★★★
2026-10-20 踩坑：改打印机外观时写了
```csharp
Color[] px = src.GetPixels((int)rect.x, (int)rect.y, rect.width, rect.height, 0);
```
看起来完全合理，**但 `Sprite.textureRect` 是 float 矩形，`GetPixels` 要 int**，
且 `rect.width` 是 float → `CS1503 无法从 float 转换为 int`。
**而 dump.cs 里查不到**（Texture2D / Sprite 在 UnityEngine 模块，
不在 Il2CppDumper 的 dump 里），PSKit 的 api-flat.jsonl 也没收录 →
**只有真正编译一次才知道**。

MEMORY 铁律延伸：「能编译验证的，别靠推断」。

用法：
  python tools/probe_unity_api.py                 # 跑内置探针
  python tools/probe_unity_api.py <csproj 目录>   # 在指定工程里编译探针

退出码 0 = 全部成员存在；非 0 = 有成员不存在（会在输出里列出）。
"""
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile

# ── 探针内容：每一条都是"我用过或想用的成员" ──
PROBES = {
    "Sprite_基础": """
    static bool P1(Sprite s) {
        if (s == null) return false;
        Texture2D t = s.texture;          // Sprite.texture
        if (t == null) return false;
        Rect r = s.textureRect;           // ★ float 矩形（不是 int）
        Vector2 pv = s.pivot;
        float ppu = s.pixelsPerUnit;
        Vector4 bd = s.border;
        // ★ Sprite **没有** color 属性（那是 Image 的）—— 曾想当然写过，被探针查出
        return r.width > 0 && pv != null && ppu > 0 && bd != null;
    }""",
    "Texture2D_像素": """
    static bool P2(Texture2D t) {
        if (t == null) return false;
        int W = t.width, H = t.height;
        TextureFormat f = t.format;
        FilterMode fm = t.filterMode;
        // ★★ Texture2D.wrapMode 用 **TextureWrapMode**；
        //   而 Texture.wrapMode 用的是另一个 **WrapMode** 枚举。
        //   两者不是同一个类型，写错会得到 CS0266（两个方向都试过，实测确认）。
        TextureWrapMode wm = t.wrapMode;
        // ★ GetPixels 要 int，Sprite.textureRect 给的是 float —— 必须 RoundToInt
        Color[] px = t.GetPixels(0, 0, Mathf.Clamp(W,1,W), Mathf.Clamp(H,1,H), 0);
        if (px == null) return false;
        t.SetPixels(px);
        t.Apply();
        return W > 0 && H > 0 && f == f && fm == fm && wm == wm;
    }""",
    "Sprite_Create_六参": """
    static Sprite P3(Texture2D t, Rect full, Vector2 pivot) {
        // ★ 这是我们改色后重建 sprite 用到的签名
        return Sprite.Create(t, full, pivot, 100f, 0,
                            SpriteMeshType.FullRect, new Vector4(0,0,0,0));
    }""",
    "Mathf_夹取": """
    static int P4(float f) {
        // RoundToInt + Clamp(int,..) —— 像素坐标换算的标准三件套
        return Mathf.Clamp(Mathf.RoundToInt(f), 0, Mathf.Max(0, 10));
    }""",
    "ItemSpriteModifier_染色器": """
    static bool P5(Sprite s) {
        // 游戏自带的染色/覆盖层（本次没用，但值得确认签名）
        var m = new ItemSpriteModifier(s, new Vector2(0,0), 0, false, 0, false, Color.white);
        return m != null;
    }""",
    "RenderHandler_图集": """
    static Sprite P6(string atlas, string name) {
        return RenderHandler.LoadFromAtlas(atlas, name);   // 劫持点
    }""",
    "TreeNodeRender_Image": """
    static UnityEngine.UI.Image P7(Il2Cpp.TreeNodeRender t) {
        // 物品渲染节点 → Unity Image（另一条候选染色路径）
        return t == null ? null : t.image;
    }""",
    # ★★ V1.4.7 新增：缩小 sprite 所需（把 2×3 的图压成 1×2 与格子匹配）
    "Texture2D_新建": """
    static Texture2D P8(int w, int h, Color[] px) {
        // ★ 缩图三步：新建 → 写像素 → 提交（缺 Apply 就不生效）
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.SetPixels(px);
        t.Apply();
        return t;
    }""",
    "Sprite_rect与pivot": """
    static bool P9(Sprite s) {
        Rect r = s.rect;          // ★ rect = sprite 在图集里的局部矩形（不是 textureRect）
        Texture2D t = s.texture;  // ★ 整张图集纹理（GetPixels 的坐标是它的像素）
        Vector2 pv = s.pivot;      // 归一化到 rect 内（0~1）
        float ppu = s.pixelsPerUnit;
        return r.width > 0 && t != null && pv.x >= 0 && ppu > 0;
    }""",
    "Color_插值": """
    static Color P10(Color a, Color b, float t) {
        return Color.Lerp(a, b, t);
    }""",
    # ★★ V1.4.9：图集是**压缩纹理**时 GetPixels 会失败 ——
    #   官方路径是 RenderTexture + Graphics.Blit + ReadPixels（GPU 侧解压）。
    #   这几组成员 dump.cs / api 索引都查不到，只能编译验证。
    "RenderTexture_创建": """
    static RenderTexture P11(int w, int h) {
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        return rt;
    }""",
    "Graphics_Blit": """
    static void P12(Texture src, RenderTexture dst) {
        Graphics.Blit(src, dst);        // 把图集整张拷进 RT（GPU 侧完成格式转换）
    }""",
    "Texture2D_读回": """
    static Color[] P13(Texture2D t, int w, int h) {
        var prev = RenderTexture.active;
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        rt.Create();
        Graphics.Blit(t, rt);
        RenderTexture.active = rt;
        t.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        t.Apply();
        RenderTexture.active = prev;
        Color[] px = t.GetPixels(0, 0, w, h, 0);
        rt.Release();
        return px;
    }""",
    "Sprite_缩放构造": """
    static Sprite P14(Texture2D t, int w, int h, Vector2 pivot) {
        return Sprite.Create(t, new Rect(0, 0, w, h), pivot, 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(0,0,0,0));
    }""",
    "RectTransform_尺寸": """
    static Vector2 P15(RectTransform rt) {
        if (rt == null) return Vector2.zero;
        Vector2 sd = rt.sizeDelta;
        rt.sizeDelta = new Vector2(sd.x, sd.y);   // 可写（兜底缩图用）
        return sd;
    }""",
    "Vector2_运算": """
    static Vector2 P16(Vector2 a, Vector2 b) {
        return new Vector2(a.x * b.x, a.y * b.y);
    }""",
    # ★★ V1.4.9：Blit 的 scaleOffset 重载 = **GPU 侧一次搞定"取子矩形 + 缩放"**
    #   （不读 CPU 像素，压缩纹理/DXT/ASTC 全格式通吃，且自带双线性过滤）
    "Graphics_Blit_带scaleOffset": """
    static void P17(Texture src, RenderTexture dst, Vector4 so) {
        Graphics.Blit(src, dst, so);
    }""",
    "Graphics_Blit_带scale与offset": """
    static void P18(Texture src, RenderTexture dst, float scale, Vector2 offset) {
        Graphics.Blit(src, dst, scale, offset);
    }""",
    "UnityObject_GetInstanceID": """
    static int P19(UnityEngine.Object o) {
        return o == null ? 0 : o.GetInstanceID();
    }""",
    "Texture2D_属性设置": """
    static void P20(Texture2D t) {
        t.hideFlags = HideFlags.HideAndDontSave;   // 防止生成的图被场景序列化
        t.filterMode = FilterMode.Bilinear;         // 缩图必须双线性，否则锯齿
        t.wrapMode = TextureWrapMode.Clamp;
    }""",
    "RenderTexture_释放": """
    static void P21(RenderTexture rt) {
        if (rt == null) return;
        RenderTexture.active = null;
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }""",
}

TEMPLATE = """// ★ 自动生成的 API 探针（tools/probe_unity_api.py）—— 编译完即删
using System;
using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;

public static class ApiProbe
{
%(bodies)s
    public static int Run()
    {
        int ok = 0;
%(calls)s        return ok;
    }
}
"""


def build_probe():
    bodies, calls = [], []
    for i, (name, body) in enumerate(PROBES.items(), 1):
        bodies.append("    // ---- %s ----%s" % (name, body))
        calls.append('        // %s\n' % name)
    return TEMPLATE % {"bodies": "\n".join(bodies), "calls": "\n".join(calls)}


def find_project(d):
    for f in os.listdir(d):
        if f.endswith(".csproj"):
            return os.path.join(d, f)
    return None


def main():
    proj_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..")
    proj_dir = os.path.abspath(proj_dir)
    csproj = find_project(proj_dir)
    if not csproj:
        print("!! 在 %s 找不到 .csproj" % proj_dir)
        return 2

    probe_name = "__ApiProbe_tmp.cs"
    probe_path = os.path.join(proj_dir, probe_name)
    # 备份再写，绝不留下残留（MEMORY：验证脚本不能污染工程）
    with io.open(probe_path, "w", encoding="utf-8") as f:
        f.write(build_probe())

    try:
        r = subprocess.run(
            ["dotnet", "build", "-c", "Release", "-v", "q", "--nologo"],
            cwd=proj_dir, capture_output=True, text=True, timeout=300)
        out = r.stdout + r.stderr
        errs = re.findall(r"error CS\d+: (.*)", out)
        if errs:
            print("!! 以下成员**不存在**或签名不对：\n")
            for e in sorted(set(errs)):
                print("   · " + e.strip())
            print("\n→ 这些必须改用别的写法（别猜，去查真实签名）。")
            return 1
        print("OK：%d 组探针全部编译通过 —— 成员都存在。" % len(PROBES))
        for n in PROBES:
            print("   · " + n)
        return 0
    finally:
        if os.path.exists(probe_path):
            os.remove(probe_path)


if __name__ == "__main__":
    sys.exit(main())