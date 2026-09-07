// =================================================================
// [스크립트 목적]  ECardKeyword(단일 비트) → 로컬라이즈 키(이름/설명) 정적 매핑.
//                 비트마스크에서 켜진 키워드를 순회하는 헬퍼 제공.
// [의존 관계]      - ECardKeyword(CardEnums) / LocalizationManager(키만 넘기면 변환)
// [설계 노트]      - 키워드는 enum 소수 + 거의 불변이므로 코드 매핑 테이블로 충분.
//                    (기획자 편집·대량 확장이 필요해지면 ScriptableObject DB 로 승격)
//                  - 텍스트 변환은 호출측(UIKeywordPanel)이 LocalizationManager.Get 로 수행.
//                    여기선 "어떤 키를 쓸지"만 정의한다 (데이터/표현 분리).
//                  - None 은 매핑에서 제외. 새 키워드 추가 시 _table 에 한 줄 추가.
// =================================================================

using System.Collections.Generic;

/// <summary>키워드 1개의 표시용 로컬라이즈 키 묶음.</summary>
public readonly struct KeywordDisplayKeys
{
    public readonly string NameKey;
    public readonly string DescriptionKey;

    public KeywordDisplayKeys(string nameKey, string descriptionKey)
    {
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
    }
}

/// <summary>
/// ECardKeyword ↔ 로컬라이즈 키 매핑 + 비트 순회 헬퍼.
/// 호버 키워드 패널이 카드의 Keywords 비트마스크를 받아 표시 항목을 만들 때 사용.
/// </summary>
public static class CardKeywordInfo
{
    // 단일 비트 → (이름 키, 설명 키). LocalizationManager 가 키를 텍스트로 변환.
    private static readonly Dictionary<ECardKeyword, KeywordDisplayKeys> _table = new()
    {
        { ECardKeyword.Innate,     new("KEYWORD_INNATE_NAME",     "KEYWORD_INNATE_DESC") },
        { ECardKeyword.Retain,     new("KEYWORD_RETAIN_NAME",     "KEYWORD_RETAIN_DESC") },
        { ECardKeyword.Exhaust,    new("KEYWORD_EXHAUST_NAME",    "KEYWORD_EXHAUST_DESC") },
        { ECardKeyword.Ethereal,   new("KEYWORD_ETHEREAL_NAME",   "KEYWORD_ETHEREAL_DESC") },
        { ECardKeyword.Unplayable, new("KEYWORD_UNPLAYABLE_NAME", "KEYWORD_UNPLAYABLE_DESC") },
        { ECardKeyword.Casting,    new("KEYWORD_CASTING_NAME",    "KEYWORD_CASTING_DESC") },
        { ECardKeyword.Reserve,    new("KEYWORD_RESERVE_NAME",    "KEYWORD_RESERVE_DESC") },
        { ECardKeyword.Absorb,     new("KEYWORD_ABSORB_NAME",     "KEYWORD_ABSORB_DESC") },
        { ECardKeyword.Offspring,  new("KEYWORD_OFFSPRING_NAME",  "KEYWORD_OFFSPRING_DESC") },
        { ECardKeyword.Predate,    new("KEYWORD_PREDATE_NAME",    "KEYWORD_PREDATE_DESC") },
        { ECardKeyword.MagicCircle,new("KEYWORD_MAGIC_CIRCLE_NAME", "KEYWORD_MAGIC_CIRCLE_DESC") },
    };

    // 표시 순서 고정용 (Dictionary 순회 순서에 의존하지 않도록).
    private static readonly ECardKeyword[] _order =
    {
        ECardKeyword.Innate,
        ECardKeyword.Retain,
        ECardKeyword.Exhaust,
        ECardKeyword.Ethereal,
        ECardKeyword.Unplayable,
        ECardKeyword.Casting,
        ECardKeyword.Reserve,
        ECardKeyword.Absorb,
        ECardKeyword.Offspring,
        ECardKeyword.Predate,
        ECardKeyword.MagicCircle,
    };

    /// <summary>비트마스크에 켜진 키워드를 정해진 표시 순서로 순회한다.</summary>
    public static IEnumerable<ECardKeyword> EnumerateKeywords(ECardKeyword keywords)
    {
        if (keywords == ECardKeyword.None) yield break;

        foreach (var bit in _order)
        {
            if ((keywords & bit) != 0)
            {
                yield return bit;
            }
        }
    }

    /// <summary>비트마스크에 켜진 키워드의 표시 키를 정해진 순서로 순회한다.</summary>
    public static IEnumerable<KeywordDisplayKeys> Enumerate(ECardKeyword keywords)
    {
        foreach (var keyword in EnumerateKeywords(keywords))
        {
            if (_table.TryGetValue(keyword, out var keys))
            {
                yield return keys;
            }
        }
    }

    /// <summary>비트마스크에 표시할 키워드가 하나라도 있는지.</summary>
    public static bool HasAny(ECardKeyword keywords) => keywords != ECardKeyword.None;
}
