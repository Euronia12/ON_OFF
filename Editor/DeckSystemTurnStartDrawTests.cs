#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using NUnit.Framework;

public sealed class DeckSystemTurnStartDrawTests
{
    [TestCase(49, 1)]
    [TestCase(50, 2)]
    [TestCase(79, 2)]
    [TestCase(80, 3)]
    [TestCase(98, 3)]
    [TestCase(99, 4)]
    public void DrawTurnStartCards_확률경계에맞춰방어카드를우선드로우한다(
        int roll,
        int expectedBlockCount)
    {
        FixedRollRandom rng = new FixedRollRandom(roll);
        DeckSystem deck = CreateDeck(blockCount: 4, otherCount: 7, rng);

        rng.ResetRollCallCount();
        deck.DrawTurnStartCards(7);

        Assert.AreEqual(7, deck.HandCount);
        Assert.AreEqual(expectedBlockCount, CountBlockCards(deck.Hand));
        Assert.AreEqual(1, rng.RollCallCount);
    }

    [Test]
    public void DrawTurnStartCards_방어카드가없으면확률추첨을건너뛴다()
    {
        FixedRollRandom rng = new FixedRollRandom(99);
        DeckSystem deck = CreateDeck(blockCount: 0, otherCount: 7, rng);

        rng.ResetRollCallCount();
        deck.DrawTurnStartCards(7);

        Assert.AreEqual(7, deck.HandCount);
        Assert.AreEqual(0, CountBlockCards(deck.Hand));
        Assert.AreEqual(0, rng.RollCallCount);
    }

    [Test]
    public void DrawTurnStartCards_A세트가부족하면B세트로채운다()
    {
        DeckSystem deck = CreateDeck(
            blockCount: 1,
            otherCount: 6,
            new FixedRollRandom(99));

        deck.DrawTurnStartCards(7);

        Assert.AreEqual(7, deck.HandCount);
        Assert.AreEqual(1, CountBlockCards(deck.Hand));
    }

    [Test]
    public void DrawTurnStartCards_B세트가부족하면남은A세트로채운다()
    {
        DeckSystem deck = CreateDeck(
            blockCount: 6,
            otherCount: 1,
            new FixedRollRandom(69));

        deck.DrawTurnStartCards(7);

        Assert.AreEqual(7, deck.HandCount);
        Assert.AreEqual(6, CountBlockCards(deck.Hand));
    }

    [Test]
    public void DrawTurnStartCards_요청장수가적으면그장수까지만방어카드를뽑는다()
    {
        DeckSystem deck = CreateDeck(
            blockCount: 4,
            otherCount: 4,
            new FixedRollRandom(99));

        deck.DrawTurnStartCards(2);

        Assert.AreEqual(2, deck.HandCount);
        Assert.AreEqual(2, CountBlockCards(deck.Hand));
    }

    [Test]
    public void DrawTurnStartCards_드로우더미가비었으면묘지를재셔플한뒤분류한다()
    {
        FixedRollRandom rng = new FixedRollRandom(99);
        DeckSystem deck = CreateDeck(blockCount: 4, otherCount: 3, rng);
        deck.DrawCards(7);
        deck.DiscardHand();

        rng.ResetRollCallCount();
        deck.DrawTurnStartCards(7);

        Assert.AreEqual(7, deck.HandCount);
        Assert.AreEqual(4, CountBlockCards(deck.Hand));
        Assert.AreEqual(1, rng.RollCallCount);
    }

    [Test]
    public void DrawCards_기존더미순서대로드로우한다()
    {
        FixedRollRandom rng = new FixedRollRandom(99);
        DeckSystem deck = CreateDeck(blockCount: 4, otherCount: 4, rng);
        Card expected = deck.DrawPile[deck.DrawPile.Count - 1];

        rng.ResetRollCallCount();
        deck.DrawCards(1);

        Assert.AreSame(expected, deck.Hand[0]);
        Assert.AreEqual(0, rng.RollCallCount);
    }

    [Test]
    public void CreateShuffleSeed_같은전투위치는같은비영시드를만든다()
    {
        int first = BattleStartContext.CreateShuffleSeed(123456, 1, 17);
        int second = BattleStartContext.CreateShuffleSeed(123456, 1, 17);

        Assert.AreNotEqual(0, first);
        Assert.AreEqual(first, second);
    }

