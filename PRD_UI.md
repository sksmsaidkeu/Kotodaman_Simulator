# 코토다망 UI 리디자인: 게임 UI 프레임 차용

앱 껍데기(패널·버튼·탭·뱃지·모달)를 코토다마 게임의 **배경 UI 언어**로 바꾼다. 캐릭터 일러스트나 연출은 대상이 아니다. 지금의 "개발자 도구" 다크 테마를 게임과 같은 크림/아이보리 카드 UI로 옮겨, 게임에서 앱으로 넘어올 때 시각적 단절이 없게 만드는 것이 목표다.

| 항목 | 내용 |
|---|---|
| 대상 | KotodamanWordFinder (WPF/.NET 8), v1.25.1 이후 |
| 참조 자료 | `Kotodaman_UI_1~3.jpg` (게임 실 스크린샷 1080x2340) |
| 기능 변경 | 없음. 순수 프레젠테이션 레이어 |
| 신규 의존성 | 0개 (폰트·아이콘 팩 포함 안 함) |

---

## 1. 지금 상태 (측정치)

```
App.xaml <Application.Resources>   : 비어 있음
윈도우별 <Style> 정의 수            : 74개 (8개 .xaml에 분산)
XAML 유니크 색상                    : 130개
코드비하인드 유니크 색상            : 110개
XAML+C# 합산 유니크 색상            : 248개
BrushFromHex() 호출                 : 213회
BrushFromHex() 정의                 : 8개 (전부 동일 구현, 복붙)
```

**문제는 색이 다크라는 게 아니라 공유 자원이 없다는 것이다.** 지금 구조에서는 어떤 방향으로 리디자인하든 8개 파일을 각각 손대야 한다. 그래서 이 PRD의 1순위 요구사항은 "게임처럼 보이게"가 아니라 **토큰 출처 1곳을 만들고 거기로 모으는 것**이다. 게임 룩은 그 위에 얹는다.

**그리고 그 출처는 XAML만으로는 부족하다.** 이 앱은 UI의 상당 부분을 코드비하인드에서 동적으로 만든다:

```csharp
Background = BrushFromHex("#222A37"),      // MainWindow.xaml.cs:467
BorderBrush = BrushFromHex("#3A4658"),
```

| 파일 | XAML 스타일 | XAML 색 | C# 호출 | C# 유니크 색 |
|---|---|---|---|---|
| MainWindow | 12 | 48 | **131** | **99** |
| DeckEditorWindow | 22 | 64 | 52 | 20 |
| DeckScreenshotImport | 4 | 37 | 12 | 6 |
| LetterStateEditor | 8 | 29 | 7 | 5 |
| DataBackup | 2 | 19 | 6 | 4 |
| CharacterBulkEditor | 13 | 30 | 5 | 4 |
| CharacterFormEditor | 3 | 24 | 5 | 4 |
| CharacterWebImport | 10 | 40 | 0 | — |

**MainWindow는 XAML(48)보다 C#(99)에 색이 두 배 많다.** XAML만 이관하면 화면 절반이 다크로 남는다.

좋은 소식은, 그 213개 호출이 전부 같은 이름 `BrushFromHex(hex)`를 지난다는 것이다. 이관은 새 구조를 세우는 일이 아니라 **이미 있는 깔때기의 입구를 토큰으로 바꾸고 사본을 지우는 일**이다.

다만 사본 8개가 **같은 구현이 아니다.** 네 가지 변종이 있다:

| 변종 | 위치 | 반환 | Freeze |
|---|---|---|---|
| `new SolidColorBrush((Color)ColorConverter…)` | MainWindow, DeckEditor, FormEditor, LetterState, DataBackup | `SolidColorBrush` | 안 함 |
| `(Brush)new BrushConverter()…` | CharacterBulkEditor | `Brush` | 안 함 |
| `(SolidColorBrush)new BrushConverter()…` | DeckScreenshotImport | `SolidColorBrush` | 안 함 |
| `BrushFromHexStatic` | DeckEditor (9번째 사본) | `Brush` | **함** |

호출부가 `SolidColorBrush`를 기대하는 곳이 있으므로 통합 시그니처는 `SolidColorBrush`로 맞춘다.

**공유 브러시 안전성은 확인했다.** 코드 전체에 `brush.Color = …` 형태의 변형 대입이 **하나도 없다**(`Freeze()` 호출 4곳 중 2곳은 Bitmap 대상). 따라서 토큰 브러시를 정적으로 공유하고 전부 `Freeze()`해도 안전하며, 213개 호출마다 새 브러시를 만들던 것도 사라진다.

