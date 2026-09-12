# Valheim 1.0.7 / Jotunn 제거 패치

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
