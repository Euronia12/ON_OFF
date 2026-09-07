// =================================================================
// [스크립트 목적]  CardDeckBuilder 실행용 에디터 메뉴 (기존 임포터 윈도우와 독립)
// [메뉴]           Tools > Framework > Build Card Deck (from CSV)
// [의존 관계]      - CardDeckBuilder
// [주의]           UNITY_EDITOR 전용. CSV 경로·출력 폴더는 상수로 관리(프로젝트에 맞게 수정).
// =================================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class CardDeckBuilderMenu
{
    // [추정 구현] 실제 프로젝트 경로로 교체 필요
    private const string CsvPath = "Assets/05_Data/CSV/Cards.csv";
    private const string OutputFolder = "Assets/00_Addressable/Data/SO/Card";

    [MenuItem("Tools/Framework/Review Card Deck CSV")]
    private static void ReviewCardDeck()
    {
        bool ok = CardDeckBuilder.Review(CsvPath);
        EditorUtility.DisplayDialog(
            "Card Deck Builder",
            ok ? "CSV 검토 완료: 오류 없음. 자세한 내용은 콘솔을 확인하세요."
               : "CSV 검토 완료: 오류가 있습니다. 콘솔 로그를 확인하세요.",
            "확인");
    }

    [MenuItem("Tools/Framework/Build Card Deck (from CSV)")]
    private static void BuildCardDeck()
    {
        int count = CardDeckBuilder.Build(CsvPath, OutputFolder);
        if (count > 0)
        {
            EditorUtility.DisplayDialog("Card Deck Builder", $"{count}개 카드 생성/갱신 완료", "확인");
        }
        else
        {
            EditorUtility.DisplayDialog("Card Deck Builder", "처리된 카드가 없습니다. 콘솔 로그를 확인하세요.", "확인");
        }
    }
}
#endif