---

## 2. 게임 UI에서 실제로 가져올 것

스크린샷 3장에서 캐릭터를 지우고 남는 **프레임**만 추린다. 각 패턴은 앱에 이미 있는 대응 화면이 있다 — 없는 패턴은 가져오지 않는다.

| # | 게임 패턴 (근거) | 앱에서 대응하는 곳 | 우선순위 |
|---|---|---|---|
| P1 | **크림 카드 모달** — 둥근 모서리(≈28px), 크림 면, 탠 테두리, 중앙 제목 + 다이아몬드 구분선, 우상단 X (img1 クエスト, img2 デッキ編成) | 7개 다이얼로그 윈도우 전부 | P0 |
| P2 | **스타디움 pill 탭** — 선택 시 주황 그라디언트 + 흰 볼드, 미선택은 흰 면 + 탠 테두리 (img1 ノーマル/イベント/1日1回) | 결과 길이 선택 토글, 현재 손패/덱 전체 전환 | P0 |
| P3 | **かな 원형 뱃지** — 카드 좌상단 흰 원 + 갈색 글자 + 탠 링 (img2/img3 こ む か ま つ ん) | 판면 7칸 글자 슬롯, 빠른 입력 | P0 |
| P4 | **캐릭터 카드 프레임** — 핑크 라운드 프레임 + 하단 LV 알약 + 스킬 아이콘 2칸 (img2/img3 덱 12칸) | 현재 손패 4명, 덱 프리셋 목록 | P1 |
| P5 | **하단 고정 액션 바** — 큰 아이보리 스타디움 버튼 1개가 화면 하단 중앙 (img2 編成, img3 ひとりで) | 다이얼로그 확인 버튼, 메인 검색 실행 | P1 |
| P6 | **상단 상태 칩** — 반투명 다크 알약에 흰 텍스트/숫자 (img1 残り8日, 7,985) | 데이터 버전 / 업데이트 상태 / 로딩 표시 | P1 |
| P7 | **뱃지** — 빨강 점(새 항목), 핑크 라벨 뱃지(発動可) (img1/img2) | "업데이트 있음", "미저장 변경", "인식 실패" | P1 |
| P8 | **경고 패널** — 빨강 테두리 + 상단 무지개 엣지 + 라벨/값 2열 (img3 スカー強襲 헤더) | 오류·경고 배너, 스크린샷 인식 실패 안내 | P2 |
| P9 | **타일 그리드 카드** — 흰 카드, 이미지 위 / 라벨 아래, 탠 테두리 (img1 夢幻の塔·戯言戦·育成) | 메인 진입 메뉴, 데이터 관리 화면 | P2 |

### 가져오지 않는 것

- **캐릭터 일러스트·SD 스프라이트** — 저작물이고, 앱은 이미 `Data/CharacterImages`로 실제 이미지를 쓴다. 흉내낼 이유 없음.
- **연출 애니메이션** (VS 컷인, 반짝임) — WPF 스토리보드 비용 대비 값이 없다. 호버/체크 전환만 150ms.
- **게임 폰트** — 라이선스. Malgun Gothic 유지 (한글 가독성이 우선). 숫자만 SemiBold로 굵혀 게임의 숫자 강조 느낌만 가져온다.
- **화면 하단 5탭 네비게이션** (育成/コトノ間/召喚/ショップ) — 앱은 단일 작업 화면이다. 탭 바를 만들면 없던 계층이 생긴다.

---

## 3. 디자인 토큰

스크린샷에서 직접 픽셀 샘플링한 값이다 (JPEG 압축으로 살짝 탁해진 값을 정리한 것). 이 표가 유일한 색 출처가 되고, XAML에 하드코딩된 `#RRGGBB`는 전부 여기 키로 치환한다.

