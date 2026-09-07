// =================================================================
// [스크립트 목적]  슬롯 래퍼가 없는 카드 타일(예: 더미 뷰어의 UICardDetailView)에
//                 붙여 호버 시 키워드 패널을 띄우는 작은 부착형 컴포넌트.
// [주요 변수]      - _panel    : 표시할 공유 키워드 패널 (Set 으로 주입)
//                  - _keywords : 이 타일이 표현하는 카드의 키워드 비트마스크
// [의존 관계]      - UIKeywordPanel / ECardKeyword / EventSystems(IPointerEnter/Exit)
// [배치]           키워드 호버가 필요한데 자체 포인터 핸들러가 없는 카드 타일 루트에 부착.
//                  (UICard·UIShopCardSlot 등 이미 IPointerEnter/Exit 를 가진 슬롯엔 불필요)
// [설계 노트]      - 슬롯이 있는 곳은 슬롯이 직접 패널을 호출한다(상호작용=슬롯 책임).
//                    슬롯이 없는 보기 전용 타일에서만 이 컴포넌트가 그 역할을 대신한다.
//                  - 포인터 이벤트 수신을 위해 같은 오브젝트(또는 자식)에 RaycastTarget
//                    켜진 Graphic 이 있어야 한다(보통 카드 배경 Image).
// =================================================================

using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>슬롯이 없는 카드 타일에 붙여 호버 시 키워드 패널을 표시/숨김하는 부착형 컴포넌트.</summary>
public sealed class UICardKeywordHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    private UIKeywordPanel _panel;
    private Card _card;

    /// <summary>표시할 공유 패널과 이 타일의 카드 데이터를 주입. (타일 생성·바인딩 시 호출)</summary>
    public void Set(UIKeywordPanel panel, Card card)
    {
        _panel = panel;
        _card = card;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (_panel == null) return;
        _panel.MoveTo(transform as RectTransform);
        _panel.Show(_card);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (_panel != null) _panel.Hide();
    }
}
