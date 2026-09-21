using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// URDF に &lt;material&gt; を書いていないロボットが全面マゼンタで表示される問題の手当て。
///
/// URDF-Importer は材質の指定が無い visual に材質を付けない (または URP のシェーダが見つからないと
/// null を返す) ので、レンダラが既定材質のまま残り、URP では「シェーダ無し」の色 (マゼンタ) になる。
/// 形は見えるので致命的ではないが、初見では「読み込みに失敗した」と誤解する。ここでは、材質が無い、
/// またはシェーダが使えないレンダラにだけ中立の灰色を割り当てる。色付きの URDF には触らない。
/// </summary>
public static class MeshMaterialFix
{
    static Material s_Neutral;
    static readonly HashSet<int> s_Done = new HashSet<int>();

    static Material Neutral()
    {
        if (s_Neutral != null) return s_Neutral;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) return null;
        var c = new Color(0.72f, 0.73f, 0.76f, 1f);
        s_Neutral = new Material(sh) { name = "UniRoboLabNeutral", color = c };
        if (s_Neutral.HasProperty("_BaseColor")) s_Neutral.SetColor("_BaseColor", c);
        if (s_Neutral.HasProperty("_Smoothness")) s_Neutral.SetFloat("_Smoothness", 0.25f);
        return s_Neutral;
    }

    static bool Broken(Material m) => m == null || m.shader == null || !m.shader.isSupported ||
                                      m.shader.name == "Hidden/InternalErrorShader";

    /// <summary>このエンティティ以下のレンダラを見て、表示できない材質だけ中立色に置き換える。</summary>
    public static void Apply(GameObject entity)
    {
        if (entity == null) return;
        Material neutral = Neutral();
        if (neutral == null) return;
        foreach (Renderer r in entity.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (!Broken(mats[i])) continue;
                mats[i] = neutral;
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    /// <summary>シーンのエンティティのうち、まだ見ていないものだけ手当てする (毎フレーム呼んでよい)。</summary>
    public static void ApplyToNewEntities(List<GameObject> entities)
    {
        if (entities == null) return;
        foreach (GameObject e in entities)
        {
            if (e == null) continue;
            if (!s_Done.Add(e.GetInstanceID())) continue;
            Apply(e);
        }
        if (s_Done.Count > 4096) s_Done.Clear();   // デスポーンを繰り返しても増え続けないように
    }
}
