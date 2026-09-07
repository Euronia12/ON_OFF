// =================================================================
// BuildEnv별 Bootstrap 설정을 일관된 프로필로 적용하는 Editor 도구.
// 사용자 저장 데이터나 암호화 salt는 수정하지 않는다.
// =================================================================
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public sealed class BuildEnvironmentProfileTool : EditorWindow
{
    private const string BootstrapPrefabPath = "Assets/Resources/Bootstrap.prefab";
    private const string TestCommandsDefine = "ONOFF_TEST_COMMANDS";

    [MenuItem("Tools/Build Environment/프로필 설정")]
    private static void Open()
    {
        GetWindow<BuildEnvironmentProfileTool>("Build Environment");
    }

    [MenuItem("Tools/Build Environment/Dev 프로필 적용")]
    private static void ApplyDev() => ApplyProfile(EBuildEnv.Dev);

    [MenuItem("Tools/Build Environment/Test 프로필 적용")]
    private static void ApplyTest() => ApplyProfile(EBuildEnv.Test);

    [MenuItem("Tools/Build Environment/Live 프로필 적용")]
    private static void ApplyLive() => ApplyProfile(EBuildEnv.Live);

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Build Environment 프로필", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Bootstrap.prefab의 BuildEnv, 저장 암호화, 오류/분석 로그 설정을 함께 변경합니다.\n" +
            "사용자 저장 파일과 저장 암호화 salt는 변경하지 않습니다.",
            MessageType.Info);

        DrawProfileButton(EBuildEnv.Dev, "Dev", "테스트 커맨드 포함 · 평문 저장 · 로컬/디버그 로그 · 테스트 Addressable 서버");
        DrawProfileButton(EBuildEnv.Test, "Test", "테스트 커맨드 포함 · 암호화 저장 · 로컬 로그 · 테스트 Addressable 서버 · 외부 분석 전송");
        DrawProfileButton(EBuildEnv.Live, "Live", "테스트 커맨드 제외 · 암호화 저장 · 로컬/디버그 로그 차단 · 운영 Addressable 서버 · 외부 분석 전송");
    }

    private static void DrawProfileButton(EBuildEnv env, string label, string description)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        EditorGUILayout.LabelField(description, EditorStyles.wordWrappedLabel);
        if (GUILayout.Button($"{label} 프로필 적용"))
            ApplyProfile(env);
        EditorGUILayout.EndVertical();
    }

    private static void ApplyProfile(EBuildEnv env)
    {
        GameObject bootstrap = AssetDatabase.LoadAssetAtPath<GameObject>(BootstrapPrefabPath);
        if (bootstrap == null)
        {
            EditorUtility.DisplayDialog("프로필 적용 실패", $"{BootstrapPrefabPath}을 찾을 수 없습니다.", "확인");
            return;
        }

        GameManager gameManager = bootstrap.GetComponentInChildren<GameManager>(true);
        SaveManager saveManager = bootstrap.GetComponentInChildren<SaveManager>(true);
        AnalyticsManager analyticsManager = bootstrap.GetComponentInChildren<AnalyticsManager>(true);
        ErrorHandler errorHandler = bootstrap.GetComponentInChildren<ErrorHandler>(true);

        if (gameManager == null || saveManager == null || analyticsManager == null || errorHandler == null)
        {
            EditorUtility.DisplayDialog("프로필 적용 실패", "Bootstrap.prefab에 필요한 매니저가 모두 존재하는지 확인하세요.", "확인");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "Build Environment 프로필 적용",
                $"{env} 프로필로 Bootstrap 설정을 변경할까요?\n\n{GetSummary(env)}",
                "적용", "취소"))
            return;

        Undo.RegisterCompleteObjectUndo(bootstrap, $"Apply {env} Build Profile");

        ApplyGameManager(gameManager, env);
        ApplySaveManager(saveManager, env);
        ApplyAnalyticsManager(analyticsManager, env);
        ApplyErrorHandler(errorHandler, env);
        ApplyTestCommandDefine(env);
        AddressableWrapper.ConfigureForBuildEnvironment(env);

        EditorUtility.SetDirty(bootstrap);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[BuildEnvironment] {env} 프로필을 Bootstrap.prefab에 적용했습니다.");
    }

    /// <summary>
    /// Dev/Test에서는 테스트 입력 코드를 컴파일하고 Live에서는 컴파일 경로 자체를 제거한다.
    /// 현재 활성 빌드 타깃에 적용하며, 다른 타깃으로 전환 후 빌드하면 BuildGuard가 불일치를 차단한다.
    /// </summary>
    private static void ApplyTestCommandDefine(EBuildEnv env)
    {
        BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        NamedBuildTarget namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
        string[] defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget)
            .Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries);
        var values = new System.Collections.Generic.List<string>(defines);
        bool shouldInclude = env != EBuildEnv.Live;
        bool contains = values.Contains(TestCommandsDefine);

        if (shouldInclude && !contains)
            values.Add(TestCommandsDefine);
        else if (!shouldInclude && contains)
            values.RemoveAll(value => value == TestCommandsDefine);
        else
            return;

        PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", values));
    }

    private static void ApplyGameManager(GameManager manager, EBuildEnv env)
    {
        var serialized = new SerializedObject(manager);
        Set(serialized, "_buildEnv", (int)env);
        Set(serialized, "_saveOnPause", true);
        Set(serialized, "_saveOnQuit", true);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ApplySaveManager(SaveManager manager, EBuildEnv env)
    {
        var serialized = new SerializedObject(manager);
        // Dev는 세이브 확인이 쉽도록 평문, Test/Live는 출시와 같은 암호화 경로를 검증한다.
        Set(serialized, "_useEncryption", env != EBuildEnv.Dev);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ApplyAnalyticsManager(AnalyticsManager manager, EBuildEnv env)
    {
        var serialized = new SerializedObject(manager);
        bool isDev = env == EBuildEnv.Dev;
        bool isLive = env == EBuildEnv.Live;

        Set(serialized, "_addDebugProvider", isDev);
        Set(serialized, "_addDebugKpiProvider", isDev);
        Set(serialized, "_useGameAnalytics", !isDev);
        Set(serialized, "_useUgsAnalytics", !isDev);
        Set(serialized, "_sendFromEditor", false);
        Set(serialized, "_useLocalLog", !isLive);
        Set(serialized, "_useDataPath", false);
        Set(serialized, "_autoSave", !isLive);
        Set(serialized, "_autoSaveInterval", isDev ? 15f : 30f);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ApplyErrorHandler(ErrorHandler manager, EBuildEnv env)
    {
        var serialized = new SerializedObject(manager);
        Set(serialized, "_showOverlayInDev", env == EBuildEnv.Dev);
        Set(serialized, "_logErrorToFile", env != EBuildEnv.Live);
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Set(SerializedObject serialized, string propertyName, bool value)
        => serialized.FindProperty(propertyName).boolValue = value;

    private static void Set(SerializedObject serialized, string propertyName, float value)
        => serialized.FindProperty(propertyName).floatValue = value;

    private static void Set(SerializedObject serialized, string propertyName, int value)
        => serialized.FindProperty(propertyName).intValue = value;

    private static string GetSummary(EBuildEnv env)
    {
        return env switch
        {
            EBuildEnv.Dev => "평문 저장, 로컬/디버그 로그, 테스트 커맨드 포함, 테스트 Addressable 서버, 외부 분석 SDK 비활성",
            EBuildEnv.Test => "암호화 저장, 로컬 로그, 테스트 커맨드 포함, 테스트 Addressable 서버, 외부 분석 SDK 활성",
            EBuildEnv.Live => "암호화 저장, 테스트 커맨드 제외, 로컬/디버그 로그 비활성, 운영 Addressable 서버, 외부 분석 SDK 활성",
            _ => string.Empty
        };
    }
}