| 토큰 키 | 값 | 용도 | 샘플 출처 |
|---|---|---|---|
| `Bg.Backdrop` | `#2E2622` | 창 바탕 (게임의 흐린 배경 자리) | img1 배경 avg `#4D423F` |
| `Bg.Panel` | `#EFEBE0` | 모달·섹션 카드 면 | img1 모달 avg `#E6E2D7` |
| `Bg.PanelInner` | `#DFDACF` | 패널 안 한 단계 들어간 영역 | img2 덱 패널 `#DDD8CF` |
| `Bg.Field` | `#FFFFFF` | 입력 필드, 흰 카드 | img3 状態異常 `#FFFFFF` |
| `Bg.Ivory` | `#F5F3EC` | 액션 버튼 면 | img2 編成 `#EBE9E2` |
| `Line.Tan` | `#CEC7B5` | 기본 테두리 | img1 모달 dark `#CEC7B5` |
| `Line.TanStrong` | `#B7A98D` | 강조 테두리·구분선 | — |
| `Text.Primary` | `#4A3B28` | 본문 | img2 버튼 텍스트 `#3E2308` |
| `Text.Secondary` | `#8A7C66` | 보조 설명 | img1 라벨 `#776C58` |
| `Accent.Orange` | `#FFB061` → `#FF9643` | 선택된 탭/pill (세로 그라디언트) | img1 pill vivid `#FF9643` |
| `Accent.Pink` | `#F0609E` | 강조 뱃지, 활성 프레임 | img3 태그 vivid `#FF44C7` |
| `Accent.PinkSoft` | `#FFB0CE` | 카드 프레임 링 | img2 프레임 vivid `#FFB0CE` |
| `Accent.PinkPale` | `#FCEDF5` | 핑크 뱃지 배경 | img2 리더 태그 `#FCEDF5` |
| `Accent.Blue` | `#E3F1FA` / 테두리 `#AEDAFF` / 글자 `#1E4C9A` | 보조 액션 (おすすめ 계열) | img3 `#184088` |
| `Alert.Red` | `#E23B2E` | 경고 테두리, 신규 뱃지 점 | img3 경고 rim vivid `#F40000` |
| `Chip.Dark` | `#2A2420` @ 60% | 배경 위 상태 칩 | img1 헤더 칩 `#433D3A` |
| `Level.Navy` | `#2B3A4C` / 숫자 `#7BE39A` | LV 알약 | img2 LV 알약 |

### 골드 계열 결정 (D-3 해소)

게임에서 주황/골드는 "선택됨"(`ノーマル` 탭)이고 앱에서는 "경고"(`⚠ 글자 상태`)다. **분리하기로 했다** — 선택은 `Accent.Orange`, 경고는 별도 `Gold.*` 계열로 두어 앱의 의미를 보존한다. 크림 전환 시 `Alert.Text`는 밝은 크림골드(`#E0C978`)에서 **어두운 딥앰버(`#8A5A00` 계열)로 반전**한다. 경고 배너만 다크로 남기는 안도 검토했으나, 다른 배너(청록·자주)와 문법이 어긋나서 채택하지 않았다.

### 상태색 (게임 스크린샷에 없지만 앱에 필요한 것)

게임 UI에는 없는데 앱에는 반드시 있어야 하는 축이다. 현재 앱은 상태를 **다크 배경 위 파스텔 글자**로 표현하고 있고(`MainPresetStatusText.Foreground` 등), 이 색들은 크림 배경에서 대비 1.3:1 수준으로 사실상 안 보인다. 크림용 대응값을 새로 정한다.

| 토큰 키 | 현재(다크) | 크림 대응 | 의미 | 현재 사용 |
|---|---|---|---|---|
| `Status.Success` | `#8FE3B1` | `#1E7A4B` | 적용됨·성공 | 32회 |
| `Status.Info` | `#B8EAF5` | `#1E5F8A` | 안내 | 21회 |
| `Status.Warn` | `#FFD08A` | `#8A5A00` | 주의 | 19회 |
| `Status.Error` | `#FF9E9E` | `#B32218` | 오류 | 4회 |
| `Status.Special` | `#D9C2FF` | `#5B3FA8` | 미라클 리더 등 특수 | 7회 |
| `Accent.Focus` | `#66D9EF` | `#C8722A` | 호버·포커스 링 | **49회** |

**`Accent.Orange`의 의미 충돌을 여기서 푼다.** 게임에서 주황은 "선택된 탭"이고, 앱에서 주황(`#FFD08A`)은 "주의"다. 같은 색이 두 뜻을 가지면 안 되므로 — 선택 = `Accent.Orange`(면 채움), 주의 = `Status.Warn`(글자색 전용, 채움 없음)으로 **적용 대상을 분리**한다.

**반경·간격** (게임 값 비율에서 환산, 1080px 폭 → 1320px 창 기준 ×0.75):
`Radius.Panel = 20`, `Radius.Card = 12`, `Radius.Pill = 999`, `Radius.Button = 10`.
간격은 4의 배수만: `4 / 8 / 12 / 16 / 24`.

---

## 4. 컴포넌트 스펙

각 항목은 `Themes/Kotodama.xaml`에 스타일 1개로 들어간다. 총 12개.

