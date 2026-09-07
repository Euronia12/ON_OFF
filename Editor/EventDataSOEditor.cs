#if UNITY_EDITOR
// =================================================================
// [스크립트 목적]  EventDataSO 커스텀 인스펙터. 프리팹을 생성하지 않고 SO 데이터를 직접 저작한다.
//                 SerializeReference 효과/조건에 타입 선택 UI 를 제공한다.
// [설계 노트]      - Choice 유형: 타이틀/설명/이미지/선택지(효과·조건). GridPuzzle: PrefabKey 만.
//                    로컬라이제이션은 SO 에 저장한 키로 런타임 LocalizationManager 가 조회.
// =================================================================

using System;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EventDataSO))]
public sealed class EventDataSOEditor : Editor
{
    // ★ EffectTypes 와 EffectLabels 는 인덱스 대응이다. 추가·삭제 시 반드시 같은 위치를 함께 고칠 것.
    private static readonly Type[] EffectTypes =
    {
        typeof(ChoiceGoldEffect), typeof(ChoiceHealEffect),
        typeof(ChoiceAddCardEffect), typeof(ChoiceRemoveCardEffect), typeof(ChoiceArrowEffect),
        typeof(ChoiceMaxHpEffect), typeof(ChoicePreserveEffect),
        typeof(ChoiceModifyMaxManaEffect), typeof(ChoiceModifyDrawEffect),
        typeof(ChoiceModifyMaxHpEffect), typeof(ChoiceCardCostEffect),
        typeof(ChoiceDuplicateCardEffect), typeof(ChoiceCardOfferEffect),
        typeof(ChoiceTransmuteCardEffect), typeof(ChoiceSkillCooldownEffect),
        typeof(ChoiceCardRewardEffect), typeof(ChoiceRandomCardTransformEffect),
        typeof(ChoiceBasicCardsTransformEffect)
    };

    private static readonly string[] EffectLabels =
    {
        "골드", "회복", "카드 추가", "카드 제거", "화살표 변경",
        "최대체력", "보존", "최대마나", "드로우",
        "최대체력 증감", "카드 코스트", "카드 복제", "카드 제시", "카드 변환", "스킬 쿨타임",
        "카드 보상", "무작위 카드 변이", "기본 카드 일괄 변이"
    };

    // 효과 추가 버튼을 한 줄에 몇 개까지 놓을지(가로로 넘치지 않도록 줄바꿈).
    private const int EffectButtonsPerRow = 5;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 긴 한국어 라벨이 인풋필드에 가려지지 않도록 라벨 폭 확대(그림 후 복원).
        float prevLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 230f;

