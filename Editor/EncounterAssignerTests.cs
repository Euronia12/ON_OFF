#if UNITY_INCLUDE_TESTS
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class EncounterAssignerTests
{
    [Test]
    public void Elite는_EncounterTier에_맞는_구간에서만_배정된다()
    {
        EnemyDataSO easy = CreateEnemy("elite_easy", EEncounterTier.Easy);
        EnemyDataSO hard = CreateEnemy("elite_hard", EEncounterTier.Hard);
        EnemyPoolSO pool = CreatePool(easy, hard);
        EnemyPoolSO hardOnlyPool = CreatePool(hard);

        try
        {
            MapGraph first = CreateEliteMap();
            EncounterAssigner.Assign(first, pool, 1234, 0.5f);

            Assert.AreEqual(easy.EnemyId, first.GetNode(1, 0).EnemyId);
            Assert.AreEqual(hard.EnemyId, first.GetNode(3, 0).EnemyId);

            MapGraph second = CreateEliteMap();
            EncounterAssigner.Assign(second, pool, 1234, 0.5f);
            Assert.AreEqual(first.GetNode(1, 0).EnemyId, second.GetNode(1, 0).EnemyId);
            Assert.AreEqual(first.GetNode(3, 0).EnemyId, second.GetNode(3, 0).EnemyId);

            MapGraph missingEasy = new MapGraph(1234, 4, 0);
            missingEasy.AddNode(new MapNode(1, 0, 1, 0, ENodeType.Elite));
            EncounterAssigner.Assign(missingEasy, hardOnlyPool, 1234, 0.5f);
            Assert.IsNull(missingEasy.GetNode(1, 0).EnemyId);
        }
        finally
        {
            Object.DestroyImmediate(pool);
            Object.DestroyImmediate(hardOnlyPool);
            Object.DestroyImmediate(easy);
            Object.DestroyImmediate(hard);
        }
    }

    private static MapGraph CreateEliteMap()
    {
        MapGraph map = new MapGraph(1234, 4, 0);
        map.AddNode(new MapNode(1, 0, 1, 0, ENodeType.Elite));
        map.AddNode(new MapNode(2, 0, 3, 0, ENodeType.Elite));
        return map;
    }

    private static EnemyDataSO CreateEnemy(string id, EEncounterTier tier)
    {
        EnemyDataSO enemy = ScriptableObject.CreateInstance<EnemyDataSO>();
        SetField(enemy, "_enemyId", id);
        SetField(enemy, "_grade", EEnemyGrade.Elite);
        SetField(enemy, "_encounterTier", tier);
        return enemy;
    }

    private static EnemyPoolSO CreatePool(params EnemyDataSO[] enemies)
    {
        WeightedEnemy[] entries = new WeightedEnemy[enemies.Length];
        for (int i = 0; i < enemies.Length; i++)
        {
            entries[i] = new WeightedEnemy { Enemy = enemies[i], Weight = 1 };
        }

        EnemyPoolSO pool = ScriptableObject.CreateInstance<EnemyPoolSO>();
        SetField(pool, "_elite", entries);
        return pool;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(target, value);
    }
}
#endif