1. **`PanelCardStyle`** (Border) — `Bg.Panel`, `Line.Tan` 1px, `Radius.Panel`, Padding 16, DropShadow(BlurRadius 12, Opacity .18, Depth 2).
2. **`DialogChromeStyle`** — PanelCard + 상단 중앙 제목(SemiBold 16, `Text.Primary`) + 제목 아래 다이아몬드 구분선 + 우상단 X 버튼. 다이아몬드는 `Path`로 그린다(에셋 추가 없음).
3. **`PillToggleStyle`** (ToggleButton) — 미선택: `Bg.Field` + `Line.Tan`; 선택: `Accent.Orange` 그라디언트 + 흰 SemiBold; Height 34, Padding 20,0, `Radius.Pill`.
4. **`ActionButtonStyle`** (Button) — `Bg.Ivory` + `Line.TanStrong` 1.5px, `Radius.Pill`, Height 44, `Text.Primary` SemiBold. 하단 고정 주 액션용.
5. **`SecondaryButtonStyle`** — `Accent.Blue` 3종. 부수 액션.
6. **`GhostButtonStyle`** — 투명 면 + `Line.Tan`. 취소/닫기.
7. **`KanaChipStyle`** — 지름 36 원, `Bg.Field`, `Line.TanStrong` 2px, 글자 `Text.Primary` Bold 16. 선택 시 링이 `Accent.Pink`로.
8. **`CharacterCardStyle`** — `Radius.Card`, `Accent.PinkSoft` 3px 프레임(비활성 시 `Line.Tan`), 하단에 `Level.Navy` 알약 오버레이.
9. **`StatusChipStyle`** — `Chip.Dark`, `Radius.Pill`, 흰 텍스트 12, Padding 12,4.
10. **`DotBadgeStyle`** — 지름 12 `Alert.Red` 원 + 흰 2px 링. Border의 우상단에 붙는 Adorner 용도.
11. **`LabelBadgeStyle`** — `Accent.PinkPale` 면 + `Accent.Pink` 글자, `Radius.Pill` (発動可 형태).
12. **`AlertBannerStyle`** — `Alert.Red` 1.5px 테두리, 상단 3px 그라디언트 엣지, `Bg.Field` 면.

**밀도 우선 영역은 예외다.** 결과 목록은 수백 행이 뜨므로 게임 카드 룩(그림자·둥근 모서리·프레임)을 씌우면 읽기가 나빠진다. `Bg.Field` 흰 면 + `Line.Tan` 얇은 행 구분선 + 짝수 행 `#F7F5EF`까지만 적용한다.

대상은 XAML의 `DataGrid` 3개 · `ListBox` 5개 · `ItemsControl` 2개, **그리고 코드비하인드의 행 생성 경로**다. 이 예외는 XAML 스타일이 아니라 렌더 코드에 걸어야 실효가 있다 — 결과 행 상당수가 C#에서 만들어지기 때문이다.

---

## 5. 구현

파일 하나를 추가하고, 나머지는 삭제 작업이다.

```
Themes/Kotodama.xaml        (신규, 토큰 + 스타일 12개)
Themes/Theme.cs             (신규, 토큰의 C# 미러 — 코드비하인드용)
App.xaml                    (MergedDictionaries 3줄 추가)
각 *.xaml                   (로컬 <Style> 74개 삭제, 색 → StaticResource)
각 *.xaml.cs                (BrushFromHex 정의 8개 삭제, 호출 213개 → Theme.X)
```

`Theme.cs`는 딕셔너리에서 브러시를 꺼내오는 정적 프로퍼티만 갖는다. 값을 두 번 적지 않는다:

```csharp
static class Theme {
    static SolidColorBrush B(string k) => (SolidColorBrush)Application.Current.Resources[k];
    public static SolidColorBrush Panel      => B("Bg.Panel");
    public static SolidColorBrush PanelInner => B("Bg.PanelInner");
    // ... 토큰 23개
}
```

프로퍼티(필드 아님)로 두는 이유: static 필드 초기화는 `App` 리소스가 로드되기 전에 돌 수 있어 null이 된다. 딕셔너리 룩업 비용은 213회 수준에서 무의미하다. 브러시는 딕셔너리에서 `PresentationOptions:Freeze="True"`로 얼려 공유한다.

그러면 `BrushFromHex("#222A37")` → `Theme.Panel`. 호출부는 그대로 대입식이라 sed 치환으로 떨어지고, 색의 유일한 출처는 여전히 `Kotodama.xaml` 한 곳이다.

