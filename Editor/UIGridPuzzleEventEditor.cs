using System;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(UIGridPuzzleEvent))]
public sealed class UIGridPuzzleEventEditor : Editor
{
    private static readonly Type[] RewardTypes =
    {
        typeof(GridPuzzleGoldReward), typeof(GridPuzzleHealReward), typeof(GridPuzzleAddCardReward)
    };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(iterator);
                continue;
            }
            EditorGUILayout.PropertyField(iterator, true);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("보상 효과 추가", EditorStyles.boldLabel);
        SerializedProperty tiers = serializedObject.FindProperty("_rewardTiers");
        for (int i = 0; i < tiers.arraySize; i++)
        {
            SerializedProperty rewards = tiers.GetArrayElementAtIndex(i).FindPropertyRelative("_rewards");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"구간 {i}");
            for (int j = 0; j < RewardTypes.Length; j++)
            {
                Type type = RewardTypes[j];
                if (GUILayout.Button(type.Name.Replace("GridPuzzle", string.Empty).Replace("Reward", string.Empty)))
                {
                    int index = rewards.arraySize++;
                    rewards.GetArrayElementAtIndex(index).managedReferenceValue = Activator.CreateInstance(type);
                }
            }
            if (rewards.arraySize > 0 && GUILayout.Button("마지막 삭제"))
                rewards.DeleteArrayElementAtIndex(rewards.arraySize - 1);
            EditorGUILayout.EndHorizontal();
        }
        serializedObject.ApplyModifiedProperties();
    }
}
