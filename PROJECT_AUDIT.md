# Project Audit

감사 기준일: 2026-11-27. 대상 커밋: `42f7afa` (DESCRIPTION: v1.2.2 매니페스트 발행 시점).
코드는 수정하지 않고 읽기·분석만 수행했다.

## 1. Executive Summary

**전체 상태는 양호하고 전체 위험도는 Medium이다.**
데이터를 직접 쓰는 사용자 문서 경로가 없고(저장은 한글이 담당), 실행 게이트·서명 업데이트·레지스트리 복원 등
핵심 안전장치가 코드로 확인된다. 2026-11-26 감사 이후 지적됐던 항목(프로세스 경합, ReJIT 상태 구분,
네이티브 로그 버퍼, 공백 경로 빌드, CLI 빈 값 검증)은 현재 코드에서 해소된 것으로 확인된다.
다만 High/Critical에 해당하는 잔여 문제는 없고, 아래 3건이 남는다.

1. 변조·손상된 `Hwp.exe`를 분석할 때 파서가 `ArgumentException`을 밖으로 흘려 CLI `list`가 비정상 종료한다 (Likely / Medium).
2. 실행 중인 한글 판정이 프로세스 이름(`Hwp`)에만 의존해 동명 프로세스에 오작동한다 (Likely / Low).
3. 복원에 실패한 업데이트의 수동 복원용 백업을 다음 업데이트가 조용히 지운다 (Likely / Low).

**데이터 손상/유실 가능성: 사용자 문서에 대해서는 없음.**
런처는 설치 파일을 읽고 자식 프로세스를 시작할 뿐 문서에 쓰지 않는다.
프로파일러가 바꾸는 것은 `ConvertToHwpxPath`의 반환값뿐이라 최악의 경우는 HWPX로 저장되는 형식 어긋남이다.
업데이트 교체 실패 시에는 smoke 검사+롤백이 있고, 수동 복원용 백업이 남는다(ISSUE-003의 보관 흠결 제외).

**가장 먼저 수정할 영역:** `HwpBuildInspector` 파서 강건화와 그 호출자(`list`)의 예외 처리(ISSUE-001).
공유 파서라 한 곳을 고치면 전 경로가 보호된다.

## 2. Project Understanding

[README](README.md), [빌드 안내](docs/BUILD.md), [버전별 호환성](docs/VERSION-COMPATIBILITY.md),
[설계](docs/superpowers/specs/2026-11-26-hwp-save-hook-design.md),
[검증 기록](docs/superpowers/specs/2026-11-26-hwp-save-hook-rejit-handoff.md),
[빌드 스크립트](Build-Local.ps1), [업데이트 매니페스트](updates/latest.json)를 확인했다.
체크아웃에 `CLAUDE.md`와 `AGENTS.md`는 없고, 이를 참조하는 문서도 없다(검색으로 확인).

- **프로젝트 목적:** 한글 2022 교육기관용 등에서 `.hwp` 저장이 `.hwpx`로 강제 전환되는 것을,
  설치 파일을 건드리지 않고 CLR 프로파일러 환경변수를 건 자식 프로세스에서만 우회하는 Windows 런처.
- **주요 entrypoint**
  - x64 WPF GUI: [App.xaml.cs](private/save-hook/gui/App.xaml.cs) → [MainWindow.xaml.cs](private/save-hook/gui/MainWindow.xaml.cs)
  - 투명 실행(`--open`, 더블클릭·바로가기): `App.RunTransparentAsync`
  - CLI: [launcher/Program.cs](private/save-hook/launcher/Program.cs) (`plan`·`launch`·`inspect`·`list`·`update-keygen`·`update-sign`)
  - x86 네이티브 프로파일러: [profiler/HwpSaveHookProfiler.cpp](private/save-hook/profiler/HwpSaveHookProfiler.cpp) (+판정표 [Target.h](private/save-hook/profiler/Target.h))
  - 형식 검사 도구: [tools/inspect_format.py](tools/inspect_format.py) (수동 개발 도구, 제품 미포함)
