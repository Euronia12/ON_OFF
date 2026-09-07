// =================================================================
// [스크립트 목적]  다국어 구글 시트 → CSV 다운로드 → Data 폴더에 그대로 저장 (파싱 X).
//                 저장된 StringChart.csv는 DataManager가 런타임 로드, LocalizationManager가 캐싱.
// [의존 관계]      - DataManager(GameData 라벨로 CSV 로드), StringChart, LocalizationManager
// [메뉴]           Tools > Framework > Localization CSV Exporter
// [주의]           UNITY_EDITOR 전용. 시트는 "링크가 있는 모든 사용자 보기" 공개 설정 필요.
//                  저장 후 Addressables에서 해당 CSV를 GameData 라벨 그룹에 포함시킬 것.
// =================================================================
#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public class LocalizationCsvExporter : EditorWindow
{
    // [추정 구현] 실제 프로젝트 시트 ID/경로로 교체 필요
    private string _spreadsheetId = "1vOq3V3uTKeOy44BjB5yZsthc4IdjleRvw6rWhaO8TUc";
    private string _gid = "0";

    // 파일명 = 클래스명 규칙 (DataManager가 이 이름으로 StringChart 타입 매칭)
    private string _fileName = "StringChart";

    // GameData 라벨로 묶이는 Data 폴더. DataManager._dataLabel 기본값과 정합.
    private string _saveFolder = "Assets/00_Addressable/Data/CSV";

    private string _log = "";
    private Vector2 _scroll;
    private bool _isExporting;
    private CancellationTokenSource _cts;

    [MenuItem("Tools/Framework/Localization CSV Exporter")]
    private static void Open()
    {
        var window = GetWindow<LocalizationCsvExporter>("Localization CSV");
        window.minSize = new Vector2(440, 380);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("다국어 CSV 익스포터", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "구글 시트(Key/KOR/ENG/JPN/CHS/CHT)를 CSV로 받아 Data 폴더에 저장합니다.\n" +
            "저장된 CSV를 Addressables의 GameData 그룹에 포함시키면\n" +
            "DataManager가 자동 로드하고 LocalizationManager가 캐싱합니다.",
            MessageType.Info);

        EditorGUILayout.Space();

        _spreadsheetId = EditorGUILayout.TextField("스프레드시트 ID", _spreadsheetId);
        _gid = EditorGUILayout.TextField("시트 GID", _gid);
        _fileName = EditorGUILayout.TextField("파일명(=클래스명)", _fileName);

        using (new EditorGUILayout.HorizontalScope())
        {
            _saveFolder = EditorGUILayout.TextField("저장 폴더", _saveFolder);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string picked = EditorUtility.OpenFolderPanel("저장 폴더 선택", Application.dataPath, "");
                if (!string.IsNullOrEmpty(picked))
                    _saveFolder = ToProjectRelativePath(picked);
            }
        }

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(_isExporting))
        {
            if (GUILayout.Button("CSV 다운로드 & 저장", GUILayout.Height(34)))
                Export().Forget();
        }

        if (_isExporting && GUILayout.Button("취소"))
            CancelExport();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("로그", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(140));
        EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("로그 지우기"))
            _log = "";
    }

    private async UniTask Export()
    {
        if (string.IsNullOrEmpty(_spreadsheetId)) { AppendLog("!!! 스프레드시트 ID가 비어있습니다."); return; }
        if (string.IsNullOrEmpty(_fileName)) { AppendLog("!!! 파일명이 비어있습니다."); return; }

        _isExporting = true;
        _cts = new CancellationTokenSource();
        AppendLog("=== CSV 다운로드 시작 ===");

        try
        {
            string url = BuildCsvUrl(_spreadsheetId, _gid);
            string csv = await DownloadCsv(url, _cts.Token);

            if (string.IsNullOrEmpty(csv))
            {
                AppendLog("!!! 다운로드 실패 (시트 공개 설정/ID/GID 확인)");
                return;
            }

            // 헤더 검증: 첫 컬럼이 Id인지 가볍게 확인 (DataManager._idKey 기본값 'Id'와 정합)
            if (!csv.TrimStart().StartsWith("Id", StringComparison.OrdinalIgnoreCase))
                AppendLog("[경고] 첫 컬럼이 'Id'가 아닙니다. 헤더가 Id/KOR/ENG/... 인지 확인하세요.");

            EnsureFolder(_saveFolder);

            string assetPath = $"{_saveFolder}/{_fileName}.csv";
            string fullPath = Path.GetFullPath(assetPath);

            // UTF-8(BOM 없음)로 저장 — 한·중·일 텍스트 깨짐 방지
            File.WriteAllText(fullPath, csv, new System.Text.UTF8Encoding(false));

            AssetDatabase.ImportAsset(assetPath);
            AssetDatabase.Refresh();

            var saved = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
            if (saved != null) EditorGUIUtility.PingObject(saved);

            AppendLog($"=== 저장 완료: {assetPath} ({csv.Length:N0} chars) ===");
            AppendLog("→ 이 CSV를 Addressables 'GameData' 그룹에 포함시키세요.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("!!! 취소됨");
        }
        catch (Exception e)
        {
            AppendLog($"!!! 오류: {e.Message}");
        }
        finally
        {
            _isExporting = false;
            _cts?.Dispose();
            _cts = null;
            Repaint();
        }
    }

    private static string BuildCsvUrl(string spreadsheetId, string gid)
        => $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={gid}";

    private static async UniTask<string> DownloadCsv(string url, CancellationToken token)
    {
        using var request = UnityWebRequest.Get(url);
        await request.SendWebRequest().WithCancellation(token);

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[LocalizationCsvExporter] 다운로드 오류: {request.error}");
            return null;
        }
        return request.downloadHandler.text;
    }

    private void CancelExport()
    {
        _cts?.Cancel();
        AppendLog("취소 요청...");
    }

    // 절대 경로 → "Assets/..." 프로젝트 상대 경로 변환
    private static string ToProjectRelativePath(string absolutePath)
    {
        absolutePath = absolutePath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (absolutePath.StartsWith(dataPath))
            return "Assets" + absolutePath.Substring(dataPath.Length);
        return absolutePath; // 프로젝트 밖이면 그대로 (저장은 실패할 수 있음)
    }

    private static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath)) return;

        var parts = folderPath.Split('/');
        string current = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private void AppendLog(string message)
    {
        _log += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _scroll.y = float.MaxValue;
        Repaint();
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
#endif