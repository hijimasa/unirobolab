using UnityEngine;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

/// <summary>
/// 画面に出る文字は起動時にフォントのアトラスへ焼き込む (Ui.EnsureJapaneseFont)。プレイヤーでは
/// 実行時に足したグリフがアトラスへ反映されず、その文字が描画されない (ボタンの文字が消える) ため。
/// ここでは、スクリプト中の非 ASCII 文字がすべて UiCharacters.All に入っていることを確かめる。
/// 足りないときは scripts/gen_ui_characters.py を実行して作り直す。
/// </summary>
public class UiCharactersTests
{
    static string ScriptsDir()
    {
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "UniRoboLab", "Scripts"));
        Assert.IsTrue(Directory.Exists(dir), dir);
        return dir;
    }

    [Test]
    public void EveryNonAsciiCharacterInTheScriptsIsBaked()
    {
        var baked = new HashSet<char>(UiCharacters.All);
        var missing = new SortedSet<char>();
        foreach (string path in Directory.GetFiles(ScriptsDir(), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == "UiCharacters.cs") continue;
            foreach (char c in File.ReadAllText(path))
                if (c > '~' && c != '\r' && c != '\n' && c != '\t' && !baked.Contains(c)) missing.Add(c);
        }
        Assert.IsEmpty(missing, "not baked into the font atlas (run scripts/gen_ui_characters.py): " + string.Join("", missing));
    }

    [Test]
    public void KanaAndCommonSymbolsAreCovered()
    {
        var baked = new HashSet<char>(UiCharacters.All);
        foreach (char c in "あいうえおアイウエオ①②③④⑤⑥。、「」±×→✓●") Assert.IsTrue(baked.Contains(c), $"missing {c}");
    }
}
