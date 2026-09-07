// =================================================================
// [스크립트 목적]  Cinemachine 3.x 가상 카메라 관리. 등록·전환(Priority)·셰이크
// [주요 변수]      - _vcams        : 등록된 가상 카메라 (이름 → CinemachineCamera)
//                  - _activePriority / _inactivePriority : 전환용 우선순위 값
//                  - _mainCamera   : 메인 Camera (브레인 부착)
// [의존 관계]      - ManagerBase<CameraManager>, Unity.Cinemachine (3.x)
// [주의]           Cinemachine 3.x는 'CinemachineCamera' (구 CinemachineVirtualCamera)
//                  ImpulseSource 미존재 시 Transform 흔들기 폴백
// [개선]           RefreshMainCamera 추가 — DontDestroyOnLoad 매니저라
//                  씬 전환 시 _mainCamera가 stale(파괴된 씬 카메라) 되는 문제 해결.
//                  SceneFlowManager가 씬 로드 완료마다 호출 → 항상 현재 씬 카메라 보장.
// [InitOrder]      85
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Cinemachine;
using UnityEngine;

public class CameraManager : ManagerBase<CameraManager>
{
    public override int InitOrder => 85;

    [Header("카메라 설정")]
    [Tooltip("메인 카메라 (CinemachineBrain 부착). 미할당 시 Camera.main 자동 탐색.\n" +
             "DontDestroyOnLoad 영속 카메라를 쓸 거면 여기 연결, 씬마다 카메라가 다르면 비워둠")]
    [SerializeField] private Camera _mainCamera;

    [Tooltip("활성 vcam 우선순위")]
    [SerializeField] private int _activePriority = 20;

    [Tooltip("비활성 vcam 우선순위")]
    [SerializeField] private int _inactivePriority = 10;

    [Header("셰이크 폴백 설정")]
    [Tooltip("ImpulseSource 없을 때 사용할 Transform 흔들기 강도 배수")]
    [SerializeField] private float _fallbackShakeMultiplier = 0.3f;

    private readonly Dictionary<string, CinemachineCamera> _vcams = new();
    private CinemachineCamera _activeVcam;
    private CinemachineImpulseSource _impulseSource;
    private CancellationTokenSource _shakeCts;

    // 폴백 셰이크 기준(원래) 위치. 셰이크 시작 시 1회 캡처, 종료 시 복원.
    // 셰이크가 흔들어 놓은 위치를 새 셰이크가 원위치로 오인하는 드리프트 방지.
    private bool _isFallbackShaking;
    private Vector3 _shakeOriginLocalPos;

    // 사용자가 Inspector에서 영속 카메라를 직접 연결했는지 여부.
    // true면 씬 전환 시 재탐색하지 않음 (영속 카메라 유지).
    private bool _hasPersistentCamera;

    public Camera MainCamera => _mainCamera;
    public CinemachineCamera ActiveVcam => _activeVcam;

    /// <summary>메인 카메라가 바뀌었을 때 알림 (UIManager가 Canvas 카메라 재연결용).</summary>
    public event Action<Camera> OnMainCameraChanged;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // Inspector에 직접 연결돼 있으면 영속 카메라로 간주 (씬 전환 시 재탐색 안 함)
        _hasPersistentCamera = _mainCamera != null;

        if (_mainCamera == null)
            _mainCamera = Camera.main;

        if (_mainCamera == null)
            GameLogger.LogWarning(ELogCategory.System, "CameraManager: 메인 카메라 없음");

