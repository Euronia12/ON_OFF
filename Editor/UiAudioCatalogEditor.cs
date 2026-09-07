using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(UiAudioCatalog))]
public sealed class UiAudioCatalogEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Changed SFX, volume, and pitch values are used by the next UI action and Preview immediately. " +
                "Play Mode edits to this ScriptableObject asset may remain after stopping.",
                MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox("Enter Play Mode to use the Preview buttons.", MessageType.Info);
        }

        DrawDefaultInspector();
    }
}

[CustomPropertyDrawer(typeof(UiAudioCue))]
public sealed class UiAudioCueDrawer : PropertyDrawer
{
    private const int LineCount = 7;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        if (!property.isExpanded)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        return LineCount * EditorGUIUtility.singleLineHeight +
               (LineCount - 1) * EditorGUIUtility.standardVerticalSpacing;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        Rect line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);
        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        int previousIndent = EditorGUI.indentLevel;
        EditorGUI.indentLevel++;

        SerializedProperty uiKey = property.FindPropertyRelative("_uiKey");
        SerializedProperty sfxKey = property.FindPropertyRelative("_sfxKey");
        SerializedProperty volume = property.FindPropertyRelative("_volume");
        SerializedProperty pitch = property.FindPropertyRelative("_pitch");
        SerializedProperty randomPitch = property.FindPropertyRelative("_randomPitch");

        DrawNextLine(ref line, uiKey);
        DrawNextLine(ref line, sfxKey);
        DrawNextLine(ref line, volume);
        DrawNextLine(ref line, pitch);
        DrawNextLine(ref line, randomPitch);

        bool canPreview = EditorApplication.isPlaying &&
                          AudioManager.HasInstance &&
                          !string.IsNullOrWhiteSpace(sfxKey.stringValue);
        using (new EditorGUI.DisabledScope(!canPreview))
        {
            NextLine(ref line);
            if (GUI.Button(EditorGUI.IndentedRect(line), "Preview UI SFX"))
            {
                property.serializedObject.ApplyModifiedProperties();
                AudioManager.Instance.PlayUiClipAsync(
                    sfxKey.stringValue,
                    volume.floatValue,
                    pitch.floatValue,
                    randomPitch.boolValue).Forget();
            }
        }

        EditorGUI.indentLevel = previousIndent;
        EditorGUI.EndProperty();
    }

    private static void DrawNextLine(ref Rect line, SerializedProperty property)
    {
        NextLine(ref line);
        EditorGUI.PropertyField(line, property);
    }

    private static void NextLine(ref Rect line)
    {
        line.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
    }
}
