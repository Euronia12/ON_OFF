// =================================================================
// [스크립트 목적]  BGM 크로스페이드 + SFX 재생. AudioMixer 연동 + 채널별 볼륨
// [주요 변수]      - _bgmSourceA / _bgmSourceB : BGM 크로스페이드용 2채널
//                  - _sfxSources              : SFX 풀 (라운드로빈 + 재생 중 우회)
//                  - _audioMixer              : 채널별 볼륨 제어용 믹서
// [의존 관계]      - ManagerBase<AudioManager>, ResourceManager, SettingsManager
// [개선]           SFX 라운드로빈 시 재생 중인 소스 건너뛰기 (끊김 방지)
//                  CrossFade 진행 중 재호출 시 진행 중 트윈 정리
//                  같은 곡 재요청은 재시작 없이 볼륨만 페이드 (맵/전투/보상 BGM 공유용)
//                  "같은 곡" 판정은 크로스페이드 진행 중이면 전환 '목표곡' 기준
//                  (이벤트→이벤트처럼 전환 도중 원래 곡으로 되돌아오는 요청 대응)
// [InitOrder]      50
// =================================================================
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;

public class AudioManager : ManagerBase<AudioManager>
{
    public override int InitOrder => 50;

    [Header("AudioMixer 설정")]
    [Tooltip("AudioMixer 에셋. Exposed Parameter는 'MasterVolume' / 'BgmVolume' / 'SfxVolume' / 'UiVolume' 명칭 권장")]
    [SerializeField] private AudioMixer _audioMixer;

    [Tooltip("Master 채널 Exposed Parameter 이름")]
    [SerializeField] private string _masterParam = "MasterVolume";
    [SerializeField] private string _bgmParam = "BgmVolume";
    [SerializeField] private string _sfxParam = "SfxVolume";
    [SerializeField] private string _uiParam = "UiVolume";

    [Tooltip("Bgm 채널 AudioMixerGroup")]
    [SerializeField] private AudioMixerGroup _bgmGroup;
    [SerializeField] private AudioMixerGroup _sfxGroup;
    [SerializeField] private AudioMixerGroup _uiGroup;

    [Header("UI SFX 카탈로그")]
    [Tooltip("UI 키별 실제 SFX, 볼륨, 피치를 설정하는 카탈로그. 미등록 키는 기존 키로 재생")]
    [SerializeField] private UiAudioCatalog _uiAudioCatalog;

    [Header("BGM 전환")]
    [Tooltip("크로스페이드 시 '이전 곡'이 사라지는 시간 비율 (crossfade 시간 대비 0~1). " +
             "낮을수록 이전 곡 잔재가 빨리 사라진다. 새 곡은 crossfade 전체 시간에 걸쳐 " +
             "천천히 들어와 끊김이 없고, 이전 곡만 앞부분에서 빠르게 빠진다. 0.4~0.5 권장")]
    [Range(0.1f, 1f)]
    [SerializeField] private float _bgmFadeOutRatio = 0.45f;

    [Header("SFX 풀 설정")]
    [Tooltip("SFX 동시 재생 가능 채널 수. 부족 시 가장 오래된 소스 덮어씀")]
    [Range(4, 64)]
    [SerializeField] private int _sfxSourceCount = 16;

    [Tooltip("randomPitch=true일 때 적용할 피치 배율 범위 (x=최소, y=최대). 타격·발사음 단조로움 방지용")]
    [SerializeField] private Vector2 _pitchRandomRange = new Vector2(0.9f, 1.1f);

    private AudioSource _bgmSourceA;
    private AudioSource _bgmSourceB;
    private AudioSource _activeBgmSource;
    private AudioSource _bgmPreviewSource;
    private int _bgmSourceAGeneration;
    private int _bgmSourceBGeneration;
    private AudioSource[] _sfxSources;
    private float[] _sfxStartTime;   // 각 소스의 재생 시작 시각 (oldest-steal 판정용)
    private int _sfxIndex;            // 빈 소스 탐색 시작 커서 (라운드로빈)

    // BGM 전환 세대 번호. 전환 요청마다 증가 → 비동기 페이드 작업이 자기 세대가
    // 최신인지 매 프레임 확인하고, 낡았으면(새 요청 발생) 자신이 만진 소스를 정리하고 종료.
    // CTS 취소 타이밍에 의존하지 않아 즉시전환/크로스페이드/페이드아웃이 섞여도 소스 상태가 안 꼬임.
    private int _bgmGeneration;

    // Addressable BGM 로드 요청 세대. 늦게 완료된 이전 요청이 최신 곡을 덮어쓰지 못하게 한다.
    private int _bgmRequestGeneration;

    // BGM 볼륨 페이드 세대. 볼륨 변경끼리는 서로를 취소하되 곡 전환(_bgmGeneration)은 건드리지
    // 않는다. 곡 전환·정지 시에도 증가시켜, 낡은 볼륨 페이드가 새 곡의 볼륨을 덮어쓰지 못하게 한다.
    private int _bgmVolumeGeneration;

