// =================================================================
// [스크립트 목적]  카드 CSV(효과당 1행) → cardId 그룹핑 → 효과 조립 → CardDataSO 생성
//                  뼈대: dev3 슬림 스키마(Arrow 4컬럼 / 강화행 제거 / RemovedHeaders 검증)
//                  내용: 전투 효과(Damage/Block/Heal/Mana 등) + VFX + EStatType + 키워드
// [주요 규칙]      - 같은 cardId 여러 행 = 다중 효과 누적
//                  - 강화(upgrade) 체계 폐지: 단일 cost/range, upgrade 효과 없음
//                  - Arrow 는 arrowMode/arrowCountWeights/arrowDirs/arrowDirWeights 4컬럼
// =================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class CardDeckBuilder
{
    // --- 메타 컬럼 ---
    private const string COL_KOREAN_NAME = "koreanName";
    private const string COL_CARD_ID = "cardId";
    private const string COL_NAME = "cardName";
    private const string COL_DESC = "descKey";
    private const string COL_ICON_KEY = "iconKey";
    private const string COL_TYPE = "type";
    private const string COL_DECK_TYPE = "deckType";
    private const string COL_POOLS = "pools";
    private const string COL_STAGES = "stages";
    private const string COL_RARITY = "rarity";
    private const string COL_KEYWORDS = "keywords";
    private const string COL_COST = "cost";
    private const string COL_UNLOCKED_BY_DEFAULT = "unlockedByDefault";

    // --- 효과 컬럼 (행마다 개별) ---
    private const string COL_EFFECT = "effectType";
    private const string COL_STAT_TYPE = "statType";
    private const string COL_AMOUNT = "amount";
    private const string COL_HIT_COUNT = "hitCount";
    private const string COL_INCLUDE_SELF = "includeSelf";
    private const string COL_OFF_PHASE = "offPhase";
    private const string COL_DAMAGE_PER_COUNT = "damagePerCount";
    private const string COL_STACK_CAP = "stackCap";
    private const string COL_TRIGGER_PHASE = "triggerPhase";
    private const string COL_DURATION = "duration";
    private const string COL_TARGET = "target";
    private const string COL_THRESHOLD = "threshold";
    private const string COL_PAYLOAD_PHASE = "payloadPhase";
    private const string COL_COUNT_SCOPE = "countScope";
    private const string COL_MODIFIER_TYPE = "modifierType";
    private const string COL_MODIFIER_VALUE = "modifierValue";
    private const string COL_ACTIVE_ONLY = "activeOnly";
    private const string COL_RESET_ON_DEACTIVATE = "resetOnDeactivate";
    private const string COL_SPAWN_CARD_ID = "spawnCardId";

    // --- 반응형(ReactiveSelfToggle) 컬럼 ---
    private const string COL_REACTIVE_EVENT = "reactiveEvent";
    private const string COL_REACTIVE_MIN_AMOUNT = "reactiveMinAmount";
    private const string COL_REACTIVE_CHANGE = "reactiveChange";
    private const string COL_REACTIVE_OWNER_STATE = "reactiveOwnerState";
    private const string COL_REACTIVE_LIMIT = "reactiveLimit";
    private const string COL_REACTIVE_ONCE_PER_TURN = "reactiveOncePerTurn";
    private const string COL_MAGIC_CIRCLE_BUFF = "magicCircleBuff";

    // --- 체인/패턴/VFX 컬럼 ---
    private const string COL_RANGE = "range";
    private const string COL_PROPAGATION = "propagation";
    private const string COL_PATTERN_TYPE = "patternType";
    private const string COL_VFX_TYPE = "vfxType";
    private const string COL_VFX_TARGET = "vfxTarget";
    private const string COL_PROJECTILE_KEY = "projectileKey";
    private const string COL_HIT_VFX_KEY = "hitVfxKey";

    // --- Arrow 4컬럼 (dev3 뼈대) ---
    private const string COL_ARROW_MODE = "arrowMode";
    private const string COL_ARROW_COUNT_WEIGHTS = "arrowCountWeights";
    private const string COL_ARROW_DIRS = "arrowDirs";
    private const string COL_ARROW_DIR_WEIGHTS = "arrowDirWeights";

    private const string KEYWORD_SEPARATOR = "|";

    // 개수 사다리: arrowCountWeights 콤마 값이 이 순서의 개수에 위치 매핑된다.
    private static readonly int[] ArrowCountLadder = { 1, 2, 4, 8 };

    private static readonly string[] RequiredHeaders =
    {
        COL_CARD_ID, COL_EFFECT,
    };

    // 구 스키마 잔재. 남아 있으면 빌드 중단(강화행/구 Arrow/육성 스탯 폐지).
    private static readonly string[] RemovedHeaders =
    {
        "isUpgrade", "upgradedCost", "upgradedRange",
        "arrowGroups", "arrowPickCount", "arrowPairGroups", "arrowWeights", "arrowRolls", "arrowFuncs",
        "stat", "countMode", "parity", "parityAxis", "extra",
    };

    private static readonly Dictionary<string, Func<CardEffectBase>> EffectFactoryMap =
        new Dictionary<string, Func<CardEffectBase>>(StringComparer.OrdinalIgnoreCase)
    {
        { "Damage", () => new DamageEffect() },
        { "Block", () => new BlockEffect() },
        { "Heal", () => new HealEffect() },
        { "DrawCard", () => new DrawCardEffect() },
        { "GainMana", () => new GainManaEffect() },
        { "Reserve", () => new ReserveEffect() },
        { "Toggle", () => new ToggleEffect() },
        { "AutoToggle", () => new AutoToggleEffect() },
        { "FirstPlacementMultiplier", () => new FirstPlacementMultiplierEffect() },
        { "GlassBladeDecay", () => new GlassBladeDecayEffect() },
        { "FinisherDamage", () => new FinisherDamageEffect() },
        { "DualStateTrigger", () => new DualStateTriggerEffect() },
        { "SelfOnTrigger", () => new SelfOnTriggerEffect() },
        { "ApplyBuff", () => new TotemAuraEffect() },
        { "CastingTrigger", () => new CastingEffect() },
        { "CastingPredate", () => new CastingPredateEffect() },
        { "GridScaledDamage", () => new GridScaledDamageEffect() },
        { "ActivationRamp", () => new ActivationRampEffect() },
        { "PeriodicTrigger", () => new PeriodicTriggerEffect() },
        { "DirectionalAbsorb", () => new DirectionalAbsorbEffect() },
        { "ForbiddenRitual", () => new ForbiddenRitualEffect() },
        { "RotateArrow", () => new RotateArrowEffect() },
        { "ReactiveSelfToggle", () => new ReactiveSelfToggleEffect() },
        { "SpawnOffspring", () => new SpawnOffspringEffect() },
        { "MagicCircle", () => new MagicCircleEffect() },
        { "BlockToDamage", () => new BlockToDamageEffect() },
        { "ManaBurst", () => new ManaBurstEffect() },
    };

    public static bool Review(string csvAssetPath)
    {
        if (!TryReadCsv(csvAssetPath, out List<List<string>> rows, out _, out Dictionary<string, int> col))
            return false;

        BuildReviewResult result = ReviewRows(rows, col);
        LogReviewResult(result, writeMode: false);
        return result.ErrorCount == 0;
    }

    public static int Build(string csvAssetPath, string outputFolder)
    {
        if (!TryReadCsv(csvAssetPath, out List<List<string>> rows, out _, out Dictionary<string, int> col))
            return 0;

        BuildReviewResult review = ReviewRows(rows, col);
        LogReviewResult(review, writeMode: true);
        if (review.ErrorCount > 0)
        {
            Debug.LogError("[CardDeckBuilder] 검토 오류가 있어 빌드를 중단합니다.");
            return 0;
        }

        Dictionary<string, CardAccum> accumMap = BuildAccumMap(rows, col);
        int count = 0;
        foreach (KeyValuePair<string, CardAccum> pair in accumMap)
        {
            if (WriteCardAsset(pair.Value, outputFolder))
                count++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CardDeckBuilder] {count}개 카드 처리 완료");
        return count;
    }

    private static bool TryReadCsv(
        string csvAssetPath,
        out List<List<string>> rows,
        out List<string> header,
        out Dictionary<string, int> col)
    {
        rows = null;
        header = null;
        col = null;

        if (!File.Exists(csvAssetPath))
        {
            Debug.LogError($"[CardDeckBuilder] CSV 없음: {csvAssetPath}");
            return false;
        }

        rows = CsvParser.Parse(File.ReadAllText(csvAssetPath));
        if (rows.Count < 2)
        {
            Debug.LogError("[CardDeckBuilder] 데이터 없음 (헤더+1행 이상 필요)");
            return false;
        }

        header = rows[0];
        col = new Dictionary<string, int>(header.Count, StringComparer.OrdinalIgnoreCase);
        HashSet<string> duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < header.Count; c++)
        {
            string name = header[c].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (col.ContainsKey(name))
            {
                duplicates.Add(name);
                continue;
            }
            col[name] = c;
        }

        if (duplicates.Count > 0)
        {
            Debug.LogError($"[CardDeckBuilder] 중복 컬럼 제거 필요: {string.Join(", ", duplicates)}");
            return false;
        }

        foreach (string required in RequiredHeaders)
        {
            if (!col.ContainsKey(required))
            {
                Debug.LogError($"[CardDeckBuilder] 필수 컬럼 누락: {required}");
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, CardAccum> BuildAccumMap(List<List<string>> rows, Dictionary<string, int> col)
    {
        Dictionary<string, CardAccum> accumMap = new Dictionary<string, CardAccum>();
        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            string cardId = GetCell(row, col, COL_CARD_ID);
            if (string.IsNullOrWhiteSpace(cardId))
                continue;

            if (!accumMap.TryGetValue(cardId, out CardAccum accum))
            {
                accum = new CardAccum { CardId = cardId };
                accumMap.Add(cardId, accum);
            }

            CardEffectBase effect = CreateEffect(GetCell(row, col, COL_EFFECT), row, col);
            if (effect == null)
                continue;

            accum.BaseEffects.Add(effect);
            accum.FillMetaIfEmpty(row, col);
        }

        return accumMap;
    }

    private static CardEffectBase CreateEffect(string effectType, List<string> row, Dictionary<string, int> col)
    {
        if (string.IsNullOrWhiteSpace(effectType))
            return null;

        if (!EffectFactoryMap.TryGetValue(effectType.Trim(), out Func<CardEffectBase> factory))
        {
            Debug.LogWarning($"[CardDeckBuilder] 미등록 effectType: '{effectType}'. EffectFactoryMap 에 추가 필요");
            return null;
        }

        CardEffectBase effect = factory();
        ETargetType target = ParseEnum(GetCell(row, col, COL_TARGET), ETargetType.SingleEnemy);
        ETriggerPhase phase = ParseEnum(GetCell(row, col, COL_TRIGGER_PHASE), ETriggerPhase.OnPlay);
        EEffectDuration duration = ParseEnum(GetCell(row, col, COL_DURATION), EEffectDuration.Instant);
        effect.ImportCommon(target, phase, duration);

        int amount = ParseInt(GetCell(row, col, COL_AMOUNT), 0);
        int secondaryValue = ResolveNumericSecondary(effect, row, col);
        effect.ImportNumeric(amount, secondaryValue);
        ApplyExtendedEffectImport(effect, row, col);

        return effect;
    }

    private static int ResolveNumericSecondary(CardEffectBase effect, List<string> row, Dictionary<string, int> col)
    {
        if (effect is DamageEffect)
            return ParseInt(GetCell(row, col, COL_HIT_COUNT), 1);

        if (effect is ReserveEffect)
            return ParseBool(GetCell(row, col, COL_INCLUDE_SELF), false) ? 1 : 0;

        if (effect is DualStateTriggerEffect)
            return (int)ParseEnum(GetCell(row, col, COL_OFF_PHASE), ETriggerPhase.BattleResolve);

        if (effect is GridScaledDamageEffect)
            return ParseInt(GetCell(row, col, COL_DAMAGE_PER_COUNT), 1);

        if (effect is ActivationRampEffect)
            return ParseInt(GetCell(row, col, COL_STACK_CAP), 0);

        return 0;
    }

    // 전투 효과별 확장 임포트 (EStatType 기반). 육성용 ECharacterStat 스탯은 사용하지 않는다.
    private static void ApplyExtendedEffectImport(CardEffectBase effect, List<string> row, Dictionary<string, int> col)
    {
        if (effect is CastingEffect castingEffect)
        {
            int threshold = Mathf.Max(1, ParseInt(
                GetCell(row, col, COL_THRESHOLD),
                ParseInt(GetCell(row, col, COL_AMOUNT), 3)));
            ETriggerPhase payloadPhase = ParseEnum(
                GetCell(row, col, COL_PAYLOAD_PHASE),
                ETriggerPhase.BattleAttack);
            castingEffect.EditorImportCasting(threshold, payloadPhase);
        }

        if (effect is TotemAuraEffect auraEffect)
        {
            // 오라 단일 계층 (수치 부호로 버프/디버프 표현: 양수=버프, 음수=디버프)
            ECardAuraScope scope = ParseAuraScope(GetCell(row, col, COL_TARGET), ECardAuraScope.Adjacent8);
            EStatType stat = ParseEnum(GetCell(row, col, COL_STAT_TYPE), EStatType.Damage);
            EStatModifierType mod = ParseEnum(GetCell(row, col, COL_MODIFIER_TYPE), EStatModifierType.Flat);
            float value = ParseFloat(GetCell(row, col, COL_MODIFIER_VALUE), ParseInt(GetCell(row, col, COL_AMOUNT), 0));
            bool activeOnly = ParseBool(GetCell(row, col, COL_ACTIVE_ONLY), true);

            auraEffect.EditorImportAura(scope, stat, mod, value, activeOnly);
        }

        if (effect is GridScaledDamageEffect scaledDamage)
        {
            EGridCountScope scope = ParseEnum(
                GetCell(row, col, COL_COUNT_SCOPE),
                EGridCountScope.GridTurnOnCount);
            scaledDamage.EditorImportScope(scope);
        }

        if (effect is ActivationRampEffect rampEffect)
        {
            rampEffect.EditorImportStat(
                ParseEnum(GetCell(row, col, COL_STAT_TYPE), EStatType.Damage));
        }

        if (effect is PeriodicTriggerEffect periodicTrigger)
        {
            int interval = Mathf.Max(1, ParseInt(
                GetCell(row, col, COL_THRESHOLD),
                ParseInt(GetCell(row, col, COL_AMOUNT), 3)));
            ETriggerPhase payloadPhase = ParseEnum(
                GetCell(row, col, COL_PAYLOAD_PHASE),
                ETriggerPhase.BattleAttack);
            periodicTrigger.EditorImportTrigger(interval, payloadPhase);
        }

        if (effect is AutoToggleEffect autoToggle)
        {
            int threshold = Mathf.Max(1, ParseInt(
                GetCell(row, col, COL_THRESHOLD),
                ParseInt(GetCell(row, col, COL_AMOUNT), 5)));
            autoToggle.EditorImportThreshold(threshold);
        }

        if (effect is FinisherDamageEffect finisher)
        {
            finisher.EditorImportScope(ParseEnum(
                GetCell(row, col, COL_COUNT_SCOPE),
                EGridCountScope.Total));
        }

        if (effect is DualStateTriggerEffect dualState)
        {
            ETriggerPhase onPhase = ParseEnum(
                GetCell(row, col, COL_PAYLOAD_PHASE),
                ETriggerPhase.BattleAttack);
            ETriggerPhase offPhase = ParseEnum(
                GetCell(row, col, COL_OFF_PHASE),
                ETriggerPhase.BattleResolve);
            dualState.EditorImportPhases(onPhase, offPhase);
        }

        if (effect is SelfOnTriggerEffect selfOn)
        {
            int threshold = Mathf.Max(1, ParseInt(
                GetCell(row, col, COL_THRESHOLD),
                ParseInt(GetCell(row, col, COL_AMOUNT), 3)));
            ETriggerPhase payloadPhase = ParseEnum(
                GetCell(row, col, COL_PAYLOAD_PHASE),
                ETriggerPhase.BattleAttack);
            bool resetEachTurn = ParseBool(
                GetCell(row, col, COL_RESET_ON_DEACTIVATE),
                false);
            selfOn.EditorImportTrigger(threshold, payloadPhase, resetEachTurn);
        }

        // 흡수: 공격/방어 타입 선택 (Damage=공격 흡수 / Block=방어 흡수)
        if (effect is DirectionalAbsorbEffect absorbEffect)
        {
            EStatType absorbStat = ParseEnum(GetCell(row, col, COL_STAT_TYPE), EStatType.Damage);
            absorbEffect.EditorImportAbsorb(absorbStat);
        }

        if (effect is SpawnOffspringEffect spawnEffect)
        {
            spawnEffect.EditorImportSpawn(GetCell(row, col, COL_SPAWN_CARD_ID));
        }

        if (effect is MagicCircleEffect magicCircle)
        {
            EMagicCircleBuffType buffType = ParseEnum(
                GetCell(row, col, COL_MAGIC_CIRCLE_BUFF),
                EMagicCircleBuffType.DamageBonus);
            int toggleCount = Mathf.Max(1, ParseInt(GetCell(row, col, COL_THRESHOLD), 1));
            int bonusAmount = Mathf.Max(0, ParseInt(GetCell(row, col, COL_AMOUNT), 0));
            magicCircle.EditorImportMagicCircle(buffType, toggleCount, bonusAmount);
        }

        if (effect is ReactiveSelfToggleEffect reactive)
        {
            EReactiveEventType eventType = ParseEnum(
                GetCell(row, col, COL_REACTIVE_EVENT),
                EReactiveEventType.CardDrawn);
            int minAmount = Mathf.Max(0, ParseInt(GetCell(row, col, COL_REACTIVE_MIN_AMOUNT), 0));
            EReactiveValueChange change = ParseEnum(
                GetCell(row, col, COL_REACTIVE_CHANGE),
                EReactiveValueChange.Any);
            EReactiveOwnerState ownerState = ParseEnum(
                GetCell(row, col, COL_REACTIVE_OWNER_STATE),
                EReactiveOwnerState.Any);
            int limit = Mathf.Max(0, ParseInt(GetCell(row, col, COL_REACTIVE_LIMIT), 0));
            bool oncePerTurn = ParseBool(GetCell(row, col, COL_REACTIVE_ONCE_PER_TURN), false);
            reactive.EditorImportSimple(eventType, minAmount, change, ownerState, limit, oncePerTurn);
        }
    }

    private static bool WriteCardAsset(CardAccum accum, string outputFolder)
    {
        // 빈칸·None 은 None 타입(어느 풀에도 미포함). Common 으로 강제하지 않는다.
        EStartingDeck deckType = ParseEnum(accum.MetaDeckType, EStartingDeck.None);

        string targetFolder = $"{outputFolder}/{deckType}";
        EnsureFolder(targetFolder);
        string assetPath = $"{targetFolder}/{accum.CardId}.asset";
        CardDataSO so = AssetDatabase.LoadAssetAtPath<CardDataSO>(assetPath);
        bool isNew = so == null;
        if (isNew)
        {
            so = ScriptableObject.CreateInstance<CardDataSO>();
            so.name = accum.CardId;
        }

        ECardType type = ParseEnum(accum.MetaType, ECardType.Normal);
        ECardRarity rarity = ParseEnum(accum.MetaRarity, ECardRarity.Common);
        ECardKeyword keywords = ParseKeywords(accum.MetaKeywords);
        ECardPool pools = ParseCardPools(accum.MetaPools);
        ECardStagePool stages = ParseCardStages(accum.MetaStages);

        // 강화 체계 폐지: upgrade 효과 없음(빈 리스트), cost/range 단일값.
        so.EditorImport(
            accum.CardId, accum.KoreanName, accum.MetaName, accum.MetaDesc, accum.IconKey,
            type, rarity, keywords,
            accum.MetaCost, accum.MetaCost,
            accum.BaseEffects, new List<CardEffectBase>());
        so.EditorImportArrow(accum.BaseArrow, new ArrowConfig(), false);
        so.EditorImportPattern(accum.PatternType);
        so.EditorImportChain(accum.Range, accum.Range, accum.Propagation);
        so.EditorImportVfx(accum.VfxType, accum.VfxTarget, accum.ProjectileKey, accum.HitVfxKey);
        so.EditorImportClassification(pools, accum.UnlockedByDefault, stages);
        so.EditorImportDeckType(deckType);

        if (isNew)
            AssetDatabase.CreateAsset(so, assetPath);
        else
            EditorUtility.SetDirty(so);

        return true;
    }

    private static BuildReviewResult ReviewRows(List<List<string>> rows, Dictionary<string, int> col)
    {
        BuildReviewResult result = new BuildReviewResult();
        Dictionary<string, int> baseEffectCountByCardId = new Dictionary<string, int>();
        Dictionary<string, string> firstDeckTypeByCardId = new Dictionary<string, string>();
        Dictionary<string, string> firstPoolsByCardId = new Dictionary<string, string>();

        foreach (string removed in RemovedHeaders)
        {
            if (col.ContainsKey(removed))
                result.Error($"제거된 컬럼이 남아 있습니다: {removed} (강화행/구 Arrow/육성 스탯 폐지)");
        }

        if (!col.ContainsKey(COL_DECK_TYPE))
            result.Warn("deckType 컬럼이 없습니다. 모든 카드는 None 으로 분류됩니다.");
        if (!col.ContainsKey(COL_POOLS))
            result.Warn("pools 컬럼이 없습니다. 모든 카드는 Normal 보상 풀로 분류됩니다.");

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            string cardId = GetCell(row, col, COL_CARD_ID);
            if (string.IsNullOrWhiteSpace(cardId)) continue;

            int line = r + 1;
            ValidateEnumCell<ECardType>(result, row, col, COL_TYPE, ECardType.Normal, line, cardId, required: false);
            ValidateEnumCell<ECardRarity>(result, row, col, COL_RARITY, ECardRarity.Common, line, cardId, required: false);
            ValidateEnumCell<EStartingDeck>(result, row, col, COL_DECK_TYPE, EStartingDeck.None, line, cardId, required: false);
            ValidateEnumCell<ETriggerPhase>(result, row, col, COL_TRIGGER_PHASE, ETriggerPhase.OnPlay, line, cardId, required: false);
            ValidateEnumCell<ETriggerPhase>(result, row, col, COL_PAYLOAD_PHASE, ETriggerPhase.BattleAttack, line, cardId, required: false);
            ValidateEnumCell<ETriggerPhase>(result, row, col, COL_OFF_PHASE, ETriggerPhase.BattleResolve, line, cardId, required: false);
            ValidateEnumCell<EEffectDuration>(result, row, col, COL_DURATION, EEffectDuration.Instant, line, cardId, required: false);
            ValidateEnumCell<EStatType>(result, row, col, COL_STAT_TYPE, EStatType.Damage, line, cardId, required: false);
            ValidateEnumCell<EStatModifierType>(result, row, col, COL_MODIFIER_TYPE, EStatModifierType.Flat, line, cardId, required: false);
            ValidateEnumCell<EChainPatternType>(result, row, col, COL_PATTERN_TYPE, EChainPatternType.Line, line, cardId, required: false);
            ValidateEnumCell<EArrowMode>(result, row, col, COL_ARROW_MODE, EArrowMode.Fixed, line, cardId, required: false);
            ValidateFlagCell<ECardKeyword>(result, row, col, COL_KEYWORDS, line, cardId);
            ValidateFlagCell<ECardPool>(result, row, col, COL_POOLS, line, cardId);
            ValidateFlagCell<ECardStagePool>(result, row, col, COL_STAGES, line, cardId);

            if (!TryParsePropagation(GetCell(row, col, COL_PROPAGATION), out _))
                result.Error($"L{line} '{cardId}': propagation 값을 읽지 못했습니다.");

            string effectType = GetCell(row, col, COL_EFFECT);
            if (string.IsNullOrWhiteSpace(effectType))
            {
                result.Warn($"L{line} '{cardId}': effectType 이 비어 있어 이 행은 import 에서 건너뜁니다.");
            }
            else if (!EffectFactoryMap.ContainsKey(effectType.Trim()))
            {
                result.Error($"L{line} '{cardId}': 미등록 effectType='{effectType}'. EffectFactoryMap 에 추가하거나 CSV를 수정하세요.");
            }
            else
            {
                baseEffectCountByCardId.TryGetValue(cardId, out int count);
                baseEffectCountByCardId[cardId] = count + 1;
            }

            string arrowError = ValidateArrow(
                GetCell(row, col, COL_ARROW_MODE),
                GetCell(row, col, COL_ARROW_COUNT_WEIGHTS),
                GetCell(row, col, COL_ARROW_DIRS),
                GetCell(row, col, COL_ARROW_DIR_WEIGHTS));
            if (!string.IsNullOrEmpty(arrowError))
                result.Error($"L{line} '{cardId}': {arrowError}");

            if (!firstDeckTypeByCardId.ContainsKey(cardId))
                firstDeckTypeByCardId[cardId] = GetCell(row, col, COL_DECK_TYPE);
            if (!firstPoolsByCardId.ContainsKey(cardId))
                firstPoolsByCardId[cardId] = GetCell(row, col, COL_POOLS);
        }

        foreach (KeyValuePair<string, string> pair in firstDeckTypeByCardId)
        {
            string cardId = pair.Key;
            EStartingDeck deckType = ParseEnum(pair.Value, EStartingDeck.None);
            if (deckType == EStartingDeck.None)
                result.Warn($"'{cardId}': deckType=None 이라 어느 런 카드 풀에도 포함되지 않습니다 (튜토리얼/특수 카드용).");

            firstPoolsByCardId.TryGetValue(cardId, out string poolsRaw);
            if (ParseCardPools(poolsRaw) == ECardPool.None)
                result.Warn($"'{cardId}': pools=None 이라 상점/보상 후보에 나오지 않습니다.");

            if (!baseEffectCountByCardId.TryGetValue(cardId, out int baseCount) || baseCount == 0)
                result.Error($"'{cardId}': 유효한 effect 행이 없습니다.");
        }

        return result;
    }

    private static string GetCell(List<string> row, Dictionary<string, int> col, string name)
    {
        string value = col.TryGetValue(name, out int idx) && idx < row.Count ? row[idx] : string.Empty;
        // CardDeckExporter가 Excel의 시간 자동 변환 방지용으로 넣은 접두사다.
        return value.Length > 1 && value[0] == '\'' && value.IndexOf(':') > 1 ? value.Substring(1) : value;
    }

    // ============================================================
    //  Arrow (dev3 뼈대): arrowMode/arrowCountWeights/arrowDirs/arrowDirWeights → ArrowConfig
    // ============================================================

    private static ArrowConfig BuildArrowConfig(
        string modeRaw, string countWeightsRaw, string dirsRaw, string dirWeightsRaw, string cardId)
    {
        ArrowConfig config = new ArrowConfig();
        EArrowMode mode = ParseEnum(modeRaw, EArrowMode.Fixed);

        if (mode == EArrowMode.Fixed)
        {
            // Fixed: arrowDirs 한 칸을 확정 방향(Flags)으로 합친다. 개수/가중치 컬럼 무시.
            config.EditorImport(EArrowMode.Fixed, ParseDirections(dirsRaw), new List<ArrowCandidate>(), 1);
            return config;
        }

        // Random: arrowDirWeights 가 차 있으면 가중치 랜덤(Weighted), 비어 있으면 균등 랜덤(Random).
        bool weighted = !string.IsNullOrWhiteSpace(dirWeightsRaw);
        List<ArrowCandidate> candidates = BuildCandidates(dirsRaw, dirWeightsRaw);
        List<ArrowCountWeight> countWeights = ParseCountWeights(countWeightsRaw);

        if (candidates.Count == 0)
            Debug.LogWarning($"[CardDeckBuilder] '{cardId}': arrowMode={mode} 이지만 화살표 후보가 없습니다. 무방향 카드로 생성됩니다.");

        config.EditorImport(
            weighted ? EArrowMode.Weighted : EArrowMode.Random,
            ECardDirection.None,
            candidates,
            1,
            countWeights);
        return config;
    }

    // Random/Weighted 의 arrowDirs 는 후보 목록이다. Fixed 와 달리 '|' 를 Flags 묶음으로 보지 않는다.
    // 예: "Up|Down|Left|Right" => 후보 4개. 가중치가 없으면 균등(1).
    private static List<ArrowCandidate> BuildCandidates(string dirsRaw, string dirWeightsRaw)
    {
        List<ArrowCandidate> result = new List<ArrowCandidate>();
        string[] dirs = SplitCandidateTokens(dirsRaw);
        string[] weights = SplitCandidateTokens(dirWeightsRaw);

        for (int i = 0; i < dirs.Length; i++)
        {
            ECardDirection direction = ParseSingleDirection(dirs[i]);
            float weight = i < weights.Length ? ParseFloat(weights[i], 1f) : 1f;
            if (direction == ECardDirection.None || weight <= 0f)
                continue;
            result.Add(new ArrowCandidate { direction = direction, weight = weight });
        }
        return result;
    }

    // arrowCountWeights 콤마 값을 개수 사다리[1,2,4,8] 위치에 매핑한다. 0/빈칸은 제외.
    private static List<ArrowCountWeight> ParseCountWeights(string raw)
    {
        List<ArrowCountWeight> result = new List<ArrowCountWeight>();
        string[] parts = SplitChips(raw);

        int n = Mathf.Min(parts.Length, ArrowCountLadder.Length);
        for (int i = 0; i < n; i++)
        {
            float weight = ParseFloat(parts[i], 0f);
            if (weight > 0f)
                result.Add(new ArrowCountWeight { count = ArrowCountLadder[i], weight = weight });
        }
        return result;
    }

    // 콤마 칩 분리. 위치 매칭 유지를 위해 빈 칸을 제거하지 않고, 각 칩만 trim 한다.
    private static string[] SplitChips(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        string[] parts = raw.Split(',');
        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim();
        return parts;
    }

    private static string[] SplitCandidateTokens(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw.Split(new[] { '|', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
    }

    private static string ValidateArrow(string modeRaw, string countWeightsRaw, string dirsRaw, string dirWeightsRaw)
    {
        EArrowMode mode = ParseEnum(modeRaw, EArrowMode.Fixed);
        if (mode == EArrowMode.Fixed)
            return ParseDirections(dirsRaw) == ECardDirection.None &&
                   !string.IsNullOrWhiteSpace(dirsRaw) &&
                   !dirsRaw.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)
                ? $"arrowDirs 방향 값을 읽지 못했습니다: {dirsRaw}"
                : null;

        if (mode != EArrowMode.Random)
            return "arrowMode 는 Fixed 또는 Random 만 사용하세요.";

        string invalidCandidates = ValidateCandidateDirections(dirsRaw);
        if (!string.IsNullOrEmpty(invalidCandidates))
            return $"Random: arrowDirs 후보 방향 값을 읽지 못했습니다: {invalidCandidates}";

        // 개수1 은 arrowDirs 후보 풀이 필요하다. (개수 2/4/8 은 고정 형태라 후보 불필요)
        List<ArrowCountWeight> countWeights = ParseCountWeights(countWeightsRaw);
        bool count1Possible = countWeights.Count == 0 || HasCount(countWeights, 1);
        if (count1Possible && BuildCandidates(dirsRaw, dirWeightsRaw).Count == 0)
            return "Random: 개수1 후보(arrowDirs)가 비어 있습니다.";

        return null;
    }

    private static bool HasCount(List<ArrowCountWeight> countWeights, int count)
    {
        for (int i = 0; i < countWeights.Count; i++)
        {
            if (countWeights[i].count == count)
                return true;
        }
        return false;
    }

    private static string ValidateCandidateDirections(string raw)
    {
        string[] tokens = SplitCandidateTokens(raw);
        List<string> invalid = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token.Equals("None", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ParseSingleDirection(token) == ECardDirection.None)
            {
                invalid ??= new List<string>();
                invalid.Add(token);
            }
        }
        return invalid != null ? string.Join("|", invalid) : null;
    }

    private static ECardDirection ParseDirections(string raw)
    {
        ECardDirection dirs = ECardDirection.None;
        if (string.IsNullOrWhiteSpace(raw))
            return dirs;

        string[] parts = raw.Split(new[] { '|', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (Enum.TryParse(parts[i].Trim(), ignoreCase: true, out ECardDirection dir))
                dirs |= dir;
        }
        return dirs;
    }

    private static ECardDirection ParseSingleDirection(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ECardDirection.None;

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out ECardDirection dir)
            ? dir
            : ECardDirection.None;
    }

    // ============================================================
    //  파싱 유틸
    // ============================================================

    private static EChainPropagation ParsePropagation(string raw)
    {
        return TryParsePropagation(raw, out EChainPropagation value) ? value : EChainPropagation.BlockOnFirstCard;
    }

    private static bool TryParsePropagation(string raw, out EChainPropagation value)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Trim().Equals("Normal", StringComparison.OrdinalIgnoreCase))
        {
            value = EChainPropagation.BlockOnFirstCard;
            return true;
        }

        if (Enum.TryParse(raw.Trim(), ignoreCase: true, out value))
            return true;

        value = EChainPropagation.BlockOnFirstCard;
        return false;
    }

    private static ECardKeyword ParseKeywords(string raw)
    {
        ECardKeyword result = ECardKeyword.None;
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        string[] parts = raw.Split(new[] { KEYWORD_SEPARATOR, ",", "/" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (Enum.TryParse(parts[i].Trim(), ignoreCase: true, out ECardKeyword kw))
                result |= kw;
        }
        return result;
    }

    private static ECardPool ParseCardPools(string raw)
    {
        ECardPool result = ECardPool.Normal;
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        result = ECardPool.None;
        string[] parts = raw.Split(new[] { KEYWORD_SEPARATOR, ",", "/" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (Enum.TryParse(parts[i].Trim(), ignoreCase: true, out ECardPool pool))
                result |= pool;
        }
        return result;
    }

    private static ECardStagePool ParseCardStages(string raw)
    {
        ECardStagePool result = ECardStagePool.All;
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        result = ECardStagePool.None;
        string[] parts = raw.Split(new[] { KEYWORD_SEPARATOR, ",", "/" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (Enum.TryParse(parts[i].Trim(), ignoreCase: true, out ECardStagePool stage))
                result |= stage;
        }
        return result;
    }

    private static ECardAuraScope ParseAuraScope(string raw, ECardAuraScope fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Trim().ToLowerInvariant() switch
        {
            "card" => ECardAuraScope.Adjacent8,
            "cross" => ECardAuraScope.Cross,
            "row" => ECardAuraScope.Row,
            "column" => ECardAuraScope.Column,
            "all" => ECardAuraScope.All,
            _ => ParseEnum(raw, fallback),
        };
    }

    private static int ParseInt(string raw, int fallback)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    private static float ParseFloat(string raw, float fallback)
    {
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    private static bool ParseBool(string raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (bool.TryParse(raw, out bool value))
            return value;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return number != 0;
        return fallback;
    }

    private static T ParseEnum<T>(string raw, T fallback) where T : struct, Enum
    {
        return !string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), ignoreCase: true, out T value)
            ? value
            : fallback;
    }

    private static void ValidateEnumCell<T>(
        BuildReviewResult result,
        List<string> row,
        Dictionary<string, int> col,
        string columnName,
        T fallback,
        int line,
        string cardId,
        bool required)
        where T : struct, Enum
    {
        string raw = GetCell(row, col, columnName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) result.Error($"L{line} '{cardId}': {columnName} 값이 비어 있습니다.");
            return;
        }

        if (!Enum.TryParse(raw.Trim(), ignoreCase: true, out T _))
            result.Error($"L{line} '{cardId}': {columnName}='{raw}' 는 {typeof(T).Name} 값이 아닙니다. 기본값 {fallback} 대신 CSV를 수정하세요.");
    }

    private static void ValidateFlagCell<T>(
        BuildReviewResult result,
        List<string> row,
        Dictionary<string, int> col,
        string columnName,
        int line,
        string cardId)
        where T : struct, Enum
    {
        string raw = GetCell(row, col, columnName);
        if (string.IsNullOrWhiteSpace(raw)) return;

        string[] parts = raw.Split(new[] { KEYWORD_SEPARATOR, ",", "/" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (!Enum.TryParse(token, ignoreCase: true, out T _))
                result.Error($"L{line} '{cardId}': {columnName} 항목 '{token}' 은 {typeof(T).Name} 값이 아닙니다.");
        }
    }

    private static void LogReviewResult(BuildReviewResult result, bool writeMode)
    {
        string mode = writeMode ? "빌드 전 검토" : "검토";
        for (int i = 0; i < result.Errors.Count; i++)
            Debug.LogError($"[CardDeckBuilder] {result.Errors[i]}");
        for (int i = 0; i < result.Warnings.Count; i++)
            Debug.LogWarning($"[CardDeckBuilder] {result.Warnings[i]}");
        Debug.Log($"[CardDeckBuilder] {mode} 완료 — error={result.ErrorCount}, warning={result.WarningCount}");
    }

    private static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath)) return;

        string[] parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private sealed class CardAccum
    {
        public string CardId;
        public readonly List<CardEffectBase> BaseEffects = new List<CardEffectBase>();

        public string KoreanName = string.Empty;
        public string MetaName = string.Empty;
        public string MetaDesc = string.Empty;
        public string IconKey = string.Empty;
        public string MetaType = string.Empty;
        public string MetaRarity = string.Empty;
        public string MetaKeywords = string.Empty;
        public string MetaDeckType = string.Empty;
        public string MetaPools = string.Empty;
        public string MetaStages = string.Empty;
        public bool UnlockedByDefault = true;
        public int MetaCost = 1;
        public int Range = 1;
        public EChainPropagation Propagation = EChainPropagation.BlockOnFirstCard;
        public ArrowConfig BaseArrow = new ArrowConfig();
        public EChainPatternType PatternType = EChainPatternType.Line;
        public EVfxType VfxType = EVfxType.None;
        public EVfxTarget VfxTarget = EVfxTarget.Enemy;
        public string ProjectileKey = string.Empty;
        public string HitVfxKey = string.Empty;
        private bool _metaFilled;

        public void FillMetaIfEmpty(List<string> row, Dictionary<string, int> col)
        {
            if (_metaFilled)
                return;

            KoreanName = GetCell(row, col, COL_KOREAN_NAME);
            MetaName = GetCell(row, col, COL_NAME);
            MetaDesc = GetCell(row, col, COL_DESC);
            IconKey = GetCell(row, col, COL_ICON_KEY);
            MetaType = GetCell(row, col, COL_TYPE);
            MetaRarity = GetCell(row, col, COL_RARITY);
            MetaKeywords = GetCell(row, col, COL_KEYWORDS);
            MetaDeckType = GetCell(row, col, COL_DECK_TYPE);
            MetaPools = GetCell(row, col, COL_POOLS);
            MetaStages = GetCell(row, col, COL_STAGES);
            UnlockedByDefault = ParseBool(GetCell(row, col, COL_UNLOCKED_BY_DEFAULT), true);
            MetaCost = ParseInt(GetCell(row, col, COL_COST), 1);
            BaseArrow = BuildArrowConfig(
                GetCell(row, col, COL_ARROW_MODE),
                GetCell(row, col, COL_ARROW_COUNT_WEIGHTS),
                GetCell(row, col, COL_ARROW_DIRS),
                GetCell(row, col, COL_ARROW_DIR_WEIGHTS),
                CardId);
            Range = Mathf.Max(1, ParseInt(GetCell(row, col, COL_RANGE), 1));
            Propagation = ParsePropagation(GetCell(row, col, COL_PROPAGATION));
            PatternType = ParseEnum(GetCell(row, col, COL_PATTERN_TYPE), EChainPatternType.Line);
            VfxType = ParseEnum(GetCell(row, col, COL_VFX_TYPE), EVfxType.None);
            VfxTarget = ParseEnum(GetCell(row, col, COL_VFX_TARGET), EVfxTarget.Enemy);
            ProjectileKey = GetCell(row, col, COL_PROJECTILE_KEY);
            HitVfxKey = GetCell(row, col, COL_HIT_VFX_KEY);
            _metaFilled = true;
        }
    }

    private sealed class BuildReviewResult
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int ErrorCount => Errors.Count;
        public int WarningCount => Warnings.Count;
        public void Error(string message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
        public void Warn(string message) { if (!string.IsNullOrEmpty(message)) Warnings.Add(message); }
    }
}
#endif
