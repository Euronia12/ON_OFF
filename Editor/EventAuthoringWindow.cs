#if UNITY_EDITOR
// =================================================================
// [스크립트 목적]  Event 저작 전용 창. 프리팹을 생성하지 않고 EventDataSO 를 편집한다.
//                 좌측 목록에서 이벤트를 고르거나 새로 만들고, 우측에서 커스텀 인스펙터로 편집.
// [설계 노트]      - 편집 UI 는 EventDataSOEditor(커스텀 인스펙터)를 CreateEditor 로 재사용.
//                    데이터는 전부 SO 에 저장되며, 로컬라이제이션은 SO 에 넣은 키로 런타임 조회.
// =================================================================

using UnityEditor;
using UnityEngine;

public sealed class EventAuthoringWindow : EditorWindow
{
    private const string DataFolder = "Assets/00_Addressable/Data/SO/Event";

    private EventDataSO _target;
    private Editor _editor;
    private Vector2 _listScroll;
    private Vector2 _bodyScroll;

    [MenuItem("Tools/Framework/Event Authoring")]
    private static void Open()
    {
        EventAuthoringWindow window = GetWindow<EventAuthoringWindow>("Event Authoring");
        window.minSize = new Vector2(780f, 560f);
    }

    private void OnDisable()
    {
        if (_editor != null) DestroyImmediate(_editor);
        _editor = null;
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawList();
            DrawEditor();
        }
    }

    private void DrawList()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(230f)))
        {
            EditorGUILayout.LabelField("이벤트 목록", EditorStyles.boldLabel);
            if (GUILayout.Button("새 이벤트 생성", GUILayout.Height(28f)))
            {
                CreateNew();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.Space(4f);

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            string[] guids = AssetDatabase.FindAssets("t:EventDataSO");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EventDataSO data = AssetDatabase.LoadAssetAtPath<EventDataSO>(path);
                if (data == null) continue;

                bool selected = data == _target;
                string label = $"{(data.Enabled ? "" : "(off) ")}{data.name}  [{data.Type}]";
                GUIStyle style = selected ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(label, style)) Select(data);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawEditor()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            _target = (EventDataSO)EditorGUILayout.ObjectField("편집 대상", _target, typeof(EventDataSO), false);
            if (_target == null)
            {
                EditorGUILayout.HelpBox("좌측에서 이벤트를 고르거나 '새 이벤트 생성'을 누르세요.", MessageType.Info);
                return;
            }

            if (_editor == null || _editor.target != _target)
            {
                if (_editor != null) DestroyImmediate(_editor);
                _editor = Editor.CreateEditor(_target);
            }

            EditorGUILayout.Space(4f);
            _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);
            _editor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("에셋 선택(Ping)")) EditorGUIUtility.PingObject(_target);
                if (GUILayout.Button("저장")) { AssetDatabase.SaveAssets(); }
            }
        }
    }

    private void CreateNew()
    {
        string path = EditorUtility.SaveFilePanelInProject(
            "새 이벤트 데이터", "NewEvent", "asset", "저장 위치를 선택하세요.", DataFolder);
        if (string.IsNullOrEmpty(path)) return;

        EventDataSO data = CreateInstance<EventDataSO>();
        AssetDatabase.CreateAsset(data, path);

        // EventId 기본값을 파일명으로 채움(비어 있으면 후보 제외되므로 편의 세팅).
        SerializedObject so = new SerializedObject(data);
        so.FindProperty("_eventId").stringValue = System.IO.Path.GetFileNameWithoutExtension(path);
        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Select(data);
    }

    private void Select(EventDataSO data)
    {
        _target = data;
        if (_editor != null) DestroyImmediate(_editor);
        _editor = null;
        Repaint();
    }
}
#endif
