#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class StartCardPackRaritySelectionTests
{
    private const string RandomPackPath =
        "Assets/00_Addressable/Data/SO/StartCardPack/05_StartCardPack_Random.asset";

    private readonly List<Object> _createdObjects = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _createdObjects.Count; i++)
            Object.DestroyImmediate(_createdObjects[i]);

        _createdObjects.Clear();
    }

    [Test]
    public void 단일_등급_가중치는_해당_등급만_선택한다()
    {
        CardDataSO common = CreateCard("common", ECardRarity.Common);
        CardDataSO rareA = CreateCard("rare_a", ECardRarity.Rare);
        CardDataSO rareB = CreateCard("rare_b", ECardRarity.Rare);
        StartCardPackSO pack = CreatePack(
            2,
            true,
            new List<CardDataSO> { common, rareA, rareB },
            0f, 100f, 0f, 0f);

        List<CardDataSO> selected = Select(pack, 1234);

        Assert.AreEqual(2, selected.Count);
        Assert.AreEqual(ECardRarity.Rare, selected[0].Rarity);
        Assert.AreEqual(ECardRarity.Rare, selected[1].Rarity);
        Assert.AreNotSame(selected[0], selected[1]);
    }

    [Test]
    public void 후보가_없으면_가까운_낮은_등급을_우선한다()
    {
        CardDataSO common = CreateCard("common", ECardRarity.Common);
        CardDataSO rare = CreateCard("rare", ECardRarity.Rare);
        StartCardPackSO pack = CreatePack(
            1,
            true,
            new List<CardDataSO> { common, rare },
            0f, 0f, 0f, 100f);

        List<CardDataSO> selected = Select(pack, 1234);

        Assert.AreSame(rare, selected[0]);
    }

    [Test]
    public void Common_후보가_없으면_가까운_상위_등급을_선택한다()
    {
        CardDataSO unique = CreateCard("unique", ECardRarity.Unique);
        CardDataSO legendary = CreateCard("legendary", ECardRarity.Legendary);
        StartCardPackSO pack = CreatePack(
            1,
            true,
            new List<CardDataSO> { unique, legendary },
            100f, 0f, 0f, 0f);

        List<CardDataSO> selected = Select(pack, 1234);

        Assert.AreSame(unique, selected[0]);
    }

    [Test]
    public void 같은_시드는_중복_없는_같은_결과를_반환한다()
    {
        List<CardDataSO> cards = new List<CardDataSO>
        {
            CreateCard("common_a", ECardRarity.Common),
            CreateCard("common_b", ECardRarity.Common),
            CreateCard("rare", ECardRarity.Rare),
            CreateCard("unique", ECardRarity.Unique),
            CreateCard("legendary", ECardRarity.Legendary),
        };
        StartCardPackSO pack = CreatePack(3, true, cards, 70f, 24f, 5f, 1f);

        List<CardDataSO> first = Select(pack, 9876);
        List<CardDataSO> second = Select(pack, 9876);

        Assert.AreEqual(3, first.Count);
        CollectionAssert.AreEqual(first, second);
        CollectionAssert.AllItemsAreUnique(first);
        CollectionAssert.IsSubsetOf(first, cards);
    }

    [Test]
    public void 가중치_비활성과_전체_지급은_기존_동작을_유지한다()
    {
        List<CardDataSO> cards = new List<CardDataSO>
        {
            CreateCard("common", ECardRarity.Common),
            CreateCard("rare", ECardRarity.Rare),
            CreateCard("unique", ECardRarity.Unique),
        };
        StartCardPackSO uniformPack = CreatePack(2, false, cards, 0f, 0f, 0f, 0f);
        StartCardPackSO allPack = CreatePack(0, true, cards, 0f, 100f, 0f, 0f);

        List<CardDataSO> uniform = Select(uniformPack, 1234);
        List<CardDataSO> all = Select(allPack, 1234);

        Assert.AreEqual(2, uniform.Count);
        CollectionAssert.AllItemsAreUnique(uniform);
        CollectionAssert.AreEqual(cards, all);
    }

    [Test]
    public void 가중치가_모두_0이면_균등_추첨으로_폴백한다()
    {
        List<CardDataSO> cards = new List<CardDataSO>
        {
            CreateCard("common", ECardRarity.Common),
            CreateCard("rare", ECardRarity.Rare),
            CreateCard("unique", ECardRarity.Unique),
        };
        StartCardPackSO pack = CreatePack(2, true, cards, 0f, 0f, 0f, 0f);
        LogAssert.Expect(
            LogType.Warning,
            "[UIStartCardPackPopup] 등급 가중치 합계가 0이므로 균등 추첨합니다: test_pack");

        List<CardDataSO> selected = Select(pack, 1234);

        Assert.AreEqual(2, selected.Count);
        CollectionAssert.AllItemsAreUnique(selected);
        CollectionAssert.IsSubsetOf(selected, cards);
    }

    [Test]
    public void Random_팩은_전용_가중치가_직렬화되어_있다()
    {
        StartCardPackSO pack = AssetDatabase.LoadAssetAtPath<StartCardPackSO>(RandomPackPath);

        Assert.IsNotNull(pack);
        Assert.AreEqual(3, pack.RandomPickCount);
        Assert.IsTrue(pack.UseRarityWeights);
        Assert.AreEqual(60f, pack.GetRarityWeight(ECardRarity.Common));
        Assert.AreEqual(27f, pack.GetRarityWeight(ECardRarity.Rare));
        Assert.AreEqual(10f, pack.GetRarityWeight(ECardRarity.Unique));
        Assert.AreEqual(3f, pack.GetRarityWeight(ECardRarity.Legendary));
    }

    private List<CardDataSO> Select(StartCardPackSO pack, int runSeed)
    {
        SetStaticField(typeof(UIStartCardPackPopup), "s_pendingRunSeed", runSeed);
        MethodInfo method = typeof(UIStartCardPackPopup).GetMethod(
            "SelectRewardCards",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        return (List<CardDataSO>)method.Invoke(null, new object[] { pack });
    }

    private CardDataSO CreateCard(string cardId, ECardRarity rarity)
    {
        CardDataSO card = ScriptableObject.CreateInstance<CardDataSO>();
        SetField(card, "_cardId", cardId);
        SetField(card, "_rarity", rarity);
        _createdObjects.Add(card);
        return card;
    }

    private StartCardPackSO CreatePack(
        int randomPickCount,
        bool useRarityWeights,
        List<CardDataSO> cards,
        float commonWeight,
        float rareWeight,
        float uniqueWeight,
        float legendaryWeight)
    {
        StartCardPackSO pack = ScriptableObject.CreateInstance<StartCardPackSO>();
        SetField(pack, "_packId", "test_pack");
        SetField(pack, "_cards", cards);
        SetField(pack, "_randomPickCount", randomPickCount);
        SetField(pack, "_useRarityWeights", useRarityWeights);
        SetField(pack, "_commonWeight", commonWeight);
        SetField(pack, "_rareWeight", rareWeight);
        SetField(pack, "_uniqueWeight", uniqueWeight);
        SetField(pack, "_legendaryWeight", legendaryWeight);
        _createdObjects.Add(pack);
        return pack;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(field, fieldName);
        field.SetValue(target, value);
    }

    private static void SetStaticField(System.Type type, string fieldName, object value)
    {
        FieldInfo field = type.GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNotNull(field, fieldName);
        field.SetValue(null, value);
    }
}

