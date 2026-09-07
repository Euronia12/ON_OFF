// =================================================================
// [스크립트 목적]  구글 시트 CSV 다운로드 → 파싱 → ScriptableObject 생성/갱신
// [주요 변수]      - 리플렉션 자동 매핑 + ISheetImportable 폴백
// [의존 관계]      - CsvParser, GoogleSheetImportSettings, ISheetImportable
// [주의]           UNITY_EDITOR 전용. 기존 에셋은 GUID 유지 위해 SetDirty로 갱신
// =================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public static class SheetImporter
{
    private const BindingFlags FieldFlags =
        BindingFlags.Public | BindingFlags.Instance;

    /// <summary>단일 엔트리 임포트. 성공 시 생성/갱신된 에셋 수 반환.</summary>
    public static async UniTask<int> ImportAsync(
        GoogleSheetImportSettings.ImportEntry entry,
        string arraySeparator,
        CancellationToken token = default)
    {
        if (entry == null || string.IsNullOrEmpty(entry.SpreadsheetId))
        {
            Debug.LogError("[SheetImporter] 잘못된 엔트리");
            return 0;
        }

        // 1. CSV 다운로드
        string url = BuildCsvUrl(entry.SpreadsheetId, entry.Gid);
        string csv = await DownloadCsv(url, token);
        if (string.IsNullOrEmpty(csv))
        {
            Debug.LogError($"[SheetImporter] CSV 다운로드 실패: {entry.DisplayName}");
            return 0;
        }

        // 2. 파싱
        var rows = CsvParser.Parse(csv);
        if (rows.Count < 2)
        {
            Debug.LogError($"[SheetImporter] 데이터 없음(헤더+1행 이상 필요): {entry.DisplayName}");
            return 0;
        }

        // 3. 타입 확인
        var targetType = FindType(entry.TargetTypeName);
        if (targetType == null || !typeof(ScriptableObject).IsAssignableFrom(targetType))
        {
            Debug.LogError($"[SheetImporter] 타입 못 찾음 또는 SO 아님: {entry.TargetTypeName}");
            return 0;
        }

        // 4. 헤더 → 컬럼 인덱스
        var header = rows[0];
        int idColIndex = header.IndexOf(entry.IdColumn);
        if (idColIndex < 0)
        {
            Debug.LogError($"[SheetImporter] ID 컬럼 없음: {entry.IdColumn}");
            return 0;
        }

        EnsureFolder(entry.OutputFolder);

        // 5. 행별 SO 생성/갱신
        int count = 0;
        for (int r = 1; r < rows.Count; r++)
        {
            token.ThrowIfCancellationRequested();

            var row = rows[r];
            if (row.Count <= idColIndex) continue;

            string id = row[idColIndex];
            if (string.IsNullOrWhiteSpace(id)) continue;

            string assetPath = Path.Combine(entry.OutputFolder, $"{id}.asset").Replace('\\', '/');

            // 기존 에셋 로드 (GUID 유지) 또는 신규 생성
            var so = AssetDatabase.LoadAssetAtPath(assetPath, targetType) as ScriptableObject;
            bool isNew = so == null;
            if (isNew)
            {
                so = ScriptableObject.CreateInstance(targetType);
                so.name = id;
            }

            // 행 → 딕셔너리
            var rowDict = BuildRowDict(header, row);

            // ISheetImportable 우선, 아니면 리플렉션 자동 매핑
            if (so is ISheetImportable importable)
                importable.ImportFromRow(rowDict);
            else
                MapByReflection(so, targetType, header, row, arraySeparator);

            if (isNew)
                AssetDatabase.CreateAsset(so, assetPath);
            else
                EditorUtility.SetDirty(so);

            count++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[SheetImporter] {entry.DisplayName}: {count}개 처리 완료");
        return count;
    }

    // ─────────────────────────────────────────────
    // CSV 다운로드
    // ─────────────────────────────────────────────

    private static string BuildCsvUrl(string spreadsheetId, string gid)
        => $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={gid}";

    private static async UniTask<string> DownloadCsv(string url, CancellationToken token)
    {
        using var request = UnityWebRequest.Get(url);
        await request.SendWebRequest().WithCancellation(token);

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[SheetImporter] 다운로드 오류: {request.error}");
            return null;
        }
        return request.downloadHandler.text;
    }

    // ─────────────────────────────────────────────
    // 매핑
    // ─────────────────────────────────────────────

    private static Dictionary<string, string> BuildRowDict(List<string> header, List<string> row)
    {
        var dict = new Dictionary<string, string>(header.Count);
        for (int c = 0; c < header.Count && c < row.Count; c++)
            dict[header[c]] = row[c];
        return dict;
    }

    /// <summary>헤더 컬럼명과 같은 이름의 public 필드를 찾아 자동 변환·할당.</summary>
    private static void MapByReflection(ScriptableObject so, Type type,
        List<string> header, List<string> row, string arraySeparator)
    {
        for (int c = 0; c < header.Count && c < row.Count; c++)
        {
            string fieldName = header[c];
            var field = type.GetField(fieldName, FieldFlags);
            if (field == null) continue;

            string raw = row[c];
            try
            {
                object value = ConvertValue(raw, field.FieldType, arraySeparator);
                if (value != null || !field.FieldType.IsValueType)
                    field.SetValue(so, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SheetImporter] 변환 실패 {type.Name}.{fieldName}='{raw}': {e.Message}");
            }
        }
    }

    private static object ConvertValue(string raw, Type targetType, string arraySeparator)
    {
        // 배열
        if (targetType.IsArray)
        {
            var elementType = targetType.GetElementType();
            string[] parts = string.IsNullOrEmpty(raw)
                ? Array.Empty<string>()
                : raw.Split(new[] { arraySeparator }, StringSplitOptions.None);

            var array = Array.CreateInstance(elementType, parts.Length);
            for (int i = 0; i < parts.Length; i++)
                array.SetValue(ConvertScalar(parts[i].Trim(), elementType), i);
            return array;
        }

        return ConvertScalar(raw, targetType);
    }

    private static object ConvertScalar(string raw, Type type)
    {
        if (type == typeof(string)) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return GetDefault(type);

        if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return ParseBool(raw);
        if (type.IsEnum) return Enum.Parse(type, raw, ignoreCase: true);

        Debug.LogWarning($"[SheetImporter] 미지원 타입: {type.Name} (값='{raw}'). 자동매핑 생략");
        return GetDefault(type);
    }

    private static bool ParseBool(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        return raw == "1" || raw == "true" || raw == "y" || raw == "yes" || raw == "o";
    }

    private static object GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    // ─────────────────────────────────────────────
    // 유틸
    // ─────────────────────────────────────────────

    private static Type FindType(string typeName)
    {
        var type = Type.GetType(typeName);
        if (type != null) return type;

        // 어셈블리 전체 탐색 (네임스페이스 없는 타입 대응)
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName);
            if (type != null) return type;

            // 짧은 이름으로도 탐색
            foreach (var t in assembly.GetTypes())
            {
                if (t.Name == typeName)
                    return t;
            }
        }
        return null;
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