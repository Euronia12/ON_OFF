// =================================================================
// [스크립트 목적]  직렬화·파일 I/O 인프라. JSON + persistentDataPath + 암호화 옵션
//                 (도메인 데이터는 UserManager가 보유. 여기는 저장/로드 전담)
// [주요 변수]      - _useEncryption : AES 암호화 사용 여부
//                  - _savePath      : 저장 루트 경로
//                  - _derivedKey    : PBKDF2로 파생된 AES 키
// [의존 관계]      - ManagerBase<SaveManager>, UniTask
// [개선]           PBKDF2 키 파생 + 랜덤 IV per-save + 매직 헤더 버전 식별
//                  SaveCoroutine 컴파일 오류 수정 (ContinueWith 제거)
//                  직렬화기 JsonUtility → Newtonsoft.Json 교체
//                  (Dictionary·다형성·private 필드 직렬화 지원)
// [InitOrder]      10
// =================================================================
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

public class SaveManager : ManagerBase<SaveManager>
{
    public override int InitOrder => 10;
    public override bool IsCritical => true; // 저장 시스템은 필수

    [Header("저장 설정")]
    [Tooltip("저장 파일 AES 암호화 사용 여부.\n켜면 세이브 변조 난이도 상승(치트 방지). 기존 평문 세이브는 자동 폴백 로드됨.\n[주의] 이미 배치된 Bootstrap.prefab의 SaveManager 인스턴스는 이 기본값이 아닌 프리팹 저장값을 쓰므로 Inspector에서도 켤 것")]
    [SerializeField] private bool _useEncryption = true;

    [Tooltip("저장 파일 확장자 (점 제외)")]
    [SerializeField] private string _extension = "sav";

    [Tooltip("암호화 키 파생 솔트. 빌드별로 변경하면 이전 빌드 저장 무효화 가능.\n출시 직전 랜덤 문자열로 교체 필수")]
    [SerializeField] private string _keySalt = "ChangeThisInProduction_RandomString_2024";

    private string _savePath;
    private byte[] _derivedKey;