```xml
<!-- App.xaml -->
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/Kotodama.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

토큰은 `SolidColorBrush` + `x:Key`로만 둔다. 테마 전환 계획이 없으므로 `DynamicResource`도, 테마 서비스 클래스도 만들지 않는다.

### 단계

| 단계 | 범위 | 산출물 | 색 변화 |
|---|---|---|---|
| **0** ✅ | `Themes/Kotodama.xaml`(토큰 27개) + `Theme.cs` 작성, `App.xaml` 병합, `check_theme.sh` 가드 | 파일 3개 | 없음 |
| **0.5a** ✅ | 값이 토큰과 **정확히 일치**하는 호출 100개 → `Theme.X` | 7개 파일 | **없음 (무손실)** |
| **0.5b** ✅ | 남은 104개(유니크 72색) → 계열별 토큰. `BrushFromHex` 전부 삭제. 데이터 색 분리 | 8개 파일 + `DataPalette.cs` | 미세 (계열 내 통일) |
| **1** ✅ | `MainWindow.xaml` 이관 (색 89개) | 89개 치환, 잔여 0 | 없음 |
| **2** ✅ | `DeckEditorWindow.xaml` 이관 (색 147개) | 147개 치환, 잔여 0 | 없음 |
| **3** ✅ | 나머지 6개 다이얼로그 (색 363개) | 363개 치환, 잔여 0 | 없음 |
| **4** ✅ | **크림 전환** — `set_palette.py cream` | 토큰 값 45개 | **전체가 한 번에 바뀜** |
| **5** | 형태 작업 (P1~P7) — 둥근 모서리·pill·뱃지·3톤 배너 | 스타일 재작성 | 형태 |
| **6** | P8 경고 배너 — MessageBox 정리와 함께 (§7 참조) | 선택 | — |

### 단계 0.5를 둘로 가른 이유 — 실측

착수 후 재보니 `BrushFromHex` 리터럴 호출 204개 중 **기존 토큰과 값이 정확히 일치하는 것은 100개(49%)뿐**이었다. 나머지 104개(유니크 72색)는 토큰에 없는 색인데, 살펴보면 의도적 구분이 아니라 **우연히 어긋난 근사색**이다:

```
#8FA0B4 #8D98AA #7E8B98 #AFC2D1  ->  전부 Text.Secondary(#AEB8C8) 자리
#FF9B9B #FF8C8C                  ->  전부 Status.Error(#FF9E9E) 자리
#FFD166 #FFD27A #E0C978          ->  전부 Status.Warn(#FFD08A) 자리
```

근접도를 재면 72색 중 **51색(호출 70회)은 토큰과 거리 60 미만**이라 스냅해도 티가 안 난다. 나머지 21색(호출 34회)만 판단이 필요하고, 대부분 1~2회 사용이다.

그래서 **0.5a(무손실 100개)와 0.5b(스냅 104개)를 분리했다.** 0.5a는 색이 하나도 안 바뀌므로 스크린샷 픽셀 동일로 검증되고, 0.5b는 "리팩터"가 아니라 **의도적인 색 통일**이므로 그렇게 표시한다. 원안이 0.5 전체를 "색 변화 0"이라 적은 것은 절반만 맞았다.

### 롱테일 문제

"213개 호출을 토큰으로 치환"은 **248개 색 각각을 어느 토큰에 넣을지 판단해야** 성립한다. 그 분포를 실측했다:

```
색 사용 총 851회 / 유니크 248개
  상위 10색  -> 326회 (38%)
  상위 30색  -> 514회 (60%)
  상위 50색  -> 596회 (70%)
  1회만 쓰인 색: 147개 (유니크의 59%)
명도 분포: 밝음 71 / 중간 60 / 어두움 117
```

**유니크 색의 59%가 단 한 번만 쓰인다.** 이 147개를 하나씩 판단하는 것이 이 작업의 진짜 비용이고, "sed로 떨어진다"는 서술은 상위 30색에만 맞는 얘기였다. 그래서 두 갈래로 나눈다:

**해결됨.** 롱테일은 명도 반전이 아니라 **계열 분류**로 풀렸다. 72색을 6개 계열(회청·골드·자주·보라·초록·청록) × 3톤(면·선·글자)으로 나누니 25개 토큰에 전부 들어갔고, 1회성 색이 많았던 것은 계열 안의 명도 미세단계였을 뿐이라 합쳐도 화면에서 구분이 안 된다(같은 계열의 두 톤이 인접해 나타나는 곳이 없다). 매핑표 전체는 `Themes/color_map.tsv`에 72줄로 남아 있어 한 줄씩 검토할 수 있다.

명도 자동 반전은 **쓰지 않았다.** 계열 분류가 되고 나니 반전할 대상 자체가 없었다.

### 0.5b에서 실제로 나온 것

계획은 "근접 스냅"이었지만 실행 중 세 가지가 드러나 방향을 바꿨다.

**① 근접 스냅은 배너를 뭉갠다.** 거리 지표로는 알림 배너 면색 4개(`#392E52` 보라 · `#203B35` 초록 · `#243F35` 초록 · `#1E3040` 청록)가 전부 "거리 60 미만, 안전" 판정인데, 스냅하면 넷 다 `Accent.BlueFace` 하나로 합쳐져 `⚠`/`✨`/`◆`/안내 구분이 사라진다. 지표는 색 거리만 보고 의미를 못 본다. **원칙을 "계열 안에서만 스냅, 계열을 넘어서는 금지"로 바꾸고** 21색을 3톤 세트로 승격했다. 토큰 27 → **41개**.

