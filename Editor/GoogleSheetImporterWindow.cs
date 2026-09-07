// =================================================================
// [스크립트 목적]  구글 시트 임포트 에디터 윈도우. 전체/개별 동기화 + 로그 + 취소
// [의존 관계]      - SheetImporter, GoogleSheetImportSettings
// [메뉴]           Tools > Framework > Google Sheet Importer
// =================================================================
#if UNITY_EDITOR
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class GoogleSheetImporterWindow : EditorWindow
{
    private GoogleSheetImportSettings _settings;
    private Vector2 _scroll;
    private string _log = "";
    private bool _isImporting;
    private CancellationTokenSource _cts;

    [MenuItem("Tools/Framework/Google Sheet Importer")]
    private static void Open()
    {
        var window = GetWindow<GoogleSheetImporterWindow>("Sheet Importer");
        window.minSize = new Vector2(420, 400);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("구글 시트 임포터", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        _settings = (GoogleSheetImportSettings)EditorGUILayout.ObjectField(
            "Import Settings", _settings, typeof(GoogleSheetImportSettings), false);

        if (_settings == null)
        {
            EditorGUILayout.HelpBox(
                "GoogleSheetImportSettings 에셋을 할당하세요.\n" +
                "(Create > Framework > Google Sheet Import Settings)",
                MessageType.Info);
            return;
        }

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(_isImporting))
        {
            if (GUILayout.Button("전체 동기화", GUILayout.Height(30)))
                ImportAll().Forget();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("개별 동기화", EditorStyles.boldLabel);

        if (_settings.ImportEntries != null)
        {
            for (int i = 0; i < _settings.ImportEntries.Count; i++)
            {
                var entry = _settings.ImportEntries[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = string.IsNullOrEmpty(entry.DisplayName)
                        ? $"Entry {i}" : entry.DisplayName;
                    EditorGUILayout.LabelField($"{label}  [{entry.Mode}]", GUILayout.Width(200));

                    // 탭 드롭다운: 등록된 SheetTabs 중 선택 시 entry.Gid 자동 채움
                    DrawTabDropdown(entry);

                    using (new EditorGUI.DisabledScope(_isImporting))
                    {
                        if (GUILayout.Button("동기화", GUILayout.Width(80)))
                            ImportSingle(entry).Forget();
                    }
                }
            }
        }

        EditorGUILayout.Space();

        if (_isImporting)
        {
            EditorGUILayout.LabelField("임포트 중...", EditorStyles.boldLabel);
            if (GUILayout.Button("취소"))
                CancelImport();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("로그", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(150));
        EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("로그 지우기"))
            _log = "";
    }

    /// <summary>
    /// 등록된 SheetTabs 를 드롭다운으로 표시. 선택 시 해당 탭의 Gid 를 entry.Gid 에 자동 복사.
    /// 임포터 본체는 여전히 entry.Gid 만 읽으므로 임포트 로직은 변경 없음(방식 A).
    /// </summary>
    private void DrawTabDropdown(GoogleSheetImportSettings.ImportEntry entry)
    {
        var tabs = _settings.SheetTabs;
        if (tabs == null || tabs.Count == 0)
        {
            // 등록된 탭 없으면 드롭다운 생략 (기존처럼 Gid 직접 입력)
            EditorGUILayout.LabelField($"gid:{entry.Gid}", GUILayout.Width(90));
            return;
        }

        // 드롭다운 옵션: "직접 입력" + 등록된 탭 이름들
        string[] options = new string[tabs.Count + 1];
        options[0] = "(직접 입력)";
        for (int t = 0; t < tabs.Count; t++)
        {
            string name = string.IsNullOrEmpty(tabs[t].Name) ? $"Tab {t}" : tabs[t].Name;
            options[t + 1] = $"{name} ({tabs[t].Gid})";
        }

        // 현재 선택값을 팝업 인덱스로 변환 (-1 → 0번 "직접 입력")
        int popupIndex = entry.SelectedTabIndex >= 0 && entry.SelectedTabIndex < tabs.Count
            ? entry.SelectedTabIndex + 1
            : 0;

        int newPopup = EditorGUILayout.Popup(popupIndex, options, GUILayout.Width(140));
        if (newPopup != popupIndex)
        {
            if (newPopup == 0)
            {
                // 직접 입력 모드로 전환 (Gid 는 유지)
                entry.SelectedTabIndex = -1;
            }
            else
            {
                // 탭 선택 → 해당 gid 를 entry.Gid 에 복사
                int tabIdx = newPopup - 1;
                entry.SelectedTabIndex = tabIdx;
                entry.Gid = tabs[tabIdx].Gid;
            }
            EditorUtility.SetDirty(_settings); // 변경 영속화
        }
    }

    private async UniTask ImportAll()
    {
        if (_settings.ImportEntries == null || _settings.ImportEntries.Count == 0)
        {
            AppendLog("임포트할 엔트리 없음");
            return;
        }

        _isImporting = true;
        _cts = new CancellationTokenSource();
        AppendLog("=== 전체 동기화 시작 ===");

        try
        {
            int total = 0;
            foreach (var entry in _settings.ImportEntries)
            {
                AppendLog($"→ {entry.DisplayName} ({entry.Mode}) 처리 중...");
                int count = await ProcessEntry(entry, _cts.Token);
                total += count;
                AppendLog($"  {count}개 완료");
            }
            AppendLog($"=== 전체 완료: 총 {total}개 ===");
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
            _isImporting = false;
            _cts?.Dispose();
            _cts = null;
            Repaint();
        }
    }

    private async UniTask ImportSingle(GoogleSheetImportSettings.ImportEntry entry)
    {
        _isImporting = true;
        _cts = new CancellationTokenSource();
        AppendLog($"=== {entry.DisplayName} 동기화 ===");

        try
        {
            int count = await ProcessEntry(entry, _cts.Token);
            AppendLog($"완료: {count}개");
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
            _isImporting = false;
            _cts?.Dispose();
            _cts = null;
            Repaint();
        }
    }

    // 엔트리 모드에 따라 분기: CreateSO → SheetImporter / SaveCsv → SheetCsvExporter
    private async UniTask<int> ProcessEntry(
        GoogleSheetImportSettings.ImportEntry entry, CancellationToken token)
    {
        switch (entry.Mode)
        {
            case GoogleSheetImportSettings.EImportMode.SaveCsv:
                return await SheetCsvExporter.ExportAsync(entry, token);

            case GoogleSheetImportSettings.EImportMode.CreateSO:
            default:
                return await SheetImporter.ImportAsync(entry, _settings.ArraySeparator, token);
        }
    }

    private void CancelImport()
    {
        _cts?.Cancel();
        AppendLog("취소 요청...");
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