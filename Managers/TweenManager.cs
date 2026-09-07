// =================================================================
// [스크립트 목적]  DOTween 전역 초기화·정책 설정 + 씬 전환 시 트윈 정리 (보호 id 제외)
// [주요 변수]      - _tweensCapacity / _sequencesCapacity : 풀 초기 용량
//                  - _protectedIds     : 씬 전환 시 Kill 제외할 트윈 id 집합 (string)
//                  - _protectedTargets : id별 추적 타겟. 타겟 파괴 시 해당 id 자동 정리
// [의존 관계]      - ManagerBase<TweenManager>, DOTween, SceneFlowManager
// [InitOrder]      86  (CameraManager 85 이후, UIManager 90 이전)
// [주의]           DOTween.Init은 1회만. 저장된 Tween 참조의 오소유를 막기 위해 전역 재활용은 끈다.
//                  영속 트윈(페이드/BGM/상시연출)은 SetId(Constants.Tween.ID_*) +
//                  RegisterProtected로 보호 → 씬 전환 시 죽지 않음
//                  트윈 id는 string으로 통일 (타입 안전·일관성). 보호 비교도 string 기준
// =================================================================
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;

public class TweenManager : ManagerBase<TweenManager>
{
    public override int InitOrder => 86; // CameraManager(85) 이후, UIManager(90) 이전

    [Header("DOTween 초기화 설정")]
    [Tooltip("트윈 풀 초기 용량. 동시 활성 트윈 예상치 + 여유")]
    [Min(0)]
    [SerializeField] private int _tweensCapacity = 200;

    [Tooltip("시퀀스 풀 초기 용량")]
    [Min(0)]
    [SerializeField] private int _sequencesCapacity = 50;

    [Tooltip("트윈 자동 재사용. 켜면 Kill 후 보관된 참조가 다른 트윈으로 재사용될 수 있으므로 기본 비활성")]
    [SerializeField] private bool _recycleAllByDefault = false;

    [Tooltip("안전 모드. 타겟 파괴된 트윈 예외 대신 무시. 끄면 성능↑ 위험↑")]
    [SerializeField] private bool _useSafeMode = true;

    [Header("씬 전환 정책")]
    [Tooltip("씬 전환 시 보호 id를 제외한 모든 트윈을 강제 정리")]
    [SerializeField] private bool _killTweensOnSceneChange = true;

    [Tooltip("씬 전환 시 트윈을 즉시 완료(콜백 실행)할지 여부.\n" +
             "false = 즉시 중단(가벼움, 기본) / true = 완료 콜백 실행 후 종료")]
    [SerializeField] private bool _completeOnSceneChange = false;

    // 씬 전환 시 Kill 대상에서 제외할 트윈 id 집합 (페이드/BGM/상시연출 등).
    // id는 string으로 통일 → 타입 실수 차단, Constants.Tween.ID_* 상수 사용 권장
    private readonly HashSet<string> _protectedIds = new HashSet<string>();

    // 보호 id별 추적 타겟. 정리 시점에 타겟이 파괴되었으면(== null) 해당 id 트윈을 자동 Kill.
    // 타겟 없이 id만 등록한 항목은 여기 들어가지 않음(영구 보호).
    private readonly Dictionary<string, Object> _protectedTargets = new Dictionary<string, Object>();

    // 타겟이 죽은 보호 id를 모았다 일괄 처리하기 위한 버퍼 (순회 중 Dictionary 변경 방지)
    private readonly List<string> _deadIdBuffer = new List<string>(8);

    // 보호 id 제외 순회 시 재사용할 버퍼 (GC 방지용). 순회 중 Kill로 컬렉션 변경되므로
    // 한 번 모았다가 일괄 Kill 하기 위한 임시 리스트
    private readonly List<Tween> _killBuffer = new List<Tween>(64);

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // DOTween 초기화 (1회만)
        DOTween.Init(
            recycleAllByDefault: _recycleAllByDefault,
            useSafeMode: _useSafeMode,
            logBehaviour: LogBehaviour.ErrorsOnly);

        DOTween.SetTweensCapacity(_tweensCapacity, _sequencesCapacity);

