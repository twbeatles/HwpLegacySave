# Project Audit

2026-11-26 기준 `main`의 `dd78848`을 감사했다. 대상은 한글 2022 교육기관용 `12.0.0.4645` x86을 지원하는 Windows x64 런처다.

## 1. Executive Summary

**전체 위험도는 Medium이다.** 격리 복사본에서 x86 프로파일러, x64 GUI와 CLI를 빌드했고 기존 테스트 24개가 모두 통과했다. 주요 위험은 프로세스 시작과 ReJIT 적용 사이의 상태 전이, 실패 진단, Windows 빌드 경로 처리에 있다.

1. CLI는 기존 한글 프로세스를 검사하지 않는다. GUI도 검사와 시작 사이에 다른 런처와 경합할 수 있다.
2. UI는 ReJIT 요청 성공을 준비 완료로 표시한다. 실제 적용 과정에서 발생하는 후속 실패는 UI에 반영되지 않는다.
3. 네이티브 로그 경로의 폴백이 Win32의 버퍼 필요 크기를 실제 문자열 길이로 사용한다.
4. 공백이 있는 체크아웃에서는 기본 빌드가 실패하고, 빈 프로파일러 경로는 CLI의 처리되지 않은 예외로 이어진다.

문서 손상이나 유실은 확인되지 않았다. 런처는 설치 파일을 읽고 자식 프로세스를 시작하며, 문서 저장은 한글이 맡는다. 적용 실패 시 예상과 다른 형식으로 저장될 수 있다. 우선 실행 경계를 공통화하고, 네이티브 로그 검증과 ReJIT 상태 표시를 보강해야 한다.

## 2. Project Understanding

[README](README.md), [빌드 안내](docs/BUILD.md), [설계](docs/superpowers/specs/2026-11-26-hwp-save-hook-design.md), [기존 검증 기록](docs/superpowers/specs/2026-11-26-hwp-save-hook-rejit-handoff.md), [빌드 스크립트](Build-Local.ps1), [솔루션](private/save-hook/HwpSaveHook.slnx)과 프로젝트 설정을 확인했다. 체크아웃에는 `CLAUDE.md`와 `AGENTS.md`가 없었다. 세션에서 제공된 CodeGraph 우선 사용 지침을 적용했다.

| 진입점 | 입력에서 결과까지 |
|---|---|
| x64 WPF `App.xaml` → `MainWindow.Window_Loaded` | 사용자 설정 → `Scan` → 수동 경로 복원 또는 설치 등록·기본 위치 조회 → PE 확인과 SHA-256 우선 선택 → 경로·지원 상태 표시 |
| GUI 토글·찾아보기·다시 찾기 | `ModeChanged` 또는 `Scan` → 사용자 모드·경로 파일을 임시 파일과 교체하거나 자동 탐지로 복귀 → 다음 GUI 시작 시 복원 |
| GUI `Launch_Click` | `Start` → 경로 재검사와 `CreatePlan` → `EnsureHangulClosed` → 공용 `Launch` → 자식 프로세스와 로그 → 실행 상태 표시 |
| CLI `LauncherCli.Run` | 인자 파싱 → 설치 탐지와 `CreatePlan` → `plan`은 계획 JSON, `launch`는 `Launch` → PID 또는 오류 반환 |
| x86 프로파일러 `DllGetClassObject` | CLR 초기화 → `ModuleLoadFinished`와 `VerifyTarget` → 전용 스레드의 ReJIT 요청 → 대상 IL 설치와 컴파일 → 한글 저장 및 로그 |
| `tools/inspect_format.py` | 파일 경로 → CFB 또는 ZIP 읽기 → 파일 헤더·필수 항목 확인 → HWP5 또는 HWPX 형식 후보와 종료 코드 반환 |

공유 상태에는 GUI 인스턴스의 `_plan`, `_useHook`, `_busy`, 사용자별 `save-mode.txt`와 `hwp-path.txt`, 자식별 로그와 프로파일러의 원자적 모듈 ID가 있다. GUI는 .NET Framework 4.8, CLI·테스트는 .NET 10, 네이티브 DLL은 MSVC x86과 CLR 프로파일링 API를 사용한다. 설치 탐지는 레지스트리를 읽는다. 빌드에는 NuGet 복원이 있지만, 저장 런타임의 자체 네트워크 API, DB, 트랜잭션 및 마이그레이션은 없다.