**② 테마 색과 데이터 색은 다르다.** 게임 속성 색(火=빨강 등 8종)과 첫 턴 확률 램프(4단계)는 토큰에 넣으면 안 된다 — 크림 전환 후에도 그대로여야 하고(火가 크림색일 수 없다), 램프는 4단계의 순서 자체가 정보다. `Themes/DataPalette.cs`로 분리했고 테마 가드의 검사 대상에서 제외한다. 속성 스위치는 `MainWindow` 와 `DeckEditorWindow` 에 **같은 내용으로 복붙돼 있던 것**이라 합치면서 중복도 사라졌다(폴백만 호출부마다 달라 인자로 받는다).

**③ 색이 문자열 파라미터로도 흐르고 있었다.** `BrushFromHex("#...")` 형태만 세었더니 `CreateHandMetaBadge(text, "#9A7BD4", "#E4D4FF")` 처럼 **헬퍼 인자로 넘어가는 hex 문자열**이 집계에서 빠져 있었다. 헬퍼 4개(`CreateHandMetaBadge` · `ShowGeneralSuggestionMessage` · `CreateResultBadge` · `GetProbabilityColors`)의 시그니처를 `string` → `Brush` 로 바꿔 호출부에서 토큰을 넘기게 했다. 이제 색이 문자열로 떠다니는 경로가 없다.

**결과**: 코드비하인드의 하드코딩 색 **248개 중 코드 쪽 0개**. `BrushFromHex` 정의 8개 전부 제거(그중 `BrushFromHexStatic` 하나는 원래부터 죽은 코드였다). 빌드 경고 0.

### 단계 4에서 드러난 것: 색은 hex 로만 흐르지 않는다

크림으로 뒤집고 실제 화면을 캡처해 보니 오십음도 글자 버튼(`あ い う え お`)의 글자가 거의 안 보였다. 원인은 `Foreground = Brushes.White` 였다. **WPF 내장 브러시라 hex 가 아니고, 그래서 색 집계에도 코드모드에도 가드에도 전혀 걸리지 않았다.** XAML 쪽에는 `Value="White"` 형태로 같은 문제가 있었다. 합쳐서 14곳.

이건 hex 를 세는 것만으로는 절대 못 찾는 종류였고, **화면을 실제로 캡처해서 눈으로 본 덕에** 발견했다. 빌드도 가드도 대비 검사기도 전부 통과한 상태였다.

고치면서 두 가지를 배웠다:

- **흰색의 목적지가 두 갈래였다.** `Accent.Pink`(주 액션, 크림에서 `#C2306B`) 위의 흰 글자는 맞고, `Accent.Orange`(선택된 탭, `#FFA24E`) 위의 흰 글자는 대비 2.1로 틀렸다. 배경 토큰을 보고 `Text.OnAccent` 와 `Text.Primary` 로 갈랐다.
- **면 색이 실행 중에 정해지는 자리가 있다.** 결과 뱃지는 면이 테마 토큰(크림에서 밝음)일 수도, `DataPalette` 확률 램프(다크 유지)일 수도 있어 글자색을 한쪽으로 고정할 수 없다. `Theme.OnFace(Brush)` 가 면의 밝기를 재서 고르게 했다. 팔레트를 뒤집어도 따라온다.

가드에 내장 브러시·내장 색 이름 검사를 추가해 재발을 막았다.

### 결과 화면까지 확인해서 나온 것 2건

빈 화면만으로는 부족해서 실제로 검색을 돌려 결과 카드까지 캡처했다. 두 건이 더 나왔다.

