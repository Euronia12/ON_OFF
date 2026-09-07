#if UNITY_INCLUDE_TESTS
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class EnemyBossPhaseTests
{
    [Test]
    public void 단일페이즈_적은_체력소진시_즉시사망한다()
    {
        EnemyDataSO data = CreateEnemy(EEnemyGrade.Normal, 20, null);
        try
        {
            EnemyActor actor = new EnemyActor(data);
            int diedCount = 0;
            actor.OnDied += () => diedCount++;

            actor.Setup(data.MaxHp);
            actor.TakeDamage(999);

            Assert.AreEqual(1, diedCount);
            Assert.IsFalse(actor.HasNextPhase);
            Assert.AreEqual(0, actor.Hp);
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    [Test]
    public void 보스는_중간페이즈에서_회복하고_최종페이즈에서만_사망한다()
    {
        EnemyIntentTurn[] secondPattern =
        {
            new EnemyIntentTurn
            {
                Intents = new[]
                {
                    new EnemyIntent { Type = EEnemyIntentType.Defend, Amount = 7 },
                },
            },
        };
        BossPhaseData secondPhase = new BossPhaseData();
        SetField(secondPhase, "_maxHp", 40);
        SetField(secondPhase, "_intentPattern", secondPattern);
        SetField(secondPhase, "_absorbSfxKey", "SFX_Phase2Absorb");
        SetField(secondPhase, "_viewPrefabKey", string.Empty);

        EnemyDataSO data = CreateEnemy(EEnemyGrade.Boss, 20, new[] { secondPhase });
        SetField(data, "_viewPrefabKey", "EnemyView_Phase1");

        try
        {
            EnemyActor actor = new EnemyActor(data);
            int depletedCount = 0;
            int diedCount = 0;
            actor.OnPhaseDepleted += _ => depletedCount++;
            actor.OnDied += () => diedCount++;

            actor.Setup(data.MaxHp);
            actor.TakeDamage(999);

            Assert.AreEqual(1, depletedCount);
            Assert.AreEqual(0, diedCount);
            Assert.AreEqual(0, actor.Hp);
            Assert.IsTrue(actor.HasNextPhase);

            Assert.IsTrue(actor.TryAdvancePhase());
            Assert.AreEqual(1, actor.CurrentPhaseIndex);
            Assert.AreEqual(40, actor.Hp);
            Assert.AreEqual(40, actor.MaxHp);
            Assert.AreEqual(EEnemyIntentType.Defend, actor.CurrentIntents[0].Type);
            Assert.AreEqual("SFX_Phase2Absorb", data.GetPhaseAbsorbSfxKey(1));
            Assert.AreEqual("EnemyView_Phase1", data.GetPhaseViewPrefabKey(1));

            actor.TakeDamage(999);

            Assert.AreEqual(1, depletedCount);
            Assert.AreEqual(1, diedCount);
            Assert.AreEqual(0, actor.Hp);
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    [Test]
    public void 보스별_추가페이즈_흡수효과음키는_서로독립적이다()
    {
        BossPhaseData firstBossPhase = CreatePhaseSfx("SFX_BossA_Absorb");
        BossPhaseData secondBossPhase = CreatePhaseSfx("SFX_BossB_Absorb");
        EnemyDataSO firstBoss = CreateEnemy(EEnemyGrade.Boss, 20, new[] { firstBossPhase });
        EnemyDataSO secondBoss = CreateEnemy(EEnemyGrade.Boss, 20, new[] { secondBossPhase });

        try
        {
            Assert.AreEqual("SFX_BossA_Absorb", firstBoss.GetPhaseAbsorbSfxKey(1));
            Assert.AreEqual("SFX_BossB_Absorb", secondBoss.GetPhaseAbsorbSfxKey(1));
        }
        finally
        {
            Object.DestroyImmediate(firstBoss);
            Object.DestroyImmediate(secondBoss);
        }
    }

    [Test]
    public void 추가페이즈_흡수효과음키가_비어있으면_null을_반환한다()
    {
        BossPhaseData secondPhase = CreatePhaseSfx(string.Empty);
        EnemyDataSO data = CreateEnemy(EEnemyGrade.Boss, 20, new[] { secondPhase });

        try
        {
            Assert.IsNull(data.GetPhaseAbsorbSfxKey(1));
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    [Test]
    public void 스테이지SO가_등급별전투와_보스2페이즈_BGM키를_소유한다()
    {
        StageDataSO stage = ScriptableObject.CreateInstance<StageDataSO>();
        SetField(stage, "_normalBattleBgmKey", "BGM_Normal");
        SetField(stage, "_eliteBattleBgmKey", "BGM_Elite");
        SetField(stage, "_bossBattleBgmKey", "BGM_Boss_P1");
        SetField(stage, "_bossPhase2BgmKey", "BGM_Boss_P2");

        try
        {
            Assert.AreEqual("BGM_Normal", stage.GetBattleBgmKey(EEnemyGrade.Normal));
            Assert.AreEqual("BGM_Normal", stage.GetBattleBgmKey(EEnemyGrade.Test));
            Assert.AreEqual("BGM_Elite", stage.GetBattleBgmKey(EEnemyGrade.Elite));
            Assert.AreEqual("BGM_Boss_P1", stage.GetBattleBgmKey(EEnemyGrade.Boss));
            Assert.AreEqual("BGM_Boss_P2", stage.BossPhase2BgmKey);
        }
        finally
        {
            Object.DestroyImmediate(stage);
        }
    }

    [Test]
    public void 적_세대전환시_이전_피해차단을_해제한다()
    {
        EnemyDataSO data = CreateEnemy(EEnemyGrade.Boss, 20, null);
        try
        {
            EnemyActor actor = new EnemyActor(data);
            actor.Setup(data.MaxHp);
            CardPhaseDispatcher dispatcher = new CardPhaseDispatcher(null, null, null, null);

            dispatcher.CancelPendingForTarget(actor);
            Assert.IsFalse(CanApplyPendingDamageToTarget(dispatcher, actor));

            dispatcher.AdvanceEnemyGeneration();
            Assert.IsTrue(CanApplyPendingDamageToTarget(dispatcher, actor));
        }
        finally
        {
            Object.DestroyImmediate(data);
        }
    }

    private static EnemyDataSO CreateEnemy(
        EEnemyGrade grade,
        int maxHp,
        BossPhaseData[] additionalPhases)
    {
        EnemyDataSO data = ScriptableObject.CreateInstance<EnemyDataSO>();
        SetField(data, "_grade", grade);
        SetField(data, "_maxHp", maxHp);
        SetField(data, "_additionalBossPhases", additionalPhases ?? System.Array.Empty<BossPhaseData>());
        return data;
    }

    private static BossPhaseData CreatePhaseSfx(string absorbSfxKey)
    {
        BossPhaseData phase = new BossPhaseData();
        SetField(phase, "_absorbSfxKey", absorbSfxKey);
        return phase;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(target, value);
    }

    private static bool CanApplyPendingDamageToTarget(
        CardPhaseDispatcher dispatcher,
        ICombatActor target)
    {
        MethodInfo method = typeof(CardPhaseDispatcher).GetMethod(
            "CanApplyPendingDamageToTarget",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return method != null && (bool)method.Invoke(dispatcher, new object[] { target });
    }
}
#endif
