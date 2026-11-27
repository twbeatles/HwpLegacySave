# 한글 버전별 호환성

이 문서는 2026-11-27 기준 공개 자료 조사와 설치된 한글 2022 `Hwp.exe`의 읽기 전용 정적 분석 결과입니다. 실기 검증한 빌드는 아직 **한글 2022 교육기관용 `12.0.0.4645` x86** 하나입니다.

## 요약

| 상황 | HWP로 저장하는 방법 | 이 런처 필요 여부 |
|---|---|---|
| 일반판(개인·기업) 2014 VP·NEO·2018·2020·2022·2024 | [도구] → [환경 설정] → [파일] → **다음 형식으로 파일 저장**에서 HWP 선택 | 필요 없음 |
| 교육기관용·공공기관용 2022 (검증 빌드) | 환경 설정과 관계없이 `.hwp` 저장이 `.hwpx`로 바뀜 | 이 런처의 대상 |
| 공공기관용 2018·2020·2024 (2026-11 업데이트 이후) | 미확인 | 구조가 같을 때만 실험 모드로 가능 |
| 교육기관용 2024 | 미확인 | 구조가 같을 때만 실험 모드로 가능 |

공공기관·교육기관에서는 HWPX 사용이 기관·정부 정책일 수 있습니다. 먼저 소속 기관의 문서 정책을 확인하세요.

## 공개 자료로 확인한 사실

