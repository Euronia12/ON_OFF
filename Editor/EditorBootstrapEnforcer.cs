// =================================================================
// [스크립트 목적]  에디터에서 어느 씬에서 Play를 눌러도 Bootstrap을 거치도록 강제
//                 매니저 초기화 누락으로 인한 NullReference 방지
// [의존 관계]      - BootstrapInitializer (RuntimeInitializeOnLoadMethod와 별개)
// [메뉴]           Tools > Framework > Toggle Play From Bootstrap
// [주의]           BootstrapInitializer는 RuntimeInitializeOnLoadMethod로도 동작하므로
//                  이 도구는 "첫 씬을 Bootstrap 씬으로 강제"하는 보조 수단.
//                  Bootstrap을 Prefab+RuntimeInit로 쓰면 이 도구 없이도 동작함.
// =================================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;


[InitializeOnLoad]
public static class EditorBootstrapEnforcer
{
    private const string MENU_PATH = "Tools/Framework/Play From Title Scene";
    private const string PREF_KEY = "Framework_PlayFromTitle";
    private const string BOOTSTRAP_SCENE_NAME = "Title";

    static EditorBootstrapEnforcer()
    {
        // 에디터 로드 시 설정 복원
        EditorApplication.delayCall += () =>
        {
            bool enabled = EditorPrefs.GetBool(PREF_KEY, false);
            Menu.SetChecked(MENU_PATH, enabled);
            ApplyPlayModeStartScene(enabled);
        };
    }

    [MenuItem(MENU_PATH)]
    private static void Toggle()
    {
        bool enabled = !EditorPrefs.GetBool(PREF_KEY, false);
        EditorPrefs.SetBool(PREF_KEY, enabled);
        Menu.SetChecked(MENU_PATH, enabled);
        ApplyPlayModeStartScene(enabled);

        Debug.Log($"[EditorBootstrapEnforcer] {BOOTSTRAP_SCENE_NAME} 씬에서 Play 시작: {(enabled ? "ON" : "OFF")}");
    }

    private static void ApplyPlayModeStartScene(bool enabled)
    {
        if (!enabled)
        {
            EditorSceneManager.playModeStartScene = null;
            return;
        }

        // Bootstrap 씬 검색
        string scenePath = FindBootstrapScenePath();
        if (string.IsNullOrEmpty(scenePath))
        {
            Debug.LogWarning(
                $"[EditorBootstrapEnforcer] '{BOOTSTRAP_SCENE_NAME}' 씬을 찾을 수 없습니다. " +
                "Build Settings에 Bootstrap 씬을 추가하세요.");
            return;
        }

        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
        EditorSceneManager.playModeStartScene = sceneAsset;
    }

    private static string FindBootstrapScenePath()
    {
        // Build Settings 등록 씬 우선
        foreach (var scene in EditorBuildSettings.scenes)
        {
            if (scene.path.Contains($"{BOOTSTRAP_SCENE_NAME}.unity"))
                return scene.path;
        }

        // 프로젝트 전체 검색
        string[] guids = AssetDatabase.FindAssets($"t:Scene {BOOTSTRAP_SCENE_NAME}");
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == BOOTSTRAP_SCENE_NAME)
                return path;
        }

        return null;
    }
}
#endif