public sealed class CardRewardServiceRarityWeightTests
{
    private const string NormalSequencePath =
        "Assets/00_Addressable/Data/SO/Map/Main/RunMapSequence.asset";
    private const string HardSequencePath =
        "Assets/00_Addressable/Data/SO/Map/Main/RunMapSequence_Hard.asset";
    private const string HellSequencePath =
        "Assets/00_Addressable/Data/SO/Map/Main/RunMapSequence_Hell.asset";

    private readonly List<Object> _createdObjects = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _createdObjects.Count; i++)
            Object.DestroyImmediate(_createdObjects[i]);

        _createdObjects.Clear();
    }

    [Test]
    public void 단일_등급_가중치는_해당_등급만_선택한다()
    {
        List<CardDataSO> cards = CreateAllRarities();
        CardRarityWeights weights = new CardRarityWeights(0f, 100f, 0f, 0f);

        CardDataSO selected = PickOne(cards, weights, rarity: null, seed: 1234);

        Assert.AreEqual(ECardRarity.Rare, selected.Rarity);
    }

    [Test]
    public void 난이도별_등급_가중치가_직렬화되어_있다()
    {
        StageSequenceSO normal = AssetDatabase.LoadAssetAtPath<StageSequenceSO>(NormalSequencePath);
        StageSequenceSO hard = AssetDatabase.LoadAssetAtPath<StageSequenceSO>(HardSequencePath);
        StageSequenceSO hell = AssetDatabase.LoadAssetAtPath<StageSequenceSO>(HellSequencePath);

        AssertWeights(normal, 60f, 24f, 10f, 6f);
        AssertWeights(hard, 70f, 24f, 5f, 1f);
        AssertWeights(hell, 70f, 24f, 5f, 1f);
    }

    [Test]
    public void 같은_시드는_같은_비복원_결과를_반환한다()
    {
        List<CardDataSO> cards = new List<CardDataSO>
        {
            CreateCard("common_a", ECardRarity.Common),
            CreateCard("common_b", ECardRarity.Common),
            CreateCard("rare_a", ECardRarity.Rare),
            CreateCard("rare_b", ECardRarity.Rare),
            CreateCard("unique", ECardRarity.Unique),
            CreateCard("legendary", ECardRarity.Legendary),
        };
        CardRarityWeights weights = new CardRarityWeights(60f, 24f, 10f, 6f);
        CardQueryCriteria criteria = CreateCriteria();
        var firstService = new CardRewardService(cards, AlwaysUnlockedProvider.Instance, weights);
        var secondService = new CardRewardService(cards, AlwaysUnlockedProvider.Instance, weights);

        List<CardDataSO> first = firstService.PickRandom(in criteria, 4, new System.Random(9876));
        List<CardDataSO> second = secondService.PickRandom(in criteria, 4, new System.Random(9876));

        CollectionAssert.AreEqual(first, second);
        CollectionAssert.AllItemsAreUnique(first);
    }

    [Test]
    public void 후보가_없는_등급은_제외하고_남은_등급을_선택한다()
    {
        List<CardDataSO> cards = new List<CardDataSO>
        {
            CreateCard("rare", ECardRarity.Rare),
        };
        CardRarityWeights weights = new CardRarityWeights(60f, 24f, 10f, 6f);

        CardDataSO selected = PickOne(cards, weights, rarity: null, seed: 1234);

        Assert.AreEqual(ECardRarity.Rare, selected.Rarity);
    }

    [Test]
    public void 특정_등급_조건은_가중치보다_우선한다()
    {
        List<CardDataSO> cards = CreateAllRarities();
        CardRarityWeights weights = new CardRarityWeights(100f, 0f, 0f, 0f);

        CardDataSO selected = PickOne(cards, weights, ECardRarity.Legendary, seed: 1234);

        Assert.AreEqual(ECardRarity.Legendary, selected.Rarity);
    }

    [Test]
    public void 모든_가중치가_0이면_기본_가중치를_사용한다()
    {
        List<CardDataSO> cards = CreateAllRarities();
        CardQueryCriteria criteria = CreateCriteria();
        var fallbackService = new CardRewardService(
            cards,
            AlwaysUnlockedProvider.Instance,
            new CardRarityWeights(0f, 0f, 0f, 0f));
        var defaultService = new CardRewardService(cards, AlwaysUnlockedProvider.Instance);

        List<CardDataSO> fallback = fallbackService.PickRandom(
            in criteria, cards.Count, new System.Random(4321));
        List<CardDataSO> defaults = defaultService.PickRandom(
            in criteria, cards.Count, new System.Random(4321));

        CollectionAssert.AreEqual(defaults, fallback);
    }

    private CardDataSO PickOne(
        List<CardDataSO> cards,
        CardRarityWeights weights,
        ECardRarity? rarity,
        int seed)
    {
        CardQueryCriteria criteria = CreateCriteria(rarity);
        var service = new CardRewardService(cards, AlwaysUnlockedProvider.Instance, weights);
        List<CardDataSO> selected = service.PickRandom(in criteria, 1, new System.Random(seed));

        Assert.AreEqual(1, selected.Count);
        return selected[0];
    }

    private List<CardDataSO> CreateAllRarities()
    {
        return new List<CardDataSO>
        {
            CreateCard("common", ECardRarity.Common),
            CreateCard("rare", ECardRarity.Rare),
            CreateCard("unique", ECardRarity.Unique),
            CreateCard("legendary", ECardRarity.Legendary),
        };
    }

    private CardDataSO CreateCard(string cardId, ECardRarity rarity)
    {
        CardDataSO card = ScriptableObject.CreateInstance<CardDataSO>();
        SetField(card, "_cardId", cardId);
        SetField(card, "_rarity", rarity);
        _createdObjects.Add(card);
        return card;
    }

    private static CardQueryCriteria CreateCriteria(ECardRarity? rarity = null)
        => new CardQueryCriteria(
            ECardPool.Normal,
            rarity,
            unlockedOnly: true,
            stagePool: ECardStagePool.All);

    private static void AssertWeights(
        StageSequenceSO sequence,
        float common,
        float rare,
        float unique,
        float legendary)
    {
        Assert.IsNotNull(sequence);
        CardRarityWeights weights = sequence.CardRewardRarityWeights;
        Assert.AreEqual(common, weights.GetWeight(ECardRarity.Common));
        Assert.AreEqual(rare, weights.GetWeight(ECardRarity.Rare));
        Assert.AreEqual(unique, weights.GetWeight(ECardRarity.Unique));
        Assert.AreEqual(legendary, weights.GetWeight(ECardRarity.Legendary));
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(field, fieldName);
        field.SetValue(target, value);
    }
}
#endif
