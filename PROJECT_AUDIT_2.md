# Project Audit — Follow-up (uncovered scopes)

감사 기준일: 2026-11-27. 대상 커밋: `42f7afa` + 작업 트리 변경분.
1차 감사(`PROJECT_AUDIT.md`)에서 다루지 않은 범위만 같은 방식으로 감사하고 수정까지 마쳤다.
코드는 읽기·분석과 아래 기록된 최소 수정만 수행했다.

## 1. Executive Summary

**이 범위의 전체 위험도는 Low다.** Medium 이상은 없고, 수정할 만한 항목 2건(Low 2건)을 고쳤다.
나머지는 검증으로 깨끗함을 확인했다.

1. 릴리스 파이프라인이 의존하는 `update-keygen`·`update-sign`이 테스트 무커버리지였다 → 왕복·거부 테스트 8건 추가 (Gap 해소).
2. `build-and-probe.ps1`의 환경 검사가 `build.ps1`보다 약했다 → 동일 가드로 보강 (Low).
3. 새로 둔 **저장 파일 형식 확인** 버튼이 이전 실행의 잔재와 함께 보일 수 있었다 → 시작·재검색 시 숨김 초기화 (Low).

**데이터 손상/유실 가능성: 없음.** 이 범위는 개발 도구·테스트·표시 영역이며 사용자 문서나 설정을 건드리지 않는다.

## 2. Scope Understanding

1차 감사가 다룬 뒤 남은 범위다.

- `profiler/tools/`: [InstantiateProbe.cpp](private/save-hook/profiler/tools/InstantiateProbe.cpp) (COM 인스턴스화 프로브),
  [build-and-probe.ps1](private/save-hook/profiler/tools/build-and-probe.ps1) (한글 없이 검증하는 개발 스크립트)
- `profiler/inc/`: `dotnet/runtime`에서 가져온 CLR 헤더 4종 + `LICENSE.TXT` (벤더 코드)
- `launcher.tests/Program.cs`의 `--cli` 심층 경로: `RunPlan` 헬퍼, 투명 실행 fake 테스트, `update-keygen`·`update-sign` 무커버리지 확인
- [MainWindow.xaml](private/save-hook/gui/MainWindow.xaml) 전체 마크업과 코드비하인드 연결
- [HwpSaveHookProfiler.def](private/save-hook/profiler/HwpSaveHookProfiler.def), [IsExternalInit.cs](private/save-hook/core/IsExternalInit.cs),
  [App.xaml](private/save-hook/gui/App.xaml), `private/release-keys/` 추적 여부

## 3. Audit Coverage & Limitations

- **CodeGraph:** `UpdateCommands.Sign`의 호출자가 `Program.cs`뿐이며 3홉 내 테스트가 없음을 확인했다.
  릴리스 워크플로(`release.yml`)가 매 태그마다 `update-sign`을 실행하므로 파이프라인 수준에서는 운동되고 있다.
- **실측 검증:**
  - 벤더 헤더 5종을 `dotnet/runtime@v8.0.11` 업스트림과 SHA-256으로 대조 → 전부 바이트 일치.
  - `build-and-probe.ps1` 직접 실행 → 프로브 빌드·`RESULT: PASS`·종료 코드 0.
  - `git ls-files private/release-keys` → 추적 파일 없음(`.gitignore` 적용 확인).
- **읽은 코드:** 위 Scope 전체. XAML의 모든 `x:Name`·이벤트 핸들러가 코드비하인드와 일치함을 대조했다.
- **한계:** 한글 실기 실행·릴리스 파이프라인 실행은 하지 않았다. `update-sign`의 실제 서명 키는 만지지 않았다.

## 4. Findings (all Low)

### [F-001] 릴리스 서명 도구가 테스트 무커버리지였다

- **위치:** [Program.cs](private/save-hook/launcher/Program.cs) `UpdateCommands` (`KeyGen`·`Sign`)
- **우선순위:** Low / **신뢰도:** Likely (버그가 아니라 커버리지 Gap; 회귀 시 릴리스가 깨진다)
- **문제:** 매 릴리스가 `update-sign`에 의존하는데 실패·거부 분기의 단위 테스트가 없었다.
- **반증 확인:** 옵션 검증·HTTPS 강제·덮어쓰기 거부 로직 자체는 읽고 정상임을 확인했다. 릴리스 워크플로가 성공 경로를 운동한다.
- **조치:** 키 생성·덮어쓰기 거부·PKCS#8 유효성·서명 왕복 검증·키 누락·비HTTPS·버전 불량을 가리는 테스트 8건 추가.
- **필요한 회귀 테스트:** 위 7건(추가됨). 릴리스 태그 시 워크플로가 성공 경로를 계속 운동한다.