        // ImpulseSource는 선택적 (없으면 폴백 흔들기)
        CacheImpulseSource();

        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        StopShakeAndRestore();
        return UniTask.CompletedTask;
    }

    // 진행 중 폴백 셰이크를 멈추고 카메라를 원위치로 복원.
    // 종료 시 셰이크의 finally가 실행되지 못해 카메라가 흔들린 위치로 남는 것 방지.
    private void StopShakeAndRestore()
    {
        _shakeCts?.Cancel();
        _shakeCts?.Dispose();
        _shakeCts = null;

        if (_isFallbackShaking && _mainCamera != null)
            _mainCamera.transform.localPosition = _shakeOriginLocalPos;
        _isFallbackShaking = false;
    }

    // ─────────────────────────────────────────────
    // 메인 카메라 재탐색 (씬 전환 대응)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 현재 씬의 메인 카메라를 다시 찾아 _mainCamera에 갱신.
    /// SceneFlowManager.OnSceneLoadComplete에서 호출 권장.
    /// CameraManager는 DontDestroyOnLoad라 씬 전환 시 _mainCamera가
    /// 파괴된 이전 씬 카메라를 가리키는(stale) 문제를 방지한다.
    /// 영속 카메라(Inspector 직접 연결)를 쓰는 경우엔 재탐색하지 않는다.
    /// </summary>
    public void RefreshMainCamera()
    {
        // 영속 카메라를 직접 연결한 경우: 살아있으면 그대로 유지
        if (_hasPersistentCamera && _mainCamera != null)
            return;

        var found = Camera.main; // MainCamera 태그 기준 (씬당 1개 가정)
        if (found == null)
        {
            GameLogger.LogWarning(ELogCategory.System,
                "RefreshMainCamera: 현재 씬에 MainCamera 태그 카메라 없음");
            return;
        }

        bool changed = found != _mainCamera;
        _mainCamera = found;

        if (changed)
        {
            CacheImpulseSource();
            OnMainCameraChanged?.Invoke(_mainCamera);
        }
    }

    // ImpulseSource 재캐싱 (카메라 교체 시 함께 갱신)
    private void CacheImpulseSource()
    {
        _impulseSource = null;
        if (_mainCamera != null)
            _mainCamera.TryGetComponent(out _impulseSource);
    }

    // ─────────────────────────────────────────────
    // vcam 등록 / 전환
    // ─────────────────────────────────────────────

    public void RegisterVcam(string id, CinemachineCamera vcam)
    {
        if (string.IsNullOrEmpty(id) || vcam == null) return;

        _vcams[id] = vcam;
        vcam.Priority = _inactivePriority;

        // 첫 등록 카메라를 활성으로
        if (_activeVcam == null)
        {
            _activeVcam = vcam;
            vcam.Priority = _activePriority;
        }
    }

    public void UnregisterVcam(string id)
    {
        if (_vcams.TryGetValue(id, out var vcam))
        {
            if (_activeVcam == vcam) _activeVcam = null;
            _vcams.Remove(id);
        }
    }

    /// <summary>등록된 vcam으로 전환. Priority 조정으로 Cinemachine이 블렌딩 처리.</summary>
    public void SwitchTo(string id)
    {
        if (!_vcams.TryGetValue(id, out var target))
        {
            GameLogger.LogWarning(ELogCategory.System, $"vcam 없음: {id}");
            return;
        }

        // 기존 활성 카메라 우선순위 낮춤
        if (_activeVcam != null)
            _activeVcam.Priority = _inactivePriority;

        target.Priority = _activePriority;
        _activeVcam = target;
    }

    public CinemachineCamera GetVcam(string id)
        => _vcams.TryGetValue(id, out var vcam) ? vcam : null;

    // ─────────────────────────────────────────────
    // 셰이크
    // ─────────────────────────────────────────────

    /// <summary>카메라 셰이크. ImpulseSource 있으면 그것을, 없으면 Transform 폴백.</summary>
    public void Shake(float force = 1f, float duration = 0.3f)
    {
        // 옵션에서 화면 흔들림을 끈 경우 무시 (접근성: 멀미 완화). SettingsManager 없으면 통과(기본 동작)
        if (SettingsManager.HasInstance && !SettingsManager.Instance.Current.ScreenShakeEnabled)
            return;

        if (_impulseSource != null)
        {
            _impulseSource.GenerateImpulseWithForce(force);
            return;
        }

        // 폴백: 메인 카메라 Transform 직접 흔들기
        if (_mainCamera == null) return;

        // 진행 중인 셰이크가 있으면 먼저 취소 + 원위치 즉시 동기 복원
        // (이전 셰이크의 finally가 다음 프레임에 실행되기 전에 새 셰이크가
        //  흔들린 위치를 원위치로 오인 캡처하는 드리프트를 차단)
        _shakeCts?.Cancel();
        _shakeCts?.Dispose();
        if (_isFallbackShaking)
        {
            _mainCamera.transform.localPosition = _shakeOriginLocalPos;
            _isFallbackShaking = false;
        }

        // 진짜 원위치에서 기준 캡처
        _shakeOriginLocalPos = _mainCamera.transform.localPosition;
        _isFallbackShaking = true;

        _shakeCts = new CancellationTokenSource();
        FallbackShakeAsync(force * _fallbackShakeMultiplier, duration, _shakeCts.Token).Forget();
    }

    private async UniTaskVoid FallbackShakeAsync(float magnitude, float duration, CancellationToken token)
    {
        if (_mainCamera == null)
        {
            _isFallbackShaking = false;
            return;
        }

        var camTransform = _mainCamera.transform;
        float elapsed = 0f;

        try
        {
            while (elapsed < duration)
            {
                if (token.IsCancellationRequested) break;

                float damper = 1f - (elapsed / duration); // 점점 약해짐
                float offsetX = UnityEngine.Random.Range(-1f, 1f) * magnitude * damper;
                float offsetY = UnityEngine.Random.Range(-1f, 1f) * magnitude * damper;
                // 매니저가 보관한 진짜 원위치 기준으로 오프셋 (드리프트 방지)
                camTransform.localPosition = _shakeOriginLocalPos + new Vector3(offsetX, offsetY, 0f);

                elapsed += Time.unscaledDeltaTime;
                await UniTask.Yield(token);
            }
        }
        finally
        {
            // 정상 완료 시에만 여기서 복원. 취소(새 셰이크)된 경우엔 Shake가 이미 복원·재캡처했으므로
            // 새 셰이크의 기준(_shakeOriginLocalPos)·진행 상태를 건드리지 않도록 가드.
            if (!token.IsCancellationRequested)
            {
                camTransform.localPosition = _shakeOriginLocalPos;
                _isFallbackShaking = false;
            }
        }
    }

    // ─────────────────────────────────────────────
    // 헬퍼
    // ─────────────────────────────────────────────

    /// <summary>활성 vcam의 Follow/LookAt 타겟 설정 (플레이어 변경 등).</summary>
    public void SetTarget(Transform follow, Transform lookAt = null)
    {
        if (_activeVcam == null) return;
        _activeVcam.Follow = follow;
        _activeVcam.LookAt = lookAt != null ? lookAt : follow;
    }

    protected override void OnDestroy()
    {
        StopShakeAndRestore();
        base.OnDestroy();
    }
}