- **핵심 모듈:** [SaveHookLauncher.cs](private/save-hook/core/SaveHookLauncher.cs) (게이트·계획·실행),
  [HwpInstallations.cs](private/save-hook/core/HwpInstallations.cs) (탐지·8초 타임아웃),
  [HwpBuildInspector.cs](private/save-hook/core/HwpBuildInspector.cs) (PE·IL 지문),
  [HangulProcesses.cs](private/save-hook/core/HangulProcesses.cs) (실행 상태),
  [WindowsIntegration.cs](private/save-hook/core/WindowsIntegration.cs) (바로가기·파일 연결·백업),
  [SaveHookSettings.cs](private/save-hook/core/SaveHookSettings.cs) (원자적 설정 기록),
  [RejitStatus.cs](private/save-hook/core/RejitStatus.cs) (로그 기반 상태),
  [UpdateInstaller.cs](private/save-hook/core/UpdateInstaller.cs) + [UpdateManifest.cs](private/save-hook/core/UpdateManifest.cs) (서명 업데이트).
- **데이터 저장 방식:** DB 없음. `%LOCALAPPDATA%\HwpSaveHook\`에 `save-mode.txt`·`hwp-path.txt`·
  `hwp-path-detected.txt`·`compatible-mode.txt`·`assoc-backup.txt`·`logs\`·`updates\`를 둔다.
  설정 기록은 임시 파일 후 교체(`File.Replace`/`Move`) 방식이다.
- **외부 의존성:** .NET 10 SDK(CLI·Core·테스트), .NET Framework 4.8(GUI, Core 소스 직접 컴파일),
  MSVC x86(프로파일러), Windows 레지스트리(HKCU/HKLM 읽기, HKCU 쓰기), GitHub 릴리스·`raw.githubusercontent.com`(업데이트 확인).
  런타임 DB·트랜잭션·마이그레이션은 없다.
- **핵심 실행 흐름:**
  - GUI 열기: `App_Startup → Window_Loaded → Scan → FindPreferred → CreatePlan → DescribeBuild·상태 표시`
  - GUI 시작: `Launch_Click → Start(재검사·CreatePlan) → Launch(뮤텍스+실행중 검사+Process.Start) → WaitAsync → 요청접수/적용됨/실패 표시(+백그라운드 ObserveCompilation)`
  - CLI: `Run → FindPreferred → CreatePlan → plan(JSON 출력) | launch(Launch+WaitAsync → launched/profiling-unconfirmed)`
  - 투명 실행: `RunTransparentAsync(저장→탐지 캐시→전체 탐지 → CreatePlan → 미적용/미훅 경고 → LaunchTransparent(뮤텍스 직렬화, 실행중 검사 없음))`
  - 프로파일러: `Initialize(실행파일명 게이트·SetEventMask2) → ModuleLoadFinished/VerifyTarget(MVID·토큰 또는 compatible 토큰) → 워커 RequestReJIT → GetReJITParameters(ldc.i4.0;ret) → 컴파일/오류 콜백 → ReJITState 로그 → RejitLog.ReadStatus/WaitAsync`
  - 업데이트: `Check(HTTPS 다운로드+서명·버전·만료 검증) → Stage(크기·해시+ZIP 탈출·필수파일 검사) → LaunchHelper(복사 후 종료) → Apply(부모 대기·한글 실행중 재확인·ReplaceFiles+smoke·실패 시 Rollback) → WriteResult → 다음 시작 시 ConsumeResult`

## 3. Audit Coverage & Limitations

- **CodeGraph:** 인덱스가 7개 파일에 대해 stale 상태여서 `codegraph sync`로 동기화한 뒤(268노드, 7파일 변경 반영)
  다음 호출 관계를 확인했다. `SaveHookLauncher.CreatePlan/Launch/LaunchTransparentCore`의 GUI·CLI 호출자 3곳,
  `LaunchTransparentCore/StartSingleInstance`의 `Local\HwpSaveHook.Launch` 뮤텍스 공유,
  `HangulProcesses.GetState/IsHangulRunningInSession`의 GUI·CLI 호출자, `SaveHookSettings` 원자 기록의 6개 호출자,
  `UpdateInstaller.Apply/Rollback/ReplaceFiles`와 `UpdateManifestVerifier.Verify/VerifySignature` 관계,
  `RejitLog.WaitAsync/ReadStatus`의 GUI·CLI 호출자, 프로파일러 `LogRejitState` 5개 호출 지점.
  전제는 CLI `codegraph`이며 MCP 도구는 사용하지 않았다.
- **직접 읽은 주요 모듈:** Core 10개 파일 전체, GUI 2개 파일 전체, CLI 전체, 테스트 하네스 개요,
  프로파일러 `.cpp`·`Target.h` 전체, `tools/inspect_format.py`, 릴리스 워크플로, 빌드 스크립트 2종.
- **실행한 검사:** `python tools/inspect_format.py LICENSE` → `unknown`/종료 코드 6으로 음성 경로가 설계대로 동작함을 확인.
  `codegraph status`(인덱스 23파일·1,526노드·2,498엣지), 테스트 영역별 집계(`Check` 113건).
- **확인하지 못한 환경/외부 서비스:** .NET SDK·MSVC가 없어 빌드·런처 테스트를 실행하지 않았다.
  실제 한글 설치가 없어 저장 E2E·ReJIT 실기·업데이트 서버 장애 주입도 수행하지 않았다.
  이전 검증 기록의 실기 결과(HWP5 저장·렌더 비교)는 문서 주장으로만 인용하고 재검증하지 않았다.
- **분석 한계:** CodeGraph 초기 스냅샷이 stale이어서 동기화 후 사용했고, C++ 측 호출 추적은 소스 직접 대조로 보완했다.
  실행하지 않은 테스트를 실행했다고 쓰지 않으며, 재현하지 못한 문제는 `Likely` 이하로만 분류했다.

## 4. High-Risk Issues

High-Risk에는 `Confirmed`와 근거가 강한 `Likely`만 둔다. 이번 감사에서 런타임 재현에 성공한 항목은 없고
(SDK·한글 부재) 아래 3건은 코드 근거가 확정적이라 `Likely`로 둔다. Critical/High는 없다.

### [ISSUE-001] 손상된 PE 입력에 파서가 ArgumentException을 흘려 CLI list가 비정상 종료한다

- **위치:** [HwpBuildInspector.cs](private/save-hook/core/HwpBuildInspector.cs) `InspectImage`·`ReadBlob`·`CallsOf`·`Image`,
  [Program.cs](private/save-hook/launcher/Program.cs) `ListInstallations`
- **우선순위:** Medium
- **신뢰도:** Likely
- **문제:** `InspectImage`의 catch는 `BadImageException·ArgumentOutOfRangeException·IndexOutOfRangeException·OverflowException`만
  처리한다. 그런데 `ReadBlob`의 `Buffer.BlockCopy(pe.Bytes, at, value, 0, length)`는 범위를 벗어나면
  `ArgumentException`을 던지고(문서화된 동작), `new byte[length]`는 거짓된 blob 길이 선언에 `OutOfMemoryException`을 던질 수 있다.
  둘 다 catch 목록에 없어 호출자로 전파된다.
- **발생 조건:** 레지스트리·설치 폴더 탐지 경로에, PE 헤더는 유효하지만 메타데이터 blob이 깨진 `Hwp.exe` 이름의 파일이 있고
  `GetBinaryType`을 통과할 때. 손상된 설치본·수동 배치·악성 배치 모두 가능하다.
  `MaxImageBytes`(512MB)는 파일 크기를 막을 뿐 blob 내부 길이 선언은 막지 못한다.
- **영향:** `hwp-save-hook list`가 처리되지 않은 예외로 죽는다(종료 코드 계약 0/2/3/5 밖). 사용자 문서·설정에는 영향이 없다.
- **근거:** catch 목록([HwpBuildInspector.cs](private/save-hook/core/HwpBuildInspector.cs#L122))과
  `BlockCopy` 호출([HwpBuildInspector.cs](private/save-hook/core/HwpBuildInspector.cs#L277)),
  `ListInstallations`의 catch가 `IOException·UnauthorizedAccessException·SecurityException·TimeoutException`뿐인 점.
- **반증 확인:** GUI `Scan`·`Start`(→`RunOperation`, `ArgumentException` 처리),
  CLI `plan`·`launch`(→종료 코드 2), CLI `inspect`(→종료 코드 2), `--open`(→포괄 catch+안내창)은
  `ArgumentException`을 받아낸다. 그래서 실제 크래시는 `list` 경로에만 남는다.
  `File.ReadAllBytes` 이전의 길이 상한, `checked` 연산, `U16/U32`의 `IndexOutOfRangeException` 경로(처리됨)도 확인했다.
- **호출/영향 범위:** CodeGraph상 `Inspect` 호출자는 `CreatePlan`(2곳)·`ChooseCandidate`·`RunSmoke`·`list`·`inspect`.
  크래시로 이어지는 것은 `list`뿐이며, `RunSmoke`는 해시 검증된 공식 artifact만 검사해 사실상 도달 불가다.
- **권장 수정 방향:** `InspectImage` catch에 `ArgumentException`을 추가해 `Unreadable`로 반환한다(공유 파서 1곳 수정).
  방어 차원에서 `ListInstallations` catch에도 `ArgumentException·NotSupportedException`을 추가한다.
  `OutOfMemoryException`은 별도 상한(예: blob 길이 선언이 남은 이미지 크기를 넘으면 `BadImageException`)으로 막는다.
- **필요한 회귀 테스트:** 유효 MZ/PE+CLR 헤더에 거짓 blob 길이를 넣은 fixture → `Inspect`는 `Unreadable`,
  `list`는 크래시 없이 종료 코드 3 또는 5. 정상 관리 어셈블리에서는 기존 판정 불변.

### [ISSUE-002] 실행 중 한글 판정이 프로세스 이름에만 의존한다

- **위치:** [HangulProcesses.cs](private/save-hook/core/HangulProcesses.cs) `GetState`,
  [SaveHookLauncher.cs](private/save-hook/core/SaveHookLauncher.cs) `IsHangulRunningInSession`
- **우선순위:** Low
- **신뢰도:** Likely
- **문제:** `Process.GetProcessesByName("Hwp")` 매칭만 하고 실행 파일 경로는 확인하지 않는다.
  한글이 아닌 동명 프로세스가 있으면 일반 `Launch`가 `already-running`으로 거부되고,
  투명 실행에서는 `SomeUnhooked` 오경고가 뜰 수 있다. 발견 당시 임시 `Hwp.exe`로 같은 문제를 재현한 기록이 있다.
- **발생 조건:** 사용자 세션에 `Hwp.exe`라는 이름의 다른 실행 파일이 떠 있을 때.
- **영향:** fail-safe 방향이라 문서 손상 위험은 없고, 실행 거부·오경고에 그친다. 워크어라운드는 해당 프로세스 종료다.
- **근거:** 두 함수 모두 이름 열거 후 세션·종료 여부만 보고, `TryGetProcessPath` 같은 경로 조회는 탐지(`AddRunningHangul`)에만 있다.
- **반증 확인:** 세션 필터, 관리자 권한 프로세스의 보수적 처리, `Local\HwpSaveHook.Launch` 뮤텍스 직렬화는 확인했다.
  그러나 이들은 런처 간 경합을 막을 뿐 이름 충돌은 막지 못한다.
- **호출/영향 범위:** CodeGraph상 호출자는 GUI `Start`(→`RunOperation`)·`RefreshIntegrationStatus` 계열·CLI `launch`·`--open` 경고 분기.
- **권장 수정 방향:** best-effort로 주 모듈 경로를 확인하고(`Hwp.exe` 파일명+읽기 가능 경로), 실패 시 기존 동작을 유지한다.
  경로 확인 실패를 `Unknown`으로 구분하는 것은 현행 유지.
- **필요한 회귀 테스트:** 동명 비한글 실행 파일을 띄운 상태에서 `launch`가 `already-running`이 아니라 정상 진행되거나
  최소한 경로 무관 판정임을 로그로 구분한다. 실제 한글 실행 중에는 여전히 거부됨을 확인한다.

### [ISSUE-003] 복원 실패한 업데이트의 수동 복원용 백업이 다음 업데이트 때 삭제된다

- **위치:** [UpdateInstaller.cs](private/save-hook/core/UpdateInstaller.cs) `ReplaceFiles`의 stale 백업 정리
- **우선순위:** Low
- **신뢰도:** Likely
- **문제:** 롤백마저 실패하면 `"backup 폴더의 파일로 직접 복원하세요"`라고 안내하면서 `.update-backup-*`를 남기는데,
  다음 업데이트의 `ReplaceFiles`는 `GetDirectories(target, ".update-backup-*")`를 무조건 지운다.
  사용자가 수동 복원을 미룬 사이 다음 업데이트가 실행되면 최후 복원 재료가 사라진다.
- **발생 조건:** 업데이트 N에서 `rolled-back`이 아닌 복원-실패가 난 뒤, 수동 복원 전에 업데이트 N+1이 실행될 때.
- **영향:** 현재 설치물은 보호된다(매번 현재 상태의 새 백업을 만든다). 잃는 것은 과거 복원 지점뿐이며 사용자 문서와 무관하다.
- **근거:** stale 정리 루프와 `rolled-back` 메시지의 모순을 코드로 확인.
  정상·롤백-성공 시 백업 삭제는 타당하므로 해당 분기는 반증된다.
- **호출/영향 범위:** `Apply → ReplaceFiles` 단일 경로. GUI `InstallUpdate`·헬퍼 `--apply-update`가 진입한다.
- **권장 수정 방향:** 직전 결과가 복원-실패면 stale 삭제를 건너뛰고 경고하거나,
  복원-실패 백업을 별도 이름으로 보관하고 자동 삭제 대상에서 제외한다.
- **필요한 회귀 테스트:** 복원-실패 fixture를 만든 뒤 다시 `Stage·Apply`하면 구 백업이 보존되고 경고가 남는다.
  정상 업데이트에서는 stale 백업이 정리됨을 함께 확인한다.

## 5. Potential Functional Gaps

- **Confirmed Gap · 저장 후 형식 자동 검증 없음:** `tools/inspect_format.py`는 수동 개발 도구다.
  ReJIT 실패·미적용 상태에서 사용자가 HWPX로 저장해도 제품은 이를 감지하지 못하고, 안내는 문구에만 의존한다.
- **Confirmed Gap · 여러 창 설정은 last-writer-wins:** 각 창이 시작 시점 값을 메모리에 들고 원자 기록으로 덮어쓴다.
  활성화·실행 시 재읽기로 완화되지만 동시 편집의 병합은 없다. 문서화된 동작이므로 제품 결정 영역이다.
- **Likely Gap · 매니페스트 만료 시 자동 확인이 조용히 스킵된다:** `Verify`의 만료는 `FormatException`이고
  시작 시 자동 확인이 이를 묻지 않고 넘긴다. 사용자가 직접 확인 버튼을 누르기 전까지 구버전에 머문다.
  수동 확인 경로에는 메시지가 있어 도달 가능성은 낮다.
- **추정 · 구조 변경 내성:** 한컴이 대상 메서드 의미(IL 지문)를 바꾸면 `target-semantics-differ`로 안전하게 거부되지만,
  그 뒤 사용자에게 남는 선택지는 OFF 일반 실행뿐이다. 자동 복구·대체 수단이 있으면 좋겠으나 현재 설계 범위를 넘는다.

## 6. Documentation Mismatches

없음. 대조한 결과는 다음과 같다.

- [docs/BUILD.md](docs/BUILD.md)의 종료 코드표(0/2/3/5)는 [Program.cs](private/save-hook/launcher/Program.cs)의 실제 반환과 일치한다.
- 업데이트 개인정보 주장(사용자·문서 정보 미전송, `HwpSaveHook-Updater`만 전송)은 `Download` 구현과 일치한다.
- `updates/latest.json`의 `version` 1.2.2은 `Directory.Build.props`의 `Version` 1.2.2과 일치하고 `expires_at`도 유효 기간 안이다.
- 이전 검증 기록의 "테스트 24개 통과"는 작성 시점 기록이며, 현재 하네스는 `Check` 113건을 포함한다.
  이 차이는 문서 오류가 아니라 경과에 따른 stale count로 본다.
- `CLAUDE.md`·`AGENTS.md`는 체크아웃에 없고 참조하는 문서도 없어 실영향이 없다.

## 7. Recommended Fix Plan

### Phase 1 — Immediate

- ISSUE-001 파서 강건화: `InspectImage`에 `ArgumentException`→`Unreadable` 처리와 blob 길이 상한 검사,
  `ListInstallations` catch 보강. 공유 파서라 파급이 크고 수정은 작다.

### Phase 2 — Stability

- ISSUE-002 프로세스 신원 확인을 경로 기준으로 보강(실패 시 기존 동작 유지).
- ISSUE-003 복원-실패 백업의 자동 삭제 제외 규칙.
- 매니페스트 만료 시 1회성 안내(자동 확인에서도 상태 문구만 남기기).

### Phase 3 — Structural

- 저장 후 형식 확인을 제품 흐름에 연결할지 결정(예: 첫 저장 후 샘플 검증 안내, 또는 범위 밖으로 명시).
- 파서·실행 게이트·프로파일러 상태 전이에 대한 테스트 분리(`--cli` 하네스에서 crafted PE fixture 추가).
- 여러 창 설정 정책(현행 유지인지 명시적 잠금인지) 문서화.

실제 코드는 수정하지 않았다.

## 8. Test Recommendations

| 유형 | 입력 조건 | 기대 결과 |
|---|---|---|
| Unit | PE 헤더 유효+blob 길이 거짓 fixture를 `Inspect`에 입력 | `Kind == Unreadable`, 예외 없음 |
| Unit | 동일 fixture로 CLI `list` 실행(탐지 경로에 배치) | 크래시 없이 종료 코드 3 또는 5, 스택 트레이스 없음 |
| Unit | `WriteSettingAtomic` 동시 2스레드 기록 | 파일이 유효한 단일 값(`on`/`off` 중 하나)으로 남음 |
| Integration | 동명 비한글 `Hwp` 프로세스 실행 중 `launch` | `already-running`이 아니라 정상 진행 |
| Integration | 실제 한글 실행 중 GUI·CLI 시작 | 자식 프로세스 미생성, `already-running` 경로 |
| Integration | 동일 PID·토큰의 `request-accepted/il-installed/compiled/failed` 로그 조합 | 요청접수와 적용됨·실패가 분리 표시되고 타 PID·타 토큰은 무시 |
| End-to-End | 지원 빌드 ON: 새 HWP 저장·기존 HWP 편집 저장·명시적 HWPX 저장·OFF 대조군 | 각 출력 형식이 기대와 일치하고 기존 HWP 재개방 가능 |
| Concurrency | GUI·CLI 동시 시작, 두 창의 설정 변경 | 자식 1건, 설정 파일 유효 |
| Regression | 수동 경로 저장 후 재실행·경로 소실·여러 버전 등록 | 수동 복원·자동 복귀·검증 해시 우선 선택 |
| Regression | 복원-실패 백업 존재 후 재업데이트 | 구 백업 보존+경고, 정상 케이스는 stale 정리 |
| Platform-specific | 공백·한글 경로 체크아웃 빌드, x64 GUI+x86 DLL, 느린 UNC 후보 | 빌드·로드 성공, 8초 타임아웃 후 안내 문구 |

## 9. Final Assessment

- **Functional Correctness:** Acceptable — 핵심 흐름(탐지·게이트·실행·상태·업데이트·복원)이 코드로 완결되고, 이전 감사의 5건이 해소됐다. 잔여 1건(Medium)은 진단 명령에 국한된다.
- **Runtime Stability:** Acceptable — 실행 경계가 뮤텍스로 직렬화되고, 타임아웃·취소·백그라운드 관찰 종료가 있다. 파서 예외 누수 1건이 흠이다.
- **Data Integrity:** Good — 사용자 문서를 쓰지 않고, 설정·레지스트리 변경은 백업 후 원자 기록한다. 업데이트도 smoke+롤백이 있다(백업 보관 1건 제외).
- **Error Resilience:** Needs Work — 대부분 경로의 예외 계약은 깨끗하지만, 공유 PE 파서의 입력 강건성이 한 단계 모자라다(ISSUE-001).
- **Cross-platform Robustness:** Acceptable — Windows 전용 설계이며, 공백·한글 경로와 x86/x64 경계는 코드상 대응됐다. 다만 이번 감사에서 빌드·실기를 실행하지 못해 검증은 코드 수준에 한정된다.
- **Test Confidence:** Acceptable — `Check` 113건의 자체 하네스가 게이트·탐지·ReJIT 상태·연동·업데이트를 가린다. crafted PE·E2E 실기 영역은 비어 있다.

**먼저 수정할 문제 3개:** ISSUE-001(파서 `ArgumentException`→`Unreadable`+`list` catch),
ISSUE-002(이름이 아닌 경로 기반 실행 중 판정), ISSUE-003(복원-실패 백업 자동 삭제 제외).
