// =================================================================
// [스크립트 목적]  Addressables 그룹을 스캔해 키-경로 맵(addressableMap.json) 자동 생성
//                 + 빌드 + ServerData 업로드 목록 추출
//                 (기존 프로젝트 AddressableUtils 개선판)
// [메뉴]           Tools > Framework > Addressable > Mapping / Build
// [의존 관계]      - Addressables Editor, EAddressableGroup, AddressableMapData
// [개선]           Application.dataPath 문자열 조작 → AssetDatabase API (경로 버그 차단)
//                  확장자 판별 if 체인 → switch (sprite/audio 추가)
//                  Prewarm 플래그 매핑 단계 부여
// =================================================================
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

public static class AddressableMapBuilder
{
    private const string MAP_FILE_NAME = "addressableMap.json";

    [MenuItem("Tools/Framework/Addressable/Mapping")]
    public static void Mapping()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[AddressableMapBuilder] Addressable Settings 없음");
            return;
        }

        int mapped = 0;
        foreach (var group in settings.groups)
        {
            if (group == null) continue;

            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema != null && !schema.IncludeInBuild)
                continue;

            // 그룹명 → enum 파싱 (불일치 시 스킵)
            if (!Enum.TryParse(group.Name, out EAddressableGroup groupType))
            {
                Debug.LogWarning($"[AddressableMapBuilder] 그룹 '{group.Name}'은 EAddressableGroup에 없음 (스킵)");
                continue;
            }

            foreach (var entry in group.entries)
            {
                if (string.IsNullOrEmpty(entry.AssetPath)) continue;
                if (entry.AssetPath.Contains(MAP_FILE_NAME)) continue;
                if (!AssetDatabase.IsValidFolder(entry.AssetPath)) continue; // 폴더 엔트리만 처리

                // 폴더를 재귀 스캔해 키-경로 매핑 json 생성.
                // 폴더 엔트리는 그대로 유지 → 원하는 대로 자유롭게 폴더 정리 가능.
                // 로드 시 ResourceManager가 키 테이블(json)로 "키→실제경로" 변환하므로
                // Addressables 키가 경로여도 코드는 단순 키("UITitle")만 쓰면 됨.
                var mapData = ScanFolder(entry.AssetPath, groupType);
                string jsonPath = $"{entry.AssetPath}/{MAP_FILE_NAME}";
                string json = JsonUtility.ToJson(mapData, true)
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Replace("\n", "\r\n");
                File.WriteAllText(jsonPath, json, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(jsonPath);
                mapped += mapData.Entries.Count;
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AddressableMapBuilder] 매핑 완료: {mapped}개 키 (폴더 구조 유지, 키 테이블 생성)");
    }

    // Preload 정책 판정용 폴더 이름 (이 폴더 하위는 미리 로드 대상)
    private const string PRELOAD_FOLDER = "Preload";

    // 폴더 재귀 스캔 (AssetDatabase 경로 기반 — dataPath 문자열 조작 제거)
    private static AddressableMapData ScanFolder(string folderPath, EAddressableGroup group)
    {
        var mapData = new AddressableMapData();

        // 하위 폴더 재귀. Sprite 그룹은 Sprite/Atlas와 Sprite/Sprite를 각각 다른 정책으로 스캔한다.
        foreach (var sub in AssetDatabase.GetSubFolders(folderPath))
        {
            mapData.AddRange(ScanFolder(sub, group).Entries);
        }

        // 경로 어딘가에 "Preload" 폴더가 있으면 미리 로드 대상
        bool isPreload = IsUnderPreloadFolder(folderPath);

        // 현재 폴더 직속 에셋 (.meta/.json 제외)
        var guids = AssetDatabase.FindAssets("", new[] { folderPath });
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetDirectoryName(path).Replace("\\", "/") != folderPath) continue; // 직속만
            if (path.EndsWith(MAP_FILE_NAME)) continue;
            if (AssetDatabase.IsValidFolder(path)) continue;
            if (ShouldSkipAsset(path, group)) continue;

            mapData.Add(new AddressableMapEntry
            {
                Group = group,
                Kind = DetectKind(path),
                Key = Path.GetFileNameWithoutExtension(path),
                Path = path,
                Prewarm = isPreload, // Preload 폴더 안이면 미리 로드 대상
            });
        }

        return mapData;
    }

    private static bool ShouldSkipAsset(string path, EAddressableGroup group)
    {
        if (group != EAddressableGroup.Sprite) return false;

        string normalized = path.Replace("\\", "/");
        bool underAtlas = normalized.Contains("/Sprite/Atlas/", StringComparison.OrdinalIgnoreCase);
        bool underSprite = normalized.Contains("/Sprite/Sprite/", StringComparison.OrdinalIgnoreCase);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (underAtlas)
            return ext != ".spriteatlas" && ext != ".spriteatlasv2";

        if (underSprite)
            return ext is not ".png" and not ".jpg" and not ".jpeg";

        return false;
    }

    // 경로에 "Preload" 폴더가 포함되는지 (대소문자 무시)
    private static bool IsUnderPreloadFolder(string folderPath)
    {
        var parts = folderPath.Split('/');
        foreach (var p in parts)
        {
            if (p.Equals(PRELOAD_FOLDER, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // 확장자 → 에셋 종류 (if 체인 → 명시적 switch)
    private static EAssetKind DetectKind(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" => EAssetKind.Sprite,
            ".spriteatlas" or ".spriteatlasv2" => EAssetKind.SpriteAtlas,
            ".json" => EAssetKind.JsonData,
            ".csv" => EAssetKind.Csv,
            ".asset" => EAssetKind.ScriptableObject,
            ".wav" or ".mp3" or ".ogg" => EAssetKind.AudioClip,
            _ => EAssetKind.Prefab,
        };
    }

    [MenuItem("Tools/Framework/Addressable/Build")]
    public static void BuildAddressable()
    {
        Mapping(); // 빌드 전 매핑 갱신

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        settings.OverridePlayerVersion = Application.version;

        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

        if (!string.IsNullOrEmpty(result.Error))
        {
            Debug.LogError($"[AddressableMapBuilder] 빌드 실패: {result.Error}");
            return;
        }

        // ServerData(원격 업로드 대상) 파일 목록 추출
        var uploadList = result.FileRegistry.GetFilePaths()
            .Where(p => p.Contains("ServerData"))
            .ToList();

        string outPath = Path.Combine(Application.dataPath, "lastBuildData.txt");
        File.WriteAllText(outPath, string.Join("\n", uploadList));

        Debug.Log($"[AddressableMapBuilder] 빌드 완료. 업로드 대상 {uploadList.Count}개 → {outPath}");
    }
}
#endif
