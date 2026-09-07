// =================================================================
// [스크립트 목적]  런타임 유저 데이터 보유 + SaveManager 통한 영속화 (프레임워크 그릇).
//                 구체 도메인(재화·프로필 등)은 모름. 등록/조회/저장 메커니즘만 제공.
// [주요 변수]      - _current  : 현재 유저 데이터 (도메인 컨테이너)
//                  - _isDirty  : 변경 플래그 (도메인이 자동 통지)
// [의존 관계]      - ManagerBase<UserManager>, SaveManager, UserData(컨테이너)
// [InitOrder]      25
// [설계]           도메인은 게임 측 콘텐츠 → UserManager는 GetData<T>로만 접근 제공.
//                  도메인 수가 늘어도 매니저는 비대해지지 않음(God Object 방지).
//                  도메인이 변경 시 MarkDirty 콜백으로 자동 통지 → 저장 누락 구조적 방지.
//                  사용할 도메인은 RegisterDomains 메서드에 게임별로 등록한다.
// =================================================================
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class UserManager : ManagerBase<UserManager>
{
    public override int InitOrder => 25;
    public override bool IsCritical => true; // 유저 데이터는 필수

    private const string DEFAULT_SLOT = "user_0";

    private UserData _current;
    private string _currentSlot = DEFAULT_SLOT;
    private bool _isDirty;
    private float _autoSaveInterval = 60f;
    private CancellationTokenSource _autoSaveCts;

    public bool HasUser => _current != null;

    // ─────────────────────────────────────────────
    // 도메인 등록 (★ 게임별로 여기에 사용할 도메인을 등록하세요)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 이 게임이 사용하는 유저 도메인을 등록한다. 신규/로드 시 자동 호출됨.
    /// ★ 프로젝트마다 아래에 자기 도메인을 추가하세요. 예:
    ///     data.RegisterDomain(new UserProfileData());
    ///     data.RegisterDomain(new UserCurrencyData());
    /// 같은 타입은 RegisterDomain이 중복 무시 → 로드된 데이터가 보존됨.
    /// </summary>
    private void RegisterDomains(UserData data)
    {
        // TODO: 이 게임이 사용할 도메인을 등록하세요.
    }

    public event Action<UserData> OnUserDataLoaded;
    public event Action OnUserDataChanged;
    public event Action OnDailyReset;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // 로드는 명시 호출 (타이틀에서 슬롯 선택 후)
        return UniTask.CompletedTask;
    }

    protected override async UniTask OnShutdownInternalAsync()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;

        if (_isDirty && _current != null)
            await SaveAsync();
    }

    // ─────────────────────────────────────────────
    // 도메인 접근 (게임 측에서 자기 데이터 조회)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 도메인 데이터 조회. 게임 측은 이걸로 자기 데이터를 꺼내 직접 다룬다.
    /// 예: UserManager.Instance.GetData&lt;UserCurrencyData&gt;().TrySpendGold(100)
    /// </summary>
    public T GetData<T>() where T : UserDataBase
        => _current?.GetDomain<T>();

    // ─────────────────────────────────────────────
    // 로드 / 저장
    // ─────────────────────────────────────────────

    public async UniTask LoadAsync(string slot = DEFAULT_SLOT, CancellationToken token = default)
    {
        _currentSlot = slot;
        _current = await SaveManager.Instance.LoadAsync<UserData>(slot, token);

        if (_current == null)
        {
            _current = new UserData();
            RegisterDomains(_current); // 게임 도메인 등록
            _current.ResetAllToDefault();
            _isDirty = true; // 신규 → 첫 저장 필요
        }
        else
        {
            // 로드된 데이터에도 누락 도메인 보강(버전 업으로 새 도메인 추가된 경우)
            RegisterDomains(_current);
            _isDirty = false;
        }

        // 도메인들이 변경 시 자동 더티 마킹하도록 콜백 배선
        _current.BindAll(MarkDirty);

        OnUserDataLoaded?.Invoke(_current);
    }

    public async UniTask SaveAsync(CancellationToken token = default)
    {
        if (_current == null) return;
        await SaveManager.Instance.SaveAsync(_currentSlot, _current, token);
        _isDirty = false;
    }

    /// <summary>도메인이 변경 시 자동 호출(콜백). 외부 직접 호출도 허용.</summary>
    public void MarkDirty()
    {
        _isDirty = true;
        OnUserDataChanged?.Invoke();
    }

    public void CreateNewUser(string slot = DEFAULT_SLOT)
    {
        _currentSlot = slot;
        _current = new UserData();
        RegisterDomains(_current);
        _current.ResetAllToDefault();
        _current.BindAll(MarkDirty);
        _isDirty = true;
        OnUserDataLoaded?.Invoke(_current);
    }

    // ─────────────────────────────────────────────
    // 시간 / 일일 초기화 (UTC 기준 — 프레임워크 시간 헬퍼)
    // ─────────────────────────────────────────────

    /// <summary>현재 UTC Unix 시각(초). 시간 비교·일일 경계 판정용 공통 헬퍼.</summary>
    public static long GetUtcNowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// UTC 날짜 기준 일일 초기화 필요 여부.
    /// </summary>
    /// <param name="lastResetUtcUnix">도메인이 보관한 마지막 초기화 시각</param>
    public static bool NeedsDailyReset(long lastResetUtcUnix)
    {
        if (lastResetUtcUnix <= 0) return true; // 미기록(신규)
        DateTime last = DateTimeOffset.FromUnixTimeSeconds(lastResetUtcUnix).UtcDateTime;
        return last.Date < DateTime.UtcNow.Date;
    }

    /// <summary>
    /// 일일 초기화 통지 발행. 실제 리셋 처리는 OnDailyReset 구독 측(게임 콘텐츠)에서.
    /// 새 기준 시각 저장은 도메인이 담당하므로, 게임 측은 구독 핸들러에서 갱신한다.
    /// </summary>
    public void NotifyDailyReset()
    {
        OnDailyReset?.Invoke();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GameLogger.Log(ELogCategory.System, "[UserManager] 일일 초기화 통지");
#endif
    }

    // ─────────────────────────────────────────────
    // 자동 저장
    // ─────────────────────────────────────────────

    public void SetAutoSave(bool enabled, float interval = 60f)
    {
        _autoSaveInterval = Mathf.Max(5f, interval);

        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;

        if (enabled)
        {
            _autoSaveCts = new CancellationTokenSource();
            AutoSaveLoop(_autoSaveCts.Token).Forget();
        }
    }

    private async UniTaskVoid AutoSaveLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(_autoSaveInterval), cancellationToken: token);
                if (_isDirty && _current != null)
                    await SaveAsync(token);
            }
        }
        catch (OperationCanceledException) { }
    }

    protected override void OnDestroy()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;
        base.OnDestroy();
    }
}