# ON&OFF

Unity 6 기반 PC 게임 **ON&OFF**의 클라이언트 프로젝트입니다.

## 상용 프로젝트 특성상 전체 Unity 프로젝트 및 리소스는 공개하지 않았기 때문에 본 저장소 단독으로 실행되지는 않습니다.
### Source Code

- `Card/` : 카드 및 배치 관련 핵심 로직
- `Managers/` : 게임 전역 시스템 및 매니저 구조
- `Editor/` : 개발 편의를 위한 에디터 도구
- `02_Scripts.zip` : 추가 클라이언트 스크립트 모음

4인 팀의 메인 프로그래머로 참여해 핵심 게임 시스템과 데이터 처리,
Steamworks 연동, Analytics, Localization 등을 구현했으며
2026년 8월 Steam에 정식 출시했습니다.

<img width="1239" height="648" alt="image" src="https://github.com/user-attachments/assets/f4ea47f7-b542-4561-8b36-62ff079ef430" />

> Steam Store: [https://store.steampowered.com/app/4914570/ONOFF/?l=koreana]

---

## Project

| 항목 | 내용 |
| --- | --- |
| 개발 기간 | 2026.06 ~ 2026.08 |
| 팀 구성 | 4인 |
| 역할 | Main Programmer |
| Engine | Unity 6 / URP |
| Language | C# |
| Platform | PC (Steam) / WebGL |
| Release | Steam 정식 출시 |

### Tech Stack

- Unity 6 / C#
- URP
- Steamworks.NET
- Unity Gaming Services Analytics
- GameAnalytics
- ScriptableObject
- Unity Editor Tool

---

## 담당 영역

### 1. Card System

<img width="979" height="676" alt="image" src="https://github.com/user-attachments/assets/6fba0dba-ccd2-44da-8338-6ab4896f774d" />


카드의 공통 동작과 개별 효과를 분리해
기존 효과를 조합한 카드는 별도의 로직 추가 없이
데이터를 통해 구성할 수 있도록 구현했습니다.

- 카드 공통 동작과 효과 로직 분리
- 카드 데이터와 런타임 로직 분리
- 기존 효과 조합을 통한 신규 카드 추가
- ScriptableObject 기반 카드 데이터 관리

### 2. Data & Editor Tool

<img width="648" height="511" alt="image" src="https://github.com/user-attachments/assets/d814749e-73d9-44e6-8198-3d787c2bca94" />

반복적인 카드 및 밸런스 데이터 입력을 줄이기 위해
Excel 데이터를 ScriptableObject로 변환하는 Editor Tool을 제작했습니다.

- Excel 데이터 → ScriptableObject 변환
- 카드 및 밸런스 데이터 생성 자동화
- 반복 입력 작업 감소

### 3. Shuffle & Validation

<img width="741" height="482" alt="image" src="https://github.com/user-attachments/assets/f4dfdd7b-677b-41ff-b005-2fb0c093a6ca" />


셔플 결과를 재현할 수 있도록 고정 Seed 기반 셔플 로직을 구현했습니다.

셔플처럼 눈으로 한 번 확인하는 것만으로 정확성을 판단하기 어려운 로직은
별도의 검증 툴을 제작해 동일 Seed의 결과를 확인했습니다.

이후 실제 게임에도 같은 Seed를 적용하고,
검증 툴에서 생성된 카드 순서와 실제 플레이의 카드 순서를 비교해
동일한 결과가 재현되는지 확인했습니다.

- 고정 Seed 기반 셔플
- 셔플 결과 검증 Tool
- Tool 결과 ↔ 실제 플레이 결과 비교

### 4. Steamworks

<img width="2549" height="459" alt="image" src="https://github.com/user-attachments/assets/10dd5f2f-b78d-4cef-b1e4-86c33b4d301e" />


Steamworks.NET을 연동해 Steam 플랫폼 기능을 적용했습니다.

- Steamworks.NET SDK 연동
- Steam 업적 시스템 구현
- Steam 출시 빌드 대응

### 5. Analytics

<img width="2560" height="454" alt="image" src="https://github.com/user-attachments/assets/8574eb89-2328-4715-985d-10e4f3de04a8" />


플레이 과정에서 필요한 데이터를 확인할 수 있도록
UGS Analytics와 GameAnalytics를 연동했습니다.

- 런 단위 플레이 데이터 수집
- 스테이지 단위 플레이 데이터 수집
- 플레이 이벤트 구조 구현

### 6. Localization

<img width="441" height="612" alt="image" src="https://github.com/user-attachments/assets/6136ea36-1cbc-423f-ab05-222e2592630e" />

문자열 Key를 기준으로 언어를 전환하는 Localization 구조를 구현했습니다.

- 문자열 Key 기반 다국어 처리
- 언어 변경 기능
- 언어별 Font 교체

---

## Gameplay Validation

개발 중 기존 전투 시스템의 일부 규칙에 대한 팀 의견이 나뉘어
점수제와 육성 방식의 프로토타입을 추가로 구현했습니다.

테스트 결과,

- 육성 방식은 그리드와 판정을 분리하면서 플레이 흐름이 느려지는 문제가 발생
- 점수 방식은 점수 계산 규칙이 추가돼 오히려 규칙 이해 비용이 증가
- 기존 전투의 일부 요소는 유저 테스트에서 예상보다 플레이 판단에 의미 있게 작용

하는 것을 확인했습니다.

이에 전투 시스템을 교체하지 않고 기존 구조를 유지하면서
부족한 부분을 개선하는 방향으로 개발을 진행했습니다.

---

## My Contribution

- Main Programmer
- 게임 핵심 시스템 구현
- 카드 및 데이터 구조 구현
- Unity Editor Tool 제작
- Steamworks.NET 및 Steam 업적 연동
- Analytics 이벤트 수집 구조 구현
- Localization 구현
- 셔플 로직 및 검증 Tool 구현
- 기획 논의 및 프로토타이핑 참여
- Steam 출시 대응