### [F-002] 프로브 스크립트의 환경 검사가 본 빌드보다 약했다

- **위치:** [build-and-probe.ps1](private/save-hook/profiler/tools/build-and-probe.ps1)
- **우선순위:** Low / **신뢰도:** Likely
- **문제:** `vswhere`·`vcvarsall`·`cl.exe` 부재 시 원인을 알기 어려운 오류가 났다(`build.ps1`은 가드를 갖춤).
- **반증 확인:** MSVC가 있는 환경에서는 끝까지 동작함을 직접 실행으로 확인했다. 제품 코드가 아니라 개발 스크립트다.
- **조치:** `build.ps1`과 동일한 가드(vswhere 존재·VC 컴포넌트 요구+폴백·vcvarsall 존재·cl.exe 확인)로 교체했다.

### [F-003] 시작·재검색 후 이전 실행의 로그/확인 버튼이 남았다

- **위치:** [MainWindow.xaml.cs](private/save-hook/gui/MainWindow.xaml.cs) `Start`·`Scan`
- **우선순위:** Low / **신뢰도:** Likely (표시 일관성, 기능 오류 아님)
- **문제:** 일반 모드 실행이나 재검색 후에도 이전 HWP 모드 실행의 버튼이 보여 오래된 로그로 안내할 수 있었다.
  새로 추가한 확인 버튼도 같은 잔재를 공유했다.
- **조치:** 시작·재검색 진입 시 버튼 숨김 + `_logPath` 초기화를 넣었다. 이후 `ShowRejitStatus`가 해당 실행 결과로 다시 표시한다.

검증 후 문제없음으로 닫은 항목: `InstantiateProbe`의 `FreeLibrary` 누락(단명 프로세스, 영향 없음),
`LNK4070` 경고(출력명 변경에 따른 안내이며 `GetProcAddress` 성공으로 내보내기 정상 확인),
`.def`·`IsExternalInit`·`App.xaml`(이상 없음), XAML 이름·핸들러 전수 일치, 릴리스 키 미추적.

## 5. Potential Functional Gaps

- **해소됨 · 서명 도구 커버리지:** F-001 테스트로 닫았다.
- **남음(추정) · 합성 문서 종단 시험 자동화:** 실제 한글 구동이 필요해 이 환경에서 자동화하지 않았다.
  수동 절차는 기존 검증 기록 문서에 있다.
- **해당 없음 · 저장 후 형식 확인:** 1차 감사 Gap이었으나 이번에 제품 흐름(GUI 버튼 + 후보 판정기)으로 연결하고 테스트했다.

## 6. Documentation Mismatches

없음. `README.md`의 보고서 목록에 이 후속 보고서를 한 줄 추가했다.

## 7. Recommended Fix Plan

전 항목 조치 완료(코드 수정 + 테스트). 남은 것은 실기 E2E 확인으로, 사용자 PC에서 GUI의 **저장 파일 형식 확인** 버튼으로 대조하면 된다.

## 8. Test Recommendations (added)

| 유형 | 입력 조건 | 기대 결과 |
|---|---|---|
| Unit | `update-keygen` 신규/중복 경로 | 생성 성공·덮어쓰기 거부(종료 2), PKCS#8 유효 |
| Unit | `update-sign` 정상/키 누락/비HTTPS/버전 불량 | 서명 성공 + 검증 통과, 나머지는 종료 2 |
| Unit | CFB·HWPX 합성·일반 텍스트·부재 파일 | HWP 후보·HWPX 후보·Unknown·Unknown |
| Integration | 동명 비한글 프로세스 실행 중 게이트 | `IsHangulRunningInSession()==false`, `GetState()==None` (실제 한글 실행 시 SKIP) |

## 9. Final Assessment (this scope)

- Functional Correctness: Good (수정 3건 모두 경계·도구·표시 영역, 핵심 로직 변경 없음)
- Runtime Stability: Good
- Data Integrity: Good (이 범위는 사용자 데이터를 쓰지 않음)
- Error Resilience: Acceptable → Good (서명 도구 거부 분기가 테스트로 고정됨)
- Cross-platform Robustness: Acceptable (Windows 전용 설계, 공백·한글 경로 대응 유지)
- Test Confidence: Acceptable (서명 도구·저장 판정·게이트 테스트 추가)

**먼저 한 일(완료):** F-001 테스트 7건, F-002 가드 교체, F-003 버튼 초기화.