        EditorGUILayout.LabelField("식별", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_eventId"), new GUIContent("Event Id"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_enabled"), new GUIContent("활성"));
        SerializedProperty typeProp = serializedObject.FindProperty("_type");
        EditorGUILayout.PropertyField(typeProp, new GUIContent("유형"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("등장 규칙", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_minStage"), new GUIContent("최소 스테이지(0=무제한)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_maxStage"), new GUIContent("최대 스테이지(0=무제한)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_weight"), new GUIContent("가중치"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("선행 조건 (2단 연쇄)", EditorStyles.boldLabel);
        SerializedProperty requiredIdProp = serializedObject.FindProperty("_requiredEventId");
        EditorGUILayout.PropertyField(requiredIdProp, new GUIContent("선행 이벤트 Id(비우면 조건 없음)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_requiredChoiceIndex"),
            new GUIContent("선행 선택지 번호(0-based, -1=무관)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_uniquePerRun"),
            new GUIContent("런당 1회만 등장"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("랜덤", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("_useSeededRandom"),
            new GUIContent("런 시드 사용"));

        if (!string.IsNullOrWhiteSpace(requiredIdProp.stringValue))
        {
            EditorGUILayout.HelpBox(
                "선택지 번호는 0-based 입니다. 아래 '선택지 1' 이 0 번입니다.\n" +
                "선행 이벤트의 선택지 순서를 바꾸면 연쇄가 조용히 끊기니 주의하세요.",
                MessageType.Info);

            if (serializedObject.FindProperty("_minStage").intValue <= 0)
            {
                EditorGUILayout.HelpBox(
                    "선행 조건이 있는데 최소 스테이지가 0(무제한)입니다. " +
                    "선행 이벤트와 같은 스테이지에 등장할 수 있으니 최소 스테이지를 지정하세요.",
                    MessageType.Warning);
            }
        }

        EditorGUILayout.Space();
        var type = (EEventType)typeProp.enumValueIndex;
        if (type == EEventType.GridPuzzle)
        {
            EditorGUILayout.LabelField("GridPuzzle", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_prefabKey"), new GUIContent("Prefab Key"));
            EditorGUILayout.HelpBox("GridPuzzle 은 지정한 프리팹(UIGridPuzzleEvent 변형)을 그대로 실행합니다.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.LabelField("Choice 데이터", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_titleKey"), new GUIContent("제목 키"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_descKey"), new GUIContent("설명 키"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_imageKey"), new GUIContent("이미지 키(선택·확장자 뺀 파일명)"));
            EditorGUILayout.HelpBox("번역 키는 StringChart.csv 에 입력한 키를 그대로 사용합니다(런타임 조회).", MessageType.None);
            DrawChoices(serializedObject.FindProperty("_choices"));
        }

        EditorGUIUtility.labelWidth = prevLabelWidth;
        serializedObject.ApplyModifiedProperties();
    }

    private static void DrawChoices(SerializedProperty choices)
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"선택지 ({choices.arraySize})", EditorStyles.boldLabel);
            if (GUILayout.Button("선택지 추가", GUILayout.Width(100f)))
            {
                int index = choices.arraySize++;
                SerializedProperty added = choices.GetArrayElementAtIndex(index);
                added.FindPropertyRelative("_effects").arraySize = 0;
                added.FindPropertyRelative("_costs").arraySize = 0;
            }
        }

        for (int i = 0; i < choices.arraySize; i++)
        {
            EditorGUILayout.Space(8f);
            SerializedProperty choice = choices.GetArrayElementAtIndex(i);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"선택지 {i + 1}", EditorStyles.boldLabel);
                    if (GUILayout.Button("삭제", GUILayout.Width(52f)))
                    {
                        choices.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
                EditorGUILayout.Space(2f);
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_textKey"), new GUIContent("버튼 라벨 키"));
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_descKey"), new GUIContent("설명 키(선택)"));
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_resultKey"), new GUIContent("결과 텍스트 키(선택)"));
                EditorGUILayout.Space(6f);
                DrawPolymorphicList(choice.FindPropertyRelative("_effects"), "효과", EffectTypes, EffectLabels);

                // 요구량(Cost): 충족 시 해금 + 선택 시 차감. 단순 리스트(폴리모픽 아님).
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("요구량 (충족 시 해금 · 선택 시 차감)", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_costs"), GUIContent.none, true);
                EditorGUILayout.Space(4f);
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_minDeckSize"),
                    new GUIContent("덱 최소 장수 게이트(0=무제한)"));
                EditorGUILayout.PropertyField(choice.FindPropertyRelative("_maxDeckSize"),
                    new GUIContent("덱 최대 장수 게이트(0=무제한)"));
                EditorGUILayout.Space(4f);
            }
        }
    }

    // SerializeReference 다형성 리스트 편집(타입 추가 버튼 + 요소 인스펙터 + 삭제).
    private static void DrawPolymorphicList(SerializedProperty list, string title, Type[] types, string[] labels)
    {
        EditorGUILayout.LabelField($"{title} ({list.arraySize})", EditorStyles.miniBoldLabel);
        for (int i = 0; i < list.arraySize; i++)
        {
            SerializedProperty element = list.GetArrayElementAtIndex(i);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string typeName = element.managedReferenceValue?.GetType().Name ?? "Missing";
                    EditorGUILayout.LabelField(typeName, EditorStyles.miniLabel);
                    if (GUILayout.Button("삭제", GUILayout.Width(52f)))
                    {
                        list.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
                EditorGUILayout.PropertyField(element, GUIContent.none, true);
            }
            EditorGUILayout.Space(2f);
        }

        // 버튼이 많아 한 줄에 다 넣으면 라벨이 뭉개진다 → EffectButtonsPerRow 개씩 줄바꿈.
        for (int row = 0; row < types.Length; row += EffectButtonsPerRow)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = row; i < row + EffectButtonsPerRow && i < types.Length; i++)
                {
                    if (GUILayout.Button($"+ {labels[i]}"))
                    {
                        int index = list.arraySize++;
                        list.GetArrayElementAtIndex(index).managedReferenceValue =
                            Activator.CreateInstance(types[i]);
                    }
                }
            }
        }
    }
}
#endif
