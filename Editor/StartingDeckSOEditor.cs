// =================================================================
// [스크립트 목적]  StartingDeckSO 전용 인스펙터. 기본 리스트 인스펙터가 항목 다수일 때
//                  count 누락·추가/제거 먹통 되는 문제를 ReorderableList 로 회피한다.
// [표시]           카드 / 장수(x) / 강화 를 한 줄에 — 항목 수와 무관하게 안정적 편집.
//                  컬럼 헤더 정렬 + 총 종류/총 장수 요약 + 빈 카드 경고로 가독성 강화.
// =================================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

[CustomEditor(typeof(StartingDeckSO))]
public sealed class StartingDeckSOEditor : Editor
{
    private const float HandleInset = 14f;
    private const float Gap = 6f;
    private const float CountLabelW = 14f;
    private const float CountFieldW = 44f;
    private const float UpgradeLabelW = 34f;
    private const float UpgradeToggleW = 18f;
    private const float RightBlockW = CountLabelW + CountFieldW + Gap + UpgradeLabelW + UpgradeToggleW;

    private SerializedProperty _deckType;
    private SerializedProperty _entries;
    private ReorderableList _list;

    private void OnEnable()
    {
        _deckType = serializedObject.FindProperty("_deckType");
        _entries = serializedObject.FindProperty("_entries");

        _list = new ReorderableList(serializedObject, _entries, true, true, true, true)
        {
            elementHeight = EditorGUIUtility.singleLineHeight + 8f,
            headerHeight = EditorGUIUtility.singleLineHeight + 4f,
        };

        _list.drawHeaderCallback = DrawHeader;
        _list.drawElementCallback = DrawElement;
        _list.onAddCallback = OnAdd;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
        }
        EditorGUILayout.Space(6f);

        EditorGUILayout.PropertyField(_deckType);
        EditorGUILayout.Space(8f);

        DrawSummary();
        _list.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSummary()
    {
        int totalCards = 0;
        for (int i = 0; i < _entries.arraySize; i++)
        {
            totalCards += Mathf.Max(1, _entries.GetArrayElementAtIndex(i).FindPropertyRelative("count").intValue);
        }

        EditorGUILayout.LabelField("덱 구성", $"{_entries.arraySize}종 · 총 {totalCards}장", EditorStyles.boldLabel);
    }

    private void DrawHeader(Rect rect)
    {
        Rect content = new Rect(rect.x + HandleInset, rect.y + 2f, rect.width - HandleInset, EditorGUIUtility.singleLineHeight);
        GetColumns(content, out Rect cardRect, out _, out _, out Rect upLabelRect, out _);

        GUIStyle style = EditorStyles.miniBoldLabel;
        EditorGUI.LabelField(cardRect, "카드", style);

        Rect countHeader = new Rect(cardRect.xMax + Gap, content.y, CountLabelW + CountFieldW, content.height);
        EditorGUI.LabelField(countHeader, "장수", style);

        EditorGUI.LabelField(new Rect(upLabelRect.x, content.y, UpgradeLabelW + UpgradeToggleW, content.height), "강화", style);
    }

    private void DrawElement(Rect rect, int index, bool active, bool focused)
    {
        if (index < 0 || index >= _entries.arraySize) return;

        SerializedProperty element = _entries.GetArrayElementAtIndex(index);
        SerializedProperty card = element.FindPropertyRelative("card");
        SerializedProperty count = element.FindPropertyRelative("count");
        SerializedProperty upgraded = element.FindPropertyRelative("upgraded");

        float height = EditorGUIUtility.singleLineHeight;
        Rect content = new Rect(rect.x, rect.y + (rect.height - height) * 0.5f, rect.width, height);
        GetColumns(content, out Rect cardRect, out Rect countLabelRect, out Rect countFieldRect, out Rect upLabelRect, out Rect upToggleRect);

        bool isEmpty = card.objectReferenceValue == null;
        Color previousColor = GUI.color;
        if (isEmpty) GUI.color = new Color(1f, 0.55f, 0.55f);
        EditorGUI.PropertyField(cardRect, card, GUIContent.none);
        GUI.color = previousColor;

        EditorGUI.LabelField(countLabelRect, "x");
        int newCount = EditorGUI.IntField(countFieldRect, Mathf.Max(1, count.intValue));
        count.intValue = Mathf.Max(1, newCount);

        EditorGUI.LabelField(upLabelRect, "강화");
        EditorGUI.PropertyField(upToggleRect, upgraded, GUIContent.none);
    }

    private void OnAdd(ReorderableList list)
    {
        int index = _entries.arraySize;
        _entries.arraySize++;

        SerializedProperty element = _entries.GetArrayElementAtIndex(index);
        element.FindPropertyRelative("card").objectReferenceValue = null;
        element.FindPropertyRelative("count").intValue = 1;
        element.FindPropertyRelative("upgraded").boolValue = false;
    }

    private static void GetColumns(
        Rect content,
        out Rect card,
        out Rect countLabel,
        out Rect countField,
        out Rect upLabel,
        out Rect upToggle)
    {
        float cardWidth = Mathf.Max(80f, content.width - RightBlockW - Gap);
        float y = content.y;
        float height = content.height;

        card = new Rect(content.x, y, cardWidth, height);

        float countX = card.xMax + Gap;
        countLabel = new Rect(countX, y, CountLabelW, height);
        countField = new Rect(countX + CountLabelW, y, CountFieldW, height);

        float upgradeX = countField.xMax + Gap;
        upLabel = new Rect(upgradeX, y, UpgradeLabelW, height);
        upToggle = new Rect(upgradeX + UpgradeLabelW, y, UpgradeToggleW, height);
    }
}
#endif
