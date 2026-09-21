using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// ビルド時に日本語のフォントアセット (アトラス込み) を作って Resources に置く。
///
/// 実行時に TMP_FontAsset を作る方式 (CreateFontAsset + 動的追加) は、プレイヤーではアトラスの
/// テクスチャが更新されず、画面によって文字が出たり出なかったりした (ボタンの文字が消える)。
/// ここでアトラスを焼いて資産として持たせれば、実行時のラスタライズが要らない。
/// 使う文字は UiCharacters.All (scripts/gen_ui_characters.py が生成) + ASCII。
/// </summary>
public static class LabFontBuilder
{
    public const string AssetPath = "Assets/UniRoboLab/Resources/JapaneseFont.asset";
    public const string ResourceName = "JapaneseFont";

    static readonly string[] k_Keys =
    {
        "notosanscjkjp", "notosanscjk", "notosansjp", "yugoth", "meiryo", "hiragino",
        "ipagothic", "ipaexgothic", "ipag", "takao", "vlgothic", "droidsansfallback",
    };

    public static TMP_FontAsset Build()
    {
        string path = FindFontFile();
        if (path == null)
        {
            Debug.LogWarning("[LabFontBuilder] no Japanese font on this machine; the player will fall back to English text");
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
        AssetDatabase.DeleteAsset(AssetPath);

        TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(path, 0, 42, 5, GlyphRenderMode.SDFAA, 4096, 4096);
        if (fa == null)
        {
            Debug.LogWarning($"[LabFontBuilder] could not read {path}");
            return null;
        }
        var all = new System.Text.StringBuilder();
        for (char c = ' '; c <= '~'; c++) all.Append(c);
        all.Append(UiCharacters.All);
        if (!fa.TryAddCharacters(all.ToString(), out string missing))
            Debug.LogWarning($"[LabFontBuilder] {missing?.Length ?? 0} characters are not in {Path.GetFileName(path)}: {missing}");

        // 焼いたアトラスをそのまま資産にする (実行時に足さない)
        fa.atlasPopulationMode = AtlasPopulationMode.Static;
        fa.name = ResourceName;
        AssetDatabase.CreateAsset(fa, AssetPath);
        foreach (Texture2D tex in fa.atlasTextures)
        {
            tex.name = ResourceName + "Atlas";
            AssetDatabase.AddObjectToAsset(tex, fa);
        }
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[LabFontBuilder] {AssetPath} from {path} ({fa.characterTable.Count} glyphs, {fa.atlasTextures.Length} atlas)");
        return fa;
    }

    static string FindFontFile()
    {
        string[] paths = Font.GetPathsToOSFonts();
        foreach (string key in k_Keys)
            foreach (string p in paths)
            {
                string file = Path.GetFileName(p).ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
                // Regular を優先する (Black や Thin が先に見つかると太すぎ/細すぎる)
                if (file.Contains(key) && (file.Contains("regular") || file.Contains("cjkjp") && !file.Contains("black") && !file.Contains("thin") && !file.Contains("light")))
                    return p;
            }
        foreach (string key in k_Keys)
            foreach (string p in paths)
                if (Path.GetFileName(p).ToLowerInvariant().Replace("-", "").Replace("_", "").Contains(key)) return p;
        return null;
    }

    [MenuItem("UniRoboLab/Rebuild Japanese font asset")]
    static void Menu() => Build();
}
