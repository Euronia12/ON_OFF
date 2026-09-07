// =================================================================
// [스크립트 목적]  CardDataSO 인스펙터에 효과/조건 SerializeReference 선택 UI와
//                  다국어 키의 한국어 미리보기를 표시한다.
// [의존 관계]      - CardDataSO / CardEffectBase / CardReactiveConditionBase / StringChart.csv
// [설계 노트]      - 런타임 SO 직렬화 필드는 건드리지 않고 에디터 표시만 확장한다.
// =================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(CardDataSO))]
public sealed class CardDataSOEditor : Editor
{
    private const string StringChartPath = "Assets/00_Addressable/Data/CSV/StringChart.csv";
    private const string IdColumn = "Id";
    private const string KoreanColumn = "KOR";
    private const string BaseEffectsProperty = "_baseEffects";
    private const string UpgradeEffectsProperty = "_upgradeEffects";
    private const string ConditionsProperty = "Conditions";

    private static Dictionary<string, string> s_koreanByKey;
    private static Type[] s_effectTypes;
    private static GUIContent[] s_effectTypeLabels;
    private static Type[] s_conditionTypes;
    private static GUIContent[] s_conditionTypeLabels;
    private static readonly Dictionary<string, int> s_addTypeIndexByPropertyPath = new Dictionary<string, int>();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
                continue;
            }

            if (iterator.propertyPath == BaseEffectsProperty || iterator.propertyPath == UpgradeEffectsProperty)
            {
                DrawManagedReferenceList(iterator, typeof(CardEffectBase));
            }
            else
            {
                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space(8f);
        DrawLocalizationPreview((CardDataSO)target);
    }

    private static void DrawManagedReferenceList(SerializedProperty list, Type baseType)
    {
        EditorGUILayout.Space(2f);
        EditorGUILayout.LabelField(list.displayName, EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            int removeIndex = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Element {i}", EditorStyles.boldLabel);
                        if (GUILayout.Button("삭제", GUILayout.Width(48f)))
                        {
                            removeIndex = i;
                        }
                    }

                    DrawManagedReferenceProperty(element, baseType);
                }
            }

            if (removeIndex >= 0)
            {
                list.DeleteArrayElementAtIndex(removeIndex);
            }

            Type selected = DrawAddTypePopup(list, baseType);
            string buttonLabel = GetAddButtonLabel(baseType);
            using (new EditorGUI.DisabledScope(selected == null))
            {
                if (GUILayout.Button(buttonLabel))
                {
                    int index = list.arraySize;
                    list.InsertArrayElementAtIndex(index);
                    SerializedProperty element = list.GetArrayElementAtIndex(index);
                    element.managedReferenceValue = Activator.CreateInstance(selected);
                    s_addTypeIndexByPropertyPath[list.propertyPath] = 0;
                }
            }
        }
    }

    private static string GetAddButtonLabel(Type baseType)
    {
        if (baseType == typeof(CardEffectBase)) return "Effect 추가";
        return "Condition 추가";
    }

    private static void DrawManagedReferenceProperty(SerializedProperty property, Type baseType)
    {
        Type currentType = property.managedReferenceValue != null
            ? property.managedReferenceValue.GetType()
            : null;

        Type selected = DrawTypePopup("Type", currentType, baseType);
        if (selected != currentType)
        {
            property.managedReferenceValue = selected != null
                ? Activator.CreateInstance(selected)
                : null;
            currentType = selected;
        }

        if (currentType == null)
        {
            EditorGUILayout.HelpBox("타입을 선택하세요.", MessageType.Info);
            return;
        }

        DrawManagedReferenceChildren(property);
    }

    private static void DrawManagedReferenceChildren(SerializedProperty property)
    {
        SerializedProperty child = property.Copy();
        SerializedProperty end = property.GetEndProperty();
        bool enterChildren = true;
        EditorGUI.indentLevel++;
        while (child.NextVisible(enterChildren) && !SerializedProperty.EqualContents(child, end))
        {
            enterChildren = false;

            if (child.propertyType == SerializedPropertyType.ManagedReference)
            {
                DrawManagedReferenceProperty(child, GetManagedReferenceBaseType(child));
            }
            else if (IsManagedReferenceList(child, out Type listBaseType))
            {
                DrawManagedReferenceList(child, listBaseType);
            }
            else
            {
                EditorGUILayout.PropertyField(child, true);
            }
        }
        EditorGUI.indentLevel--;
    }

    private static bool IsManagedReferenceList(SerializedProperty property, out Type baseType)
    {
        baseType = null;
        if (!property.isArray || property.propertyType == SerializedPropertyType.String)
        {
            return false;
        }

        if (property.name == ConditionsProperty)
        {
            baseType = typeof(CardReactiveConditionBase);
            return true;
        }

        return false;
    }

    private static Type GetManagedReferenceBaseType(SerializedProperty property)
    {
        string fieldType = property.managedReferenceFieldTypename;
        if (!string.IsNullOrWhiteSpace(fieldType))
        {
            string[] parts = fieldType.Split(' ');
            if (parts.Length == 2)
            {
                Type type = Type.GetType($"{parts[1]}, {parts[0]}");
                if (type != null)
                {
                    return type;
                }
            }
        }

        return typeof(CardReactiveConditionBase);
    }

    private static Type DrawTypePopup(string label, Type currentType, Type baseType)
    {
        Type[] types = GetAssignableTypes(baseType);
        GUIContent[] labels = GetTypeLabels(baseType);

        int currentIndex = 0;
        if (currentType != null)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == currentType)
                {
                    currentIndex = i + 1;
                    break;
                }
            }
        }

        GUIContent[] popupLabels = new GUIContent[labels.Length + 1];
        popupLabels[0] = new GUIContent("(None)");
        Array.Copy(labels, 0, popupLabels, 1, labels.Length);

        int selected = EditorGUILayout.Popup(new GUIContent(label), currentIndex, popupLabels);
        return selected <= 0 ? null : types[selected - 1];
    }

    private static Type DrawAddTypePopup(SerializedProperty list, Type baseType)
    {
        Type[] types = GetAssignableTypes(baseType);
        GUIContent[] labels = GetTypeLabels(baseType);
        GUIContent[] popupLabels = new GUIContent[labels.Length + 1];
        popupLabels[0] = new GUIContent("(None)");
        Array.Copy(labels, 0, popupLabels, 1, labels.Length);

        string key = list.propertyPath;
        if (!s_addTypeIndexByPropertyPath.TryGetValue(key, out int selectedIndex))
        {
            selectedIndex = 0;
        }

        selectedIndex = EditorGUILayout.Popup(new GUIContent("추가할 타입"), selectedIndex, popupLabels);
        s_addTypeIndexByPropertyPath[key] = selectedIndex;
        return selectedIndex <= 0 ? null : types[selectedIndex - 1];
    }

    private static Type[] GetAssignableTypes(Type baseType)
    {
        if (baseType == typeof(CardEffectBase))
        {
            EnsureEffectTypes();
            return s_effectTypes;
        }

        EnsureConditionTypes();
        return s_conditionTypes;
    }

    private static GUIContent[] GetTypeLabels(Type baseType)
    {
        if (baseType == typeof(CardEffectBase))
        {
            EnsureEffectTypes();
            return s_effectTypeLabels;
        }

        EnsureConditionTypes();
        return s_conditionTypeLabels;
    }

    private static void EnsureEffectTypes()
    {
        if (s_effectTypes != null) return;
        s_effectTypes = TypeCache.GetTypesDerivedFrom<CardEffectBase>()
            .Where(IsSelectableManagedReferenceType)
            .OrderBy(type => type.Name)
            .ToArray();
        s_effectTypeLabels = s_effectTypes
            .Select(type => new GUIContent(type.Name))
            .ToArray();
    }

    private static void EnsureConditionTypes()
    {
        if (s_conditionTypes != null) return;
        s_conditionTypes = TypeCache.GetTypesDerivedFrom<CardReactiveConditionBase>()
            .Where(IsSelectableManagedReferenceType)
            .OrderBy(type => type.Name)
            .ToArray();
        s_conditionTypeLabels = s_conditionTypes
            .Select(type => new GUIContent(type.Name))
            .ToArray();
    }

    private static bool IsSelectableManagedReferenceType(Type type)
    {
        return type != null &&
               !type.IsAbstract &&
               !type.IsGenericType &&
               type.GetConstructor(Type.EmptyTypes) != null;
    }

    private static void DrawLocalizationPreview(CardDataSO card)
    {
        EnsureLoaded();

        EditorGUILayout.LabelField("한국어 미리보기", EditorStyles.boldLabel);
        DrawPreview("DisplayNameKey", card.DisplayNameKey);
        DrawPreview("DescriptionKey", card.DescriptionKey);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("StringChart 다시 읽기", GUILayout.Width(150f)))
            {
                s_koreanByKey = null;
                EnsureLoaded();
            }
        }
    }

    private static void DrawPreview(string label, string key)
    {
        string value = ResolveKorean(key);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(label, key);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextArea(value, GUILayout.MinHeight(36f));
            }
        }
    }

    private static string ResolveKorean(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(키 없음)";
        if (s_koreanByKey == null) return "(StringChart 로드 실패)";
        return s_koreanByKey.TryGetValue(key, out string text) && !string.IsNullOrEmpty(text)
            ? text
            : "(한국어 번역 없음)";
    }

    private static void EnsureLoaded()
    {
        if (s_koreanByKey != null) return;

        s_koreanByKey = new Dictionary<string, string>();
        TextAsset csv = AssetDatabase.LoadAssetAtPath<TextAsset>(StringChartPath);
        if (csv == null) return;

        List<List<string>> rows = CsvParser.Parse(csv.text);
        if (rows == null || rows.Count == 0) return;

        List<string> header = rows[0];
        int idIndex = header.IndexOf(IdColumn);
        int koreanIndex = header.IndexOf(KoreanColumn);
        if (idIndex < 0 || koreanIndex < 0) return;

        for (int i = 1; i < rows.Count; i++)
        {
            List<string> row = rows[i];
            if (row == null || row.Count <= idIndex) continue;

            string key = row[idIndex];
            if (string.IsNullOrEmpty(key)) continue;

            string korean = row.Count > koreanIndex ? row[koreanIndex] : string.Empty;
            s_koreanByKey[key] = korean;
        }
    }
}