    // StopBgm 의 페이드아웃이 진행 중인지. 페이드아웃 동안에도 AudioSource.isPlaying 은 true 라
    // "아직 그 곡이 흐르는 중"으로 오판하기 쉽다. 논리적으로는 정지 중임을 이 플래그로 구분한다.
    private bool _isBgmStopping;

    // 옵션 BGM 미리듣기 로드 세대. 정지 또는 새 요청 뒤에 완료된 이전 비동기 로드를 무시한다.
    private int _bgmPreviewGeneration;

    // 진행 중인 크로스페이드의 목표(= 지금 볼륨이 올라오는 중인 곡/소스/볼륨).
    // 전환이 끝나기 전까지 _activeBgmSource 는 아직 '이전 곡'을 가리키므로,
    // "같은 곡인가" 판정과 다음 전환의 페이드아웃 대상은 이쪽을 기준으로 잡아야 한다.
    private AudioClip _pendingBgmClip;
    private AudioSource _pendingBgmSource;
    private float _pendingBgmVolume;
    private int _pendingBgmGeneration;

    /// <summary>곡 전환(크로스페이드)이 아직 진행 중인지. 낡은 세대의 목표는 무시한다.</summary>
    private bool HasPendingBgmTransition =>
        _pendingBgmClip != null && _pendingBgmGeneration == _bgmGeneration;

    // 종료가 시작된 뒤 늦게 완료된 Addressables 로드/비동기 UI 콜백이
    // 오디오를 다시 재생하지 못하게 하는 수명 가드.
    private bool _isShuttingDown;

    /// <summary>
    /// BGM 이 논리적으로 재생 중인지. 정지 페이드아웃이 진행 중이면(곧 멈출 예정) false 다.
    /// </summary>
    public bool IsBgmPlaying =>
        !_isBgmStopping && _activeBgmSource != null && _activeBgmSource.isPlaying;

    /// <summary>
    /// 현재 BGM 의 재생 위치(초). 재생 중이 아니면 0.
    /// 같은 곡의 변주로 교체할 때(보스 페이즈 등) 위치를 이어받는 용도.
    /// </summary>
    public float BgmPlaybackTime =>
        _activeBgmSource != null && _activeBgmSource.isPlaying ? _activeBgmSource.time : 0f;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        _isShuttingDown = false;
        _isBgmStopping = false;

        // BGM 2채널 (크로스페이드용)
        _bgmSourceA = CreateSource("BGM_A", _bgmGroup, loop: true);
        _bgmSourceB = CreateSource("BGM_B", _bgmGroup, loop: true);
        _activeBgmSource = _bgmSourceA;
        _bgmPreviewSource = CreateSource("BGM_Preview", _bgmGroup, loop: true);

        // SFX 풀
        _sfxSources = new AudioSource[_sfxSourceCount];
        _sfxStartTime = new float[_sfxSourceCount];

        for (int i = 0; i < _sfxSourceCount; i++)
            _sfxSources[i] = CreateSource($"SFX_{i:D2}", _sfxGroup, loop: false);

        // SettingsManager 연동 (있으면 값 반영)
        if (SettingsManager.HasInstance)
        {
            ApplyVolumesFromSettings(SettingsManager.Instance.Current);
            SettingsManager.Instance.OnSettingsChanged += OnSettingsChanged;
        }

        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        if (SettingsManager.HasInstance)
            SettingsManager.Instance.OnSettingsChanged -= OnSettingsChanged;

