// =================================================================
// [스크립트 목적]  CardDataSO 제작 전용 EditorWindow. 복잡한 카드 SO 필드를
//                  작업 탭/검증/fallback Raw 편집으로 나누어 제공한다.
// [의존 관계]      - CardDataSO / CardEffectBase / CardReactiveConditionBase / CardDeckBuilder
// [설계 노트]      - CardDataSO 직렬화 구조는 변경하지 않고 SerializedObject 로만 편집한다.
// =================================================================
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public sealed class CardAuthoringWindow : EditorWindow
{
    private const string CardFolder = "Assets/00_Addressable/Data/SO/Card";
    private const string StringChartPath = "Assets/00_Addressable/Data/CSV/StringChart.csv";
    private const string CsvPath = "Assets/05_Data/CSV/Cards.csv";
    private const string CsvOutputFolder = "Assets/00_Addressable/Data/SO/Card";
    private const string IdColumn = "Id";
    private const string KoreanColumn = "KOR";

    private static readonly string[] s_tabs =
    {
        "기본",
        "분류",
        "효과",
        "화살표/체인",
        "VFX",
        "검증",
        "Raw"
    };

    private readonly List<CardDataSO> _cards = new List<CardDataSO>();
    private readonly Dictionary<string, string> _koreanByKey = new Dictionary<string, string>();
    private readonly Dictionary<string, int> _cardIdCounts = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _addTypeIndexByPropertyPath = new Dictionary<string, int>();

    private Vector2 _leftScroll;
    private Vector2 _rightScroll;
    private Vector2 _effectScroll;
    private CardDataSO _selectedCard;
    private SerializedObject _serializedCard;
    private string _search = string.Empty;
    private string _newCardId = "New_Card";
    private int _tabIndex;
    private Type[] _effectTypes;
    private GUIContent[] _effectTypeLabels;
    private Type[] _conditionTypes;
    private GUIContent[] _conditionTypeLabels;
    private Dictionary<Type, string> _csvKeyByEffectType;

    [MenuItem("Tools/Framework/Card Authoring")]
    private static void Open()
    {
        CardAuthoringWindow window = GetWindow<CardAuthoringWindow>("Card Authoring");
        window.minSize = new Vector2(980f, 620f);
        window.RefreshCards();
    }

    private void OnEnable()
    {
        RefreshCards();
        LoadStringChart();
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawCardList();
            DrawSelectedCard();
        }
    }

    private void DrawCardList()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(280f)))
        {
            EditorGUILayout.LabelField("CardDataSO", EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField("검색", _search);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("새로고침"))
                {
                    RefreshCards();
                    LoadStringChart();
                }

                if (GUILayout.Button("CSV 빌드"))
                {
                    int count = CardDeckBuilder.Build(CsvPath, CsvOutputFolder);
                    RefreshCards();
                    EditorUtility.DisplayDialog("Card Deck Builder", $"{count}개 카드 처리 완료", "확인");
                }

                if (GUILayout.Button("CSV Export"))
                {
                    InvokeCardDeckExporter();
                }
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _newCardId = EditorGUILayout.TextField("신규 Card ID", _newCardId);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newCardId)))
                {
                    if (GUILayout.Button("신규 카드 생성"))
                    {
                        CreateCard(_newCardId.Trim());
                    }
                }
            }

            EditorGUILayout.Space(4f);
            _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
            foreach (CardDataSO card in GetFilteredCards())
            {
                DrawCardListItem(card);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawCardListItem(CardDataSO card)
    {
        List<ValidationMessage> messages = ValidateCard(card);
        MessageType worst = GetWorstMessageType(messages);
        GUIStyle style = card == _selectedCard ? EditorStyles.helpBox : GUI.skin.box;

        using (new EditorGUILayout.VerticalScope(style))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(card.name, EditorStyles.label))
                {
                    SelectCard(card);
                }

                GUILayout.Label(GetStatusLabel(worst), GUILayout.Width(48f));
            }

            EditorGUILayout.LabelField(card.CardId, EditorStyles.miniLabel);
        }
    }

    private void DrawSelectedCard()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            if (_selectedCard == null)
            {
                EditorGUILayout.HelpBox("왼쪽 목록에서 CardDataSO를 선택하거나 신규 카드를 생성하세요.", MessageType.Info);
                return;
            }

            if (_serializedCard == null || _serializedCard.targetObject != _selectedCard)
            {
                _serializedCard = new SerializedObject(_selectedCard);
            }

            _serializedCard.Update();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField($"{_selectedCard.name} / {_selectedCard.CardId}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Ping", EditorStyles.toolbarButton, GUILayout.Width(48f)))
                {
                    EditorGUIUtility.PingObject(_selectedCard);
                }
                if (GUILayout.Button("저장", EditorStyles.toolbarButton, GUILayout.Width(48f)))
                {
                    SaveSelectedCard();
                }
            }

            _tabIndex = GUILayout.Toolbar(_tabIndex, s_tabs);
            _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);

            switch (_tabIndex)
            {
                case 0:
                    DrawBasicTab();
                    break;
                case 1:
                    DrawClassificationTab();
                    break;
                case 2:
                    DrawEffectsTab();
                    break;
                case 3:
                    DrawArrowAndChainTab();
                    break;
                case 4:
                    DrawVfxTab();
                    break;
                case 5:
                    DrawValidationTab();
                    break;
                default:
                    DrawRawTab();
                    break;
            }

            EditorGUILayout.EndScrollView();

            if (_serializedCard.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(_selectedCard);
                RebuildCardIdCounts();
            }
        }
    }

    private void DrawBasicTab()
    {
        DrawHelp("카드 ID는 런타임/CSV 식별자입니다. 현재 DataManager는 SO 이름으로도 조회하므로 asset name과 맞추는 편이 안전합니다.");
        DrawProperty("_cardId");
        DrawProperty("_displayNameKey");
        DrawLocalizationPreview("표시 이름", "_displayNameKey");
        DrawProperty("_descriptionKey");
        DrawLocalizationPreview("설명", "_descriptionKey");
        DrawProperty("_iconKey");
        DrawProperty("_cost");
        DrawProperty("_upgradedCost");
    }

    private void DrawClassificationTab()
    {
        DrawHelp("카드 분류/희귀도/키워드와 보상 풀은 서로 독립된 축입니다.");
        DrawProperty("_deckType");
        DrawProperty("_cardType");
        DrawProperty("_rarity");
        DrawProperty("_keywords");
        DrawProperty("_pools");
        DrawProperty("_stagePools");
        DrawProperty("_unlockedByDefault");
    }

    private void DrawEffectsTab()
    {
        DrawHelp("Effect는 SerializeReference 타입입니다. 새 effect가 추가되면 public 기본 생성자가 있는 타입만 자동 노출됩니다.");
        _effectScroll = EditorGUILayout.BeginScrollView(_effectScroll);
        DrawManagedReferenceList(Find("_baseEffects"), typeof(CardEffectBase));
        EditorGUILayout.Space(8f);
        DrawManagedReferenceList(Find("_upgradeEffects"), typeof(CardEffectBase));
        EditorGUILayout.EndScrollView();
    }

    private void DrawArrowAndChainTab()
    {
        DrawHelp("화살표는 카드가 토글될 때 다음 신호를 어디로 보낼지 결정합니다. 체인은 그 신호의 거리/차단/패턴 규칙입니다.");
        DrawProperty("_baseArrow");
        DrawProperty("_hasUpgradeArrow");
        using (new EditorGUI.DisabledScope(!Find("_hasUpgradeArrow").boolValue))
        {
            DrawProperty("_upgradeArrow");
        }
        DrawProperty("_range");
        DrawProperty("_upgradedRange");
        DrawProperty("_propagation");
        DrawProperty("_patternType");
    }

    private void DrawVfxTab()
    {
        DrawHelp("VFX 키는 PoolManager.SpawnAsync 키입니다. VfxType이 Projectile/Hit/Both일 때 필요한 키가 비어 있으면 연출이 생략될 수 있습니다.");
        DrawProperty("_vfxType");
        DrawProperty("_vfxTarget");
        DrawProperty("_projectileKey");
        DrawProperty("_hitVfxKey");
    }

    private void DrawValidationTab()
    {
        List<ValidationMessage> messages = ValidateCard(_selectedCard);
        DrawCsvSummary(_selectedCard);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("검증 결과", EditorStyles.boldLabel);
        if (messages.Count == 0)
        {
            EditorGUILayout.HelpBox("현재 감지된 문제가 없습니다.", MessageType.Info);
            return;
        }

        for (int i = 0; i < messages.Count; i++)
        {
            ValidationMessage message = messages[i];
            EditorGUILayout.HelpBox(message.Text, message.Type);
            if (message.QuickFix == ValidationQuickFix.MatchAssetNameToCardId)
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_selectedCard.CardId)))
                {
                    if (GUILayout.Button("SO 이름을 Card ID와 맞추기"))
                    {
                        RenameSelectedAssetToCardId();
                    }
                }
            }
        }
    }

    private void DrawRawTab()
    {
        DrawHelp("새 serialized field가 생겨도 이 탭에서 Unity 기본 렌더러로 편집할 수 있습니다.");
        SerializedProperty iterator = _serializedCard.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            using (new EditorGUI.DisabledScope(iterator.propertyPath == "m_Script"))
            {
                EditorGUILayout.PropertyField(iterator, true);
            }
        }
    }

    private void DrawManagedReferenceList(SerializedProperty list, Type baseType)
    {
        if (list == null)
        {
            return;
        }

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
                        string typeName = element.managedReferenceValue != null
                            ? element.managedReferenceValue.GetType().Name
                            : "(None)";
                        EditorGUILayout.LabelField($"Element {i}: {typeName}", EditorStyles.boldLabel);
                        if (GUILayout.Button("삭제", GUILayout.Width(48f)))
                        {
                            removeIndex = i;
                        }
                    }

                    DrawManagedReferenceProperty(element, baseType);
                    DrawEffectExtras(element);
                }
            }

            if (removeIndex >= 0)
            {
                list.DeleteArrayElementAtIndex(removeIndex);
            }

            Type selected = DrawAddTypePopup(list, baseType);
            string buttonLabel = baseType == typeof(CardEffectBase) ? "Effect 추가" : "Condition 추가";
            using (new EditorGUI.DisabledScope(selected == null))
            {
                if (GUILayout.Button(buttonLabel))
                {
                    int index = list.arraySize;
                    list.InsertArrayElementAtIndex(index);
                    SerializedProperty element = list.GetArrayElementAtIndex(index);
                    element.managedReferenceValue = Activator.CreateInstance(selected);
                    _addTypeIndexByPropertyPath[list.propertyPath] = 0;
                }
            }
        }
    }

    private void DrawManagedReferenceProperty(SerializedProperty property, Type baseType)
    {
        Type currentType = property.managedReferenceValue != null
            ? property.managedReferenceValue.GetType()
            : null;

        Type selected = DrawTypePopup("Type", currentType, baseType);
        if (selected != currentType)
        {
            property.managedReferenceValue = selected != null ? Activator.CreateInstance(selected) : null;
            currentType = selected;
        }

        if (currentType == null)
        {
            EditorGUILayout.HelpBox("타입을 선택하세요.", MessageType.Info);
            return;
        }

        DrawManagedReferenceChildren(property);
    }

    private void DrawManagedReferenceChildren(SerializedProperty property)
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

    private void DrawEffectExtras(SerializedProperty effectProperty)
    {
        if (effectProperty.managedReferenceValue is not ReactiveSelfToggleEffect)
        {
            return;
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("ReactiveSelfToggle 프리셋", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("드로우"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.DrawEffectResolved, EReactiveValueChange.Any);
            }
            if (GUILayout.Button("에너지 증가"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.EnergyChanged, EReactiveValueChange.Increased);
            }
            if (GUILayout.Button("에너지 감소"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.EnergyChanged, EReactiveValueChange.Decreased);
            }
            if (GUILayout.Button("적 피해"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.EnemyDamaged, EReactiveValueChange.Any);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("디스카드"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.CardDiscarded, EReactiveValueChange.Any);
            }
            if (GUILayout.Button("소멸"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.CardExhausted, EReactiveValueChange.Any);
            }
            if (GUILayout.Button("배치"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.CardPlaced, EReactiveValueChange.Any);
            }
            if (GUILayout.Button("토글"))
            {
                ApplyReactivePreset(effectProperty, EReactiveEventType.CardToggled, EReactiveValueChange.Any);
            }
        }
    }

    private void ApplyReactivePreset(
        SerializedProperty effectProperty,
        EReactiveEventType eventType,
        EReactiveValueChange change)
    {
        if (effectProperty.managedReferenceValue is not ReactiveSelfToggleEffect reactive)
        {
            return;
        }

        Undo.RecordObject(_selectedCard, "Apply Reactive Preset");
        reactive.EditorImportSimple(
            eventType,
            minAmount: 0,
            change,
            EReactiveOwnerState.Any,
            triggerLimit: 0,
            oncePerTurn: false);
        effectProperty.managedReferenceValue = reactive;
        EditorUtility.SetDirty(_selectedCard);
    }

    private Type DrawAddTypePopup(SerializedProperty list, Type baseType)
    {
        Type[] types = GetAssignableTypes(baseType);
        GUIContent[] labels = GetTypeLabels(baseType);
        GUIContent[] popupLabels = new GUIContent[labels.Length + 1];
        popupLabels[0] = new GUIContent("(None)");
        Array.Copy(labels, 0, popupLabels, 1, labels.Length);

        string key = list.propertyPath;
        if (!_addTypeIndexByPropertyPath.TryGetValue(key, out int selectedIndex))
        {
            selectedIndex = 0;
        }

        selectedIndex = EditorGUILayout.Popup(new GUIContent("추가할 타입"), selectedIndex, popupLabels);
        _addTypeIndexByPropertyPath[key] = selectedIndex;
        return selectedIndex <= 0 ? null : types[selectedIndex - 1];
    }

    private Type DrawTypePopup(string label, Type currentType, Type baseType)
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

    private Type[] GetAssignableTypes(Type baseType)
    {
        if (baseType == typeof(CardEffectBase))
        {
            EnsureEffectTypes();
            return _effectTypes;
        }

        EnsureConditionTypes();
        return _conditionTypes;
    }

    private GUIContent[] GetTypeLabels(Type baseType)
    {
        if (baseType == typeof(CardEffectBase))
        {
            EnsureEffectTypes();
            return _effectTypeLabels;
        }

        EnsureConditionTypes();
        return _conditionTypeLabels;
    }

    private void EnsureEffectTypes()
    {
        if (_effectTypes != null) return;
        _effectTypes = TypeCache.GetTypesDerivedFrom<CardEffectBase>()
            .Where(IsSelectableManagedReferenceType)
            .OrderBy(type => type.Name)
            .ToArray();
        _effectTypeLabels = _effectTypes.Select(type => new GUIContent(type.Name)).ToArray();
    }

    private void EnsureConditionTypes()
    {
        if (_conditionTypes != null) return;
        _conditionTypes = TypeCache.GetTypesDerivedFrom<CardReactiveConditionBase>()
            .Where(IsSelectableManagedReferenceType)
            .OrderBy(type => type.Name)
            .ToArray();
        _conditionTypeLabels = _conditionTypes.Select(type => new GUIContent(type.Name)).ToArray();
    }

    private static bool IsSelectableManagedReferenceType(Type type)
    {
        return type != null &&
               !type.IsAbstract &&
               !type.IsGenericType &&
               type.GetConstructor(Type.EmptyTypes) != null;
    }

    private static bool IsManagedReferenceList(SerializedProperty property, out Type baseType)
    {
        baseType = null;
        if (!property.isArray || property.propertyType == SerializedPropertyType.String)
        {
            return false;
        }

        if (property.name == "Conditions")
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

    private void DrawCsvSummary(CardDataSO card)
    {
        EnsureCsvMap();
        EditorGUILayout.LabelField("CSV 호환 요약", EditorStyles.boldLabel);
        IReadOnlyList<CardEffectBase> effects = card.GetEffects(false);
        if (effects == null || effects.Count == 0)
        {
            EditorGUILayout.HelpBox("기본 effect가 없어 CSV effectType 요약을 만들 수 없습니다.", MessageType.Warning);
            return;
        }

        for (int i = 0; i < effects.Count; i++)
        {
            CardEffectBase effect = effects[i];
            if (effect == null)
            {
                EditorGUILayout.HelpBox($"Base Effect {i}: 비어 있음", MessageType.Warning);
                continue;
            }

            if (_csvKeyByEffectType.TryGetValue(effect.GetType(), out string key))
            {
                EditorGUILayout.LabelField(effect.GetType().Name, $"effectType={key}");
            }
            else
            {
                EditorGUILayout.HelpBox($"{effect.GetType().Name}: Inspector 제작 가능 / CSV import 미지원", MessageType.Info);
            }
        }
    }

    private void EnsureCsvMap()
    {
        if (_csvKeyByEffectType != null) return;
        _csvKeyByEffectType = new Dictionary<Type, string>();

        FieldInfo field = typeof(CardDeckBuilder).GetField(
            "EffectFactoryMap",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not IDictionary map)
        {
            return;
        }

        foreach (DictionaryEntry entry in map)
        {
            if (entry.Key is not string key || entry.Value is not Delegate factory)
            {
                continue;
            }

            try
            {
                if (factory.DynamicInvoke() is CardEffectBase effect)
                {
                    _csvKeyByEffectType[effect.GetType()] = key;
                }
            }
            catch (Exception)
            {
                // CSV 요약은 보조 기능이므로 개별 factory 실패는 무시한다.
            }
        }
    }

    private List<ValidationMessage> ValidateCard(CardDataSO card)
    {
        List<ValidationMessage> result = new List<ValidationMessage>();
        if (card == null)
        {
            return result;
        }

        SerializedObject so = new SerializedObject(card);
        string cardId = card.CardId;
        if (string.IsNullOrWhiteSpace(cardId))
        {
            result.Add(new ValidationMessage(MessageType.Error, "Card ID가 비어 있습니다."));
        }
        else
        {
            if (card.name != cardId)
            {
                result.Add(new ValidationMessage(
                    MessageType.Warning,
                    $"SO 이름('{card.name}')과 Card ID('{cardId}')가 다릅니다.",
                    ValidationQuickFix.MatchAssetNameToCardId));
            }

            if (_cardIdCounts.TryGetValue(cardId, out int count) && count > 1)
            {
                result.Add(new ValidationMessage(MessageType.Error, $"중복 Card ID입니다: {cardId}"));
            }
        }

        AddStringKeyWarning(result, "displayNameKey", card.DisplayNameKey);
        AddStringKeyWarning(result, "descriptionKey", card.DescriptionKey);

        if (string.IsNullOrWhiteSpace(card.IconKey))
        {
            result.Add(new ValidationMessage(MessageType.Info, "iconKey가 비어 있습니다. 카드 이미지가 fallback으로 보일 수 있습니다."));
        }

        SerializedProperty baseEffects = so.FindProperty("_baseEffects");
        SerializedProperty upgradeEffects = so.FindProperty("_upgradeEffects");
        if (baseEffects == null || baseEffects.arraySize == 0)
        {
            result.Add(new ValidationMessage(MessageType.Warning, "baseEffects가 비어 있습니다."));
        }

        SerializedProperty cost = so.FindProperty("_cost");
        SerializedProperty upgradedCost = so.FindProperty("_upgradedCost");
        if ((upgradeEffects == null || upgradeEffects.arraySize == 0) &&
            cost != null &&
            upgradedCost != null &&
            cost.intValue != upgradedCost.intValue)
        {
            result.Add(new ValidationMessage(MessageType.Warning, "upgradeEffects가 비었지만 upgradedCost가 기본 비용과 다릅니다."));
        }

        ValidateReactiveEffects(result, card.GetEffects(false));
        if (card.CanUpgrade)
        {
            ValidateReactiveEffects(result, card.GetEffects(true));
        }
        ValidateVfx(result, card);

        return result;
    }

    private void ValidateReactiveEffects(List<ValidationMessage> result, IReadOnlyList<CardEffectBase> effects)
    {
        if (effects == null) return;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] is not ReactiveSelfToggleEffect reactive)
            {
                continue;
            }

            CardReactiveConditionBase condition = GetReactiveCondition(reactive);
            if (condition == null)
            {
                result.Add(new ValidationMessage(MessageType.Error, "ReactiveSelfToggleEffect 조건이 비어 있습니다."));
                continue;
            }

            if (!ContainsConditionType<ReactiveOwnerOnGridCondition>(condition))
            {
                result.Add(new ValidationMessage(MessageType.Warning, "ReactiveSelfToggleEffect에 owner grid 조건이 없습니다."));
            }
        }
    }

    private static CardReactiveConditionBase GetReactiveCondition(ReactiveSelfToggleEffect reactive)
    {
        FieldInfo field = typeof(ReactiveSelfToggleEffect).GetField(
            "_condition",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(reactive) as CardReactiveConditionBase;
    }

    private static bool ContainsConditionType<T>(CardReactiveConditionBase condition)
        where T : CardReactiveConditionBase
    {
        if (condition == null)
        {
            return false;
        }
        if (condition is T)
        {
            return true;
        }
        if (condition is ReactiveAllCondition all)
        {
            return all.Conditions != null && all.Conditions.Any(ContainsConditionType<T>);
        }
        if (condition is ReactiveAnyCondition any)
        {
            return any.Conditions != null && any.Conditions.Any(ContainsConditionType<T>);
        }
        if (condition is ReactiveNotCondition not)
        {
            FieldInfo field = typeof(ReactiveNotCondition).GetField("Condition");
            return field?.GetValue(not) is CardReactiveConditionBase child && ContainsConditionType<T>(child);
        }
        return false;
    }

    private static void ValidateVfx(List<ValidationMessage> result, CardDataSO card)
    {
        if ((card.VfxType == EVfxType.Projectile || card.VfxType == EVfxType.Both) &&
            string.IsNullOrWhiteSpace(card.ProjectileKey))
        {
            result.Add(new ValidationMessage(MessageType.Warning, "VfxType이 투사체를 요구하지만 projectileKey가 비어 있습니다."));
        }

        if ((card.VfxType == EVfxType.Hit || card.VfxType == EVfxType.Both) &&
            string.IsNullOrWhiteSpace(card.HitVfxKey))
        {
            result.Add(new ValidationMessage(MessageType.Warning, "VfxType이 적중 이펙트를 요구하지만 hitVfxKey가 비어 있습니다."));
        }
    }

    private void AddStringKeyWarning(List<ValidationMessage> result, string label, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            result.Add(new ValidationMessage(MessageType.Warning, $"{label}가 비어 있습니다."));
            return;
        }
        if (_koreanByKey.Count > 0 && !_koreanByKey.ContainsKey(key))
        {
            result.Add(new ValidationMessage(MessageType.Warning, $"{label} '{key}'를 StringChart에서 찾지 못했습니다."));
        }
    }

    private void DrawLocalizationPreview(string label, string propertyPath)
    {
        SerializedProperty property = Find(propertyPath);
        string key = property?.stringValue;
        string value = ResolveKorean(key);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField($"{label} 미리보기", key);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextArea(value, GUILayout.MinHeight(34f));
            }
        }
    }

    private string ResolveKorean(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(키 없음)";
        return _koreanByKey.TryGetValue(key, out string text) && !string.IsNullOrEmpty(text)
            ? text
            : "(한국어 번역 없음)";
    }

    private void LoadStringChart()
    {
        _koreanByKey.Clear();
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
            _koreanByKey[key] = korean;
        }
    }

    private void RefreshCards()
    {
        _cards.Clear();
        string[] guids = AssetDatabase.FindAssets("t:CardDataSO", new[] { CardFolder });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            CardDataSO card = AssetDatabase.LoadAssetAtPath<CardDataSO>(path);
            if (card != null)
            {
                _cards.Add(card);
            }
        }

        _cards.Sort((a, b) => string.Compare(a.CardId, b.CardId, StringComparison.OrdinalIgnoreCase));
        RebuildCardIdCounts();

        if (_selectedCard != null && !_cards.Contains(_selectedCard))
        {
            SelectCard(null);
        }
    }

    private void RebuildCardIdCounts()
    {
        _cardIdCounts.Clear();
        for (int i = 0; i < _cards.Count; i++)
        {
            string cardId = _cards[i] != null ? _cards[i].CardId : null;
            if (string.IsNullOrWhiteSpace(cardId))
            {
                continue;
            }

            _cardIdCounts.TryGetValue(cardId, out int count);
            _cardIdCounts[cardId] = count + 1;
        }
    }

    private IEnumerable<CardDataSO> GetFilteredCards()
    {
        if (string.IsNullOrWhiteSpace(_search))
        {
            return _cards;
        }

        string keyword = _search.Trim();
        return _cards.Where(card =>
            card != null &&
            (card.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
             (!string.IsNullOrEmpty(card.CardId) &&
              card.CardId.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)));
    }

    private void CreateCard(string cardId)
    {
        if (!AssetDatabase.IsValidFolder(CardFolder))
        {
            Debug.LogError($"[CardAuthoringWindow] 카드 폴더가 없습니다: {CardFolder}");
            return;
        }

        string fileName = MakeSafeFileName(cardId);
        string path = AssetDatabase.GenerateUniqueAssetPath($"{CardFolder}/{fileName}.asset");
        CardDataSO card = CreateInstance<CardDataSO>();
        AssetDatabase.CreateAsset(card, path);

        SerializedObject so = new SerializedObject(card);
        SetString(so, "_cardId", cardId);
        SetString(so, "_displayNameKey", cardId);
        SetString(so, "_descriptionKey", cardId);
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(card);
        AssetDatabase.SaveAssets();
        RefreshCards();
        SelectCard(card);
        EditorGUIUtility.PingObject(card);
    }

    private static string MakeSafeFileName(string value)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string result = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "New_Card" : result;
    }

    private void SelectCard(CardDataSO card)
    {
        _selectedCard = card;
        _serializedCard = card != null ? new SerializedObject(card) : null;
        _rightScroll = Vector2.zero;
    }

    private void SaveSelectedCard()
    {
        if (_selectedCard == null)
        {
            return;
        }

        _serializedCard?.ApplyModifiedProperties();
        EditorUtility.SetDirty(_selectedCard);
        AssetDatabase.SaveAssets();
        RefreshCards();
    }

    private static void InvokeCardDeckExporter()
    {
        Type exporterType = typeof(CardAuthoringWindow).Assembly.GetType("CardDeckExporter");
        MethodInfo method = exporterType?.GetMethod(
            "ExportWithSavePanel",
            BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            EditorUtility.DisplayDialog(
                "Card Deck Exporter",
                "CardDeckExporter를 찾지 못했습니다. Unity 프로젝트 파일을 갱신한 뒤 다시 시도하세요.",
                "확인");
            return;
        }

        method.Invoke(null, null);
    }

    private void RenameSelectedAssetToCardId()
    {
        if (_selectedCard == null || string.IsNullOrWhiteSpace(_selectedCard.CardId))
        {
            return;
        }

        string path = AssetDatabase.GetAssetPath(_selectedCard);
        string error = AssetDatabase.RenameAsset(path, MakeSafeFileName(_selectedCard.CardId));
        if (!string.IsNullOrEmpty(error))
        {
            EditorUtility.DisplayDialog("Rename Failed", error, "확인");
            return;
        }

        AssetDatabase.SaveAssets();
        RefreshCards();
    }

    private SerializedProperty Find(string propertyPath)
    {
        return _serializedCard?.FindProperty(propertyPath);
    }

    private void DrawProperty(string propertyPath)
    {
        SerializedProperty property = Find(propertyPath);
        if (property == null)
        {
            EditorGUILayout.HelpBox($"필드를 찾을 수 없습니다: {propertyPath}", MessageType.Info);
            return;
        }

        EditorGUILayout.PropertyField(property, true);
    }

    private static void SetString(SerializedObject so, string propertyPath, string value)
    {
        SerializedProperty property = so.FindProperty(propertyPath);
        if (property != null)
        {
            property.stringValue = value;
        }
    }

    private static void DrawHelp(string text)
    {
        EditorGUILayout.HelpBox(text, MessageType.Info);
    }

    private static MessageType GetWorstMessageType(List<ValidationMessage> messages)
    {
        if (messages.Any(message => message.Type == MessageType.Error)) return MessageType.Error;
        if (messages.Any(message => message.Type == MessageType.Warning)) return MessageType.Warning;
        if (messages.Any(message => message.Type == MessageType.Info)) return MessageType.Info;
        return MessageType.None;
    }

    private static string GetStatusLabel(MessageType type)
    {
        return type switch
        {
            MessageType.Error => "Error",
            MessageType.Warning => "Warn",
            MessageType.Info => "Info",
            _ => "OK"
        };
    }

    private readonly struct ValidationMessage
    {
        public readonly MessageType Type;
        public readonly string Text;
        public readonly ValidationQuickFix QuickFix;

        public ValidationMessage(
            MessageType type,
            string text,
            ValidationQuickFix quickFix = ValidationQuickFix.None)
        {
            Type = type;
            Text = text;
            QuickFix = quickFix;
        }
    }

    private enum ValidationQuickFix
    {
        None,
        MatchAssetNameToCardId
    }
}
#endif
