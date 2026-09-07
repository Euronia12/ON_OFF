// =================================================================
// [스크립트 목적]  CardDataSO 에셋을 신 카드 CSV 스키마로 역출력한다 (round-trip 검증용).
// [메뉴]           Tools > Framework > Export Card Deck (to CSV)
// [설계 노트]      - 뼈대: dev3 슬림 스키마(Arrow 4컬럼 / 강화행 제거). 내용: 전투 효과.
//                  - SO 는 수정하지 않는다. CSV 로 표현 불가능한 값은 warning 으로 보고한다.
// =================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class CardDeckExporter
{
    private const string CardFolder = "Assets/00_Addressable/Data/SO/Card";
    private const string DefaultCsvPath = "Assets/05_Data/CSV/Cards_Exported.csv";
    private const string ExportMenuPath = "Tools/Framework/Export Card Deck (to CSV)";
    private const string KeywordSeparator = "|";

    // 개수 사다리: arrowCountWeights 위치 매핑 순서 (CardDeckBuilder 와 동일해야 함).
    private static readonly int[] ArrowCountLadder = { 1, 2, 4, 8 };

    // 신 스키마 헤더 (그룹별 정렬). 좌측=모든 카드 공통, 우측=특정 효과 전용. keywords 제외.
    private static readonly List<string> Headers = new List<string>
    {
        // [공통 메타]
        "koreanName", "cardId", "cardName", "descKey", "iconKey", "type", "deckType", "pools", "stages", "rarity", "keywords", "cost", "unlockedByDefault",
        // [효과 기본]
        "effectType", "amount", "triggerPhase", "duration", "target",
        // [배치/체인]
        "range", "propagation", "patternType",
        // [화살표]
        "arrowMode", "arrowCountWeights", "arrowDirs", "arrowDirWeights",
        // [연출 VFX]
        "vfxType", "vfxTarget", "projectileKey", "hitVfxKey",
        // [특정 효과 전용 — 우측]
        "statType", "hitCount", "includeSelf", "offPhase", "damagePerCount", "stackCap",
        "threshold", "payloadPhase", "countScope", "modifierType", "modifierValue",
        "activeOnly", "resetOnDeactivate",
        "reactiveEvent", "reactiveMinAmount", "reactiveChange", "reactiveOwnerState", "reactiveLimit", "reactiveOncePerTurn",
        "spawnCardId",
        "magicCircleBuff",
    };

    [MenuItem(ExportMenuPath)]
    public static void ExportWithSavePanel()
    {
        string path = EditorUtility.SaveFilePanelInProject(
            "Export Card Deck CSV",
            Path.GetFileName(DefaultCsvPath),
            "csv",
            "CardDataSO 에셋을 신 카드 CSV 스키마로 내보냅니다.",
            Path.GetDirectoryName(DefaultCsvPath)?.Replace('\\', '/') ?? "Assets/05_Data/CSV");

        if (string.IsNullOrEmpty(path))
            return;

        int count = Export(path);
        EditorUtility.DisplayDialog("Card Deck Exporter", $"{count}개 effect 행을 내보냈습니다.", "확인");
    }

    public static int Export(string csvAssetPath = DefaultCsvPath)
    {
        List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
        Dictionary<Type, string> effectTypeKeys = BuildEffectTypeKeyMap();

        string[] guids = AssetDatabase.FindAssets("t:CardDataSO", new[] { CardFolder });
        Array.Sort(guids, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            CardDataSO card = AssetDatabase.LoadAssetAtPath<CardDataSO>(assetPath);
            if (card == null)
                continue;

            try
            {
                ExportCard(card, assetPath, effectTypeKeys, rows);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CardDeckExporter] '{assetPath}' export 실패: {ex.Message}");
            }
        }

        WriteCsv(csvAssetPath, Headers, rows);
        if (!CardDeckBuilder.Review(csvAssetPath))
            Debug.LogError($"[CardDeckExporter] 재import 검토 실패: {csvAssetPath}");

        AssetDatabase.Refresh();
        Debug.Log($"[CardDeckExporter] {rows.Count}개 effect 행 export 완료: {csvAssetPath}");
        return rows.Count;
    }

    private static void ExportCard(
        CardDataSO card,
        string assetPath,
        Dictionary<Type, string> effectTypeKeys,
        List<Dictionary<string, string>> rows)
    {
        IReadOnlyList<CardEffectBase> effects = card.GetEffects(false);
        if (effects == null || effects.Count == 0)
        {
            Debug.LogWarning($"[CardDeckExporter] '{assetPath}'는 effect가 없어 export를 건너뜁니다.");
            return;
        }

        // 강화 체계 폐지: upgrade 효과 행은 내보내지 않는다.
        if (card.CanUpgrade)
            Debug.LogWarning($"[CardDeckExporter] '{assetPath}'는 upgrade 효과가 있지만 신 CSV는 강화 행을 내보내지 않습니다.");

        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] == null) continue;

            Dictionary<string, string> row = CreateBaseRow(card);
            FillEffect(row, card, effects[i], effectTypeKeys, assetPath);
            rows.Add(row);
        }
    }

    private static Dictionary<string, string> CreateBaseRow(CardDataSO card)
    {
        Dictionary<string, string> row = CreateEmptyRow(Headers);
        row["koreanName"] = card.KoreanName;
        row["cardId"] = card.CardId;
        row["cardName"] = card.DisplayNameKey;
        row["descKey"] = card.DescriptionKey;
        row["iconKey"] = card.IconKey;
        row["type"] = card.CardType.ToString();
        row["deckType"] = card.DeckType.ToString();
        row["pools"] = card.Pools == ECardPool.All ? nameof(ECardPool.All) : FormatFlags(card.Pools);
        row["stages"] = FormatFlags(card.StagePools);
        row["rarity"] = card.Rarity.ToString();
        row["keywords"] = FormatFlags(card.Keywords);
        row["cost"] = FormatInt(card.GetCost(false));
        row["unlockedByDefault"] = FormatBool(card.UnlockedByDefault);
        row["range"] = FormatInt(card.GetRange(false));
        row["propagation"] = FormatPropagation(card.Propagation);
        row["patternType"] = card.PatternType.ToString();
        row["vfxType"] = card.VfxType.ToString();
        row["vfxTarget"] = card.VfxTarget.ToString();
        row["projectileKey"] = card.ProjectileKey;
        row["hitVfxKey"] = card.HitVfxKey;
        FillArrow(row, card.GetArrowConfig(false));
        return row;
    }

    // ArrowConfig → arrowMode/arrowCountWeights/arrowDirs/arrowDirWeights (dev3 뼈대)
    private static void FillArrow(Dictionary<string, string> row, ArrowConfig arrow)
    {
        if (arrow == null)
            return;

        if (arrow.Mode == EArrowMode.Fixed)
        {
            row["arrowMode"] = "Fixed";
            row["arrowDirs"] = FormatDirections(arrow.FixedDirs);
            return;
        }

        // Import 규약: Random + arrowDirWeights 유무로 Random/Weighted 를 구분한다.
        row["arrowMode"] = "Random";
        bool weighted = arrow.Mode == EArrowMode.Weighted;
        List<string> dirs = new List<string>();
        List<string> weights = new List<string>();

        if (arrow.Candidates != null)
        {
            foreach (ArrowCandidate candidate in arrow.Candidates)
            {
                if (candidate.direction == ECardDirection.None)
                    continue;
                dirs.Add(FormatDirections(candidate.direction));
                weights.Add(FormatFloat(candidate.weight));
            }
        }

        row["arrowDirs"] = string.Join("|", dirs);
        row["arrowDirWeights"] = weighted ? string.Join("|", weights) : string.Empty;
        row["arrowCountWeights"] = FormatCountWeights(arrow.CountWeights);
    }

    // _countWeights 를 개수 사다리[1,2,4,8] 위치 문자열로 변환한다. 예: count1=70,count2=30 → "70,30".
    private static string FormatCountWeights(IReadOnlyList<ArrowCountWeight> countWeights)
    {
        if (countWeights == null || countWeights.Count == 0)
            return string.Empty;

        float[] byIndex = new float[ArrowCountLadder.Length];
        int maxIndex = -1;
        for (int i = 0; i < countWeights.Count; i++)
        {
            int idx = Array.IndexOf(ArrowCountLadder, countWeights[i].count);
            if (idx < 0 || countWeights[i].weight <= 0f)
                continue;
            byIndex[idx] = countWeights[i].weight;
            if (idx > maxIndex)
                maxIndex = idx;
        }

        if (maxIndex < 0)
            return string.Empty;

        List<string> parts = new List<string>();
        for (int i = 0; i <= maxIndex; i++)
            parts.Add(byIndex[i] > 0f ? FormatFloat(byIndex[i]) : string.Empty);

        return string.Join(",", parts);
    }

    private static void FillEffect(
        Dictionary<string, string> row,
        CardDataSO card,
        CardEffectBase effect,
        Dictionary<Type, string> effectTypeKeys,
        string assetPath)
    {
        Type effectType = effect.GetType();
        row["effectType"] = effectTypeKeys.TryGetValue(effectType, out string csvKey) ? csvKey : effectType.Name;
        row["triggerPhase"] = effect.TriggerPhase.ToString();
        row["target"] = effect.TargetType.ToString();
        row["duration"] = effect.Duration.ToString();

        if (!effectTypeKeys.ContainsKey(effectType))
            Debug.LogWarning($"[CardDeckExporter] '{assetPath}'의 {effectType.Name}은 CSV import map에 없습니다. 공통 컬럼만 export합니다.");

        FillEffectSpecific(row, card, effect, assetPath);
    }

    private static void FillEffectSpecific(
        Dictionary<string, string> row,
        CardDataSO card,
        CardEffectBase effect,
        string assetPath)
    {
        switch (effect)
        {
            case DamageEffect damage:
                row["amount"] = FormatInt(GetPrivate(damage, "_amount", 0));
                row["hitCount"] = FormatInt(GetPrivate(damage, "_hitCount", 1));
                break;
            case BlockEffect block:
                row["amount"] = FormatInt(GetPrivate(block, "_amount", 0));
                break;
            case HealEffect heal:
                row["amount"] = FormatInt(GetPrivate(heal, "_amount", 0));
                break;
            case DrawCardEffect draw:
                row["amount"] = FormatInt(GetPrivate(draw, "_drawCount", 1));
                break;
            case GainManaEffect mana:
                row["amount"] = FormatInt(GetPrivate(mana, "_amount", 1));
                break;
            case ReserveEffect reserve:
                row["amount"] = FormatInt(GetPrivate(reserve, "_preserveAmount", 1));
                row["includeSelf"] = FormatBool(GetPrivate(reserve, "_includeSelf", false));
                break;
            case AutoToggleEffect autoToggle:
                row["amount"] = FormatInt(GetPrivate(autoToggle, "_threshold", 5));
                row["threshold"] = row["amount"];
                break;
            case FirstPlacementMultiplierEffect first:
                row["amount"] = FormatInt(GetPrivate(first, "_multiplier", 2));
                break;
            case GlassBladeDecayEffect glass:
                row["amount"] = FormatInt(GetPrivate(glass, "_damageLossPerTrigger", 1));
                break;
            case FinisherDamageEffect finisher:
                row["amount"] = FormatInt(GetPrivate(finisher, "_damagePerCard", 3));
                row["countScope"] = GetPrivate(finisher, "_countScope", EGridCountScope.Total).ToString();
                break;
            case DualStateTriggerEffect dual:
                row["payloadPhase"] = GetPrivate(dual, "_onPhase", ETriggerPhase.BattleAttack).ToString();
                row["offPhase"] = GetPrivate(dual, "_offPhase", ETriggerPhase.BattleResolve).ToString();
                break;
            case SelfOnTriggerEffect selfOn:
                row["amount"] = FormatInt(GetPrivate(selfOn, "_requiredCount", 3));
                row["threshold"] = row["amount"];
                row["payloadPhase"] = GetPrivate(selfOn, "_payloadPhase", ETriggerPhase.BattleAttack).ToString();
                row["resetOnDeactivate"] = FormatBool(GetPrivate(selfOn, "_resetEachTurn", false));
                break;
            case TotemAuraEffect aura:
                FillAura(row, aura);
                break;
            case CastingEffect casting:
                row["amount"] = FormatInt(GetPrivate(casting, "_requiredCount", 3));
                row["threshold"] = row["amount"];
                row["payloadPhase"] = GetPrivate(casting, "_payloadPhase", ETriggerPhase.BattleAttack).ToString();
                break;
            case CastingPredateEffect castingPredate:
                row["amount"] = FormatInt(GetPrivate(castingPredate, "_damagePerConsumedCard", 1));
                break;
            case GridScaledDamageEffect scaledDamage:
                row["amount"] = FormatInt(GetPrivate(scaledDamage, "_baseAmount", 0));
                row["damagePerCount"] = FormatFloat(GetPrivate(scaledDamage, "_damagePerCount", 1f));
                row["countScope"] = GetPrivate(scaledDamage, "_countScope", EGridCountScope.GridTurnOnCount).ToString();
                break;
            case ActivationRampEffect ramp:
                row["amount"] = FormatFloat(GetPrivate(ramp, "_amountPerActivation", 1f));
                row["stackCap"] = FormatInt(GetPrivate(ramp, "_stackCap", 0));
                row["statType"] = GetPrivate(ramp, "_statType", EStatType.Damage).ToString();
                break;
            case PeriodicTriggerEffect periodic:
                row["amount"] = FormatInt(GetPrivate(periodic, "_interval", 3));
                row["threshold"] = row["amount"];
                row["payloadPhase"] = GetPrivate(periodic, "_payloadPhase", ETriggerPhase.BattleAttack).ToString();
                row["resetOnDeactivate"] = FormatBool(GetPrivate(periodic, "_resetEachTurn", false));
                break;
            case DirectionalAbsorbEffect absorb:
                row["amount"] = FormatFloat(GetPrivate(absorb, "_ratio", 1f));
                row["statType"] = GetPrivate(absorb, "_statType", EStatType.Damage).ToString();
                break;
            case ForbiddenRitualEffect ritual:
                row["amount"] = FormatInt(GetPrivate(ritual, "_damagePerExhaustedCard", 1));
                break;
            case RotateArrowEffect rotate:
                row["amount"] = FormatInt(GetPrivate(rotate, "_clockwiseSteps", 1));
                break;
            case SpawnOffspringEffect spawn:
                row["spawnCardId"] = GetPrivate(spawn, "_spawnCardId", string.Empty);
                break;
            case ManaBurstEffect manaBurst:
                row["amount"] = FormatInt(GetPrivate(manaBurst, "_amount", 1));
                break;
            case MagicCircleEffect magicCircle:
                row["magicCircleBuff"] = magicCircle.BuffType.ToString();
                row["threshold"] = FormatInt(magicCircle.ToggleCount);
                row["amount"] = FormatInt(magicCircle.BonusAmount);
                break;
            case ReactiveSelfToggleEffect reactive:
                FillReactive(row, card, reactive, assetPath);
                break;
        }
    }

    private static void FillAura(Dictionary<string, string> row, TotemAuraEffect aura)
    {
        row["target"] = GetPrivate(aura, "_buffScope", ECardAuraScope.Adjacent8).ToString();
        row["statType"] = GetPrivate(aura, "_buffStat", EStatType.Damage).ToString();
        row["modifierType"] = GetPrivate(aura, "_buffModifierType", EStatModifierType.Flat).ToString();
        row["modifierValue"] = FormatFloat(GetPrivate(aura, "_buffValue", 1f));
        row["amount"] = row["modifierValue"];
        row["activeOnly"] = FormatBool(GetPrivate(aura, "_activeOnly", true));
    }

    private static void FillReactive(
        Dictionary<string, string> row,
        CardDataSO card,
        ReactiveSelfToggleEffect reactive,
        string assetPath)
    {
        CardReactiveConditionBase root = GetPrivate<CardReactiveConditionBase>(reactive, "_condition", null);
        if (root == null)
        {
            Debug.LogWarning($"[CardDeckExporter] '{assetPath}' {card.CardId}: ReactiveSelfToggleEffect 조건이 비어 있습니다.");
            return;
        }

        List<CardReactiveConditionBase> conditions = TryGetFlatAllConditions(root);
        if (conditions == null)
        {
            Debug.LogWarning($"[CardDeckExporter] '{assetPath}' {card.CardId}: 복잡한 Reactive 조건 트리는 CSV 축약 export가 불완전합니다.");
            conditions = new List<CardReactiveConditionBase> { root };
        }

        for (int i = 0; i < conditions.Count; i++)
        {
            switch (conditions[i])
            {
                case ReactiveEventTypeCondition eventType:
                    row["reactiveEvent"] = eventType.EventType.ToString();
                    break;
                case ReactiveMinAmountCondition minAmount:
                    row["reactiveMinAmount"] = FormatInt(minAmount.MinAmount);
                    break;
                case ReactiveValueChangeCondition valueChange:
                    row["reactiveChange"] = valueChange.Change.ToString();
                    break;
                case ReactiveOwnerStateCondition ownerState:
                    row["reactiveOwnerState"] = ownerState.OwnerState.ToString();
                    break;
                case ReactiveTriggerLimitCondition limit:
                    row["reactiveLimit"] = FormatInt(GetPrivate(limit, "Limit", 0));
                    row["reactiveOncePerTurn"] = FormatBool(GetPrivate(limit, "OncePerTurn", false));
                    break;
                case ReactiveOwnerOnGridCondition:
                    break;
                default:
                    Debug.LogWarning($"[CardDeckExporter] '{assetPath}' {card.CardId}: {conditions[i].GetType().Name}은 CSV 축약 export 대상이 아닙니다.");
                    break;
            }
        }

        if (string.IsNullOrEmpty(row["reactiveEvent"]))
            row["reactiveEvent"] = EReactiveEventType.CardDrawn.ToString();
    }

    private static List<CardReactiveConditionBase> TryGetFlatAllConditions(CardReactiveConditionBase root)
    {
        if (root is ReactiveAllCondition all)
        {
            return all.Conditions != null
                ? new List<CardReactiveConditionBase>(all.Conditions.Where(condition => condition != null))
                : new List<CardReactiveConditionBase>();
        }

        return root is ReactiveEventTypeCondition ? new List<CardReactiveConditionBase> { root } : null;
    }

    private static Dictionary<Type, string> BuildEffectTypeKeyMap()
    {
        Dictionary<Type, string> result = new Dictionary<Type, string>();
        FieldInfo field = typeof(CardDeckBuilder).GetField(
            "EffectFactoryMap",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not System.Collections.IDictionary map)
            return result;

        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (entry.Key is not string key || entry.Value is not Delegate factory)
                continue;

            try
            {
                if (factory.DynamicInvoke() is CardEffectBase effect)
                    result[effect.GetType()] = key;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CardDeckExporter] effectType '{key}' factory 확인 실패: {ex.Message}");
            }
        }
        return result;
    }

    private static Dictionary<string, string> CreateEmptyRow(IEnumerable<string> headers)
    {
        Dictionary<string, string> row = new Dictionary<string, string>();
        foreach (string header in headers)
            row[header] = string.Empty;
        return row;
    }

    private static void WriteCsv(string path, List<string> headers, List<Dictionary<string, string>> rows)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        StringBuilder builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        for (int i = 0; i < rows.Count; i++)
        {
            Dictionary<string, string> row = rows[i];
            builder.AppendLine(string.Join(",", headers.Select(header =>
                EscapeCsv(row.TryGetValue(header, out string value) ? value : string.Empty))));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Excel이 12:30 같은 텍스트를 시간으로 바꾸지 않도록 접두사를 넣고 importer에서 제거한다.
        if (value.IndexOf(':') >= 0)
            value = "'" + value;

        bool needsQuote = value.IndexOfAny(new[] { ',', ':', '"', '\r', '\n' }) >= 0;
        string escaped = value.Replace("\"", "\"\"");
        return needsQuote ? $"\"{escaped}\"" : escaped;
    }

    private static T GetPrivate<T>(object instance, string fieldName, T fallback)
    {
        if (instance == null) return fallback;
        FieldInfo field = FindField(instance.GetType(), fieldName);
        if (field == null) return fallback;
        object value = field.GetValue(instance);
        return value is T typed ? typed : fallback;
    }

    private static FieldInfo FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static string FormatFlags<T>(T value) where T : Enum
    {
        long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (number == 0) return string.Empty;

        List<string> parts = new List<string>();
        foreach (T flag in Enum.GetValues(typeof(T)))
        {
            long flagNumber = Convert.ToInt64(flag, CultureInfo.InvariantCulture);
            if (flagNumber != 0 && (number & flagNumber) == flagNumber)
                parts.Add(flag.ToString());
        }
        return parts.Count > 0 ? string.Join(KeywordSeparator, parts) : value.ToString();
    }

    private static string FormatDirections(ECardDirection directions)
    {
        return directions == ECardDirection.None ? "None" : FormatFlags(directions);
    }

    private static string FormatPropagation(EChainPropagation propagation)
    {
        return propagation == EChainPropagation.BlockOnFirstCard ? "Normal" : propagation.ToString();
    }

    private static string FormatInt(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string FormatBool(bool value)
    {
        return value ? "1" : "0";
    }
}
#endif
