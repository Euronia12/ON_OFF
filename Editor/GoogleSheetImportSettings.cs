// =================================================================
// [스크립트 목적]  구글 시트 임포트 설정 SO. 시트별 매핑 정보 보관
// [주요 변수]      - ImportEntries : 임포트할 시트 목록
// [의존 관계]      - SheetImporter, GoogleSheetImporterWindow
// [주의]           시트는 "웹에 게시" 또는 "링크가 있는 모든 사용자 보기" 설정 필요
// =================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "GoogleSheetImportSettings", menuName = "Framework/Google Sheet Import Settings")]
public class GoogleSheetImportSettings : ScriptableObject
{
    // 시트 변환 방식
    public enum EImportMode
    {
        // 시트 → ScriptableObject 직접 생성 (기존 SheetImporter, 행=SO 1개)
        CreateSO,
        // 시트 → CSV 파일 저장 (DataManager가 런타임 로드, 권장: 데이터 대량·핫픽스)
        SaveCsv,
    }

    [Serializable]
    public class ImportEntry
    {
        [Tooltip("에디터 표시용 이름")]
        public string DisplayName;

        [Tooltip("변환 방식.\nCreateSO : 시트 → SO 에셋 직접 생성 (행=SO 1개)\nSaveCsv : 시트 → CSV 파일 저장 (DataManager 런타임 로드)")]
        public EImportMode Mode = EImportMode.CreateSO;

        [Tooltip("스프레드시트 ID (URL의 /d/ 와 /edit 사이 문자열)")]
        public string SpreadsheetId;

        [Tooltip("시트 GID (URL 끝 gid= 뒤 숫자. 첫 시트는 0)")]
        public string Gid = "0";

        [Tooltip("[자동 입력] 등록된 SheetTabs 중 선택한 탭 인덱스. 윈도우 드롭다운으로 선택 시 위 Gid 가 자동 채워진다. -1은 미선택(직접 입력)")]
        public int SelectedTabIndex = -1;

        [Tooltip("[CreateSO 전용] 생성할 SO 타입 이름 (네임스페이스 포함 전체 이름)")]
        public string TargetTypeName;

        [Tooltip("출력 폴더 (Assets/ 기준).\nCreateSO: SO 저장 폴더 / SaveCsv: CSV 저장 폴더")]
        public string OutputFolder = "Assets/_Project/ScriptableObjects/Data";

        [Tooltip("[CreateSO 전용] SO 파일명·식별자로 쓸 컬럼명 (보통 'id' 또는 'Id')")]
        public string IdColumn = "id";

        [Tooltip("[SaveCsv 전용] 저장할 CSV 파일명(확장자 제외).\nDataManager가 자동 로드하려면 파일명 = 클래스명 (예: 'StringChart')")]
        public string OutputFileName = "";
    }

    [Serializable]
    public class SheetTab
    {
        [Tooltip("드롭다운에 표시될 탭 이름 (예: 카드, 적, 아이템)")]
        public string Name;

        [Tooltip("이 탭의 GID (URL 끝 gid= 뒤 숫자)")]
        public string Gid = "0";
    }

    [Header("시트 탭 목록 (드롭다운 선택용)")]
    [Tooltip("자주 쓰는 시트 탭을 이름+GID로 등록해두면, 각 엔트리에서 드롭다운으로 선택 가능")]
    public List<SheetTab> SheetTabs = new();

    [Header("임포트 대상 시트")]
    [Tooltip("임포트할 구글 시트 목록")]
    public List<ImportEntry> ImportEntries = new();

    [Header("공통 설정")]
    [Tooltip("배열 필드 구분자 (한 셀 안에서 여러 값 분리용)")]
    public string ArraySeparator = "|";
}