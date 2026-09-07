// =================================================================
// [스크립트 목적]  키워드 패널 항목 1개. 키워드 이름 + 설명 텍스트를 그린다.
//                 UIKeywordPanel 이 키워드 수만큼 풀에서 스폰해 채운다.
// [주요 변수]      - _nameText / _descriptionText : 로컬라이즈된 이름·설명 표시
// [의존 관계]      - UIPoolableMonoBehaviour(풀링) / LocalizationManager / TMP
// [배치]           UIKeywordPanel 의 항목 컨테이너 자식으로 스폰됨. 프리팹 루트에 부착.
// [설계 노트]      - 표현만 담당. 어떤 키워드를 보일지는 패널이 결정해 키를 넘긴다.
//                  - 로컬라이즈는 키만 받으면 LocalizationManager 가 변환(없으면 키 폴백).
// =================================================================

using TMPro;
using UnityEngine;

/// <summary>키워드 패널의 항목 한 개. Setup(이름키, 설명키)로 채운다.</summary>
public sealed class UIKeywordEntry : UIPoolableMonoBehaviour, ILocalizable
{
    [Header("텍스트")]
    [Tooltip("키워드 이름 표시. 비우면 생략")]
    [SerializeField] private TMP_Text _nameText;

    [Tooltip("키워드 설명 표시. 비우면 생략")]
    [SerializeField] private TMP_Text _descriptionText;

    private string _nameKey;
    private string _descriptionKey;

    /// <summary>로컬라이즈 키로 이름·설명을 채운다.</summary>
    public void Setup(in KeywordDisplayKeys keys)
    {
        _nameKey = keys.NameKey;
        _descriptionKey = keys.DescriptionKey;
        RefreshLocalization();
    }

    public void OnLocalize()
    {
        RefreshLocalization();
    }

    public override void OnSpawnFromPool()
    {
        base.OnSpawnFromPool();
        if (LocalizationManager.HasInstance)
            LocalizationManager.Instance.Register(this);
        else
            ApplyLocalizedFonts();
    }

    public override void OnReturnToPool()
    {
        if (LocalizationManager.HasInstance)
            LocalizationManager.Instance.Unregister(this);
        _nameKey = null;
        _descriptionKey = null;
        base.OnReturnToPool();
    }

    private void OnDestroy()
    {
        if (LocalizationManager.HasInstance)
            LocalizationManager.Instance.Unregister(this);
    }

    private void RefreshLocalization()
    {
        ApplyLocalizedFonts();
        if (_nameText != null) _nameText.text = Localize(_nameKey);
        if (_descriptionText != null) _descriptionText.text = Localize(_descriptionKey);
    }

    private void ApplyLocalizedFonts()
    {
        TMP_FontAsset font = LocalizedFontUtility.CurrentFont;
        LocalizedFontUtility.ApplyFont(_nameText, font);
        LocalizedFontUtility.ApplyFont(_descriptionText, font);
    }

    private static string Localize(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (LocalizationManager.HasInstance)
        {
            return LocalizationManager.Instance.Get(key);
        }
        return key;
    }
}
