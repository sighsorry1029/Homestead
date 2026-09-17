# Valheim 호환성 기록

## 1.0.12 → 1.0.14 (2026-09-17)

기준: `main` / `40fd385` (Homestead 1.2.16), 작업 시작 시 변경 없음. 클라이언트
Steam build **25364265**, 데디케이트 **25364309** 원본을 사용했다. 설치된
`assembly_valheim`, `assembly_utils`, `assembly_guiutils`의 SHA-256을 양쪽
1.0.14 전역 스냅샷과 대조했다. 아래 1.0.7 검토는 과거 기록으로 보존한다.

### 필요한 대응

- **건축 카메라 재빌드:** 기존 Release DLL은 `ZInput.GetJoyLeftStickX/Y(bool)`와
  `GetJoyRightStickX/Y(bool)`를 호출한다. 1.0.14에는 네 bool 오버로드가 없다.
  `ZoneBuildCamera`의 인자 없는 C# 호출은 그대로 두고 새 게임 DLL로 재컴파일했다.
  최종 Debug DLL은 네 인자 없는 메서드를 호출한다. 입력 방향·감도 정책은 변경하지 않았다.
- **반환 재료 소실 방지:** `ZoneMaterialEscrow.GiveOrDropItem`의 `CanAddItem` 성공을
  실제 추가 성공으로 간주하던 처리를 수정했다. 1.0.14의 `AddItem`은 `m_cheated`가
  다른 스택에 합치지 않지만 `CanAddItem`의 공간 계산은 이를 구분하지 않는다.
  원본 런타임에서 변경 전 수량 소실을 재현했다. 이제 `AddItem` 실패 시 실제 남은
  `item.m_stack`만 드롭하여 부분 병합 후에도 수량·반환 아이템 메타데이터를 보존한다.
  기존 품질 불일치에서도 발생할 수 있었던 실패 무시 문제가 새 치트 스택 분리로 확대된 경우다.
- **격리 서버 검사:** 설치본 `steam_appid.txt`의 896660 때문에 `Invalid APPID`로
  종료되는 문제를 확인했다. 테스트 실행기는 격리 사본에만 게임 App ID 892970을
  기록한다. 실제 서버 설치본은 수정하지 않았다.

### 검토 범위와 유지한 계약

`Plugin.cs`의 BepInEx 플러그인, 선택적 연동 선언, `Homestead.csproj`의 리소스와
Debug 복사/Release 패키징 경로, `GameReferences.targets` 및 최종 ILRepack 입력을
확인했다. 고정 ServerSync `valheim-1.0.7-r1`과 YamlDotNet의 기존 병합을 유지한다.
원본 분석과 `obj` 안의 기존 publicizer 컴파일 사본을 구분했으며 설정을 일괄 교체하지 않았다.

최종 병합 DLL의 게임 3개 어셈블리에 대한 MemberRef 544개를 클라이언트·서버 원본에서
각각 해석하여 누락 0개를 확인했다. 이것만으로 모든 비공개 접근이나 간접 호출의 실행을
보장하지 않으므로 아래 Mono/Unity 실행 검사도 수행했다.
Harmony 대상·리플렉션 검색과 1.0.14 변경점의 교집합을 검토했다. 핫바 분기를 가정하는
transpiler는 없고, 기존 배치 회전 transpiler의 대상 및 70개 패치 설치를 실행에서 확인했다.
지형 지지대 생성은 실제 수정 대상 compiler가 없을 때만 기존 생성 경로를 사용하며,
게임의 이웃 paint 조회를 옛 생성 방식으로 되돌리지 않는다. owner·권한·롤백 검사는 유지한다.

설정 키/기본값, RPC·저장 형식, creator/관리자 정책, 재료 에스크로와 거래 정책은 변경하지 않았다.
에스크로가 개별 아이템 메타데이터 대신 재료별 수량을 보관하는 기존 정책은 이번 수정과
별개이며, 전체 거래의 치트 출처 보존을 새로 보장하는 패치가 아니다.
다른 모드의 소스·라이브러리 업데이트, 리소스 전체 추출과 셰이더 의미 분석은 제외했다.

### 검증