- **주황 셀 위 캡션이 AA 미달.** 배치 셀(`Accent.Orange`, `#FFA24E`)의 `이번 배치` 캡션이 `Status.Info`(`#1E5F8A`)여서 대비 3.3이었다. `Theme.OnFace(Theme.Orange)` 로 바꿔 4.8이 됐다. 렌더 경로가 두 벌이라 두 곳 모두 고쳤다.
- **문구가 색 이름을 부르고 있었다.** 범례가 "파랑: 이번에 놓을 문자"였는데, 다크 팔레트에서 `Accent.Orange` 의 값이 `#245E88`(파랑)이라 그때는 맞는 말이었다. 크림에서 주황이 되면서 문구가 거짓이 됐다. **색 이름 대신 셀 안에 이미 쓰여 있는 말**(`'이번 배치'` / `'기존 판면'`)을 쓰도록 고쳤다. 팔레트를 뒤집어도 다시 틀리지 않는다.

두 번째가 이 작업의 교훈을 다시 보여준다. **UI 문구가 색을 지칭하면 그 문구도 팔레트의 일부다.** 토큰화는 색만 옮기고 문구는 건드리지 않으므로, 색 이름을 부르는 문구는 따로 찾아야 한다.

확인한 것: 결과 뱃지 3종(`7글자` 연파랑 · `예상 7콤보` 연초록 · `첫 턴 54.3%` 진초록+흰글자)이 모두 읽힌다. 특히 마지막은 `Theme.OnFace` 가 의도대로 동작한 증거다 -- 앞 둘은 밝은 테마 토큰 면이라 갈색 글자, 마지막은 `DataPalette` 확률 램프의 어두운 면이라 흰 글자를 골랐다. 게임 속성 색(闇/火/冥)도 크림 위에서 그대로 유지된다.

### 크림 전환 시점을 뒤로 옮겼다 (실행 중 발견)

원안은 코드비하인드 이관(0.5) 직후 바로 크림으로 뒤집는 것이었다. 실제로 해보니 **앱이 반반으로 갈렸다** — 코드비하인드가 만드는 UI는 크림인데 XAML이 선언한 패널은 다크로 남았다. 크림 전환의 전제는 "모든 표면이 토큰을 지난다"인데, 0.5b 시점에는 코드비하인드만 그 조건을 만족했다. XAML에는 색이 **599곳(유니크 172개)** 남아 있었다.

그래서 크림 전환을 **XAML 이관 이후로** 옮겼다. 단계 1~3은 색이 하나도 안 변하는 이관이고, 단계 4에서 한 번에 뒤집는다.

**되돌리기를 실물로 만들었다.** `ReleaseTools/set_palette.py dark|cream` 이 토큰 '이름'은 두고 '값'만 갈아끼운다. 앱 코드는 어느 쪽이든 손댈 필요가 없고, 크림이 별로면 `set_palette.py dark` 한 줄이다. PRD가 계속 약속해 온 "되돌리기는 파일 하나"가 이걸로 실제 기능이 됐다.

**대비 검사기도 붙였다.** `ReleaseTools/check_contrast.py` 가 글자/면 32쌍의 WCAG 대비를 검사한다. 크림 전환에서 가장 흔한 실패가 '옅은 면 위 옅은 글자'인데 빌드는 이걸 못 잡는다. 실제로 기본 테두리가 패널과 1.23으로 미달해 `#C9C0AB`(1.52)로 조정했다 — 게임 원본(`#CEC7B5`, 1.41)은 그림자로 경계를 만들지만 앱은 장시간 보는 화면이라 테두리 자체가 보여야 한다.

**단계 0.5 안에서도 순서가 바뀌었다.** 원안은 "MainWindow부터 이관"이었지만, MainWindow가 C# 호출 131개로 제일 무거운 파일이라 첫 단계로 부적합했다. 대신 **단계 0.5(리팩터)와 색 교체를 분리**한다. 이 순서의 이점:

- 단계 0.5는 색을 하나도 안 바꾸므로 **스크린샷이 픽셀 단위로 동일해야 한다** — 회귀 검증이 자명하다.
- 단계 4는 **명령 한 줄로 앱 전체 색이 바뀐다.** 크림이 별로면 `set_palette.py dark` 로 즉시 되돌아간다. 원안처럼 화면별로 크림을 발라나가면, 되돌릴 때 이미 손댄 화면 전부를 되짚어야 했다.
- 단계 5 이후는 색이 아니라 **형태**(둥근 모서리·pill·뱃지) 작업이라 성격이 다르고, 도중에 멈춰도 앱이 일관되게 보인다.