## 3. Audit Coverage & Limitations

- **CodeGraph MCP**에서 GUI 시작 → 설정 복원 → 설치 선택 → 해시 계산, `CreatePlan`과 `Launch`의 GUI·CLI 호출자, `WaitForRejitRequest`의 GUI 호출자, 네이티브 모듈 검증과 IL 설치를 확인했다. XAML 이벤트, PowerShell의 프로세스 빌드 경계와 CLR 콜백·로그 간 연결은 소스와 API 계약으로 보완했다.
- **실행한 검사**는 `git archive HEAD`로 만든 임시 복사본의 기본 빌드와 테스트 24개, x86 COM probe의 콜백 3·4 인스턴스 생성, CLI `plan`의 정상 탐지·DLL 없음·강제 OFF·잘못된 파일 이름·빈 프로파일러 경로다. `launch`의 중복 시작은 임시 무동작 x86 실행 파일로 시험했다.
- **경계 검사**로 공백이 포함된 체크아웃을 빌드하고, 별도 프로세스에서 `GetTempPathW`의 반환 길이를 확인했다. 길이 시험은 Win32 API만 호출했다. 네이티브 프로파일러의 잘못된 메모리 읽기나 충돌을 유발하는 시험은 수행하지 않았다.
- 이번 감사에서 실제 한글 저장, 다중 Windows 세션, 네트워크 장애, 다른 Windows 버전을 다시 시험하지 않았다. 이전 문서의 실제 HWP5 저장·렌더 비교와 이번 결과를 구분했다. COM probe는 CLR 부착이나 ReJIT 동작까지 검증하지 않는다. 빌드와 실험은 임시 폴더에서 진행했다.

## 4. High-Risk Issues

### [ISSUE-001] CLI의 기존 세션 검사 누락과 GUI의 시작 경합

