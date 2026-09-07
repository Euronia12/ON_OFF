// =================================================================
// [스크립트 목적]  구글 시트 → CSV 다운로드 → OutputFolder에 그대로 저장 (파싱 X).
//                 저장된 CSV는 DataManager가 GameData 라벨로 런타임 로드·파싱한다.
// [의존 관계]      - GoogleSheetImportSettings.ImportEntry, DataManager(런타임 소비)
// [주의]           UNITY_EDITOR 전용. 시트는 "링크가 있는 모든 사용자 보기" 공개 필요.
//                  파일명 = 클래스명 규칙(DataManager 타입 매칭). UTF-8(BOM 없음) 저장.
// =================================================================
#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public static class SheetCsvExporter
{
    /// <summary>단일 엔트리 CSV 저장. 성공 시 1, 실패/스킵 시 0 반환.</summary>
    public static async UniTask<int> ExportAsync(
        GoogleSheetImportSettings.ImportEntry entry,
        CancellationToken token = default)
    {
        if (entry == null || string.IsNullOrEmpty(entry.SpreadsheetId))
        {
            Debug.LogError("[SheetCsvExporter] 잘못된 엔트리 (SpreadsheetId 비어있음)");
            return 0;
        }

        // 파일명 결정: OutputFileName 우선, 없으면 TargetTypeName 폴백
        string fileName = !string.IsNullOrEmpty(entry.OutputFileName)
            ? entry.OutputFileName
            : entry.TargetTypeName;

        if (string.IsNullOrEmpty(fileName))
        {
            Debug.LogError($"[SheetCsvExporter] 파일명 없음: {entry.DisplayName} (OutputFileName 또는 TargetTypeName 필요)");
            return 0;
        }

        // 1. CSV 다운로드
        string url = BuildCsvUrl(entry.SpreadsheetId, entry.Gid);
        string csv = await DownloadCsv(url, token);
        if (string.IsNullOrEmpty(csv))
        {
            Debug.LogError($"[SheetCsvExporter] CSV 다운로드 실패: {entry.DisplayName}");
            return 0;
        }

        // 2. 폴더 보장 + 저장
        EnsureFolder(entry.OutputFolder);

        string assetPath = $"{entry.OutputFolder}/{fileName}.csv";
        string fullPath = Path.GetFullPath(assetPath);

        // UTF-8(BOM 없음) — 한·중·일 텍스트 깨짐 방지
        File.WriteAllText(fullPath, csv, new System.Text.UTF8Encoding(false));

        AssetDatabase.ImportAsset(assetPath);
        AssetDatabase.Refresh();

        Debug.Log($"[SheetCsvExporter] 저장 완료: {assetPath} ({csv.Length:N0} chars)");
        return 1;
    }

    private static string BuildCsvUrl(string spreadsheetId, string gid)
        => $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={gid}";

    private static async UniTask<string> DownloadCsv(string url, CancellationToken token)
    {
        using var request = UnityWebRequest.Get(url);
        await request.SendWebRequest().WithCancellation(token);

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[SheetCsvExporter] 다운로드 오류: {request.error}");
            return null;
        }
        return request.downloadHandler.text;
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
}
#endif