- **일반판 설정:** 한컴 FAQ는 [도구] → [환경 설정] → [파일] 탭의 "다음 형식으로 파일 저장"에서 HWP/HWPX를 고를 수 있다고 안내합니다. 새로 설치하면 기본값은 HWPX입니다. 기존 설치본은 업데이트 뒤에도 HWP가 기본이고 HWPX 권고 메시지가 표시됩니다. 자동 변환은 하지 않는다고 설명합니다. [한컴 FAQ 2784](https://www.hancom.com/support/faqCenter/faq/detail/2784)
- **2021년 패치:** 2021년 4월 정기 패치로 2014 VP·NEO·2018·2020에도 HWPX 기본 저장 선택 기능이 들어갔습니다. [바이라인네트워크](https://byline.network/2021/04/15-117/), [아주경제](https://www.ajunews.com/view/20210415145023299)
- **2026년 11월 공공기관용 전환:** 한컴은 공공기관용 '한글'에 HWPX 기본 저장을 적용한다고 발표했습니다. 2018부터 시작해 2020·2022·2024와 최신 버전까지 2026년 11월 26일까지 순차 업데이트하며, 업데이트 뒤에는 별도 설정 없이 새 문서가 HWPX로 저장됩니다. HWP 문서 일괄 변환기도 함께 공급합니다. 기사들은 HWP 선택이 계속 가능한지, 교육기관용도 대상인지는 다루지 않았습니다. [아이뉴스24](https://m.inews24.com/v/2029648), [테크월드](https://www.epnc.co.kr/news/articleView.html?idxno=410224), [콕스뉴스](https://www.coxnews.co.kr/news/articleView.html?idxno=6918)
- **2024의 구성:** 한컴 오류 FAQ는 2024 설치에 .NET Framework가 필요하다고 안내합니다. `Office 2024\HOffice130\Bin\HncOfficeModule.dll`도 언급합니다. 이 DLL은 2022 `Hwp.exe`가 에디션 판정에 쓰는 어셈블리와 이름이 같습니다. 2024 `Hwp.exe`도 같은 구조일 가능성이 있지만 아직 확인하지 않았습니다. [한컴 FAQ 3285](https://m.hancom.com/support/faqCenter/faq/detail/3285)
- **설치 폴더 번호:** 공개 자료의 설치 폴더와 버전 번호는 다음과 같습니다. 2018=`HOffice100`(10.0), 2020=`HOffice110`(11.0), 2022=`HOffice120`(12.0), 2024=`HOffice130`(13.0). 2024 경로는 [윈도우 포럼](https://windowsforum.kr/qna/20315147)과 위 FAQ에서 확인했고, 나머지는 같은 규칙으로 추정했습니다.

## 한글 2022 교육기관용의 실제 동작 (정적 분석)

설치된 `Hwp.exe`(`12.0.0.4645`, SHA-256 `8211F34E…CDD4`)는 x86 전용 .NET Framework 4 WPF 앱입니다. `PresentationFramework`를 참조하고, `HwpAppModule`·`HncOfficeModule`·`HwpEngineWrapper` 어셈블리로 문서 엔진을 부릅니다.

저장 흐름은 `HwpFrame::DoSaveDocument`와 `DoSaveActiveDocument`에서 시작합니다. 한컴 실행 파일은 수정하지 않고 IL을 읽기만 했습니다.

```text
DoSaveDocument
  ├─ ConvertToHwpxPath(ref path, ref format)   ← 이 런처가 false를 돌려주게 하는 메서드
  ├─ HwpFrameProxy.SaveDocument(path, format)
  └─ (전환됐으면) ShowHwpxConvertedMessage → "IDS_HwpxConvertDoneMsg" 메시지 상자
```

`ConvertToHwpxPath`는 아래 조건을 **모두** 만족할 때만 경로를 `GetUniqueHwpxPath`로 바꾸고 형식을 `"HWPX"`로 고친 뒤 `true`를 돌려줍니다.

1. 경로가 비어 있지 않고 `.hwp`로 끝난다.
2. 형식이 `"HWP30"`(한글 3.0 형식)이 아니다.
3. 가상 파일(VFS)이 아니고 로컬 파일 URI다.
4. `HncCertificationProxy.IsGovernmentProduct()` 또는 `IsEducationProduct()`가 참이다.

나머지 경우는 모두 `false`입니다. 따라서 다음 두 가지가 성립합니다.

- 교육기관용·공공기관용에서는 사용자가 [다른 이름으로 저장]에서 HWP를 골라도 `.hwpx`로 저장됩니다. 환경 설정의 기본 형식과 관계없는 별도 분기입니다.
- 이 런처가 `false`를 돌려주는 것은 **일반판에서 이 메서드가 하는 동작과 같습니다.** 저장 엔진·형식 처리 자체는 건드리지 않습니다.

## 다른 버전에 적용하기 위한 조건

1. `Hwp.exe`에 CLR 헤더가 있다(.NET 어셈블리).
2. x86 전용(`32BITREQUIRED`)이다. 프로파일러 DLL이 x86입니다.
3. `Hwp.HwpFrame`에 `instance bool ConvertToHwpxPath(string&, string&)`가 있다.
4. 그 메서드가 위와 같은 의미다(에디션 조건에서만 경로를 HWPX로 바꾼다).

1~3번은 런처가 자동으로 확인합니다. 4번은 **IL 지문**으로 정적으로 좁힙니다. 메서드 본문의 호출 대상에 에디션 판정(`IsGovernmentProduct` 또는 `IsEducationProduct`)과 `GetUniqueHwpxPath`가 모두 있어야 합니다. 없으면 `target-semantics-differ`로 적용하지 않습니다. IL 지문이 맞아도 저장 결과는 실기 시험으로 확인해야 합니다.

### 버전별 예상과 확인 상태

| 버전 | `Hwp.exe` 구조 | 강제 전환 위치 | 상태 |
|---|---|---|---|
| 2022 교육기관용 12.0.0.4645 | .NET WPF, x86 | `ConvertToHwpxPath` | 검증 |
| 2022 공공기관용 | 같은 코드에 공공기관 조건 포함 | 같은 메서드일 가능성 높음 | 빌드가 다르면 미확인 |
| 2024 (13.0) | .NET Framework 필요, `HncOfficeModule.dll` 존재 | 미확인 | 설치본 분석 필요 |
| 2018 (10.0)·2020 (11.0) 공공기관용 | 미확인 | 2026-11 업데이트로 새로 들어감 | 설치본 분석 필요. 네이티브면 지원 불가 |
| 2022 공공기관용 업데이트 이후 빌드 | 해시가 바뀌어 검증 목록에서 빠짐 | 바뀌었을 수 있음 | `inspect`로 재확인 필요 |

**주의:** 2026년 11월 공공기관용 업데이트처럼 한컴이 `Hwp.exe`를 갱신하면 SHA-256이 바뀝니다. 그러면 검증된 빌드에서도 런처가 compatible 판정으로 넘어갑니다. 업데이트 뒤에는 `inspect`로 다시 확인하세요.

## 런처의 판정 단계

| 판정(`reason`) | 의미 | HWP 모드 |
|---|---|---|
| `build-hash-match` | SHA-256이 검증 목록과 일치 | 적용(verified) |
| `compatible-needs-opt-in` | 검증되지 않았지만 1~3번 구조 일치 | 사용자가 **검증되지 않은 한글 버전에도 시도**를 켜야 적용 |
| `compatible-signature` | 위 설정을 켠 상태 | 적용(compatible, 실험) |
| `native-build-unsupported` | `Hwp.exe`가 .NET이 아님 | 불가 |
| `target-not-found` | .NET이지만 대상 메서드가 없거나 x86 전용이 아님 | 불가 |
| `target-semantics-differ` | 이름·시그니처는 같지만 IL 지문(에디션 판정·HWPX 경로 생성)이 다름 | 불가 |

프로파일러도 같은 판정을 한 번 더 합니다.
- **verified:** MVID·토큰이 `profiler/Target.h`의 `VERIFIED_TARGETS` 한 행과 일치해야 합니다.
- **compatible:** 이름·시그니처로 찾은 토큰이 런처가 넘긴 `HWP_SAVEHOOK_TARGET_TOKEN`과 같아야 합니다.

불일치하면 아무것도 바꾸지 않고 한글 기본 동작으로 실행됩니다.

## 새 빌드를 검증 목록에 추가하는 방법

1. 해당 한글이 설치된 PC에서 빌드 정보를 읽습니다. 실행 파일은 읽기만 합니다.

   ```powershell
   dotnet private/save-hook/launcher/bin/Release/net10.0-windows/win-x64/hwp-save-hook.dll inspect --hwp "C:\Program Files (x86)\HNC\Office 2024\HOffice130\Bin\Hwp.exe"
   ```

   `kind`가 `managed`이고 `x86`과 `targetSemanticsMatch`가 `true`이면 후보입니다. `native`이면 지원할 수 없습니다. 여러 버전이 설치돼 있으면 `list`로 전체 설치본과 자동 선택 결과를 봅니다.
2. 일반 실행(OFF)으로 대조군을 먼저 확인합니다. [다른 이름으로 저장]에서 HWP를 골랐을 때 HWPX로 바뀌는지 봅니다. 바뀌지 않으면 그 빌드는 런처가 필요 없습니다.
3. GUI에서 **검증되지 않은 한글 버전에도 시도**를 켜고 [검증 기록](superpowers/specs/2026-11-26-hwp-save-hook-rejit-handoff.md)의 시험을 반복합니다. 빈 문서, 서식 문서, 기존 HWP 편집 저장, 명시적 HWPX 저장, OFF 대조군입니다. `tools/inspect_format.py`로 결과 형식을 검사합니다.
4. 통과하면 `inspect`의 `sha256`·`fileVersion`·`mvid`·`targetToken`을 같은 값으로 두 곳에 추가합니다.
   - `private/save-hook/core/SaveHookLauncher.cs`의 `HwpBuildCatalog.Verified`
   - `private/save-hook/profiler/Target.h`의 `VERIFIED_TARGETS`
5. 검증 기록 문서에 시험 결과를 남깁니다.

`inspect` 결과(JSON)를 이슈로 보내 주시면 목록 추가에 도움이 됩니다. 설치 경로(`hwp`)는 지우고 보내도 됩니다.