    /// <summary>
    /// 공용 직렬화 설정. 다형성(List&lt;Base&gt; 등) 지원을 위해 TypeNameHandling.Auto 사용.
    /// Auto는 선언 타입과 실제 타입이 다를 때만 $type을 기록 → 용량 최소화.
    /// SerializationBinder로 허용 어셈블리를 제한해 역직렬화 공격 표면 축소.
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        SerializationBinder = new SafeSerializationBinder(),
    };

    // 암호화 파일 매직 헤더 (4바이트). 버전 식별로 하위 호환
    private static readonly byte[] MAGIC_HEADER_V1 = { 0x53, 0x41, 0x56, 0x31 }; // "SAV1"
    private const int IV_SIZE = 16;
    private const int KEY_SIZE = 32; // AES-256

    public event Action<string> OnSaveComplete;
    public event Action<string> OnLoadComplete;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        _savePath = Path.Combine(Application.persistentDataPath, "Saves");
        if (!Directory.Exists(_savePath))
            Directory.CreateDirectory(_savePath);

        _derivedKey = DeriveKey();

        // 비정상 종료로 남은 임시 파일(.tmp) 정리 (atomic write 중단 잔재)
        CleanupTempFiles();

        return UniTask.CompletedTask;
    }

    // 저장 도중 크래시로 남은 .tmp 파일 제거 (다음 저장이 덮어쓰지만 위생상 정리)
    private void CleanupTempFiles()
    {
        try
        {
            var temps = Directory.GetFiles(_savePath, $"*.{_extension}.tmp");
            for (int i = 0; i < temps.Length; i++)
                File.Delete(temps[i]);
        }
        catch (Exception e)
        {
            GameLogger.LogWarning(ELogCategory.Save, $"임시 파일 정리 실패: {e.Message}");
        }
    }

    /// <summary>
    /// 키 파생. Application.identifier + salt로 PBKDF2.
    /// 디컴파일로 salt가 노출돼도 identifier가 빌드/플랫폼별로 다르면 다른 키.
    /// 진짜 민감 데이터는 서버 검증 필요.
    /// </summary>
    private byte[] DeriveKey()
    {
        string seed = Application.identifier + "_" + _keySalt;
        byte[] saltBytes = Encoding.UTF8.GetBytes(_keySalt);
        using var pbkdf2 = new Rfc2898DeriveBytes(seed, saltBytes, 10000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KEY_SIZE);
    }

    private string GetFilePath(string slot) => Path.Combine(_savePath, $"{slot}.{_extension}");
    private string GetTempPath(string slot) => GetFilePath(slot) + ".tmp";
    private string GetBackupPath(string slot) => GetFilePath(slot) + ".bak";

    // ─────────────────────────────────────────────
    // Save (UniTask)
    // ─────────────────────────────────────────────

    public async UniTask SaveAsync<T>(string slot, T data, CancellationToken token = default) where T : ISaveData
    {
        try
        {
            string json = JsonConvert.SerializeObject(data, JsonSettings);
            byte[] bytes = _useEncryption
                ? Encrypt(json)
                : Encoding.UTF8.GetBytes(json);

            string path = GetFilePath(slot);
            string temp = GetTempPath(slot);
            string backup = GetBackupPath(slot);

            // 저장 디렉터리가 런타임에 사라질 수 있어(클라우드 동기화·외부 삭제 등) 매 저장 시 보장한다.
            // 이미 존재하면 no-op이라 비용이 없다. (부팅 1회 생성만 믿으면 DirectoryNotFound 발생)
            Directory.CreateDirectory(_savePath);

            // 1) 임시 파일에 먼저 기록 (저장 중 크래시 시 원본 보존)
            await File.WriteAllBytesAsync(temp, bytes, token);

            // 2) 기존 정상본을 백업(.bak)으로 1세대 보존 후 교체
            //    → 새 파일이 나중에 손상돼도 직전 정상본으로 복구 가능
            if (File.Exists(path))
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup); // 기존본 → 백업
            }
            File.Move(temp, path);       // 임시본 → 본파일

            OnSaveComplete?.Invoke(slot);
            GameLogger.Log(ELogCategory.Save, $"저장 완료: {slot}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.Save, $"저장 실패 {slot}: {e.Message}");
            throw;
        }
    }

    public async UniTask<T> LoadAsync<T>(string slot, CancellationToken token = default) where T : ISaveData
    {
        string path = GetFilePath(slot);
        if (!File.Exists(path))
        {
            // 본파일이 없지만 백업이 있으면(교체 중 크래시) 백업에서 복구 시도
            string backupOnly = GetBackupPath(slot);
            if (File.Exists(backupOnly))
            {
                GameLogger.LogWarning(ELogCategory.Save, $"본파일 없음 — 백업 복구 시도: {slot}");
                var recovered = await TryLoadFromAsync<T>(backupOnly, token);
                if (recovered != null)
                {
                    OnLoadComplete?.Invoke(slot);
                    return recovered;
                }
            }

            GameLogger.Log(ELogCategory.Save, $"저장 없음: {slot}");
            return default;
        }

        // 1) 본파일 로드 시도
        var data = await TryLoadFromAsync<T>(path, token);
        if (data != null)
        {
            OnLoadComplete?.Invoke(slot);
            return data;
        }

        // 2) 본파일 손상 → 백업(.bak)에서 복구 시도
        string backup = GetBackupPath(slot);
        if (File.Exists(backup))
        {
            GameLogger.LogWarning(ELogCategory.Save, $"본파일 손상 — 백업 복구 시도: {slot}");
            var recovered = await TryLoadFromAsync<T>(backup, token);
            if (recovered != null)
            {
                OnLoadComplete?.Invoke(slot);
                return recovered;
            }
        }

        GameLogger.LogError(ELogCategory.Save, $"로드 실패(본파일·백업 모두 불가): {slot}");
        return default;
    }

    /// <summary>
    /// 단일 파일에서 로드·역직렬화 시도. 성공 시 데이터, 실패(손상·취소 외)면 default 반환.
    /// 취소는 상위로 전파(throw).
    /// </summary>
    private async UniTask<T> TryLoadFromAsync<T>(string filePath, CancellationToken token) where T : ISaveData
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
            string json = _useEncryption || HasMagicHeader(bytes, MAGIC_HEADER_V1)
                ? Decrypt(bytes)
                : Encoding.UTF8.GetString(bytes);

            return JsonConvert.DeserializeObject<T>(json, JsonSettings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.Save, $"로드/역직렬화 실패 {Path.GetFileName(filePath)}: {e.Message}");
            return default;
        }
    }

    // ─────────────────────────────────────────────
    // Save/Load (Coroutine 래퍼)
    // ─────────────────────────────────────────────

    public IEnumerator SaveCoroutine<T>(string slot, T data, Action onComplete = null) where T : ISaveData
        => SaveCoroutineInternal(slot, data, onComplete);

    private IEnumerator SaveCoroutineInternal<T>(string slot, T data, Action onComplete) where T : ISaveData
    {
        var task = SaveAsync(slot, data);
        var awaiter = task.GetAwaiter();
        while (!awaiter.IsCompleted) yield return null;

        try { awaiter.GetResult(); }
        catch (Exception e) { GameLogger.LogError(ELogCategory.Save, $"SaveCoroutine 예외: {e.Message}"); }

        onComplete?.Invoke();
    }

    public IEnumerator LoadCoroutine<T>(string slot, Action<T> onComplete) where T : ISaveData
        => AsyncBridge.ToCoroutine(LoadAsync<T>(slot), onComplete);

    // ─────────────────────────────────────────────
    // 슬롯 관리
    // ─────────────────────────────────────────────

    public bool Exists(string slot) => File.Exists(GetFilePath(slot));

    public void Delete(string slot)
    {
        // 본파일 + 백업 + 임시파일 모두 정리 (잔재 방지)
        string path = GetFilePath(slot);
        string backup = GetBackupPath(slot);
        string temp = GetTempPath(slot);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(backup)) File.Delete(backup);
        if (File.Exists(temp)) File.Delete(temp);
    }

    public string[] GetAllSlots()
    {
        if (!Directory.Exists(_savePath)) return Array.Empty<string>();
        var files = Directory.GetFiles(_savePath, $"*.{_extension}");
        var slots = new string[files.Length];
        for (int i = 0; i < files.Length; i++)
            slots[i] = Path.GetFileNameWithoutExtension(files[i]);
        return slots;
    }

    public void SetEncryption(bool enabled) => _useEncryption = enabled;

    // ─────────────────────────────────────────────
    // 암호화 (AES-256 CBC, 랜덤 IV per-save)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 암호화. 출력: [4바이트 매직][16바이트 IV][암호화된 데이터].
    /// IV는 매번 랜덤 → 같은 평문도 다른 암호문.
    /// </summary>
    private byte[] Encrypt(string plain)
    {
        using var aes = Aes.Create();
        aes.Key = _derivedKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV(); // 매번 새 IV

        byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
        using var enc = aes.CreateEncryptor();
        byte[] cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // [매직][IV][암호문] 조합
        byte[] output = new byte[MAGIC_HEADER_V1.Length + IV_SIZE + cipher.Length];
        Buffer.BlockCopy(MAGIC_HEADER_V1, 0, output, 0, MAGIC_HEADER_V1.Length);
        Buffer.BlockCopy(aes.IV, 0, output, MAGIC_HEADER_V1.Length, IV_SIZE);
        Buffer.BlockCopy(cipher, 0, output, MAGIC_HEADER_V1.Length + IV_SIZE, cipher.Length);
        return output;
    }

    /// <summary>
    /// 복호화. 매직 헤더로 버전 분기. 헤더 없으면 평문 JSON으로 폴백(하위 호환).
    /// </summary>
    private string Decrypt(byte[] data)
    {
        if (HasMagicHeader(data, MAGIC_HEADER_V1))
            return DecryptV1(data);

        // 헤더 없음 → 평문 JSON으로 시도 (이전 평문 저장 호환)
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            throw new InvalidDataException("[SaveManager] 알 수 없는 파일 포맷");
        }
    }

    private string DecryptV1(byte[] data)
    {
        int headerLen = MAGIC_HEADER_V1.Length;
        int cipherStart = headerLen + IV_SIZE;
        int cipherLen = data.Length - cipherStart;

        if (cipherLen <= 0)
            throw new InvalidDataException("[SaveManager] V1 파일 손상");

        byte[] iv = new byte[IV_SIZE];
        Buffer.BlockCopy(data, headerLen, iv, 0, IV_SIZE);

        byte[] cipher = new byte[cipherLen];
        Buffer.BlockCopy(data, cipherStart, cipher, 0, cipherLen);

        using var aes = Aes.Create();
        aes.Key = _derivedKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var dec = aes.CreateDecryptor();
        byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static bool HasMagicHeader(byte[] data, byte[] header)
    {
        if (data == null || data.Length < header.Length) return false;
        for (int i = 0; i < header.Length; i++)
            if (data[i] != header[i]) return false;
        return true;
    }
}

