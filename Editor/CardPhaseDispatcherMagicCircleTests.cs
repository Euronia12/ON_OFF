#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

public sealed class CardPhaseDispatcherMagicCircleTests
{
    [Test]
    public async Task DamageBonus_비피해카드만별도피해를준다()
    {
        TestCardGameService service = new TestCardGameService();
        PlayerActor player = new PlayerActor();
        player.Setup(100);

        TestCombatActor drawEnemy = new TestCombatActor();
        CardPhaseDispatcher drawDispatcher = CreateDispatcher(player, drawEnemy, service);
        drawDispatcher.RegisterMagicCircleBuff(CreateCard("MagicCircle"), EMagicCircleBuffType.DamageBonus, 1, 3);
        await drawDispatcher.DispatchActivationAsync(
            CreateCard("Draw", new DrawCardEffect()), player, null, CancellationToken.None);

        Assert.AreEqual(1, service.DrawCount);
        Assert.AreEqual(3, drawEnemy.DamageTaken);

        DamageEffect damageEffect = new DamageEffect();
        SetPrivateField(damageEffect, "_amount", 5);
        TestCombatActor damageEnemy = new TestCombatActor();
        CardPhaseDispatcher damageDispatcher = CreateDispatcher(player, damageEnemy, service);
        damageDispatcher.RegisterMagicCircleBuff(CreateCard("MagicCircle"), EMagicCircleBuffType.DamageBonus, 1, 3);
        await damageDispatcher.DispatchActivationAsync(
            CreateCard("Damage", damageEffect), player, null, CancellationToken.None);

        Assert.AreEqual(8, damageEnemy.DamageTaken);

        player.GainBlock(10);
        TestCombatActor blockEnemy = new TestCombatActor();
        CardPhaseDispatcher blockDispatcher = CreateDispatcher(player, blockEnemy, service);
        blockDispatcher.RegisterMagicCircleBuff(CreateCard("MagicCircle"), EMagicCircleBuffType.DamageBonus, 1, 3);
        await blockDispatcher.DispatchActivationAsync(
            CreateCard("BlockToDamage", new BlockToDamageEffect()), player, null, CancellationToken.None);

        Assert.AreEqual(8, blockEnemy.DamageTaken);

        player.SetupEnergy(2);
        TestCombatActor manaEnemy = new TestCombatActor();
        CardPhaseDispatcher manaDispatcher = CreateDispatcher(player, manaEnemy, service);
        manaDispatcher.RegisterMagicCircleBuff(CreateCard("MagicCircle"), EMagicCircleBuffType.DamageBonus, 1, 3);
        await manaDispatcher.DispatchActivationAsync(
            CreateCard("ManaBurst", new ManaBurstEffect()), player, null, CancellationToken.None);

        Assert.AreEqual(8, manaEnemy.DamageTaken);

        TestCombatActor deadEnemy = new TestCombatActor { IsAlive = false };
        CardPhaseDispatcher deadTargetDispatcher = CreateDispatcher(player, deadEnemy, service);
        deadTargetDispatcher.RegisterMagicCircleBuff(CreateCard("MagicCircle"), EMagicCircleBuffType.DamageBonus, 1, 3);
        await deadTargetDispatcher.DispatchActivationAsync(
            CreateCard("DrawDeadTarget", new DrawCardEffect()), player, null, CancellationToken.None);

        Assert.AreEqual(0, deadEnemy.DamageTaken);
        Assert.AreEqual(0, deadTargetDispatcher.ActiveBuffViews.Count);
    }

    private static CardPhaseDispatcher CreateDispatcher(
        ICombatActor player,
        ICombatActor enemy,
        ICardGameService service)
    {
        return new CardPhaseDispatcher(
            new TargetResolver(player, enemy),
            service,
            new CardStatCalculator(null),
            null);
    }

    private static Card CreateCard(string id, CardEffectBase effect = null)
    {
        List<CardEffectBase> effects = effect != null
            ? new List<CardEffectBase> { effect }
            : new List<CardEffectBase>();
        ArrowConfig arrow = new ArrowConfig();
        CardRuntimeData data = new CardRuntimeData(
            id,
            id,
            id,
            string.Empty,
            ECardType.Normal,
            ECardRarity.Common,
            ECardKeyword.None,
            0,
            false,
            effects,
            arrow,
            1,
            EChainPropagation.BlockOnFirstCard,
            EChainPatternType.Line,
            EVfxType.None,
            EVfxTarget.Enemy,
            string.Empty,
            string.Empty);
        return new Card(data, effects, new CardStats(), new CardArrowState(arrow), false);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(target, value);
    }

    private sealed class TestCombatActor : ICombatActor
    {
        public bool IsAlive { get; set; } = true;
        public int DamageTaken { get; private set; }

        public void TakeDamage(int amount) => DamageTaken += amount;
        public void GainBlock(int amount) { }
        public void Heal(int amount) { }
    }

    private sealed class TestCardGameService : ICardGameService
    {
        public int DrawCount { get; private set; }

        public void DrawCards(int count, Card source = null) => DrawCount += count;
        public void AddCardToHand(Card card) { }
        public void GainEnergy(int amount) { }
        public void ExhaustCard(Card card) { }
        public void DrawInitialHand(int count) { }
    }
}