        StopAllAudioImmediate();
        return UniTask.CompletedTask;
    }

    // 창 X버튼 등 매니저 역순 종료(RequestQuit)를 타지 않는 종료 경로에서도
    // 오디오를 안전하게 멈춘다. RequestQuit 경로와 이중 호출돼도 idempotent 하다.
    private void OnApplicationQuit()
    {
        StopAllAudioImmediate();
    }

    /// <summary>
    /// 종료 시 모든 오디오를 즉시 정지하고 클립 참조를 끊는다.
    /// Addressable AudioClip 해제·프로세스 종료 전에 재생을 멈춰야
    /// 오디오 스레드가 파괴된 클립을 접근하는 네이티브 크래시를 막는다.
    /// </summary>
    private void StopAllAudioImmediate()
    {
        _isShuttingDown = true;

        // 진행 중인 BGM 페이드 작업 무효화 (세대 증가 → 다음 프레임에 스스로 종료)
        _bgmGeneration++;
        _bgmRequestGeneration++;
        _bgmVolumeGeneration++;
        StopBgmPreview();

        StopBgm(0f);
        StopAllSfx();
        if (_bgmSourceA != null) _bgmSourceA.clip = null;
        if (_bgmSourceB != null) _bgmSourceB.clip = null;

        // Stop()만으로는 AudioSource가 Addressable AudioClip 참조를 계속 보유한다.
        // 종료 중 다른 경로에서 핸들이 해제되더라도 네이티브 오디오가 파괴된 클립을
        // 다시 만질 여지를 없애기 위해 모든 SFX 참조도 명시적으로 끊는다.
        if (_sfxSources != null)
        {
            for (int i = 0; i < _sfxSources.Length; i++)
            {
                if (_sfxSources[i] != null)
                    _sfxSources[i].clip = null;
            }
        }
    }

    private AudioSource CreateSource(string name, AudioMixerGroup group, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform);
        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = loop;
        src.outputAudioMixerGroup = group;
        return src;
    }

    private void OnSettingsChanged(GameSettings s)
    {
        ApplyVolumesFromSettings(s);
    }

    /// <summary>
    /// 설정값을 믹서에 일괄 반영. 채널별 음소거(BgmMuted 등)와 전체 음소거(IsMuted)는
    /// GetEffectiveVolume이 OR 합성해 0 또는 볼륨값을 반환 → 음소거 해제 시 슬라이더 값 그대로 복구.
    /// </summary>
    private void ApplyVolumesFromSettings(GameSettings s)
    {
        // SettingsManager 초기화/리셋 전환 중 설정 인스턴스가 아직 준비되지 않은 이벤트는
        // 마지막으로 적용된 믹서 값을 유지한다. 여기서 예외가 나면 옵션 기본값 초기화 전체가 중단된다.
        if (s == null) return;

        SetVolume(EAudioChannel.Master, s.GetEffectiveVolume(EAudioChannel.Master));
        SetVolume(EAudioChannel.Bgm, s.GetEffectiveVolume(EAudioChannel.Bgm));
        SetVolume(EAudioChannel.Sfx, s.GetEffectiveVolume(EAudioChannel.Sfx));
        SetVolume(EAudioChannel.Ui, s.GetEffectiveVolume(EAudioChannel.Ui));
    }

    // ─────────────────────────────────────────────
    // 볼륨
    // ─────────────────────────────────────────────

    /// <summary>채널 볼륨 설정. linear 0~1 → dB 변환.</summary>
    public void SetVolume(EAudioChannel channel, float linear)
    {
        if (_audioMixer == null) return;

        linear = Mathf.Clamp(linear, 0.0001f, 1f);
        float db = Mathf.Log10(linear) * 20f;

        string param = channel switch
        {
            EAudioChannel.Master => _masterParam,
            EAudioChannel.Bgm => _bgmParam,
            EAudioChannel.Sfx => _sfxParam,
            EAudioChannel.Ui => _uiParam,
            _ => _masterParam
        };

        _audioMixer.SetFloat(param, db);
    }

    public float GetVolume(EAudioChannel channel)
    {
        if (_audioMixer == null) return 1f;

        string param = channel switch
        {
            EAudioChannel.Master => _masterParam,
            EAudioChannel.Bgm => _bgmParam,
            EAudioChannel.Sfx => _sfxParam,
            EAudioChannel.Ui => _uiParam,
            _ => _masterParam
        };

        if (_audioMixer.GetFloat(param, out float db))
            return Mathf.Pow(10f, db / 20f);
        return 1f;
    }

    // ─────────────────────────────────────────────
    // BGM
    // ─────────────────────────────────────────────

    /// <summary>BGM 재생. crossfade 0이면 즉시 전환.</summary>
    /// <param name="startTime">
    /// 재생 시작 위치(초). 0이면 곡 처음부터. 같은 곡의 변주로 교체할 때
    /// BgmPlaybackTime 을 넘기면 마디가 어긋나지 않고 같은 구간에서 이어진다.
    /// 곡 길이를 넘으면 길이로 나눈 나머지 위치로 접어 넣는다.
    /// </param>
    public void PlayBgm(AudioClip clip, float crossfade = 1f, float volume = 1f, float startTime = 0f)
    {
        if (_isShuttingDown) return;
        PlayBgmInternal(clip, crossfade, volume, startTime, ++_bgmRequestGeneration);
    }

    private void PlayBgmInternal(
        AudioClip clip, float crossfade, float volume, float startTime, int requestGeneration)
    {
        if (_isShuttingDown || clip == null) return;
        if (requestGeneration != _bgmRequestGeneration) return;

        // 크로스페이드가 진행 중이면 '지금 흐르는 곡'은 _activeBgmSource(아직 이전 곡)가 아니라
        // 볼륨이 올라오는 중인 전환 목표곡이다. 이 구분이 없으면 이벤트→이벤트처럼 같은 곡으로
        // 되돌아오는 요청이, 아직 페이드아웃 중인 '이전 곡'을 보고 "이미 재생 중"으로 오판해
        // 전환을 통째로 건너뛴다(그 사이 진행 중이던 맵 BGM 전환이 그대로 완료되어 버린다).
        bool hasPending = HasPendingBgmTransition;

        // 정지 페이드아웃 중에는 아래 두 분기를 모두 제외한다 — isPlaying 이 아직 true 라
        // 걸리면 새 재생을 시작하지 않은 채 페이드아웃만 끝나 무음이 된다(볼륨 페이드는 같은
        // 세대의 정지 페이드를 취소하지 못한다).
        if (!_isBgmStopping)
        {
            if (hasPending)
            {
                // 같은 곡으로 가는 전환이 이미 진행 중 → 재시작 없이 목표 볼륨만 갱신.
                if (_pendingBgmClip == clip)
                {
                    _pendingBgmVolume = volume;
                    return;
                }
            }
            // 이미 같은 곡이 흐르고 있으면 다시 틀지 않고 볼륨만 목표값으로 옮긴다.
            // 맵·전투·전리품이 스테이지별로 같은 BGM 을 공유하면서 볼륨으로만 상황을 구분하는 경로.
            else if (_activeBgmSource != null &&
                     _activeBgmSource.clip == clip && _activeBgmSource.isPlaying)
            {
                FadeBgmVolume(volume, crossfade);
                return;
            }
        }

        // 페이드아웃 대상은 '지금 실제로 들리는' 소스다. 전환 도중이라면 이전 곡이 아니라
        // 방금까지 볼륨이 올라오던 목표 소스가 그에 해당한다(이전 곡은 이미 거의 무음).
        AudioSource outgoing = hasPending && _pendingBgmSource != null
            ? _pendingBgmSource
            : _activeBgmSource;

        // 새 전환 요청 → 세대 증가. 진행 중이던 페이드 작업은 다음 프레임에 자기 세대가
        // 낡았음을 감지하고 스스로 정리·종료한다.
        int generation = ++_bgmGeneration;

        // 이전 곡을 향해 진행 중이던 볼륨 페이드 무효화 (새 곡의 볼륨을 덮어쓰지 않게).
        _bgmVolumeGeneration++;

        // 새 곡 전환이 확정됐으므로 정지 진행 상태를 해제한다(위 세대 증가로 정지 페이드도 무효화됨).
        _isBgmStopping = false;

        if (crossfade <= 0f)
        {
            // 즉시 전환: 모든 BGM 소스를 정지하고 활성 소스에 바로 재생 (진행 중 페이드 잔재 제거)
            StopAllBgmSourcesExcept(null);
            ClearPendingBgmTransition();
            _activeBgmSource.clip = clip;
            _activeBgmSource.volume = volume;
            ApplyBgmStartTime(_activeBgmSource, clip, startTime);
            _activeBgmSource.Play();
            SetBgmSourceGeneration(_activeBgmSource, generation);
            return;
        }

        CrossFadeAsync(clip, crossfade, volume, startTime, generation, outgoing).Forget();
    }

    /// <summary>전환 목표 정보 해제. 최신 세대를 소유한 경로에서만 호출한다.</summary>
    private void ClearPendingBgmTransition()
    {
        _pendingBgmClip = null;
        _pendingBgmSource = null;
    }

    /// <summary>Addressable/Resource 키로 BGM 로드 후 재생.</summary>
    public async UniTask PlayBgmAsync(string clipKey, float crossfade = 1f, float volume = 1f,
        CancellationToken token = default)
    {
        if (_isShuttingDown || string.IsNullOrEmpty(clipKey) || !ResourceManager.HasInstance) return;
        int requestGeneration = ++_bgmRequestGeneration;
        var clip = await ResourceManager.Instance.LoadAsync<AudioClip>(clipKey, token);
        if (clip != null) PlayBgmInternal(clip, crossfade, volume, startTime: 0f, requestGeneration);
    }

    /// <summary>
    /// 기본 키의 BGM을 로드하고, 키가 비었거나 로드에 실패하면 폴백 키를 한 번 시도한다.
    /// 두 키 모두 로드하지 못하면 현재 BGM을 변경하지 않는다.
    /// </summary>
    public async UniTask PlayBgmWithFallbackAsync(
        string clipKey,
        string fallbackClipKey,
        float crossfade = 1f,
        float volume = 1f,
        CancellationToken token = default)
    {
        if (_isShuttingDown || !ResourceManager.HasInstance) return;

        int requestGeneration = ++_bgmRequestGeneration;
        AudioClip clip = null;

        if (!string.IsNullOrWhiteSpace(clipKey))
            clip = await ResourceManager.Instance.LoadAsync<AudioClip>(clipKey, token);

        // 로드 중 더 최신 BGM 요청이 들어왔으면 폴백 로드와 재생 모두 중단한다.
        if (_isShuttingDown || requestGeneration != _bgmRequestGeneration) return;

        if (clip == null &&
            !string.IsNullOrWhiteSpace(fallbackClipKey) &&
            !string.Equals(clipKey, fallbackClipKey, StringComparison.Ordinal))
        {
            clip = await ResourceManager.Instance.LoadAsync<AudioClip>(fallbackClipKey, token);
        }

        if (clip != null)
            PlayBgmInternal(clip, crossfade, volume, startTime: 0f, requestGeneration);
    }

    /// <summary>
    /// 지정 위치에서 재생을 시작하도록 소스를 맞춘다. clip 할당 후 Play() 전에 호출해야 한다.
    /// 변주 곡이 원곡과 길이가 달라도 범위를 벗어나지 않도록 곡 길이로 접어 넣는다
    /// (범위를 벗어난 값을 넣으면 Unity 가 예외를 던진다).
    /// </summary>
    private static void ApplyBgmStartTime(AudioSource source, AudioClip clip, float startTime)
    {
        if (source == null || clip == null || startTime <= 0f) return;

        float length = clip.length;
        if (length <= 0f) return;

        source.time = startTime % length;
    }

    private async UniTaskVoid CrossFadeAsync(AudioClip newClip, float duration, float targetVolume,
        float startTime, int generation, AudioSource oldSource)
    {
        var newSource = oldSource == _bgmSourceA ? _bgmSourceB : _bgmSourceA;

        // 새 소스 준비 (이전 페이드 잔재가 있을 수 있으므로 강제 리셋)
        newSource.Stop();
        newSource.clip = newClip;
        newSource.volume = 0f;
        ApplyBgmStartTime(newSource, newClip, startTime);
        newSource.Play();
        SetBgmSourceGeneration(newSource, generation);

        // 페이드아웃 대상도 이 세대가 소유한다. 앞선 전환이 "내가 틀던 소스"라며
        // 이 소스를 도중에 정지시키면(StopBgmSourceIfOwned) 들리던 곡이 뚝 끊긴다.
        if (oldSource != null)
            SetBgmSourceGeneration(oldSource, generation);

        // 전환 목표 공개 — 전환이 끝나기 전에 도착한 재생 요청은 이 값을 기준으로 판단한다.
        _pendingBgmClip = newClip;
        _pendingBgmSource = newSource;
        _pendingBgmVolume = targetVolume;
        _pendingBgmGeneration = generation;

        float elapsed = 0f;
        float startOldVolume = oldSource != null ? oldSource.volume : 0f;

        // 비대칭 크로스페이드:
        //  - 이전 곡: 앞부분(fadeOutDuration)에서 빠르게 0 으로 → 화면 전환 후 잔재 최소화.
        //  - 새 곡  : 전체 duration 에 걸쳐 서서히 → 진입은 부드럽게(끊김 방지).
        float fadeOutDuration = Mathf.Max(0.01f, duration * _bgmFadeOutRatio);

        while (elapsed < duration)
        {
            // 씬 종료/중복 매니저 제거처럼 정상 Shutdown을 거치지 않고 GameObject가
            // 파괴될 수도 있다. 이 경우 세대 번호는 같아도 자식 AudioSource는 이미
            // MissingReference 상태이므로 Unity Object 수명을 먼저 확인한다.
            if (this == null || newSource == null)
                return;

            // 내 세대가 더 이상 최신이 아니면(새 전환 요청 발생) 정리하고 종료.
            // 내가 만든 newSource를 정지해 어정쩡한 볼륨으로 남는 것 방지.
            // _activeBgmSource는 건드리지 않음(최신 세대 작업이 책임).
            if (generation != _bgmGeneration)
            {
                StopBgmSourceIfOwned(newSource, generation);
                return;
            }

            elapsed += Time.unscaledDeltaTime;
            float tIn = Mathf.Clamp01(elapsed / duration);
            // 목표 볼륨은 필드에서 읽는다 — 전환 중 같은 곡이 다른 볼륨으로 재요청되면
            // (맵→전리품 등) 곡을 재시작하지 않고 목표만 바뀐다.
            newSource.volume = Mathf.Lerp(0f, _pendingBgmVolume, tIn);
            if (oldSource != null)
            {
                float tOut = Mathf.Clamp01(elapsed / fadeOutDuration);
                oldSource.volume = Mathf.Lerp(startOldVolume, 0f, tOut);
            }
            await UniTask.Yield();
        }

        // 정상 완료 — 최신 세대 확인 후 확정
        if (this == null || newSource == null)
            return;

        if (generation != _bgmGeneration)
        {
            StopBgmSourceIfOwned(newSource, generation);
            return;
        }

        newSource.volume = _pendingBgmVolume;
        if (oldSource != null)
        {
            oldSource.Stop();
            oldSource.volume = 0f;
            SetBgmSourceGeneration(oldSource, 0);
        }
        _activeBgmSource = newSource;
        ClearPendingBgmTransition();
    }

    public void StopBgm(float fadeOut = 0.5f)
    {
        // 대기 중인 Addressable BGM 로드를 무효화한다. 정지 후 이전 요청이 늦게 끝나
        // BGM을 되살리는 일을 막는다.
        _bgmRequestGeneration++;

        // 진행 중이던 볼륨 페이드도 무효화 — 정지 페이드와 볼륨 페이드가 같은 소스를 두고
        // 서로 볼륨을 덮어쓰지 않게 한다.
        _bgmVolumeGeneration++;
        if (_activeBgmSource == null || !_activeBgmSource.isPlaying)
        {
            _isBgmStopping = false;
            return;
        }

        // 정지도 전환의 일종 → 세대 증가로 진행 중 페이드 무효화
        int generation = ++_bgmGeneration;
        StopAllBgmSourcesExcept(_activeBgmSource);

        if (fadeOut <= 0f)
        {
            StopAllBgmSourcesExcept(null);
            _isBgmStopping = false;
            return;
        }

        // 페이드아웃이 끝날 때까지는 isPlaying 이 true 로 남는다. 그 사이 도착한 재생 요청이
        // "이미 흐르는 중"으로 오판하지 않도록 정지 진행 중임을 표시한다.
        _isBgmStopping = true;
        FadeOutBgmAsync(fadeOut, generation).Forget();
    }

    private async UniTaskVoid FadeOutBgmAsync(float duration, int generation)
    {
        var source = _activeBgmSource;
        if (source == null) return;

        float startVolume = source.volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            if (this == null || source == null)
                return;

            // 페이드 아웃 중 새 BGM 요청이 오면(세대 변경) 조용히 종료(새 작업이 소스 관리)
            if (generation != _bgmGeneration) return;

            elapsed += Time.unscaledDeltaTime;
            source.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            await UniTask.Yield();
        }

        if (this == null || source == null || generation != _bgmGeneration) return;
        source.Stop();
        _isBgmStopping = false;
    }

    /// <summary>
    /// 재생 중인 BGM 의 볼륨만 목표값으로 옮긴다(곡은 끊기지 않고 그대로 이어진다).
    /// 맵→전투→전리품처럼 같은 곡을 공유하면서 상황별 크기만 바꿀 때 사용한다.
    /// 채널 전체를 조절하는 SetVolume(EAudioChannel.Bgm) 과 달리 유저 설정을 건드리지 않으며,
    /// 최종 음량은 이 값 × 유저의 BgmVolume 설정이 된다.
    /// </summary>
    /// <param name="targetVolume">목표 볼륨(0~1). AudioKeys.BgmVolume 상수 사용 권장.</param>
    /// <param name="fadeDuration">목표에 도달하는 시간(초). 0 이면 즉시 적용.</param>
    public void FadeBgmVolume(float targetVolume, float fadeDuration = 0.5f)
    {
        if (_isShuttingDown) return;

        targetVolume = Mathf.Clamp01(targetVolume);

        // 곡 전환이 진행 중이면 조절 대상은 이전 곡이 아니라 올라오는 중인 목표곡이다.
        // 이 경우 도달 시간은 진행 중인 크로스페이드의 남은 시간을 따른다.
        if (HasPendingBgmTransition)
        {
            _pendingBgmVolume = targetVolume;
            return;
        }

        var source = _activeBgmSource;
        if (source == null || !source.isPlaying) return;

        // 볼륨 변경 요청 → 세대 증가. 진행 중이던 볼륨 페이드는 다음 프레임에 스스로 종료한다.
        int volumeGeneration = ++_bgmVolumeGeneration;

        if (fadeDuration <= 0f)
        {
            source.volume = targetVolume;
            return;
        }

        FadeBgmVolumeAsync(source, targetVolume, fadeDuration, _bgmGeneration, volumeGeneration)
            .Forget();
    }

    private async UniTaskVoid FadeBgmVolumeAsync(
        AudioSource source,
        float targetVolume,
        float duration,
        int transitionGeneration,
        int volumeGeneration)
    {
        float startVolume = source.volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            // 씬 종료 등으로 GameObject 가 파괴됐을 수 있다(세대가 같아도 참조는 이미 죽음).
            if (this == null || source == null) return;

            // 더 최신 볼륨 요청이 왔거나 곡 자체가 전환되면, 그쪽에 소스를 넘기고 조용히 종료.
            if (volumeGeneration != _bgmVolumeGeneration) return;
            if (transitionGeneration != _bgmGeneration) return;

            elapsed += Time.unscaledDeltaTime;
            source.volume = Mathf.Lerp(startVolume, targetVolume, Mathf.Clamp01(elapsed / duration));
            await UniTask.Yield();
        }

        if (this == null || source == null) return;
        if (volumeGeneration != _bgmVolumeGeneration || transitionGeneration != _bgmGeneration) return;
        source.volume = targetVolume;
    }

    protected override void OnDestroy()
    {
        // Bootstrap shutdown 외의 파괴 경로에서도 다음 continuation이 소스를 만지지 않게 한다.
        _bgmGeneration++;
        _bgmRequestGeneration++;
        _bgmPreviewGeneration++;
        _bgmVolumeGeneration++;
        base.OnDestroy();
    }

    /// <summary>BGM 소스 전체 정지(볼륨 리셋 포함). except에 지정한 소스는 건너뜀.</summary>
    private void StopAllBgmSourcesExcept(AudioSource except)
    {
        if (_bgmSourceA != null && _bgmSourceA != except)
        {
            _bgmSourceA.Stop();
            _bgmSourceA.volume = 0f;
            _bgmSourceAGeneration = 0;
        }
        if (_bgmSourceB != null && _bgmSourceB != except)
        {
            _bgmSourceB.Stop();
            _bgmSourceB.volume = 0f;
            _bgmSourceBGeneration = 0;
        }
    }

    private void StopBgmSourceIfOwned(AudioSource source, int generation)
    {
        if (source == null || GetBgmSourceGeneration(source) != generation) return;
        source.Stop();
        source.volume = 0f;
        SetBgmSourceGeneration(source, 0);
    }

    private int GetBgmSourceGeneration(AudioSource source)
    {
        if (source == _bgmSourceA) return _bgmSourceAGeneration;
        if (source == _bgmSourceB) return _bgmSourceBGeneration;
        return 0;
    }

    private void SetBgmSourceGeneration(AudioSource source, int generation)
    {
        if (source == _bgmSourceA)
            _bgmSourceAGeneration = generation;
        else if (source == _bgmSourceB)
            _bgmSourceBGeneration = generation;
    }

    /// <summary>진행 중인 BGM 전 채널을 일시정지한다. 크로스페이드 중인 양쪽 소스도 함께 멈춘다.</summary>
    public void PauseBgm()
    {
        _bgmSourceA?.Pause();
        _bgmSourceB?.Pause();
    }

    /// <summary>PauseBgm으로 멈춘 BGM 전 채널을 원래 재생 위치에서 재개한다.</summary>
    public void ResumeBgm()
    {
        _bgmSourceA?.UnPause();
        _bgmSourceB?.UnPause();
    }

    // ─────────────────────────────────────────────
    // BGM 미리듣기
    // ─────────────────────────────────────────────

    /// <summary>
    /// 옵션 화면 전용 BGM 미리듣기. 게임에서 재생 중인 BGM 및 크로스페이드 상태와 독립적으로
    /// BGM 믹서 그룹을 통해 반복 재생한다.
    /// </summary>
    public void PlayBgmPreview(AudioClip clip, float volume = 1f)
    {
        if (_isShuttingDown || clip == null || _bgmPreviewSource == null) return;

        if (_bgmPreviewSource.clip == clip && _bgmPreviewSource.isPlaying)
            return;

        _bgmPreviewSource.Stop();
        _bgmPreviewSource.clip = clip;
        _bgmPreviewSource.volume = volume;
        _bgmPreviewSource.loop = true;
        _bgmPreviewSource.outputAudioMixerGroup = _bgmGroup;
        _bgmPreviewSource.Play();
    }

    public async UniTask PlayBgmPreviewAsync(string clipKey, float volume = 1f,
        CancellationToken token = default)
    {
        if (_isShuttingDown || string.IsNullOrEmpty(clipKey) || !ResourceManager.HasInstance) return;
        int generation = ++_bgmPreviewGeneration;
        var clip = await ResourceManager.Instance.LoadAsync<AudioClip>(clipKey, token);
        if (!_isShuttingDown && generation == _bgmPreviewGeneration && clip != null)
            PlayBgmPreview(clip, volume);
    }

    public void StopBgmPreview()
    {
        _bgmPreviewGeneration++;
        if (_bgmPreviewSource == null) return;
        _bgmPreviewSource.Stop();
        _bgmPreviewSource.clip = null;
    }

    // ─────────────────────────────────────────────
    // SFX
    // ─────────────────────────────────────────────

    /// <summary>
    /// SFX 재생. randomPitch가 true면 _pitchRandomRange 범위로 피치를 랜덤 변조.
    /// UI음·보이스·징글 등 음정이 중요한 클립은 false(기본값) 유지.
    /// </summary>
    public void PlaySfx(AudioClip clip, float volume = 1f, float pitch = 1f, bool randomPitch = false)
    {
        if (_isShuttingDown || clip == null || _sfxSources == null) return;

        int idx = GetSfxSourceIndex();
        var source = _sfxSources[idx];

        // 덮어쓰기가 실제 동작하도록 PlayOneShot 대신 clip 할당 + Play() 사용
        source.Stop();
        source.clip = clip;
        source.volume = volume;
        // randomPitch=true면 pitch 인자를 무시하고 랜덤 범위 적용 / false면 pitch 그대로
        source.pitch = randomPitch
            ? UnityEngine.Random.Range(_pitchRandomRange.x, _pitchRandomRange.y)
            : pitch;
        source.outputAudioMixerGroup = _sfxGroup;
        source.Play();

        _sfxStartTime[idx] = Time.unscaledTime;
    }

    /// <summary>UI 효과음. UI Mixer 그룹으로 재생 (있으면).</summary>
    public void PlayUi(AudioClip clip, float volume = 1f, float pitch = 1f, bool randomPitch = false)
    {
        if (_isShuttingDown || clip == null || _sfxSources == null) return;

        int idx = GetSfxSourceIndex();
        var source = _sfxSources[idx];

        // clip + Play() 방식에서는 group을 임시 스왑할 필요 없이 재생 동안 UI 그룹으로 둠
        source.Stop();
        source.clip = clip;
        source.volume = volume;
        source.pitch = randomPitch
            ? UnityEngine.Random.Range(_pitchRandomRange.x, _pitchRandomRange.y)
            : pitch;
        source.outputAudioMixerGroup = _uiGroup != null ? _uiGroup : _sfxGroup;
        source.Play();

        _sfxStartTime[idx] = Time.unscaledTime;
    }

    public async UniTask PlaySfxAsync(string clipKey, float volume = 1f, float pitch = 1f,
            bool randomPitch = false, CancellationToken token = default)
    {
        if (_isShuttingDown || string.IsNullOrEmpty(clipKey) || !ResourceManager.HasInstance) return;
        var clip = await ResourceManager.Instance.LoadAsync<AudioClip>(clipKey, token);
        if (!_isShuttingDown && clip != null) PlaySfx(clip, volume, pitch, randomPitch);
    }

    /// <summary>Addressable/Resource 키로 UI 효과음을 로드해 UI Mixer 채널에서 재생.</summary>
    public async UniTask PlayUiAsync(string clipKey, float volume = 1f, CancellationToken token = default)
    {
        if (_isShuttingDown || string.IsNullOrEmpty(clipKey) || !ResourceManager.HasInstance) return;

        string resolvedKey = clipKey;
        float resolvedVolume = volume;
        float resolvedPitch = 1f;
        bool randomPitch = false;

        if (_uiAudioCatalog != null && _uiAudioCatalog.TryFind(clipKey, out UiAudioCue cue))
        {
            resolvedKey = cue.SfxKey;
            resolvedVolume *= cue.Volume;
            resolvedPitch = cue.Pitch;
            randomPitch = cue.RandomPitch;
        }

        await PlayUiClipAsync(resolvedKey, resolvedVolume, resolvedPitch, randomPitch, token);
    }

    public async UniTask PlayUiClipAsync(
        string clipKey,
        float volume = 1f,
        float pitch = 1f,
        bool randomPitch = false,
        CancellationToken token = default)
    {
        if (_isShuttingDown || string.IsNullOrEmpty(clipKey) || !ResourceManager.HasInstance) return;
        var clip = await ResourceManager.Instance.LoadAsync<AudioClip>(clipKey, token);
        if (!_isShuttingDown && clip != null) PlayUi(clip, volume, pitch, randomPitch);
    }

    /// <summary>
    /// 재생할 SFX 소스 인덱스 반환.
    /// 1순위: 비어있는(재생 중 아닌) 소스. 없으면 2순위: 가장 오래 재생된 소스를 교체(oldest-steal).
    /// </summary>
    private int GetSfxSourceIndex()
    {
        // 1순위: 재생 중이 아닌 소스 검색 (한 바퀴)
        for (int i = 0; i < _sfxSources.Length; i++)
        {
            int idx = (_sfxIndex + i) % _sfxSources.Length;
            if (!_sfxSources[idx].isPlaying)
            {
                _sfxIndex = (idx + 1) % _sfxSources.Length;
                return idx;
            }
        }

        // 2순위: 모두 재생 중 → 가장 오래된(시작 시각이 가장 이른) 소스 선택
        int oldestIdx = 0;
        float oldestTime = _sfxStartTime[0];
        for (int i = 1; i < _sfxSources.Length; i++)
        {
            if (_sfxStartTime[i] < oldestTime)
            {
                oldestTime = _sfxStartTime[i];
                oldestIdx = i;
            }
        }

        _sfxIndex = (oldestIdx + 1) % _sfxSources.Length;
        return oldestIdx;
    }

    public void StopAllSfx()
    {
        if (_sfxSources == null) return;
        for (int i = 0; i < _sfxSources.Length; i++)
            // 종료 teardown 중에는 개별 소스가 이미 파괴됐을 수 있다(Unity == 로 감지).
            if (_sfxSources[i] != null)
                _sfxSources[i].Stop();
    }
}