- 기준/수정 후 `dotnet build Homestead.csproj -c Debug -p:DeployToGame=true` 성공,
  경고/오류 0개. 병합 후 plugins의 Homestead.dll과 출력 DLL SHA-256 일치.
- `pwsh -NoProfile -File tests/Run-RegressionChecks.ps1` 통과.
- 원본 1.0.14 Unity/Mono 격리 클라이언트에서 수정 전 치트 표시 불일치 반환 실패를 재현.
  수정 후 치트 표시 불일치, 부분 병합, 정상 병합, 공간 없음의 네 수량/메타데이터 검사 통과.
- 격리 클라이언트 로컬 월드: Harmony 70개 대상, 네 상자 프리팹, 내용물 보호,
  Homestead 탭 격리, 상점/가격 UI, 배치 모드 배타성, 휠 줌 차단, 아이콘과 UI 정리 검사 완료.
- 격리 데디케이트: App ID 교정 후 월드 로드, Harmony 70개 대상, 프리팹/ZDO,
  미로드 상자·진열대 보호 검사 완료. 두 보고서 모두 `COMPLETE` 확인.
- 실제 플레이어 간 접속/거래, 2인 owner 이전, 크로스플레이, 접속 종료·재접속,
  실제 게임패드 입력/감도, 지형 경계 변경 및 선택적 모드 조합은 **미검증**.

검토 기준은 1.2.16이었으며, 확인된 대응과 후속 접근 제어 변경은 1.2.17에 포함했다.
게임 DLL로 다시 컴파일했으므로 이 Debug DLL의 구 게임 실행 호환성을 보장하지 않는다.

전역 근거: `C:/Users/blizz/.codex/references/valheim/comparisons/1.0.12--1.0.14-windows-x64/mods/Homestead-1.2.16/`.

## Valheim 1.0.7 / Jotunn 제거 패치

2026-09-11, Homestead 1.2.13 기준.

## 대상과 근거

- Windows x64 클라이언트 Steam build **25185596**, 데디케이트 서버 build **25185644**, 게임 **1.0.7**.
- BepInExPack **5.4.2350**. 런타임 검사에는 BepInEx 5.4.23.5, HarmonyX 2.9.0.0 및 설치된 원본 Unity/게임 DLL을 사용했다.
- 비교 기준: Homestead commit `8e6565abd217a021c032a413a0b1be005682a277`, Valheim 0.221.12 및 Jotunn 2.29.2.
- 원본 스냅샷: `C:/Users/blizz/.codex/references/valheim/snapshots/client-b25185596-windows-x64-20260909T131109Z` 및 `dedicated-server-b25185644-windows-x64-20260909T131109Z`.
- 분석 기록: `C:/Users/blizz/.codex/references/valheim/comparisons/0.221.12--1.0.7-windows-x64/mods/Homestead-1.2.11/REVIEW.md`, `JOTUNN-REMOVAL.md`. 이 두 문서는 패치 전 영향 분석이다.

0.221.x 실행 지원이나 Homestead 자체의 구 게임 데이터 마이그레이션을 추가하지 않았다.

## 변경한 책임