public sealed class ReactiveToggleQueueTests
{
    [Test]
    public async Task FlushAsync_Reaches100ActivationsBeforeClearingRemainingRequests()
    {
        GameObject gridObject = new GameObject("ReactiveToggleQueueTestGrid");
        try
        {
            GridManager grid = gridObject.AddComponent<GridManager>();
            Card card = CreateCard("ReactiveToggle");
            AddPlacedCard(grid, card, Vector2Int.zero);

            ChainExecutor chain = new ChainExecutor(
                grid,
                null,
                new NullChainPresenter(),
                null,
                chainLoopActivationLimit: 100);
            int milestoneCount = 0;
            chain.OnCardActivatedInChainAsync += (_, count, _) =>
            {
                if (count == 100)
                {
                    milestoneCount++;
                }
                return UniTask.CompletedTask;
            };

            ReactiveToggleQueue queue = new ReactiveToggleQueue();
            queue.Bind(chain, grid);
            for (int i = 0; i < 201; i++)
            {
                queue.Enqueue(card);
            }

            await queue.FlushAsync(CancellationToken.None);

            Assert.AreEqual(100, chain.ChainActivatedCount);
            Assert.IsTrue(chain.HasReachedActivationLimit);
            Assert.AreEqual(1, milestoneCount);
            Assert.IsTrue(card.GridState.IsActivated);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gridObject);
        }
    }

    [Test]
    public async Task FinalWave_TogglesAllCardsButCapsDisplayedChainAt100()
    {
        GameObject gridObject = new GameObject("ChainFinalWaveTestGrid");
        try
        {
            GridManager grid = gridObject.AddComponent<GridManager>();
            Card first = CreateCard("First");
            Card second = CreateCard("Second");
            AddPlacedCard(grid, first, Vector2Int.zero);
            AddPlacedCard(grid, second, Vector2Int.right);

            RecordingChainPresenter presenter = new RecordingChainPresenter();
            ChainExecutor chain = new ChainExecutor(
                grid,
                null,
                presenter,
                null,
                chainLoopActivationLimit: 100);
            SetPrivateField(chain, "_chainActivatedCount", 99);
            SetPrivateField(chain, "_stepSafetyLimit", int.MaxValue);

            int milestoneCount = 0;
            chain.OnCardActivatedInChainAsync += (_, count, _) =>
            {
                if (count == 100)
                {
                    milestoneCount++;
                }
                return UniTask.CompletedTask;
            };

            await InvokeWaveAsync(chain, first, second);

            Assert.IsTrue(first.GridState.IsActivated);
            Assert.IsTrue(second.GridState.IsActivated);
            Assert.AreEqual(100, chain.ChainActivatedCount);
            Assert.AreEqual(1, presenter.ActivatedToggleCount);
            Assert.AreEqual(1, milestoneCount);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gridObject);
        }
    }

    private static async UniTask InvokeWaveAsync(ChainExecutor chain, Card first, Card second)
    {
        Type signalType = typeof(ChainExecutor).GetNestedType("ChainSignal", BindingFlags.NonPublic);
        Assert.IsNotNull(signalType);

        ConstructorInfo signalConstructor = signalType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(Card), typeof(int) },
            null);
        Assert.IsNotNull(signalConstructor);

        Type signalListType = typeof(List<>).MakeGenericType(signalType);
        IList signals = (IList)Activator.CreateInstance(signalListType);
        signals.Add(signalConstructor.Invoke(new object[] { first, 1 }));
        signals.Add(signalConstructor.Invoke(new object[] { second, 1 }));

        MethodInfo activateWave = typeof(ChainExecutor).GetMethod(
            "ActivateChainAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { signalListType, typeof(CancellationToken) },
            null);
        Assert.IsNotNull(activateWave);

        await (UniTask)activateWave.Invoke(chain, new object[] { signals, CancellationToken.None });
    }

    private static void AddPlacedCard(GridManager grid, Card card, Vector2Int position)
    {
        FieldInfo positionsField = typeof(GridManager).GetField(
            "_cardPositions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var positions = (Dictionary<Card, Vector2Int>)positionsField?.GetValue(grid);
        Assert.IsNotNull(positions);
        positions.Add(card, position);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(target, value);
    }

    private static Card CreateCard(string id)
    {
        List<CardEffectBase> effects = new List<CardEffectBase>();
        ArrowConfig arrow = new ArrowConfig();
        CardRuntimeData data = new CardRuntimeData(
            id,
            id,
            id,
            string.Empty,
            ECardType.Normal,
            ECardRarity.Common,
            ECardKeyword.None,
            0,
            false,
            effects,
            arrow,
            1,
            EChainPropagation.BlockOnFirstCard,
            EChainPatternType.Line,
            EVfxType.None,
            EVfxTarget.Self,
            string.Empty,
            string.Empty);
        return new Card(data, effects, new CardStats(), new CardArrowState(arrow), false);
    }

    private sealed class RecordingChainPresenter : IChainPresenter
    {
        public int ActivatedToggleCount { get; private set; }

        public UniTask PlayActivationFeedbackAsync(IReadOnlyList<Card> wave, CancellationToken token)
            => UniTask.CompletedTask;

        public void ScheduleActivationVfx(IReadOnlyList<Card> wave, CancellationToken token) { }
        public void SetCardVisualSpeed(Card card, float visualSpeedMultiplier) { }
        public void ClearCardVisualSpeeds() { }

        public void OnCardToggled(Card card, bool activated)
        {
            if (activated)
            {
                ActivatedToggleCount++;
            }
        }

        public void OnSignalPropagated(
            Card emitter,
            Vector3 from,
            Vector3 to,
            Card reachedCard,
            float visualSpeedMultiplier) { }

        public void OnInfiniteLoopDetected(Card rootCard) { }
    }
}
#endif