- **위치** [CLI `Run`](private/save-hook/launcher/Program.cs#L121), [GUI `Start`와 `EnsureHangulClosed`](private/save-hook/gui/MainWindow.xaml.cs#L154), [공용 `Launch`](private/save-hook/core/SaveHookLauncher.cs#L164)
- **우선순위** High
- **신뢰도** Confirmed (CLI 검사 누락과 중복 시작). GUI 동시 시작은 Likely.
- **문제** CLI가 기존 한글 프로세스를 조회하지 않는다. GUI의 조회와 `Process.Start`도 런처 사이에서 직렬화되지 않는다. CLI의 `hooked`는 실행 계획 값이고 `launched`는 프로세스 시작 결과다.
- **발생 조건** 같은 세션에 한글이 실행 중인 상태에서 CLI를 사용하거나, 두 런처가 한글이 없는 상태를 동시에 확인한다.
- **영향** 한글이 기존 인스턴스로 요청을 전달하면 새 프로세스에 넘긴 설정이 기존 창에 적용되지 않는다. 이때 사용자는 기존 저장 동작을 만날 수 있다. 실제 한글의 전달 동작은 이번 감사에서 재현하지 않았다.
- **근거** 임시 폴더의 `Hwp.exe`가 살아 있는 동안 CLI를 OFF로 호출했다. CLI는 `launched`를 반환했고 같은 임시 실행 파일의 두 PID가 동시에 존재했다.
- **반증 확인** `_busy`는 동일 GUI 창의 반복 요청을 막는다. `EnsureHangulClosed`는 호출 시점에 이미 실행 중인 프로세스를 막는다. 두 보호 장치 모두 CLI를 보호하거나 검사와 시작을 한 번에 처리하지는 않는다.
- **호출/영향 범위** CodeGraph에서 확인한 GUI·CLI의 공용 `Launch`와 그 이후 자식 프로세스의 실행 모드 및 상태 표시.
- **권장 수정 방향** 공용 실행 경계에 세션 검사와 런처 간 직렬화를 적용한다. 시작한 PID가 바로 종료되는 경우를 별도로 확인하고 실행 계획과 적용 결과를 분리한다.
- **필요한 회귀 테스트** 기존 대체 `Hwp.exe`가 있을 때 GUI·CLI 실행을 거부한다. GUI와 CLI를 동시에 시작하면 자식이 한 건만 생긴다.

### [ISSUE-002] ReJIT 요청 수락 이후의 실패가 GUI에 반영되지 않는다

- **위치** [GUI `WaitForRejitRequest`](private/save-hook/gui/MainWindow.xaml.cs#L224), [`RequestOnWorker`](private/save-hook/profiler/HwpSaveHookProfiler.cpp#L370), [IL 설치·컴파일·오류 콜백](private/save-hook/profiler/HwpSaveHookProfiler.cpp#L180)
- **우선순위** Medium
- **신뢰도** Confirmed (상태 판정 경로). 실제 한글에서 메서드별 실패를 주입하지 않았다.
- **문제** GUI는 요청 성공 로그 한 줄로 준비 완료를 표시하고 로그 확인을 끝낸다. 이후 `ReJITError`, IL 설치 실패나 비정상 컴파일 결과를 읽지 않는다.
- **발생 조건** 요청이 성공을 반환했지만 대상 메서드 등록 또는 첫 실행 때 IL 설치·컴파일이 실패한다.
- **영향** 준비 완료가 표시돼도 사용자가 예상한 HWP5 저장이 이루어지지 않을 수 있다. 사용자가 로그나 저장 파일을 직접 확인해야 상태 차이를 알 수 있다.
- **근거** CLR의 요청 성공은 대상 등록 시도를 뜻하며, 메서드별 오류는 별도 콜백에 전달된다. [Microsoft RequestReJIT 계약](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/profiling/icorprofilerinfo4-requestrejit-method)
- **반증 확인** 문서는 준비 완료를 요청 수락으로 정의하고 프로파일러는 실패를 기록한다. GUI가 그 기록을 읽지 않는 문제는 남는다. 실제 컴파일은 첫 저장에 지연될 수 있으므로 시작 시 최종 컴파일 결과까지 기다리게 하면 정상 흐름도 막을 수 있다.
- **호출/영향 범위** GUI `Start` → `WaitForRejitRequest`, CLR 콜백 → 세션 로그 → 사용자 안내.
- **권장 수정 방향** 요청 접수, 적용 확인, 실패를 별도 상태로 표시하고 동일 PID·토큰의 후속 로그를 반영한다.
- **필요한 회귀 테스트** 요청 직후 메서드 오류, IL 설치 실패, 첫 저장의 지연된 성공, 자식 조기 종료에서 각각 다른 상태가 표시된다.

### [ISSUE-003] 네이티브 로그 폴백에서 버퍼 범위를 넘겨 읽을 수 있다

- **위치** [프로파일러의 `LogPath`와 `Log`](private/save-hook/profiler/HwpSaveHookProfiler.cpp#L19)
- **우선순위** Medium
- **신뢰도** Confirmed (Win32 반환값과 코드의 경계 불일치). 프로파일러 충돌 자체는 재현하지 않았다.
- **문제** `GetTempPathW`의 반환값이 버퍼 용량보다 커도 그 길이로 `std::wstring`을 만든다. 이때 반환값은 필요한 버퍼 크기다.
- **발생 조건** 명시 로그 경로가 없거나 채택되지 않고, 임시 경로가 260자 버퍼에 들어가지 않는다.
- **영향** 경로 조립 중 네이티브 코드가 버퍼 범위를 넘어 읽는다. 로그 손실이나 자식 한글 프로세스의 비정상 종료 가능성이 있다. 데이터 손상은 확인하지 않았다.
- **근거** 격리된 API 호출에서 260자 버퍼에 대해 반환 길이 261을 확인했다. [Microsoft GetTempPathW 계약](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-gettemppathw)
- **반증 확인** GUI의 정상 실행은 명시 로그 경로를 전달한다. CLI의 일반적인 후크 실행은 임시 경로 폴백을 사용할 수 있다. `Log`의 파일 열기 오류 처리는 그 전에 수행하는 문자열 생성을 보호하지 못한다.
- **호출/영향 범위** CLR 초기화·모듈 로드·ReJIT·종료 콜백과 워커 → `Log` → 최초 `LogPath`.
- **권장 수정 방향** 실패와 필요 길이를 먼저 검사한다. 제한 내에서 적절한 버퍼로 재시도하거나 로그를 생략하고, 로깅 실패를 CLR 콜백 경계에서 격리한다.
- **필요한 회귀 테스트** 경로 미지정, API 반환값 0·정상·버퍼 초과, 쓰기 불가 폴더를 재현해 로그를 쓰거나 안전하게 생략하는지 확인한다.

### [ISSUE-004] 공백이 있는 체크아웃에서 기본 빌드가 실패한다

- **위치** [네이티브 빌드의 MSVC 인자](private/save-hook/profiler/build.ps1#L65), [루트 빌드의 PowerShell 호출](Build-Local.ps1#L19)
- **우선순위** Medium
- **신뢰도** Confirmed
- **문제** Windows PowerShell에서 공백과 끝 역슬래시가 있는 개체 출력 경로를 네이티브 명령줄로 넘길 때 인자 경계가 깨진다.
- **발생 조건** 경로에 공백이 있는 폴더에서 문서의 기본 빌드를 실행한다.
- **영향** x86 DLL 빌드가 실패하면서 GUI 게시와 테스트까지 진행하지 못한다.
- **근거** 같은 커밋을 `checkout with spaces` 아래에서 빌드했다. MSVC가 경로 조각을 별도 파일로 인식했고 링크 시 `LNK1181`이 발생했다. 공백 없는 복사본은 통과했다.
- **반증 확인** 인자 배열, 절대 경로, 종료 코드 처리가 있다. Windows PowerShell의 네이티브 인자 직렬화 결과는 이 장치들로 고쳐지지 않는다.
- **호출/영향 범위** `Build-Local.ps1` → Windows PowerShell → `profiler/build.ps1` → `cl.exe`와 링커.
- **권장 수정 방향** `/Fo`에 끝 구분자 대신 명시적 개체 파일 경로를 사용하고 probe의 인자 전달도 확인한다.
- **필요한 회귀 테스트** 공백이 없는 경로와 공백·한글이 섞인 경로 모두에서 EXE·DLL이 만들어지고 테스트가 통과한다.

### [ISSUE-005] CLI의 빈 프로파일러 경로가 처리되지 않은 예외로 끝난다

- **위치** [CLI 옵션 파싱과 계획 생성](private/save-hook/launcher/Program.cs#L55), [`CreatePlan`의 경로 정규화](private/save-hook/core/SaveHookLauncher.cs#L136)
- **우선순위** Medium
- **신뢰도** Confirmed
- **문제** `--profiler`에 빈 문자열을 받을 수 있고, `Path.GetFullPath`를 호출하는 `CreatePlan`은 경로 탐지와 프로세스 시작의 예외 처리 구간 바깥에 있다.
- **발생 조건** 읽을 수 있는 한글이 선택된 상태에서 호출자가 비어 있는 프로파일러 경로를 전달한다.
- **영향** `plan`과 `launch`가 입력 오류를 정해진 방식으로 반환하지 못한다. 상위 자동화는 일반 입력 오류를 구분하기 어렵다.
- **근거** 빈 인자를 그대로 전달한 `plan`에서 `ArgumentException`과 스택 추적을 확인했다. 잘못된 한글 실행 파일 이름은 정상적으로 종료 코드 2를 반환했다.
- **반증 확인** 옵션 누락 검증, 한글 파일 이름 검사, 자식 실행 예외 처리는 각각 있다. 빈 값의 계획 생성 예외는 그 범위에 들지 않는다. GUI는 기본 DLL 경로와 자체 예외 처리 경계를 사용한다.
- **호출/영향 범위** `LauncherCli.Run` → 공용 `CreatePlan` → 경로 정규화. CLI 입력 오류 경로에 국한된다.
- **권장 수정 방향** 필수 옵션 값의 누락과 공백을 검증하고 계획 생성도 CLI 예외 처리 범위에 둔다.
- **필요한 회귀 테스트** 빈 값·공백만 있는 값·누락은 정해진 오류 코드로 반환한다. 유효한 문법이지만 존재하지 않는 DLL 경로는 `profiler-missing`으로 구분한다.

## 5. Potential Functional Gaps

- **Confirmed Gap · 종단 자동 검증** 기존 자동 테스트는 계획, 환경변수, 설치 후보에 집중한다. COM probe 성공으로 토글 재시작, 실제 HWP5 저장이나 ReJIT 실패 안내의 회귀를 확인할 수 없다.
- **Confirmed Gap · 여러 런처의 설정 갱신** 각 창이 시작 시 읽은 모드를 메모리에 보관한다. 한 창에서 변경한 값은 이미 열린 다른 창에 즉시 반영되지 않는다. 재시작 후 복원은 문서에 명시된 동작이며, 이미 열린 창의 갱신은 제품 결정이다.
- **Likely Gap · 느린 설치 후보 취소** 작업 스레드에서 탐지하지만 취소 경로가 없다. 등록된 UNC 경로에서 열거 또는 읽기가 지연되면 `_busy` 동안 찾아보기와 재검색을 사용할 수 없다. 실제 네트워크 장애로 시간은 측정하지 않았다.

## 6. Documentation Mismatches

- [빌드 안내](docs/BUILD.md)에는 공백 없는 체크아웃 제약이 없다. 기본 명령이 공백 경로에서 실패하는 것을 확인했다. ISSUE-004 참조.
- [README](README.md)와 [빌드 안내](docs/BUILD.md)는 GUI의 기존 한글 종료 절차를 안내한다. CLI 절은 기존 프로세스 검사가 GUI에만 있다는 점을 설명하지 않아 동일한 보호를 기대하기 쉽다. ISSUE-001 참조.
- [빌드 안내](docs/BUILD.md)는 준비 완료를 ReJIT 요청 수락 단계로 정확히 설명한다. 화면의 문구에는 적용 확인과의 구분이 드러나지 않는다. ISSUE-002 참조.

## 7. Recommended Fix Plan

### Phase 1 — Immediate

공용 실행 경계에 기존 프로세스 검사와 직렬화를 적용한다(ISSUE-001). 네이티브 로그 폴백의 길이 검사를 보완한다(ISSUE-003). ReJIT 요청 접수와 적용 성공·실패 안내를 분리한다(ISSUE-002).

### Phase 2 — Stability

MSVC 인자 전달을 수정하고 공백·한글 경로에서 빌드한다(ISSUE-004). CLI 계획 예외와 입력 오류 계약을 보완한다(ISSUE-005). 필요하면 지연되는 설치 후보를 건너뛰는 동작과 여러 창의 설정 정책을 정의한다.

### Phase 3 — Structural

프로세스 실행과 프로파일러 상태 전이를 별도 테스트 대상으로 만든다. 합성 문서를 사용한 Windows 종단 시험을 추가하고, 파일 형식 검사와 실제 한글 재개방을 함께 검증한다.

## 8. Test Recommendations

| 유형 | 조건 | 기대 결과 |
|---|---|---|
| Unit | 빈 CLI 옵션·공백 값·누락과 문법상 유효하지만 없는 DLL | 입력 오류와 `profiler-missing`을 구분한다. |
| Unit | 네이티브 임시 경로 API 결과 0, 정상 길이, 버퍼 초과와 쓰기 불가 폴더 | 로그를 쓰거나 안전하게 생략한다. |
| Integration | 대체 실행 파일이 열린 상태에서 CLI·GUI로 시작 | 새 자식이 생성되지 않는다. |
| Integration | 동일 PID의 요청 접수, 등록 오류, IL 설치 실패, 컴파일 성공 로그를 각각 공급 | 접수와 실제 적용이 분리되며, 다른 PID·토큰의 로그를 성공으로 처리하지 않는다. |
| End-to-End | 지원 한글에서 ON으로 새 HWP 저장, 기존 HWP 수정 저장, 명시적 HWPX 저장, OFF 대조군 | 각 출력의 실제 형식이 기대와 같고 기존 HWP 수정본을 다시 열 수 있다. |
| Concurrency | 두 런처의 동시 시작과 서로 다른 창의 설정 변경 | 실행은 한 건이며 설정 파일은 유효한 단일 값으로 남는다. |
| Regression | 수동 경로로 시작 후 재실행, 경로가 사라진 상태, 여러 버전 동시 등록 | 수동 경로 복원, 자동 탐지 복귀, 지원 해시 우선 선택을 확인한다. |
| Platform-specific | Windows의 공백·한글 경로, x64 WPF와 x86 DLL, 느린 UNC 후보 | 빌드와 로드가 성공하며 느린 후보에서 UI를 복구할 수 있다. |

## 9. Final Assessment

| 항목 | 평가 | 근거 |
|---|---|---|
| Functional Correctness | Acceptable | 기본 실행 계획과 형식 선택은 일관되지만 실제 적용 상태의 표시가 부족하다. |
| Runtime Stability | Needs Work | 런처 간 중복 실행과 네이티브 폴백의 경계 처리가 남아 있다. |
| Data Integrity | Acceptable | 데이터 손상을 확인하지 않았다. 적용 실패 시 출력 형식이 예상과 다를 수 있다. |
| Error Resilience | Needs Work | 계획 예외와 요청 이후 ReJIT 실패가 일관된 결과로 전달되지 않는다. |
| Cross-platform Robustness | Needs Work | Windows 전용은 설계 범위이며, 그 범위 안에서도 공백 체크아웃에서 빌드가 실패한다. |
| Test Confidence | Needs Work | 24개 테스트와 COM probe는 통과했다. 경합·프로세스 상태·실제 저장의 자동 회귀 범위는 부족하다. |

**실제로 먼저 수정할 세 가지**는 공용 실행 경계의 중복 시작 방지(ISSUE-001), 네이티브 로그 경로의 버퍼 검증(ISSUE-003), ReJIT 요청과 적용 상태의 분리(ISSUE-002)다.

## 10. Remediation Status

2026-11-26 작업 트리(미커밋) 기준이다. `Build-Local.ps1` 전체 빌드는 성공했고 테스트 39/39가 통과했다. 실제 한글 저장은 다시 시험하지 않았다.

| 항목 | 조치 | 검증 |
|---|---|---|
| ISSUE-001 | 공용 `Launch`가 `Local\HwpSaveHook.Launch` 뮤텍스 안에서 세션의 `Hwp.exe`를 검사한 뒤 시작한다(`StartSingleInstance`). GUI의 개별 검사는 제거했다. GUI·CLI는 `already-running`을 구분해 표시하고, 일반 모드에서 바로 종료된 자식도 따로 보고한다. | 동시 요청 시 자식 1건, 거부 후 재사용 테스트 |
| ISSUE-002 | 프로파일러가 `ReJITState: token=… state=` 로그(`request-accepted`, `il-installed`, `compiled`, `failed`)를 남긴다. `RejitLog`가 같은 PID·토큰만 읽어 요청 접수, 적용, 실패, 조기 종료를 구분한다. GUI는 접수 뒤에도 첫 저장의 컴파일 결과를 계속 관찰한다. | 상태 판정 단위 테스트 5건 |
| ISSUE-003 | `GetEnvironmentVariableW`와 `GetTempPathW`의 필요 길이를 확인하고 재시도한다. 실패하면 로그를 생략한다. `Log`는 `noexcept`이며 예외를 CLR 경계 밖으로 내보내지 않는다. | 빌드 확인. 버퍼 초과 재현 시험은 미실시 |
| ISSUE-004 | `/Fo`에 명시적 개체 파일 경로를 넘기도록 바꿨다(`build.ps1`, `build-and-probe.ps1`). | 공백·한글이 포함된 경로에서 x86 DLL 빌드 성공 |
| ISSUE-005 | 값이 필요한 옵션의 누락, 공백, 다음 옵션과의 혼동을 종료 코드 2로 거부한다. SHA-256 형식을 검증하고 계획 생성도 예외 처리 범위에 넣었다. | CLI 하위 프로세스 테스트 6건 |
| 느린 설치 후보 | 설치 탐지에 취소 토큰과 8초 시한(`InspectWithTimeoutAsync`)을 적용했다. | 응답 없는 검사의 시한 반환 테스트 |
| 여러 창의 설정 | 창 활성화 시와 **한글 열기** 직전에 저장된 모드를 다시 읽는다. | 수동 확인 필요 |
| 문서 불일치 | [빌드 안내](docs/BUILD.md)에 공통 실행 보호, 단계별 ReJIT 상태, 설치 확인 시한, CLI 상태·종료 코드를 반영했다. | — |

남은 일은 실제 한글에서 ON/OFF 저장 종단 시험, ReJIT 실패 주입 시험, 네이티브 로그 경로의 버퍼 초과 재현이다.