        // 프레임워크 기본 보호 id 등록 (사용자가 페이드/BGM/상시연출을 DOTween으로 만들 경우 대비).
        // 현 프레임워크의 SceneFlowManager 페이드·AudioManager BGM은 수동 Lerp라 트윈이 아니지만,
        // 사용자가 DOTween 기반 영속 연출을 추가할 때를 위한 방어 장치
        _protectedIds.Add(Constants.Tween.ID_FADE);
        _protectedIds.Add(Constants.Tween.ID_AUDIO);
        _protectedIds.Add(Constants.Tween.ID_PERSISTENT);

        // 씬 전환 시 트윈 정리 구독
        if (_killTweensOnSceneChange && SceneFlowManager.HasInstance)
            SceneFlowManager.Instance.OnSceneLoadStart += OnSceneLoadStart;

        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        if (SceneFlowManager.HasInstance)
            SceneFlowManager.Instance.OnSceneLoadStart -= OnSceneLoadStart;

        DOTween.KillAll();

        // 매니저 종료 시 보호 목록도 비움 (재초기화 대비 — 단독 배치/도메인 리로드 등)
        _protectedIds.Clear();
        _protectedTargets.Clear();
        return UniTask.CompletedTask;
    }

    private void OnSceneLoadStart(string sceneName) => KillAllSceneTweens(_completeOnSceneChange);

    // ─────────────────────────────────────────────
    // 보호 id 등록 / 해제
    // ─────────────────────────────────────────────

    /// <summary>
    /// 씬 전환 시 죽이지 않을 트윈 id 등록. 트윈 쪽엔 SetId(id)를 걸어둬야 함.
    /// 예) bgmTween.SetId(Constants.Tween.ID_AUDIO);
    ///     TweenManager.Instance.RegisterProtected(Constants.Tween.ID_AUDIO);
    /// (프레임워크 기본 id 3종은 초기화 시 자동 등록되므로 추가 호출 불필요)
    /// 이 오버로드는 타겟 추적을 하지 않으므로, 타겟이 영구 생존하는 경우에만 사용.
    /// </summary>
    public void RegisterProtected(string id)
    {
        if (!string.IsNullOrEmpty(id)) _protectedIds.Add(id);
    }

    /// <summary>
    /// 타겟을 함께 등록하는 보호. 씬 전환 정리 시 target이 파괴되었으면(== null)
    /// 해당 id 트윈을 자동 Kill하고 보호 목록에서 제거 → 좀비 트윈/누수 방지.
    /// target이 살아있는 동안만 보호된다.
    /// 예) _spinnerTween.SetId(Constants.Tween.ID_PERSISTENT);
    ///     TweenManager.Instance.RegisterProtected(Constants.Tween.ID_PERSISTENT, spinnerCanvasGroup);
    /// </summary>
    public void RegisterProtected(string id, Object target)
    {
        if (string.IsNullOrEmpty(id)) return;

        _protectedIds.Add(id);
        // target이 null이면 추적 불가 → id만 보호 (타겟 추적 없음)
        if (target != null) _protectedTargets[id] = target;
    }

    /// <summary>보호 id 해제. 이후 씬 전환부터 해당 id 트윈도 정리 대상이 됨.</summary>
    public void UnregisterProtected(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _protectedIds.Remove(id);
        _protectedTargets.Remove(id);
    }

    // ─────────────────────────────────────────────
    // 씬 전환 트윈 정리
    // ─────────────────────────────────────────────

    /// <summary>
    /// 씬에 종속된 트윈 일괄 정리. 보호 id로 등록된 트윈(페이드/BGM/상시연출)은 살려둠.
    /// 단 보호 id라도 추적 타겟이 파괴됐으면 자동 정리(PurgeDeadProtected).
    /// </summary>
    /// <param name="complete">true면 완료 콜백 실행 후 종료, false(기본)면 즉시 중단.</param>
    public void KillAllSceneTweens(bool complete = false)
    {
        // ① 보호 id 중 타겟이 파괴된 것을 먼저 정리 (좀비 트윈 방지).
        //    여기서 Kill + 보호 해제되므로, 이후 단계는 살아있는 보호 트윈만 다룸.
        PurgeDeadProtected(complete);

        int killed;

        // ② 보호 대상이 없으면 빠른 경로 (전체 KillAll)
        if (_protectedIds.Count == 0)
        {
            killed = DOTween.KillAll(complete);
        }
        else
        {
            // 보호 id를 제외하고 개별 Kill (재생 중 + 일시정지 트윈 모두 순회)
            killed = KillUnprotected(complete);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GameLogger.Log(ELogCategory.System,
            $"씬 전환 트윈 정리: {killed}개 (보호 {_protectedIds.Count}종, complete={complete})");
#endif
    }

    /// <summary>
    /// 추적 타겟이 파괴된 보호 id를 찾아 해당 id 트윈을 Kill하고 보호 목록에서 제거.
    /// Unity Object의 오버로드된 == 로 "파괴됨" 상태(== null)까지 잡는다.
    /// </summary>
    private void PurgeDeadProtected(bool complete)
    {
        if (_protectedTargets.Count == 0) return;

        _deadIdBuffer.Clear();

        // Dictionary 직접 순회 중 제거는 불가 → 죽은 id만 버퍼에 수집 후 일괄 제거.
        // foreach on Dictionary는 Enumerator가 struct라 박싱 없음 (GC Zero).
        foreach (var pair in _protectedTargets)
        {
            // target == null : 파괴됐거나 원래 null. Unity 오버로드 == 사용
            if (pair.Value == null)
                _deadIdBuffer.Add(pair.Key);
        }

        for (int i = 0; i < _deadIdBuffer.Count; i++)
        {
            string id = _deadIdBuffer[i];
            DOTween.Kill(id, complete);     // 해당 id 트윈 즉시 정리
            _protectedIds.Remove(id);       // 보호 해제 → 다음부터 일반 정리 대상
            _protectedTargets.Remove(id);
        }

        _deadIdBuffer.Clear();
    }

    /// <summary>
    /// 보호 id를 제외한 모든 활성 트윈 Kill.
    /// 순회 중 Kill하면 내부 컬렉션이 변경되므로, 먼저 버퍼에 모은 뒤 일괄 Kill.
    /// </summary>
    private int KillUnprotected(bool complete)
    {
        _killBuffer.Clear();

        // 재생 중 + 일시정지 트윈을 모두 포함해야 보호 외 트윈을 빠짐없이 정리
        CollectUnprotected(DOTween.PlayingTweens()); // null 가능
        CollectUnprotected(DOTween.PausedTweens());  // null 가능

        int count = _killBuffer.Count;
        for (int i = 0; i < count; i++)
            _killBuffer[i].Kill(complete);

        _killBuffer.Clear();
        return count;
    }

    /// <summary>리스트에서 보호 id가 아닌 트윈만 버퍼에 수집.</summary>
    private void CollectUnprotected(List<Tween> source)
    {
        if (source == null) return;

        for (int i = 0; i < source.Count; i++)
        {
            Tween tween = source[i];
            if (tween == null || !tween.IsActive()) continue;

            // tween.id는 DOTween상 object 타입 → string으로 캐스팅 후 보호 집합과 비교.
            // string id가 아니거나 id가 없으면(null) 보호 대상 아님 → 정리됨
            if (tween.id is string id && _protectedIds.Contains(id)) continue;

            _killBuffer.Add(tween);
        }
    }

    // ─────────────────────────────────────────────
    // 범용 트윈 제어
    // ─────────────────────────────────────────────

    /// <summary>특정 타겟(객체)의 트윈만 정리. id가 아니라 SetTarget/트윈 대상 기준.</summary>
    public void Kill(object target, bool complete = false)
        => DOTween.Kill(target, complete);

    /// <summary>특정 id(string)의 트윈만 정리.</summary>
    public void KillById(string id, bool complete = false)
        => DOTween.Kill(id, complete);

    /// <summary>모든 트윈 즉시 완료(콜백 실행). 보호 id 무관 — 명시 호출용.</summary>
    public void CompleteAll() => DOTween.CompleteAll();

    public void PauseAll() => DOTween.PauseAll();
    public void PlayAll() => DOTween.PlayAll();
}
