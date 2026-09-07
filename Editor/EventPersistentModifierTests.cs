#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class EventPersistentModifierTests
{
    [Test]
    public void EventCardChange_단일발광이미지가연결되어있다()
    {
        GameObject eventPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/00_Addressable/UI/UIChoiceEvent.prefab");
        Assert.IsNotNull(eventPrefab);
        UIChoiceEvent eventView = eventPrefab.GetComponent<UIChoiceEvent>();
        Assert.IsNotNull(eventView);
        var eventSerialized = new SerializedObject(eventView);
        Sprite flashSprite = eventSerialized
            .FindProperty("_cardChangeFlashSprite").objectReferenceValue as Sprite;
        Assert.IsNotNull(flashSprite);
        Assert.AreEqual(213f, flashSprite.rect.width);
        Assert.AreEqual(311f, flashSprite.rect.height);
    }

    [Test]
    public void SkillCooldown_기본값과런타임값은항상1이상이다()
    {
        SkillRuntimeData data = CreateSkillRuntimeData(cooldown: 0);
        SkillRuntime runtime = new SkillRuntime(data);

        Assert.AreEqual(SkillDataSO.CooldownMin, data.Cooldown);
        Assert.AreEqual(SkillDataSO.CooldownMin, runtime.MaxCooldown);

        runtime.Use();
        Assert.AreEqual(SkillDataSO.CooldownMin, runtime.RemainingCooldown);

        runtime.TickTurnEnd();
        Assert.AreEqual(0, runtime.RemainingCooldown);
    }

    [Test]
    public void SkillPrompt_스킬데이터의단계별안내키가런타임에복사된다()
    {
        SkillDataSO skill = ScriptableObject.CreateInstance<SkillDataSO>();
        try
        {
            SetPrivateField(skill, "_directionPromptKey", "UI_SKILL_DIRECTION_PROMPT");
            SetPrivateField(skill, "_targetPromptKey", "UI_SKILL_TARGET_PROMPT");

            SkillRuntimeData data = skill.ToRuntimeData();

            Assert.AreEqual("UI_SKILL_DIRECTION_PROMPT", data.DirectionPromptKey);
            Assert.AreEqual("UI_SKILL_TARGET_PROMPT", data.TargetPromptKey);
        }
        finally
        {
            Object.DestroyImmediate(skill);
        }
    }

    [Test]
    public void SkillCooldown_이벤트보정은런타임생성시정확히한번적용된다()
    {
        SkillDataSO skill = ScriptableObject.CreateInstance<SkillDataSO>();
        try
        {
            SetPrivateField(skill, "_skillId", "skill_test");
            SetPrivateField(skill, "_cooldown", 4);

            SkillRuntimeData first = skill.ToRuntimeData(-1);
            SkillRuntimeData second = skill.ToRuntimeData(-1);

            Assert.AreEqual(3, first.Cooldown);
            Assert.AreEqual(3, second.Cooldown);
            Assert.AreEqual(4, skill.Cooldown, "런타임 변환이 SO 기본값을 변경하면 보정이 중복 적용된다.");
        }
        finally
        {
            Object.DestroyImmediate(skill);
        }
    }

    [Test]
    public void SkillCooldown_외부보정과이벤트보정은합산후한번만클램프한다()
    {
        SkillDataSO skill = ScriptableObject.CreateInstance<SkillDataSO>();
        try
        {
            SetPrivateField(skill, "_cooldown", 3);
            int difficultyDelta = 2;
            int eventDelta = -1;

            Assert.AreEqual(4, skill.ResolveCooldown(difficultyDelta + eventDelta));
            Assert.AreEqual(
                SkillDataSO.CooldownMin,
                skill.ResolveCooldown(-100));
        }
        finally
        {
            Object.DestroyImmediate(skill);
        }
    }

    [Test]
    public void SkillCooldown_반복이벤트보정과저장복원후계산이일치한다()
    {
        RunContext source = new RunContext(100);
        source.SetSkillCooldownDelta("skill_test", -1);
        source.SetSkillCooldownDelta("skill_test", -2);

        RunSaveData save = source.ToSaveData(0);
        RunContext restored = new RunContext(100);
        restored.RestoreFromSave(save);

        Assert.AreEqual(9, save.version);
        Assert.AreEqual(-2, restored.GetSkillCooldownDelta("skill_test"));

        SkillDataSO skill = ScriptableObject.CreateInstance<SkillDataSO>();
        try
        {
            SetPrivateField(skill, "_skillId", "skill_test");
            SetPrivateField(skill, "_cooldown", 3);
            SkillRuntimeData data = skill.ToRuntimeData(
                restored.GetSkillCooldownDelta(skill.SkillId));

            Assert.AreEqual(1, data.Cooldown);
            SkillRuntime runtime = new SkillRuntime(data);
            runtime.Use();
            Assert.AreEqual(runtime.MaxCooldown, runtime.RemainingCooldown);
        }
        finally
        {
            Object.DestroyImmediate(skill);
        }
    }

    [Test]
    public void RunSaveData_버전7의누락된신규필드는빈상태로복원된다()
    {
        RunContext run = new RunContext(200);
        run.RecordEventChoice("event_old", 1);
        run.SetCardCostDelta(10, -1);
        run.SetSkillCooldownDelta("skill_old", -1);

        RunSaveData legacy = new RunSaveData
        {
            version = 7,
            maxHp = 10,
            currentHp = 10,
            eventChoices = null,
            cardCostDeltas = null,
            skillCooldownDeltas = null,
        };

        run.RestoreFromSave(legacy);

        Assert.IsFalse(run.TryGetEventChoice("event_old", out _));
        Assert.AreEqual(0, run.GetCardCostDelta(10));
        Assert.AreEqual(0, run.GetSkillCooldownDelta("skill_old"));
    }

    [Test]
    public void CardCost_영구보정은비용초기화후에도유지된다()
    {
        Card card = CardFactory.CreatePreview(CreateCardRuntimeData(cost: 3));

        card.ApplyPermanentCostDelta(-1);
        Assert.AreEqual(2, card.CurrentCost);

        card.ModifyCost(2);
        card.ResetCost();

        Assert.AreEqual(2, card.CurrentCost);
        Assert.AreEqual(-1, card.PermanentCostDelta);
    }

    [Test]
    public void EventCheckpoint_카드상세결과가저장복원된다()
    {
        RunContext source = new RunContext(300);
        source.BeginPendingEvent(12, "event_result_test");
        source.BeginEventChoiceProgress(12, "event_result_test", 0);

        source.SetEventEffectResult(0, ChoiceEventEffectResult.CardsTransformed(
            new[]
            {
                new ChoiceEventCardTransformResult(
                    "Fire_Bolt", ECardDirection.Up, "Card_Result", ECardDirection.Left),
            }));
        source.SetEventEffectResult(1, ChoiceEventEffectResult.CardCostChanged(
            new ChoiceEventCardCostResult(
                "Card_Cost", ECardDirection.Up, beforeCost: 3, afterCost: 2)));

        var arrowEntry = new RunDeckEntry(77, "Card_Arrow");
        arrowEntry.SetInitialArrowDirection(ECardDirection.Up | ECardDirection.Right);
        source.SetEventEffectResult(
            2,
            ChoiceEventEffectResult.ArrowAdded(arrowEntry, ECardDirection.Right));

        RunSaveData save = source.ToSaveData(0, currentNodeResolved: false);
        RunContext restored = new RunContext(300);
        // 이 단위 테스트는 맵을 생성하지 않으므로 위치 복원 반환값은 false다.
        // 이벤트 체크포인트 필드는 맵 위치 판정 전에 복원되므로 상세 결과만 검증한다.
        restored.RestoreFromSave(save);

        RunSaveEventEffectResult transform = restored.GetEventEffectResult(0);
        Assert.IsNotNull(transform);
        Assert.AreEqual(1, transform.cardTransforms.Count);
        Assert.AreEqual("Fire_Bolt", transform.cardTransforms[0].sourceCardId);
        Assert.AreEqual((int)ECardDirection.Up, transform.cardTransforms[0].sourceDirection);
        Assert.AreEqual("Card_Result", transform.cardTransforms[0].resultCardId);
        Assert.AreEqual((int)ECardDirection.Left, transform.cardTransforms[0].resultDirection);

        RunSaveEventEffectResult cost = restored.GetEventEffectResult(1);
        Assert.IsNotNull(cost);
        Assert.IsTrue(cost.hasCardCostChange);
        Assert.AreEqual("Card_Cost", cost.costCardId);
        Assert.AreEqual(3, cost.costBefore);
        Assert.AreEqual(2, cost.costAfter);

        RunSaveEventEffectResult arrow = restored.GetEventEffectResult(2);
        Assert.IsNotNull(arrow);
        Assert.IsTrue(arrow.hasArrowAddition);
        Assert.AreEqual(77, arrow.arrowTargetEntryId);
        Assert.AreEqual("Card_Arrow", arrow.arrowTargetCardId);
        Assert.AreEqual((int)ECardDirection.Right, arrow.addedArrowDirection);
    }

    [Test]
    public void EventCardChangeGrid_1장2장3장12장이영역안에배치된다()
    {
        MethodInfo method = typeof(UIChoiceEvent).GetMethod(
            "CalculateCardChangeGrid",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        (int columns, int rows, float scale) one = InvokeCardChangeGrid(method, 1);
        Assert.AreEqual(1, one.columns);
        Assert.AreEqual(1, one.rows);
        Assert.AreEqual(1f, one.scale);

        (int columns, int rows, float scale) two = InvokeCardChangeGrid(method, 2);
        Assert.AreEqual(2, two.columns);
        Assert.AreEqual(1, two.rows);
        Assert.AreEqual(1f, two.scale);

        (int columns, int rows, float scale) three = InvokeCardChangeGrid(method, 3);
        Assert.AreEqual(3, three.columns);
        Assert.AreEqual(1, three.rows);
        Assert.AreEqual(1f, three.scale);

        (int columns, int rows, float scale) twelve = InvokeCardChangeGrid(method, 12);
        Assert.Greater(twelve.rows, 1);
        Assert.LessOrEqual(twelve.scale, 1f);
        AssertCardChangeGridFits(twelve.columns, twelve.rows, twelve.scale);
    }

    private static (int columns, int rows, float scale) InvokeCardChangeGrid(
        MethodInfo method, int count)
    {
        object[] args = { count, new Vector2(760f, 450f), 18f, 0, 0 };
        float scale = (float)method.Invoke(null, args);
        return ((int)args[3], (int)args[4], scale);
    }

    private static void AssertCardChangeGridFits(int columns, int rows, float scale)
    {
        const float CardWidth = 213f;
        const float CardHeight = 311f;
        const float Spacing = 18f;
        float width = (columns * CardWidth + Mathf.Max(0, columns - 1) * Spacing) * scale;
        float height = (rows * CardHeight + Mathf.Max(0, rows - 1) * Spacing) * scale;
        Assert.LessOrEqual(width, 760.01f);
        Assert.LessOrEqual(height, 450.01f);
    }

    private static SkillRuntimeData CreateSkillRuntimeData(int cooldown)
    {
        return new SkillRuntimeData(
            0,
            "skill_test",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            System.Array.Empty<string>(),
            cooldown,
            System.Array.Empty<SkillEffectBase>());
    }

    private static CardRuntimeData CreateCardRuntimeData(int cost)
    {
        return new CardRuntimeData(
            "card_test",
            "card_test",
            "card_test",
            string.Empty,
            ECardType.Normal,
            ECardRarity.Common,
            ECardKeyword.None,
            cost,
            false,
            new List<CardEffectBase>(),
            new ArrowConfig(),
            1,
            EChainPropagation.BlockOnFirstCard,
            EChainPatternType.Line,
            EVfxType.None,
            EVfxTarget.Enemy,
            string.Empty,
            string.Empty);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"필드를 찾을 수 없습니다: {fieldName}");
        field.SetValue(target, value);
    }
}
#endif