| 영역 | 구현과 보존 범위 |
| --- | --- |
| 의존성 | 프로젝트의 Jotunn DLL 참조, BepInDependency, manifest 의존성을 제거했다. Jotunn을 사용하는 다른 모드나 설치된 Jotunn DLL은 변경하지 않는다. |
| 프리팹 | `HomesteadPrefabs`가 소유한 네트워크 상자 네 종류만 등록한다. 비활성 부모에서 복제하여 템플릿의 Awake/ZDO 생성을 막고, ZNetScene.Awake 후 등록한다. 기존 이름·해시를 유지하며 월드 변경 시 재등록하고 해시 충돌은 거부한다. |
| 건축 메뉴 | 기존 PieceTable 삽입 경로를 사용하며 불필요해진 Jotunn 등록 상태를 삭제했다. 새 BuildUi에 IPieceList와 탭 하나를 붙이고 네이티브 버튼 풀·선택·호버 경로를 사용한다. 수리 버튼을 유지하고 망치 이외의 도구에서는 탭을 숨긴다. |
| 번역 | 기존 YAML 파서·키·외부 번역 우선순위를 유지하고 Localization.SetupLanguage 후 영어 fallback 및 선택 언어를 적용한다. |
| 창과 입력 | `HomesteadUi`는 실제 사용하는 네 가지 컨트롤만 만든다. 게임 폰트·스프라이트·재질을 사용하고 기존 창 위치·크기 설정을 유지한다. 자체 모달 입력 상태만 소유하며 창 닫기·세션 초기화·종료 시 정리한다. |
| 설계도 아이콘 | `HomesteadIconRenderer`가 기존 캐시와 복제된 시각 모델을 사용한다. 요청당 임시 카메라·렌더 텍스처를 정리하고 전역 렌더 상태를 복원한다. 대기열은 프레임당 하나씩 처리하고 데디케이트에서는 실행하지 않는다. |
| 게임 API | 새 PieceTable HashSet/카테고리 목록, InventoryElement, SimulationDistance/Vector2s, TerrainComp.Save/Poke, Inventory.Changed 및 VisEquipment.AttachItem 시그니처를 반영했다. 변경된 private 접근은 `HomesteadGameAccess` 등의 캐시된 Harmony 접근자를 사용한다. |
| 패치 대상 | Inventory.Load 및 ItemData.GetTooltip의 정확한 오버로드를 명시했다. 기존 Harmony 우선순위와 상태 전달은 유지하고 자체 프리팹·탭 등록은 마지막 우선순위를 사용한다. |
| 제작자 | Piece.SetCreator의 새 플랫폼 ID 인자를 채운다. 원격 요청은 준비된 peer와 player ID를 대조하고 인증된 socket의 플랫폼 ID를 사용하며, 해석 실패는 생성 전에 거부한다. |
| 설정 동기화 | 별도 원인인 ServerSync 오류는 고정된 `valheim-1.0.7-r1` DLL로 대응했다. 기존 병합 방식을 유지한다. 출처·해시는 `Libs/ServerSync.md`에 기록했다. |

기존 설정 키, 블루프린트/YAML 및 상점 저장 형식, 공개 연동 표면, 커스텀 ZDO/RPC 키, 구매·지급·재료 에스크로 정책은 변경하지 않았다. 선택적 모드 연동 코드는 유지했다. 다른 모드의 비등록 프리팹을 Jotunn 전역 캐시에서 찾던 간접 경로는 이제 ObjectDB/ZNetScene에 등록된 프리팹을 기준으로 하므로 해당 조합은 별도 확인이 필요하다.

## 구조 교체와 구분되는 내용물 보호 수정

1.0.7에서는 컨테이너 내용물이 byte[]이고 진열대 부착물이 정수 해시다. 이전 읽기 방식은 미로드 상자·진열대를 빈 것으로 볼 수 있었다. Area Dismantle은 새 형식을 읽고, 버전 109의 정확한 빈 인벤토리 payload만 빈 것으로 인정한다. 손상·잘림·알 수 없는 버전·지원하지 않는 문자열 데이터는 철거 대상으로 허용하지 않는다. 실물 인벤토리, 소유권, 거리, 권한 및 중복 처리 보호를 제거하지 않았다.

## 컴파일 참조와 접근 제한

`environment.props`의 원본 게임 DLL이 입력이다. 기존 프로젝트의 다수 비공개 접근을 일괄 재작성하지 않기 위해 `GameReferences.targets`는 **BepInEx.AssemblyPublicizer.MSBuild 0.4.2**로 `obj` 아래에만 컴파일용 사본을 만든다. 분석은 보존된 원본에서 수행했다. 게임 폴더의 과거 `publicized_assemblies`는 빌드 입력에서 제외했다.

컴파일용 사본의 public 표시를 원본 API로 간주하지 않는다. 도구의 Mono unsafe 모듈 처리와 생성된 접근 검사 특성을 최종 병합 DLL에 포함하며, 접근 제한을 바꾸지 않은 실제 Mono 게임에서 검사했다. 원본 게임 DLL을 덮어쓰거나 배포하지 않는다. 이 구성의 검증 명령은 `dotnet build`이며 다른 MSBuild 호스트와 Linux 런타임은 미검증이다.

