// =================================================================
// 빌드 직전 Bootstrap의 BuildEnv를 명시적으로 확인한다.
// 실수로 Dev/Test 설정의 빌드를 배포하는 일을 방지하기 위한 Editor 전용 가드.
// =================================================================
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class BuildEnvironmentBuildGuard : IPreprocessBuildWithReport
{
    private const string BootstrapPrefabPath = "Assets/Resources/Bootstrap.prefab";
    private const string TestCommandsDefine = "ONOFF_TEST_COMMANDS";

    // 다른 전처리보다 먼저 환경을 확인한다.
    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        GameObject bootstrap = AssetDatabase.LoadAssetAtPath<GameObject>(BootstrapPrefabPath);
        GameManager gameManager = bootstrap != null
            ? bootstrap.GetComponentInChildren<GameManager>(true)
            : null;

        if (gameManager == null)
        {
            throw new BuildFailedException(
                $"BuildEnv를 확인할 수 없습니다. '{BootstrapPrefabPath}'의 GameManager를 확인하세요.");
        }

        EBuildEnv buildEnv = gameManager.BuildEnv;
        ValidateTestCommandDefine(buildEnv, report.summary.platform);

        string policy = buildEnv switch
        {
            EBuildEnv.Dev => "개발용: 테스트 커맨드가 포함되고 모든 개발 로그가 활성화됩니다.",
            EBuildEnv.Test => "테스트용: 테스트 커맨드가 포함되고 활성화됩니다.",
            EBuildEnv.Live => "출시용: 테스트 커맨드 컴파일 경로가 제외됩니다.",
            _ => "정의되지 않은 환경입니다."
        };

        string message = $"현재 BuildEnv: {buildEnv}\n\n{policy}\n\n이 설정으로 빌드를 계속할까요?";

        // CI에서는 대화상자를 띄울 수 없으므로, 설정값을 로그로 남기고 계속 진행한다.
        if (Application.isBatchMode)
        {
            Debug.Log($"[BuildEnvironment] {message}");
            return;
        }

        if (!EditorUtility.DisplayDialog("Build Environment 확인", message, "빌드 계속", "취소"))
            throw new BuildFailedException($"사용자가 {buildEnv} BuildEnv 빌드를 취소했습니다.");
    }

    private static void ValidateTestCommandDefine(EBuildEnv buildEnv, BuildTarget buildTarget)
    {
        BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(buildTarget);
        NamedBuildTarget namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
        string defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
        bool contains = System.Array.Exists(
            defines.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries),
            value => value == TestCommandsDefine);
        bool expected = buildEnv != EBuildEnv.Live;

        if (contains != expected)
        {
            throw new BuildFailedException(
                $"BuildEnv={buildEnv}와 {TestCommandsDefine} 컴파일 심볼 설정이 일치하지 않습니다. " +
                "Tools/Build Environment에서 해당 프로필을 다시 적용하세요.");
        }
    }

}
