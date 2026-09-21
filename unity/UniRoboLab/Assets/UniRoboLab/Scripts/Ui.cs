using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// コードで組む uGUI の小さなヘルパーと、言語 (日本語フォントがあれば日本語) の辞書。
/// 画面はすべてこれで組む (シーンの手編集はしない)。
/// </summary>
public static class Ui
{
    public static readonly Color Bg = new Color(0.10f, 0.10f, 0.12f, 1f);
    public static readonly Color Panel = new Color(0.15f, 0.15f, 0.18f, 1f);
    public static readonly Color Panel2 = new Color(0.20f, 0.20f, 0.24f, 1f);
    public static readonly Color Text = new Color(0.88f, 0.88f, 0.88f, 1f);
    public static readonly Color Muted = new Color(0.60f, 0.60f, 0.64f, 1f);
    public static readonly Color Header = new Color(0.97f, 0.97f, 0.97f, 1f);
    public static readonly Color Accent = new Color(0.35f, 0.80f, 0.45f, 1f);
    public static readonly Color Warn = new Color(0.95f, 0.70f, 0.30f, 1f);
    public static readonly Color Bad = new Color(0.95f, 0.40f, 0.40f, 1f);
    public static readonly Color BtnNormal = new Color(0.27f, 0.27f, 0.32f, 1f);
    public static readonly Color BtnActive = new Color(0.30f, 0.55f, 0.85f, 1f);
    public static readonly Color BtnDone = new Color(0.25f, 0.45f, 0.30f, 1f);

    /// <summary>表示言語 (true = 日本語)。切り替えは LabWizard.SwitchLanguage から。</summary>
    public static bool Japanese { get; private set; }

    /// <summary>日本語フォントが使えるか。使えないときは切り替えても字が出ないので、切り替えを出さない。</summary>
    public static bool JapaneseAvailable { get; private set; }