    [Test]
    public void CreateShuffleSeed_스테이지나노드가다르면다른시드를만든다()
    {
        int origin = BattleStartContext.CreateShuffleSeed(123456, 1, 17);
        int otherStage = BattleStartContext.CreateShuffleSeed(123456, 2, 17);
        int otherNode = BattleStartContext.CreateShuffleSeed(123456, 1, 18);

        Assert.AreNotEqual(origin, otherStage);
        Assert.AreNotEqual(origin, otherNode);
        Assert.AreNotEqual(otherStage, otherNode);
    }

    [Test]
    public void ResetRandom_이전난수소비와무관하게초기카드흐름을재현한다()
    {
        const int seed = 987654;
        DeckSystem deck = new DeckSystem();

        deck.ResetRandom(seed);
        deck.InitializeFromCards(CreateUniqueCards(12));
        deck.DrawTurnStartCards(7);
        List<int> expectedHand = GetEntryIds(deck.Hand);
        List<int> expectedDrawPile = GetEntryIds(deck.DrawPile);

        // 첫 전투에서 난수를 더 소비한 뒤 같은 전투를 재시작한 상황을 재현한다.
        deck.DiscardHand();
        deck.DrawTurnStartCards(5);

        deck.ResetRandom(seed);
        deck.InitializeFromCards(CreateUniqueCards(12));
        deck.DrawTurnStartCards(7);

        CollectionAssert.AreEqual(expectedHand, GetEntryIds(deck.Hand));
        CollectionAssert.AreEqual(expectedDrawPile, GetEntryIds(deck.DrawPile));
    }

    [Test]
    public void ResetRandom_같은플레이면후속재셔플까지재현한다()
    {
        const int seed = 246810;
        DeckSystem first = CreateSeededDeck(seed, 7);
        DeckSystem second = CreateSeededDeck(seed, 7);

        first.DrawTurnStartCards(7);
        second.DrawTurnStartCards(7);
        CollectionAssert.AreEqual(GetEntryIds(first.Hand), GetEntryIds(second.Hand));

        first.DiscardHand();
        second.DiscardHand();
        first.DrawTurnStartCards(7);
        second.DrawTurnStartCards(7);

        CollectionAssert.AreEqual(GetEntryIds(first.Hand), GetEntryIds(second.Hand));
        CollectionAssert.AreEqual(GetEntryIds(first.DrawPile), GetEntryIds(second.DrawPile));
    }

    private static DeckSystem CreateDeck(
        int blockCount,
        int otherCount,
        FixedRollRandom rng)
    {
        List<Card> cards = new List<Card>(blockCount + otherCount);
        for (int i = 0; i < blockCount; i++)
        {
            cards.Add(CreateCard($"Block_{i}", hasBlockEffect: true));
        }

        for (int i = 0; i < otherCount; i++)
        {
            cards.Add(CreateCard($"Other_{i}", hasBlockEffect: false));
        }

        DeckSystem deck = new DeckSystem(rng);
        deck.InitializeFromCards(cards);
        return deck;
    }

    private static Card CreateCard(string id, bool hasBlockEffect)
    {
        List<CardEffectBase> effects = hasBlockEffect
            ? new List<CardEffectBase> { new BlockEffect() }
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

    private static DeckSystem CreateSeededDeck(int seed, int cardCount)
    {
        DeckSystem deck = new DeckSystem();
        deck.ResetRandom(seed);
        deck.InitializeFromCards(CreateUniqueCards(cardCount));
        return deck;
    }

    private static List<Card> CreateUniqueCards(int count)
    {
        List<Card> cards = new List<Card>(count);
        for (int i = 0; i < count; i++)
        {
            Card card = CreateCard($"Card_{i}", hasBlockEffect: false);
            card.SetRunDeckEntryId(i + 1);
            cards.Add(card);
        }
        return cards;
    }

    private static List<int> GetEntryIds(IReadOnlyList<Card> cards)
    {
        List<int> entryIds = new List<int>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            entryIds.Add(cards[i].RunDeckEntryId);
        }
        return entryIds;
    }

    private static int CountBlockCards(IReadOnlyList<Card> cards)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            Card card = cards[i];
            if (card != null && card.HasEffect(ECardEffectFlag.Block))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class FixedRollRandom : Random
    {
        private readonly int _roll;

        public int RollCallCount { get; private set; }

        public FixedRollRandom(int roll)
        {
            _roll = roll;
        }

        public override int Next(int maxValue)
        {
            if (maxValue == 100)
            {
                RollCallCount++;
                return _roll;
            }

            return 0;
        }

        public void ResetRollCallCount()
        {
            RollCallCount = 0;
        }
    }
}
#endif
