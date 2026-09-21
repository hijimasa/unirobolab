#!/usr/bin/env python3
"""Regenerate Assets/UniRoboLab/Scripts/UiCharacters.cs from the sources.

The GUI bakes every non-ASCII character it can show into the font atlas at start-up, because
glyphs added later are not uploaded to the atlas texture in a built player (they simply do not
draw). Run this after adding Japanese text to the C# scripts or to the Python messages the GUI
displays, then run the editor tests: UiCharactersTests fails when a character is missing.
"""
import glob
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATTERNS = ("unity/UniRoboLab/Assets/UniRoboLab/**/*.cs", "python/src/unirobolab/**/*.py")
RANGES = ((0x3000, 0x303F), (0x3040, 0x309F), (0x30A0, 0x30FF), (0xFF01, 0xFF5E),
          (0x2010, 0x201F), (0x2190, 0x2193))
EXTRA = "°±×÷→←↑↓✓●■▶!?…※©℃㎡"
OUT = os.path.join(ROOT, "unity/UniRoboLab/Assets/UniRoboLab/Scripts/UiCharacters.cs")


def collect() -> str:
    chars = set()
    for pat in PATTERNS:
        for f in glob.glob(os.path.join(ROOT, pat), recursive=True):
            if f.endswith("UiCharacters.cs"):
                continue
            with open(f, encoding="utf-8") as fh:
                chars.update(c for c in fh.read() if ord(c) > 0x7E and c not in "\r\n\t")
    for lo, hi in RANGES:
        chars.update(chr(c) for c in range(lo, hi + 1))
    chars.update(EXTRA)
    return "".join(sorted(chars))


def main() -> int:
    chars = collect()
    esc = {'"': '\\"', "\\": "\\\\"}
    lines, cur = [], ""
    for c in chars:
        cur += esc.get(c, c)
        if len(cur) >= 72:
            lines.append(cur); cur = ""
    if cur:
        lines.append(cur)
    body = "\n        + ".join('"%s"' % l for l in lines)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("// 自動生成 (scripts/gen_ui_characters.py)。手で編集しない。\n"
                "// 画面に出る非 ASCII 文字をすべて並べたもの。起動時にフォントのアトラスへ焼き込む。\n"
                "// 実行時に足したグリフはプレイヤーではアトラスへ反映されない (描画されない) ため、\n"
                "// 使う文字は最初に全部入れておく必要がある。\n"
                "public static class UiCharacters\n{\n    public const string All =\n        " + body + ";\n}\n")
    print(f"wrote {OUT} ({len(chars)} characters)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
