# 한글 버전별 호환성

이 문서는 2026-11-27 기준 조사 결과와 런처의 버전 판정 방식을 정리합니다. 실기 검증한 빌드는 아직 **한글 2022 교육기관용 `12.0.0.4645` x86** 하나입니다.

## 왜 다른 버전도 대상인가

HWPX 기본 저장은 2022만의 동작이 아닙니다.

- 2021년 4월 15일 한컴 정기 패치(자동 업데이트)로 **한글 2014 VP, 한컴오피스 NEO, 2018, 2020**의 기본 저장 형식이 HWPX로 바뀌었습니다. [바이라인네트워크](https://byline.network/2021/04/15-117/), [아주경제](https://www.ajunews.com/view/20210415145023299)
- 이후 신규 설치에서는 설치 단계에서 기본 형식(HWPX 기본값/HWP)을 고를 수 있다는 한컴 공지가 인용돼 있습니다. [클리앙 게시글](https://www.clien.net/service/board/park/16782200)
- 2022·2024의 교육기관용·공공기관용 에디션에서 HWP로 되돌리는 공식 설정 위치는 공개 자료로 확인하지 못했습니다. 먼저 사용 중인 한글의 [도구] → [환경 설정]과 설치 옵션을 확인하세요. 기관 정책상 HWPX가 요구되는 경우도 있습니다. 2026년 정부 문서 유통은 HWPX 중심으로 바뀌고 있습니다. [전자신문](https://m.etnews.com/20260423000325)

## 이 런처가 바꾸는 지점

런처는 한글 2022의 `Hwp.exe`가 **x86 .NET Framework 4 어셈블리**라는 점을 이용합니다. CLR 프로파일러로 `Hwp.HwpFrame::ConvertToHwpxPath(ref string, ref string) : bool` 하나만 ReJIT해 `false`(변환 안 함)를 돌려주게 합니다. 따라서 다른 버전에 적용하려면 다음 조건을 모두 만족해야 합니다.

1. `Hwp.exe`에 CLR 헤더가 있다(.NET 어셈블리).
2. x86 전용(`32BITREQUIRED`)이다. 프로파일러 DLL이 x86입니다.
3. 같은 이름·같은 시그니처(`instance bool (string&, string&)`)의 메서드가 `Hwp.HwpFrame`에 있다.
4. 그 메서드가 같은 의미다. 즉 `false`를 돌려주면 HWPX 전환만 건너뛰고 원래 저장을 계속해야 합니다. **이 조건은 구조 검사로 확인할 수 없어 실기 시험이 필요합니다.**

`Hwp.exe`가 네이티브(C++) 실행 파일인 버전은 1번에서 탈락합니다. 이 방식으로는 지원할 수 없습니다. 2018·2020·2024의 `Hwp.exe` 구조는 해당 설치본이 없어 이번에 확인하지 못했습니다. 런처가 실행 시 직접 판정합니다.

## 런처의 판정 단계

| 판정(`reason`) | 의미 | HWP 모드 |
|---|---|---|
| `build-hash-match` | SHA-256이 검증 목록과 일치 | 적용(verified) |
| `compatible-needs-opt-in` | 검증되지 않았지만 1~3번 구조 일치 | 사용자가 **검증되지 않은 한글 버전에도 시도**를 켜야 적용 |
| `compatible-signature` | 위 설정을 켠 상태 | 적용(compatible, 실험) |
| `native-build-unsupported` | `Hwp.exe`가 .NET이 아님 | 불가 |
| `target-not-found` | .NET이지만 대상 메서드가 없거나 x86 전용이 아님 | 불가 |

프로파일러도 같은 판정을 한 번 더 합니다.

- **verified:** 모듈 MVID와 찾은 토큰이 `profiler/Target.h`의 `VERIFIED_TARGETS` 한 행과 일치해야 합니다.
- **compatible:** 이름·시그니처로 찾은 토큰이 런처가 같은 파일에서 분석해 넘긴 토큰(`HWP_SAVEHOOK_TARGET_TOKEN`)과 같아야 합니다.

불일치하면 아무것도 바꾸지 않고 한글 기본 동작으로 실행됩니다.

## 새 빌드를 검증 목록에 추가하는 방법

1. 해당 한글이 설치된 PC에서 빌드 정보를 읽습니다. 실행 파일은 읽기만 합니다.

   ```powershell
   dotnet private/save-hook/launcher/bin/Release/net10.0-windows/win-x64/hwp-save-hook.dll inspect --hwp "C:\Program Files (x86)\HNC\Office 2024\HOffice130\Bin\Hwp.exe"
   ```

   `kind`가 `managed`, `x86`이 `true`, `targetToken`이 값이면 후보입니다. `native`이면 지원할 수 없습니다.
2. GUI에서 **검증되지 않은 한글 버전에도 시도**를 켜고 [검증 기록](superpowers/specs/2026-11-26-hwp-save-hook-rejit-handoff.md)의 시험을 반복합니다. 빈 문서·서식 문서·기존 HWP 편집 저장·명시적 HWPX 저장·OFF 대조군을 확인하고 `tools/inspect_format.py`로 형식을 검사합니다.
3. 통과하면 `inspect`의 `sha256`·`fileVersion`·`mvid`·`targetToken`을 두 곳에 같은 값으로 추가합니다.
   - `private/save-hook/core/SaveHookLauncher.cs`의 `HwpBuildCatalog.Verified`
   - `private/save-hook/profiler/Target.h`의 `VERIFIED_TARGETS`
4. 검증 기록 문서에 시험 결과를 남깁니다.

`inspect` 결과(JSON)를 이슈로 보내 주시면 목록 추가에 도움이 됩니다. 결과에는 사용자 정보가 없지만 설치 경로(`hwp`)는 지우고 보내도 됩니다.