    static string LanguageFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "unirobolab", "language.txt");

    /// <summary>選んだ言語を覚える (次回の起動でもその言語)。</summary>
    public static void SetJapanese(bool japanese)
    {
        Japanese = japanese && JapaneseAvailable;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LanguageFile));
            System.IO.File.WriteAllText(LanguageFile, (Japanese ? "ja" : "en") + "\n");
        }
        catch (Exception) { }
    }

    static bool? SavedLanguage()
    {
        try
        {
            if (!System.IO.File.Exists(LanguageFile)) return null;
            string v = System.IO.File.ReadAllText(LanguageFile).Trim().ToLowerInvariant();
            if (v.StartsWith("ja")) return true;
            if (v.StartsWith("en")) return false;
        }
        catch (Exception) { }
        return null;
    }
    static bool s_FontProbed;

    /// <summary>ja/en の 2 択。ラベルも文も同じ言語で出す。</summary>
    public static string T(string ja, string en) => Japanese ? ja : en;

    /// <summary>OS の日本語フォントから TMP の動的フォントを作ってフォールバックに足す。無ければ英語表示。</summary>
    static TMP_FontAsset s_Font;

    /// <summary>作った TMP のテキストに日本語フォントを直接あてる。予備フォント (fallback) 任せにすると、
    /// 予備側の字を描く子メッシュが作られ、無効な状態で作られた画面では日本語が丸ごと出ない
    /// (ボタンの文字が消え、ラベルが ASCII の 1 文字で切れる)。直接あてればその経路を通らない。</summary>
    static void ApplyFont(TMP_Text t)
    {
        if (s_Font != null && t != null) t.font = s_Font;
    }

    public static void EnsureJapaneseFont()
    {
        if (s_FontProbed) return;
        s_FontProbed = true;
        try
        {
            // ビルド時に焼いたアセット (Editor/LabFontBuilder)。実行時のラスタライズが要らないので、
            // 画面によって文字が消える問題が起きない。
            TMP_FontAsset baked = Resources.Load<TMP_FontAsset>("JapaneseFont");
            if (baked != null)
            {
                UseFont(baked, "Resources/JapaneseFont (baked)");
                return;
            }

            // 日本語は画面に出る字種が多い。TMP の既定の作り方 (CreateFontAsset(family, style, size)) は
            // 1024x1024 のアトラスなので、この画面数の日本語では途中で埋まり、以後に必要になった字が
            // 空白になる (ボタンの文字が消える、ラベルが 1 文字で切れる)。フォントのファイルを直接指定して
            // 4096x4096 で作る。見つからなければ従来の作り方に落ちる。
            string[] keys = { "notosanscjk", "notosansjp", "yugoth", "meiryo", "hiragino", "ipagothic", "ipaexgothic",
                              "ipag", "takao", "vlgothic", "droidsansfallback", "notoserifcjk" };
            foreach (string key in keys)
            {
                foreach (string path in Font.GetPathsToOSFonts())
                {
                    string file = System.IO.Path.GetFileName(path).ToLowerInvariant().Replace("-", "").Replace("_", "");
                    if (!file.Contains(key)) continue;
                    TMP_FontAsset fa = null;
                    try
                    {
                        fa = TMP_FontAsset.CreateFontAsset(path, 0, 32, 6, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 4096, 4096);
                    }
                    catch (Exception) { fa = null; }
                    if (fa == null) continue;
                    fa.isMultiAtlasTexturesEnabled = true;
                    UseFont(fa, path);
                    return;
                }
            }
            string[] installed = Font.GetOSInstalledFontNames();
            var candidates = new List<string>();
            foreach (string key in new[] { "noto sans cjk jp", "noto sans jp", "yu gothic ui", "meiryo", "hiragino sans", "noto sans cjk", "droid sans fallback", "ipagothic", "ipaexgothic", "takao", "vl gothic" })
                foreach (string n in installed)
                    if (n.ToLowerInvariant().Contains(key) && !candidates.Contains(n)) candidates.Add(n);
            foreach (string name in candidates)
            {
                TMP_FontAsset fa = null;
                foreach (string style in new[] { "Regular", "" })
                {
                    try { fa = TMP_FontAsset.CreateFontAsset(name, style, 24); } catch (Exception) { fa = null; }
                    if (fa != null) break;
                }
                if (fa == null) continue;
                fa.isMultiAtlasTexturesEnabled = true;
                UseFont(fa, name);
                return;
            }
            Debug.Log("[Ui] no japanese OS font; english text");
        }
        catch (Exception e) { Debug.LogWarning("[Ui] font probe failed: " + e.Message); }
    }

    static void UseFont(TMP_FontAsset fa, string source)
    {
        var fallbacks = TMP_Settings.fallbackFontAssets ?? new List<TMP_FontAsset>();
        fallbacks.Add(fa);
        TMP_Settings.fallbackFontAssets = fallbacks;
        JapaneseAvailable = true;
        // 保存した選択があればそれに従う。無ければ日本語 (フォントがある環境の既定)
        Japanese = SavedLanguage() ?? true;
        s_Font = fa;
        // 画面に出る日本語をまとめて焼いておく。必要になってから足す方式だと、アトラスが埋まった時点で
        // 以後の文字が空白になり、ボタンの文字が消える。ここで入らなければログに出る (診断用)。
        if (fa.atlasPopulationMode != AtlasPopulationMode.Static)
        {
            var sb = new System.Text.StringBuilder();
            for (char c = ' '; c <= '~'; c++) sb.Append(c);   // ASCII (パス、関節名、数値)
            sb.Append(UiCharacters.All);                      // 画面に出る日本語と記号 (自動生成)
            fa.TryAddCharacters(sb.ToString(), out _);
        }
        Debug.Log($"[Ui] japanese font: {source} (atlas {fa.atlasWidth}x{fa.atlasHeight} x{fa.atlasTextures.Length}, "
                  + $"glyphs {fa.characterTable.Count}, mode {fa.atlasPopulationMode})");
    }

    // ------------------------------------------------------------------ layout
    public static GameObject Column(Transform parent, string name, float spacing = 4f, int pad = 0, bool flexible = true)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var l = go.GetComponent<VerticalLayoutGroup>(); l.spacing = spacing; l.padding = new RectOffset(pad, pad, pad, pad);
        l.childForceExpandHeight = false; l.childControlHeight = true; l.childControlWidth = true; l.childForceExpandWidth = true;
        go.GetComponent<LayoutElement>().flexibleHeight = flexible ? 1f : 0f;
        return go;
    }

    public static GameObject Row(Transform parent, float height, float spacing = 6f)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var l = go.GetComponent<HorizontalLayoutGroup>(); l.spacing = spacing; l.childForceExpandWidth = true; l.childForceExpandHeight = false; l.childControlWidth = true; l.childControlHeight = true; l.childAlignment = TextAnchor.MiddleLeft;
        var le = go.GetComponent<LayoutElement>(); le.preferredHeight = height; le.flexibleHeight = 0f;
        return go;
    }

    public static GameObject Spacer(Transform parent, float flexible = 1f, float height = 0f)
    {
        var go = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var le = go.GetComponent<LayoutElement>(); le.flexibleHeight = flexible; le.preferredHeight = height;
        return go;
    }

    public static Image Box(Transform parent, Color color, float height = 0f, bool flexible = false)
    {
        var go = new GameObject("Box", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        var le = go.GetComponent<LayoutElement>(); if (height > 0f) le.preferredHeight = height; le.flexibleHeight = flexible ? 1f : 0f;
        return go.GetComponent<Image>();
    }

    public static TMP_Text Label(Transform parent, string text, float size = 13f, Color? color = null, bool wrap = false, float height = 0f, float width = 0f)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var le = go.GetComponent<LayoutElement>(); le.flexibleHeight = 0f;
        if (wrap)
        {
            // 折り返す文は行数が中身で決まる。ここで高さを固定すると、実際に必要な高さと食い違ったときに
            // レイアウトと TMP の組版が噛み合わず、同じ画面の他のテキストまで組まれずに消える
            // (② の物体タスクでボタンの文字が全部消えていた)。最低限だけ決めて、高さは TMP に任せる。
            le.minHeight = height > 0f ? height : size + 8f;
            le.preferredHeight = -1f;
        }
        else
        {
            le.preferredHeight = height > 0f ? height : size + 8f;
        }
        if (width > 0f) { le.preferredWidth = width; le.flexibleWidth = 0f; }
        var tmp = go.AddComponent<TextMeshProUGUI>(); ApplyFont(tmp);
        tmp.text = text; tmp.fontSize = size; tmp.color = color ?? Text; tmp.enableWordWrapping = wrap;
        // Overflow にする: 行の高さが足りないとき、Truncate は「入らない」と判断して何も描かない。
        // 画面が縦に詰まると文字が消えてしまうので、はみ出してでも描く
        tmp.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Overflow;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return tmp;
    }

    public static TMP_InputField Input(Transform parent, string placeholder, float height = 26f, bool multiline = false)
    {
        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.layoutPriority = 2; le.preferredHeight = height; le.flexibleHeight = 0f;   // TMP_InputField 自身の申告より優先
        var f = go.GetComponent<TMP_InputField>();
        ApplyFont(f.textComponent); ApplyFont(f.placeholder as TMP_Text);
        // 入力欄は内側に RectMask2D を持つ。マスクの切り取りはマテリアル単位なので、フォントの共有
        // マテリアルのままだと、同じフォントを使う画面上の別のテキスト (ボタンの文字など) まで切り取られて
        // 消える。入力欄のテキストには専用のマテリアルを持たせて切り離す。
        if (f.textComponent != null) f.textComponent.fontMaterial.name = "InputText";
        TMP_Text placeholderText = f.placeholder as TMP_Text;
        if (placeholderText != null) placeholderText.fontMaterial.name = "InputPlaceholder";
        f.pointSize = 12f;
        if (f.placeholder is TMP_Text ph) { ph.text = placeholder; ph.fontSize = 12f; }
        if (multiline) { f.lineType = TMP_InputField.LineType.MultiLineNewline; f.textComponent.alignment = TextAlignmentOptions.TopLeft; }
        return f;
    }

    public static Button Btn(Transform parent, string label, UnityEngine.Events.UnityAction onClick, float width = 0f, float height = 28f)
    {
        GameObject go = DefaultControls.CreateButton(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = height; le.flexibleHeight = 0f;
        if (width > 0f) { le.preferredWidth = width; le.flexibleWidth = 0f; }
        var text = go.GetComponentInChildren<Text>();
        if (text != null) UnityEngine.Object.Destroy(text.gameObject);
        var tgo = new GameObject("Text", typeof(RectTransform));
        tgo.transform.SetParent(go.transform, false);
        var rt = tgo.GetComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(6, 2); rt.offsetMax = new Vector2(-6, -2);
        var tmp = tgo.AddComponent<TextMeshProUGUI>(); ApplyFont(tmp); tmp.text = label; tmp.fontSize = 13f; tmp.color = Header; tmp.alignment = TextAlignmentOptions.Center; tmp.overflowMode = TextOverflowModes.Overflow;
        go.GetComponent<Image>().color = BtnNormal;
        var b = go.GetComponent<Button>();
        var colors = b.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f); colors.pressedColor = new Color(0.8f, 0.8f, 0.8f); colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f); b.colors = colors;
        b.onClick.AddListener(onClick);
        return b;
    }

    public static void SetBtn(Button b, string label = null, Color? bg = null, bool? interactable = null)
    {
        if (b == null) return;
        if (label != null) { var t = b.GetComponentInChildren<TMP_Text>(); if (t != null) t.text = label; }
        if (bg.HasValue) b.GetComponent<Image>().color = bg.Value;
        if (interactable.HasValue) b.interactable = interactable.Value;
    }

    public static Slider Slider(Transform parent, float min, float max, float value, UnityEngine.Events.UnityAction<float> onChange, bool whole = false)
    {
        GameObject go = DefaultControls.CreateSlider(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 20f; le.flexibleHeight = 0f;
        var s = go.GetComponent<Slider>(); s.minValue = min; s.maxValue = max; s.wholeNumbers = whole; s.value = value;
        s.onValueChanged.AddListener(onChange);
        return s;
    }

    /// <summary>ラジオ風の選択: ボタン列で 1 つだけ強調。</summary>
    public class Choice
    {
        public readonly List<Button> Buttons = new List<Button>();
        public int Index { get; private set; }
        public Action<int> OnChange;
        public Choice(Transform parent, string[] labels, int initial, float height = 26f)
        {
            GameObject row = Row(parent, height);
            for (int i = 0; i < labels.Length; i++) { int k = i; Buttons.Add(Btn(row.transform, labels[i], () => Set(k), 0f, height)); }
            Set(initial);
        }
        public void Set(int i)
        {
            Index = Mathf.Clamp(i, 0, Buttons.Count - 1);
            for (int k = 0; k < Buttons.Count; k++) SetBtn(Buttons[k], null, k == Index ? BtnActive : BtnNormal);
            OnChange?.Invoke(Index);
        }
    }

    /// <summary>ラベル + 入力欄の 1 行 (ラベル幅固定)。</summary>
    public static TMP_InputField Field(Transform parent, string label, string placeholder, float labelWidth = 110f, float height = 26f)
    {
        GameObject row = Row(parent, height);
        Label(row.transform, label, 12f, Muted, false, 0f, labelWidth);
        return Input(row.transform, placeholder, height - 2f);
    }

    public static RawImage Image(Transform parent, Texture tex, float height)
    {
        var go = new GameObject("Image", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var le = go.GetComponent<LayoutElement>(); le.preferredHeight = height; le.flexibleHeight = 0f;
        var img = go.GetComponent<RawImage>(); img.texture = tex;
        return img;
    }

    public static Texture2D DarkTexture(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h]; for (int i = 0; i < px.Length; i++) px[i] = new Color32(20, 20, 24, 255);
        tex.SetPixels32(px); tex.Apply();
        return tex;
    }

    /// <summary>JSON 文字列から数値を拾う (最小限の走査。GUI は JSON を組み立てない)。</summary>
    public static float Num(string json, string key, float fallback)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return fallback;
        int c = json.IndexOf(':', i); int e = c + 1;
        while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-' || json[e] == 'e' || json[e] == ' ')) e++;
        return float.TryParse(json.Substring(c + 1, e - c - 1).Trim(), out float v) ? v : fallback;
    }

    public static string Str(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int q1 = json.IndexOf('"', json.IndexOf(':', i)); int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
        return q1 >= 0 && q2 > q1 ? json.Substring(q1 + 1, q2 - q1 - 1) : null;
    }
}
