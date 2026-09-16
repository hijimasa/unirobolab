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

    public static bool Japanese { get; private set; }
    static bool s_FontProbed;

    /// <summary>ja/en の 2 択。ラベルも文も同じ言語で出す。</summary>
    public static string T(string ja, string en) => Japanese ? ja : en;

    /// <summary>OS の日本語フォントから TMP の動的フォントを作ってフォールバックに足す。無ければ英語表示。</summary>
    public static void EnsureJapaneseFont()
    {
        if (s_FontProbed) return;
        s_FontProbed = true;
        try
        {
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
                var fallbacks = TMP_Settings.fallbackFontAssets ?? new List<TMP_FontAsset>();
                fallbacks.Add(fa);
                TMP_Settings.fallbackFontAssets = fallbacks;
                Japanese = true;
                Debug.Log("[Ui] japanese font: " + name);
                return;
            }
            Debug.Log("[Ui] no japanese OS font; english text");
        }
        catch (Exception e) { Debug.LogWarning("[Ui] font probe failed: " + e.Message); }
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
        var le = go.GetComponent<LayoutElement>(); le.preferredHeight = height > 0f ? height : size + 8f; le.flexibleHeight = 0f;
        if (width > 0f) { le.preferredWidth = width; le.flexibleWidth = 0f; }
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.fontSize = size; tmp.color = color ?? Text; tmp.enableWordWrapping = wrap;
        tmp.overflowMode = TextOverflowModes.Truncate; tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return tmp;
    }

    public static TMP_InputField Input(Transform parent, string placeholder, float height = 26f, bool multiline = false)
    {
        GameObject go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.layoutPriority = 2; le.preferredHeight = height; le.flexibleHeight = 0f;   // TMP_InputField 自身の申告より優先
        var f = go.GetComponent<TMP_InputField>();
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
        var tmp = tgo.AddComponent<TextMeshProUGUI>(); tmp.text = label; tmp.fontSize = 13f; tmp.color = Header; tmp.alignment = TextAlignmentOptions.Center; tmp.overflowMode = TextOverflowModes.Truncate;
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
