#if UNITY_INCLUDE_TESTS
// =================================================================
// [스크립트 목적]  개인정보 수집 동의 설정의 기본값·복제·저장·멱등 동작 검증
// [주요 변수]      - 없음
// [의존 관계]      - GameSettings / SettingsManager / AnalyticsManager / NUnit
// =================================================================
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class AnalyticsConsentTests
{
    [Test]
    public void 신규_동의_필드는_안전한_기본값을_가진다()
    {
        var settings = new GameSettings();

        Assert.IsFalse(settings.AnalyticsConsent);
        Assert.IsFalse(settings.HasAnalyticsConsentBeenAsked);
    }

    [Test]
    public void Clone은_동의_필드_두_개를_복사한다()
    {
        var settings = new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        };

        GameSettings clone = settings.Clone();

        Assert.IsFalse(clone.AnalyticsConsent);
        Assert.IsTrue(clone.HasAnalyticsConsentBeenAsked);
    }

    [Test]
    public void ValueEquals는_동의_필드_두_개의_차이를_감지한다()
    {
        var settings = new GameSettings();

        GameSettings consentDiffers = settings.Clone();
        consentDiffers.AnalyticsConsent = true;
        Assert.IsFalse(settings.ValueEquals(consentDiffers));

        GameSettings askedDiffers = settings.Clone();
        askedDiffers.HasAnalyticsConsentBeenAsked = true;
        Assert.IsFalse(settings.ValueEquals(askedDiffers));
    }

    [Test]
    public void 최초_확정_전에는_표시값과_무관하게_수집이_허용되지_않는다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = true,
            HasAnalyticsConsentBeenAsked = false
        });

        try
        {
            Assert.IsFalse(manager.IsAnalyticsConsentGranted);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 최초_true_동의_확정은_수집_활성_이벤트를_발행한다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings());
        int eventCount = 0;
        bool received = false;
        manager.OnAnalyticsConsentChanged += granted =>
        {
            eventCount++;
            received = granted;
        };

        try
        {
            manager.SetAnalyticsConsent(true);

            Assert.AreEqual(1, eventCount);
            Assert.IsTrue(received);
            Assert.IsTrue(manager.HasAnalyticsConsentBeenAsked);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 최초_false_확정도_확정_이벤트를_한번_발행한다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings());
        int eventCount = 0;
        bool received = true;
        manager.OnAnalyticsConsentChanged += granted =>
        {
            eventCount++;
            received = granted;
        };

        try
        {
            manager.SetAnalyticsConsent(false);

            Assert.AreEqual(1, eventCount);
            Assert.IsFalse(received);
            Assert.IsTrue(manager.HasAnalyticsConsentBeenAsked);
            Assert.IsFalse(manager.IsAnalyticsConsentGranted);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 같은_동의값을_재호출하면_이벤트가_중복_발행되지_않는다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings());
        int eventCount = 0;
        manager.OnAnalyticsConsentChanged += _ => eventCount++;

        try
        {
            manager.SetAnalyticsConsent(true);
            manager.SetAnalyticsConsent(true);

            Assert.AreEqual(1, eventCount);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 기본값_초기화_후에도_동의값은_보존된다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        });
        int qualityLevel = QualitySettings.GetQualityLevel();
        int vSyncCount = QualitySettings.vSyncCount;
        int targetFrameRate = Application.targetFrameRate;
        bool audioPaused = AudioListener.pause;

        try
        {
            manager.ResetToDefault();

            Assert.IsFalse(manager.Current.AnalyticsConsent);
            Assert.IsTrue(manager.Current.HasAnalyticsConsentBeenAsked);
        }
        finally
        {
            QualitySettings.SetQualityLevel(qualityLevel, true);
            QualitySettings.vSyncCount = vSyncCount;
            Application.targetFrameRate = targetFrameRate;
            AudioListener.pause = audioPaused;
            DestroyManager(manager);
        }
    }

    [Test]
    public void 옵션_동의_변경은_확인_전까지_저장값과_수집상태를_바꾸지_않는다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        });
        int eventCount = 0;
        manager.OnAnalyticsConsentChanged += _ => eventCount++;

        try
        {
            manager.SetAnalyticsConsentDraft(true);

            Assert.IsTrue(manager.IsDirty);
            Assert.IsTrue(manager.Current.AnalyticsConsent);
            Assert.IsFalse(manager.IsAnalyticsConsentGranted);
            Assert.AreEqual(0, eventCount);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 옵션_동의_취소는_기존_저장값으로_복원한다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        });
        int eventCount = 0;
        manager.OnAnalyticsConsentChanged += _ => eventCount++;

        try
        {
            manager.SetAnalyticsConsentDraft(true);
            manager.Cancel();

            Assert.IsFalse(manager.IsDirty);
            Assert.IsFalse(manager.Current.AnalyticsConsent);
            Assert.IsFalse(manager.IsAnalyticsConsentGranted);
            Assert.AreEqual(0, eventCount);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 옵션_동의_확인은_저장값과_수집상태를_함께_반영한다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        });
        int eventCount = 0;
        bool received = false;
        manager.OnAnalyticsConsentChanged += granted =>
        {
            eventCount++;
            received = granted;
        };

        try
        {
            manager.SetAnalyticsConsentDraft(true);
            manager.Commit();

            Assert.IsFalse(manager.IsDirty);
            Assert.IsTrue(manager.Current.AnalyticsConsent);
            Assert.IsTrue(manager.IsAnalyticsConsentGranted);
            Assert.AreEqual(1, eventCount);
            Assert.IsTrue(received);
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 최초_시작_UI는_미확정이면_표시상_true이고_확정값은_그대로_읽는다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = false
        });

        try
        {
            MethodInfo method = typeof(UIFirstStarPanel).GetMethod(
                "ReadInitialConsent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            Assert.IsTrue((bool)method.Invoke(null, null));

            manager.Current.HasAnalyticsConsentBeenAsked = true;
            Assert.IsFalse((bool)method.Invoke(null, null));
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    [Test]
    public void 옵션_UI는_현재_동의값을_토글에_반영한다()
    {
        SettingsManager manager = CreateSettingsManager(new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        });
        var root = new GameObject("AnalyticsConsentTests_UIOptionTabGameplay");
        UIOptionTabGameplay tab = root.AddComponent<UIOptionTabGameplay>();
        Toggle consentToggle = new GameObject("ConsentToggle").AddComponent<Toggle>();
        consentToggle.transform.SetParent(root.transform);
        TMP_Dropdown languageDropdown = new GameObject("LanguageDropdown").AddComponent<TMP_Dropdown>();
        languageDropdown.transform.SetParent(root.transform);
        SetField(tab, "_analyticsConsentToggle", consentToggle);
        SetField(tab, "_languageDropdown", languageDropdown);

        try
        {
            tab.SyncFromSettings();
            Assert.IsFalse(consentToggle.isOn);

            manager.Current.AnalyticsConsent = true;
            tab.SyncFromSettings();
            Assert.IsTrue(consentToggle.isOn);
        }
        finally
        {
            Object.DestroyImmediate(root);
            DestroyManager(manager);
        }
    }

    [Test]
    public void 구버전_JSON에_동의_필드가_없으면_신규_기본값이_유지된다()
    {
        GameSettings settings = JsonUtility.FromJson<GameSettings>("{\"_version\":1}");

        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.AnalyticsConsent);
        Assert.IsFalse(settings.HasAnalyticsConsentBeenAsked);
    }

    [Test]
    public void 기존_사용자_승계는_튜토리얼_진입했고_동의기록이_없을_때만_실행된다()
    {
        MethodInfo method = typeof(SettingsManager).GetMethod(
            "ShouldMigrateLegacyAnalyticsConsent",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var unasked = new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = false
        };
        var explicitlyDenied = new GameSettings
        {
            AnalyticsConsent = false,
            HasAnalyticsConsentBeenAsked = true
        };

        Assert.IsTrue((bool)method.Invoke(null, new object[] { unasked, true }));
        Assert.IsFalse((bool)method.Invoke(null, new object[] { unasked, false }));
        Assert.IsFalse((bool)method.Invoke(null, new object[] { explicitlyDenied, true }));
    }

    [Test]
    public void 같은_동의값을_반복_적용해도_외부_SDK가_등록되지_않는다()
    {
        AnalyticsManager manager = CreateAnalyticsManagerWithoutExternalSdk();

        try
        {
            Assert.DoesNotThrow(() =>
            {
                manager.ApplyAnalyticsConsent(true);
                manager.ApplyAnalyticsConsent(true);
            });

            Assert.IsFalse(manager.IsExternalAnalyticsEnabled);
            Assert.IsTrue(GetField<bool>(manager, "_hasAppliedConsent"));
            Assert.IsTrue(GetField<bool>(manager, "_lastConsentGranted"));
            Assert.IsNull(GetField<object>(manager, "_gaProvider"));
            Assert.IsNull(GetField<object>(manager, "_ugsProvider"));
        }
        finally
        {
            DestroyManager(manager);
        }
    }

    private static SettingsManager CreateSettingsManager(GameSettings saved)
    {
        SettingsManager.ResetInstance();
        var gameObject = new GameObject("AnalyticsConsentTests_SettingsManager");
        SettingsManager manager = gameObject.AddComponent<SettingsManager>();
        SetField(manager, "_saved", saved);
        SetSingletonInstance(manager);
        return manager;
    }

    /// <summary>
    /// EditMode 테스트에서는 AddComponent 직후 MonoBehaviour.Awake 호출 여부에 의존할 수 없다.
    /// UI 코드가 사용하는 Singleton 정적 경로도 테스트 인스턴스를 보도록 명시적으로 등록한다.
    /// </summary>
    private static void SetSingletonInstance(SettingsManager manager)
    {
        FieldInfo instanceField = typeof(Singleton<SettingsManager>).GetField(
            "_instance",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(instanceField, "SettingsManager 싱글턴 필드를 찾을 수 없습니다.");
        instanceField.SetValue(null, manager);
        Assert.AreSame(manager, SettingsManager.Instance);
    }

    private static AnalyticsManager CreateAnalyticsManagerWithoutExternalSdk()
    {
        AnalyticsManager.ResetInstance();
        var gameObject = new GameObject("AnalyticsConsentTests_AnalyticsManager");
        AnalyticsManager manager = gameObject.AddComponent<AnalyticsManager>();
        SetField(manager, "_useGameAnalytics", false);
        SetField(manager, "_useUgsAnalytics", false);
        SetField(manager, "_sendFromEditor", true);
        return manager;
    }

    private static void DestroyManager<T>(T manager) where T : Component
    {
        if (manager != null)
            Object.DestroyImmediate(manager.gameObject);

        if (typeof(T) == typeof(SettingsManager))
            SettingsManager.ResetInstance();
        else if (typeof(T) == typeof(AnalyticsManager))
            AnalyticsManager.ResetInstance();
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"필드를 찾을 수 없습니다: {target.GetType().Name}.{fieldName}");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"필드를 찾을 수 없습니다: {target.GetType().Name}.{fieldName}");
        return (T)field.GetValue(target);
    }
}
#endif