## 검증과 재현

### 빌드 및 자동 검사

```powershell
dotnet build Homestead.csproj -c Debug -p:DeployToGame=true
./tests/Run-RegressionChecks.ps1
```

Debug 빌드 및 ILRepack 병합 성공, 경고/오류 0개. 최종 DLL/manifest의 Jotunn 의존성 부재, 480개 listing ID, 깊은 복제 독립성, 샘플 블루프린트 네 개의 직렬화 왕복, 원자적 파일 저장 보호, 내장 번역/설계도 리소스, circlet 입력/정리 순서 검사를 통과했다. `DeployToGame=true`는 최종 `Homestead.dll`만 plugins에 복사한다.

### 실제 Unity/게임 실행 검사

```powershell
dotnet build tests/RuntimeProbe/RuntimeProbe.csproj -c Debug
./tests/Start-RuntimeProbe.ps1 -Role server
./tests/Start-RuntimeProbe.ps1 -Role client
```

실행 스크립트는 프로세스를 시작하고 즉시 반환한다. 각 `artifacts/runtime-<role>/probe.txt`의 `COMPLETE` 또는 `FAIL`, 프로세스 종료 및 BepInEx 로그를 함께 확인한다. 테스트 플러그인은 일반 게임 폴더에서는 실행하지 않으며, 빌드·패키징에 포함되지 않는다. 게임 데이터/Mono는 설치 폴더의 junction으로 읽고 실행 파일·플러그인·설정·로그·테스트 월드/캐릭터는 artifacts에 격리한다. 이 폴더를 정리할 때 junction의 원본 대상을 재귀 삭제해서는 안 된다.

클라이언트는 서버용 `-savedir` 인자를 처리하지 않으므로 테스트 플러그인의 Awake에서 공개 `Utils.SetSaveDataPath`를 호출하고 경로를 검사한다. 초기 검사에서 일반 저장 폴더에 생성했던 `homestead_probe` 캐릭터와 `HomesteadProbe` 월드/캐시는 `artifacts/initial-probe-saves`로 옮겼다. 기존 사용자 캐릭터·월드는 이동하지 않았다. 경로 수정 후 클라이언트와 서버 검사를 다시 통과했다.

클라이언트의 격리 로컬 호스트 및 데디케이트 프로세스에서 **Jotunn 없이** 로딩, Harmony 패치 설치, 번역 등록, 상자 템플릿/ZDO 생성, 미로드 상자·진열대 내용물 보호를 검사했다. 클라이언트에서는 건축 목록과 탭, 수리 버튼, 기본 휠 스프라이트, 설계도 아이콘 렌더, 상점 창/로컬 목록 응답, 입력 차단 해제를 검사했다. 숨김 batchmode의 검은 화면 캡처 대신 실제 UI Canvas를 임시 카메라로 렌더한 이미지를 사용했다. 화면 재현은 일반 플레이 조작과 구분한다.

### 아직 필요한 검증

- 서로 다른 클라이언트의 호스트/데디케이트 접속, Steam/PlayFab 교차 플레이, 접속 중단·재접속 및 설정 권한/잠금 동기화.
- 실제 다중 사용자 구매·지급·설계도 재료 투입·철거 중 소유권 이전, 재전송, 인벤토리 가득 참 및 서버 저장/재시작 시 소실·복제 방지.
- 선택적 모드와 다른 건축 카테고리 등록 모드의 동시 사용, 여러 ServerSync 내장 모드의 패치 합성.
- 일반 플레이에서 다양한 해상도·UI 배율·한글·게임패드, 모달 중첩/드래그, 실제 지형 저장/복원, circlet 원격 표시.
- 대형 설계도의 프레임 시간·GC 측정. 자체 기능의 리소스 검색은 HUD 초기화 시, UI 생성은 기존 지연 생성 시점, 리플렉션 탐색은 접근자 초기화 시에 한정했지만 정량 성능 개선 수치는 측정하지 않았다.

이 실행 검사는 두 프로세스 간 실제 접속이나 전체 게임 기능 검증을 의미하지 않는다.