단계 1이 끝난 시점에 판단한다: 크림 테마가 실제 작업 화면에서 눈에 편한가. 아니면 단계 2로 넘어가지 말고 토큰 값만 다크 계열로 되돌린다 — 딕셔너리 구조는 그대로 쓸 수 있으므로 되돌리는 비용은 색 표 한 장이다. **이 되돌림 여지를 남기는 것이 딕셔너리를 먼저 만드는 이유다.**

---

## 6. 검증

1. `dotnet build` 통과 — XAML 리소스 키 오타는 빌드가 아니라 런타임에 터지므로 빌드만으로는 부족하다.
2. **실행 스모크** — `run.bat`으로 띄워 각 윈도우를 한 번씩 연다. 없는 `StaticResource`는 이 시점에 즉시 예외로 드러난다.
3. **스크린샷 비교** — 캡처 시 `SetProcessDPIAware` 필수. 이 머신 125% 스케일에서 안 넣으면 오른쪽 20%가 잘려 레이아웃을 오진한다.
4. **테마 회귀 가드** — 이관 끝난 파일에 생색이 다시 들어오는 걸 막는다:

```sh
# ReleaseTools/check_theme.sh
# 이관 완료 파일에 하드코딩 #RRGGBB가 남아 있으면 실패
# .xaml 과 .xaml.cs 를 함께 본다 — C#에 색이 더 많다
MIGRATED="MainWindow.xaml MainWindow.xaml.cs"   # 단계마다 추가
fail=0
for f in $MIGRATED; do
  hits=$(grep -oE '#[0-9A-Fa-f]{6}' "$f" | sort -u)
  if [ -n "$hits" ]; then echo "FAIL $f: $hits"; fail=1; fi
done
# BrushFromHex 사본이 되살아나는 것도 막는다
if grep -l 'Brush BrushFromHex\|SolidColorBrush BrushFromHex' *.xaml.cs 2>/dev/null | grep -q .; then
  echo "FAIL: BrushFromHex 사본 부활"; fail=1
fi
[ $fail -eq 0 ] && echo "OK: theme tokens only"
exit $fail
```

`Themes/Kotodama.xaml` 자체는 검사 대상에서 제외한다(거기가 색의 유일한 출처다).

---

## 7. 리스크

| 리스크 | 대응 |
|---|---|
| 다크 → 크림이 장시간 작업 화면에서 눈부심 | 단계 1이 토큰 17줄 커밋 하나다. 되돌리기 = revert 1회 |
| **`MessageBox.Show` 30회는 테마가 안 먹는다** | Win32 다이얼로그라 크림 앱에 시스템 회색 박스가 뜬다. **30개를 다 커스텀으로 바꾸지 않는다** — 확인/취소성 알림은 OS 룩이 오히려 맞다. 자주 뜨는 오류·경고만 P8 인앱 배너로 옮긴다 (단계 5) |
| 213개 호출 치환 중 회귀 | 단계 0.5는 색 변화가 0이므로 스크린샷 픽셀 동일 여부로 검증 가능 |
| **롱테일 218색 자동 반전이 어색하게 나옴** | 상위 30색은 손으로 잡으므로 눈에 띄는 면적은 정확하다. 나머지는 1회성이라 어색한 것만 사후 승격. 반전 스크립트는 소스를 고치고 버리므로 결과를 직접 수정할 수 있다 |
| 게임 UI를 그대로 베낀 인상 | 프레임 문법만 차용, 일러스트·로고·폰트는 미사용 (§2 "가져오지 않는 것") |
| 배포 중인 v1.25.1과 충돌 | UI 작업은 릴리스 공개(현 PRD.md 남은 일 2~4) **이후** 시작 |

**리스크에서 뺀 것: OS 타이틀바 불일치.** 커스텀 창 크롬을 쓰는 곳이 없어(`AllowsTransparency`는 전부 ComboBox 팝업 것) 타이틀바는 Windows 11 시스템 테마를 따른다. 지금도 다크 셸 + 시스템 타이틀바로 어긋나 있으므로, 크림 전환은 이 축에서는 오히려 나아진다.

---

## 8. 결정 필요

- **D-1** 크림 라이트 테마로 간다 vs 다크 유지하고 구조 패턴(P2·P3·P4·P7)만 차용 → 이 PRD는 전자를 기본안으로 잡되 단계 1에서 되돌릴 수 있게 설계했다.
- **D-2** P9(타일 그리드 메뉴)는 지금 앱에 대응 화면이 사실상 없다. 새 메뉴 화면을 만들 게 아니면 드랍.
- **D-3** `MessageBox` 30개 중 몇 개를 인앱 배너로 옮길지. 기본안은 "오류·경고만, 확인성은 그대로".
