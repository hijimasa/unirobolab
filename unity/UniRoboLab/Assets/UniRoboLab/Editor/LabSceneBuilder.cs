using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// UniRoboLab のプレイヤー用シーンをコードから作る (シーン YAML の手編集を避ける)。
/// 中身: 注視カメラ、平行光、地面、SimulationControl (核) を載せた Simulation。
/// パネル (LabPanel / PolicyPanel) は RuntimeInitializeOnLoadMethod で自分から付く。
///   Unity -batchmode -nographics -quit -projectPath . -executeMethod LabSceneBuilder.Build
/// </summary>
public static class LabSceneBuilder
{
    public const string ScenePath = "Assets/UniRoboLab/Scenes/Lab.unity";

    [MenuItem("UniRoboLab/Rebuild Lab scene")]
    public static void Build()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        // ライトマップの自動生成を止める (バッチビルドで焼き込みが走り、何十分も掛かる)。
        var ls = new LightingSettings { autoGenerate = false, bakedGI = false, realtimeGI = false };
        AssetDatabase.CreateAsset(ls, "Assets/UniRoboLab/Scenes/LabLighting.lighting");
        Lightmapping.lightingSettings = ls;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.6f, 0.65f, 0.75f);
        RenderSettings.ambientEquatorColor = new Color(0.45f, 0.45f, 0.5f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.25f, 0.25f);

        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(LabCamera));
        cam.tag = "MainCamera";
        var c = cam.GetComponent<Camera>(); c.clearFlags = CameraClearFlags.Skybox; c.nearClipPlane = 0.02f;
        cam.GetComponent<LabCamera>().distance = 3f;

        var light = new GameObject("Directional Light", typeof(Light));
        var l = light.GetComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.0f; l.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(5f, 1f, 5f);   // 50 m x 50 m
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit != null)
        {
            // 1 m の市松模様: スケール感のため。テクスチャはコードで作る
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            tex.SetPixels(new[] { new Color(0.42f, 0.43f, 0.45f), new Color(0.36f, 0.37f, 0.39f), new Color(0.36f, 0.37f, 0.39f), new Color(0.42f, 0.43f, 0.45f) });
            tex.Apply();
            AssetDatabase.CreateAsset(tex, "Assets/UniRoboLab/Scenes/GroundChecker.asset");
            var mat = new Material(lit) { color = Color.white, mainTexture = tex, mainTextureScale = new Vector2(25f, 25f) };
            mat.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(mat, "Assets/UniRoboLab/Scenes/Ground.mat");
            ground.GetComponent<Renderer>().sharedMaterial = mat;
        }

        var sim = new GameObject("Simulation", typeof(SimulationControl));

        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[LabSceneBuilder] wrote " + ScenePath);
    }
}
