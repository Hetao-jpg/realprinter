#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
部署前自检：MelonGame / MelonInfo 属性是否与游戏实际一致。

★★★ 为什么必须有这个脚本 ★★★
2026-10-03 血泪：RealPrinter 的 `MelonGame("Kinda Brave", "Probably Stolen")`
公司名是**串到别的游戏去了**（从没核实过）。后果不是报错，而是：

    **整个 mod 被 MelonLoader 静默拒绝加载** ——
    没有一行日志、没有异常、功能一点都没有、cfg 段也不出现。

核桃按 F6 毫无反应，日志里连 `[真印]` 都找不到，最后靠
"cfg 段不存在" 才反推出「mod 根本没被加载」。

编译期不报、运行期无日志 —— 这类故障**只能靠机器查**。

用法：
  python tools/check_mod_identity.py            # 查工作区里的工程
  python tools/check_mod_identity.py <csproj目录> ...
"""
import io
import os
import re
import sys

DEFAULT_GAME_DIR = r"D:/SteamLibrary/steamapps/common/Probably Stolen Demo"


def read_game_identity(game_dir):
    """从游戏自己的 app.info 读出**权威**的（公司名, 游戏名）。"""
    p = os.path.join(game_dir, "Probably Stolen_Data", "app.info")
    if not os.path.exists(p):
        return None, None, "找不到 " + p
    raw = io.open(p, "rb").read()
    # app.info 是普通 utf-8；用启发式挑出两段可打印 ASCII
    parts = []
    cur = []
    for b in raw:
        if 32 <= b < 127:
            cur.append(chr(b))
        else:
            if len("".join(cur).strip()) >= 4:
                parts.append("".join(cur).strip())
            cur = []
    if "".join(cur).strip():
        parts.append("".join(cur).strip())
    if len(parts) < 2:
        return None, None, "app.info 里没解析出两段字符串（内容=%r）" % (parts,)
    return parts[0], parts[1], None


def check_project(proj_dir, want_company, want_game):
    src = None
    for f in os.listdir(proj_dir):
        if f.endswith(".csproj"):
            src = os.path.join(proj_dir, f)
    if src is None:
        return ["%s：找不到 .csproj" % proj_dir]

    asm_name = None
    m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>",
                  io.open(src, encoding="utf-8", errors="ignore").read())
    if m:
        asm_name = m.group(1)

    cs_files = [f for f in os.listdir(proj_dir) if f.endswith(".cs")]
    problems = []

    # —— MelonGame ——
    mg_found = False
    for f in cs_files:
        txt = io.open(os.path.join(proj_dir, f), encoding="utf-8", errors="ignore").read()
        for m in re.finditer(r'MelonGame\(\s*"([^"]*)"\s*,\s*"([^"]*)"', txt):
            mg_found = True
            company, game = m.group(1), m.group(2)
            if company != want_company:
                problems.append(
                    '★★ MelonGame 公司名不匹配：写的是 "%s"，游戏实际是 "%s"\n'
                    '   后果：整个 mod 被 MelonLoader 静默拒绝加载（无日志、无异常、功能全无）。\n'
                    '   位置：%s' % (company, want_company, f))
            if game != want_game:
                problems.append(
                    '★★ MelonGame 游戏名不匹配：写的是 "%s"，游戏实际是 "%s"\n'
                    '   位置：%s' % (game, want_game, f))
    if not mg_found:
        problems.append("⚠ 源码里找不到 MelonGame 特性 —— 没有它 mod 不会加载。")

    # —— cfg 段名必须 = DLL 名（ModManager 面板一项都不显示的经典原因）——
    if asm_name and not re.match(r"^[A-Za-z0-9]+$", asm_name):
        problems.append("★★ AssemblyName 含非字母数字（%s）→ ModManager 面板可能一项都不显示。"
                        % asm_name)

    # —— 构建产物里必须**不含**旧公司名（确认改干净了）——
    dll = None
    for base in ("bin/Release", "bin/Debug"):
        cand = os.path.join(proj_dir, base, (asm_name or "x") + ".dll")
        if os.path.exists(cand):
            dll = cand
            break
    if dll:
        blob = io.open(dll, "rb").read()
        if want_company.encode() not in blob:
            problems.append("★★ 构建产物里找不到正确的公司名 '%s' —— "
                            "DLL 可能不是最新构建的。" % want_company)
        if b"Kinda Brave" in blob:
            problems.append("★★ 构建产物里仍残留旧公司名 'Kinda Brave' —— "
                            "重新 clean 构建。")
    else:
        problems.append("⚠ 找不到构建产物 DLL，跳过产物层校验。")

    return problems


def main():
    dirs = sys.argv[1:] or [os.path.dirname(os.path.abspath(__file__)) + "/.."]
    company, game, err = read_game_identity(DEFAULT_GAME_DIR)
    if err:
        print("!! 无法读取游戏身份：%s" % err)
        print("   请手工确认 MelonGame(\"<公司名>\", \"%s\") 是否正确。" % game)
        return 1

    print("游戏权威身份（读自 app.info）：")
    print("    公司 = %s" % company)
    print("    游戏 = %s\n" % game)

    bad = 0
    for d in dirs:
        d = os.path.abspath(d)
        print("── 检查 %s" % d)
        probs = check_project(d, company, game)
        if not probs:
            print("   OK：MelonGame 与游戏一致，产物含正确公司名。\n")
        else:
            bad += 1
            for p in probs:
                print("   " + p)
            print("")

    print("=" * 60)
    print("全部通过，可以部署。" if bad == 0 else "有 %d 个工程不合格，**不要部署**。" % bad)
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())