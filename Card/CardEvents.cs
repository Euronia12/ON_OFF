// =================================================================
// [스크립트 목적]  카드 관련 EventManager 이벤트 struct 정의
// [사용 예시]      EventManager.Instance.Publish(new OnCardPlaced { Card = card, Cell = cell });
// [의존 관계]      UICard, GridCell
// =================================================================

/// <summary>카드를 그리드 셀에 성공적으로 배치했을 때 발행</summary>
public struct OnCardPlaced
{
    public UICard Card;
    public GridCell Cell;
}

/// <summary>드롭 실패 — 유효한 셀 없음. 카드 손패로 복귀할 때 발행</summary>
public struct OnCardReturnedToHand
{
    public UICard Card;
}

/// <summary>그리드 셀에 올려놓은 카드가 제거됐을 때 발행 (카드 회수 기능 추가 시 사용)</summary>
public struct OnCardRemovedFromCell
{
    public UICard Card;
    public GridCell Cell;
}