/// <summary>
/// 역직렬화 화이트리스트 바인더. TypeNameHandling 사용 시 임의 타입 주입($type)으로
/// 위험 객체가 생성되는 공격을 차단. 게임 코드 어셈블리(Assembly-CSharp 등)와
/// 기본 컬렉션(mscorlib/System.Private.CoreLib)만 허용.
/// 외부 어셈블리 타입을 저장에 써야 하면 _allowedAssemblies에 추가.
/// </summary>
public sealed class SafeSerializationBinder : Newtonsoft.Json.Serialization.ISerializationBinder
{
    // 허용 어셈블리 접두사 (시작 문자열 매칭)
    private static readonly string[] _allowedAssemblies =
    {
        "Assembly-CSharp",        // Unity 기본 게임 코드
        "mscorlib",               // .NET 컬렉션 (Mono)
        "System.Private.CoreLib", // .NET 컬렉션 (CoreCLR/IL2CPP)
    };

    public void BindToName(Type serializedType, out string assemblyName, out string typeName)
    {
        assemblyName = serializedType.Assembly.GetName().Name;
        typeName = serializedType.FullName;
    }

    public Type BindToType(string assemblyName, string typeName)
    {
        if (!IsAllowed(assemblyName))
            throw new JsonSerializationException(
                $"[SaveManager] 허용되지 않은 어셈블리 역직렬화 시도: {assemblyName}");

        string qualified = string.IsNullOrEmpty(assemblyName)
            ? typeName
            : $"{typeName}, {assemblyName}";
        return Type.GetType(qualified, throwOnError: true);
    }

    private static bool IsAllowed(string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return true; // 어셈블리 미지정 = 현재 어셈블리
        for (int i = 0; i < _allowedAssemblies.Length; i++)
            if (assemblyName.StartsWith(_allowedAssemblies[i], StringComparison.Ordinal))
                return true;
        return false;
    }
}