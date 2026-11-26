# 로컬 빌드와 실행

Windows x64, .NET 10 SDK, Visual Studio Build Tools의 MSVC x86/x64 및 Windows SDK가 필요합니다. 새로 클론했다면 `dotnet.exe`를 PATH에 둡니다. .NET Framework 4.8 참조 어셈블리가 로컬에 없으면 SDK가 NuGet에서 복원합니다. 기존 PC에 `private/toolchain/`의 로컬 SDK와 NuGet 캐시가 있으면 빌드 스크립트가 사용합니다.

```powershell
powershell.exe -NoProfile -File .\Build-Local.ps1
```

스크립트는 x86 프로파일러와 CLI를 빌드하고 x64 WPF UI를 .NET Framework 4.8용으로 게시합니다. 마지막으로 런처 테스트를 실행합니다. UI는 아래 경로에 생성됩니다.

```text
private/save-hook/dist/win-x64/
  HwpSaveHook.exe
  HwpSaveHook.exe.config
  profiler/HwpSaveHookProfiler.dll
  LICENSE
  THIRD-PARTY-NOTICES.md
```

배포할 때는 라이선스 파일 두 개를 함께 포함하세요. 파일의 상대 경로를 유지하세요. GUI는 PC에 설치된 .NET Framework 4.8 이상을 사용합니다. 게시 폴더에 .NET 런타임은 포함되지 않습니다.

## UI에서 확인할 항목

- 처음 열 때 Windows의 사용자별·시스템 설치 등록, 한글 문서 연결과 기본 설치 위치에서 `Hwp.exe`를 찾습니다. 여러 버전이 있으면 SHA-256이 일치하는 지원 빌드를 우선합니다.
- **찾아보기…**로 지정한 경로는 같은 Windows 사용자의 다음 실행에도 사용됩니다. 경로가 없어지거나 **다시 찾기**를 누르면 자동 탐지합니다.
- **HWP 저장 유지** 토글을 ON으로 두면 지원 해시와 DLL이 확인된 경우에 **한글 열기**를 사용할 수 있습니다. OFF로 바꾸면 일반 실행합니다.
- 선택한 모드는 `%LOCALAPPDATA%\HwpSaveHook\save-mode.txt`에 저장됩니다. 이미 실행 중인 한글을 닫고 런처에서 다시 열면 선택한 모드가 적용됩니다. 런처 창을 여러 개 열었다면 창이 활성화될 때와 **한글 열기**를 누를 때 저장된 모드를 다시 읽습니다.
- 한글이 같은 Windows 세션에 열려 있거나 다른 런처(GUI·CLI)가 한글을 시작하는 중이면 런처는 시작을 중단합니다. 문서를 저장하고 한글을 닫은 뒤 다시 시도하세요.
- 설치 경로 확인이 8초 안에 끝나지 않으면(예: 응답 없는 네트워크 경로) 확인을 중단합니다. **찾아보기…**로 `Hwp.exe`를 직접 지정하거나 **다시 찾기**를 누르세요.
- **실행 로그 열기**로 마지막 HWP 모드 실행의 로그를 확인할 수 있습니다. 로그 위치는 `%LOCALAPPDATA%\HwpSaveHook\logs\`입니다.
- HWP 모드 상태는 단계별로 표시됩니다. **요청 접수**는 CLR이 ReJIT 요청을 수락했다는 뜻이며 아직 적용 확인은 아닙니다. 첫 저장에서 IL 설치와 컴파일이 성공하면 **적용됨**으로, 등록·IL 설치·컴파일 중 하나라도 실패하면 **적용 실패**로 바뀝니다. 같은 프로세스 ID와 대상 토큰의 `ReJITState` 로그만 반영합니다.

## CLI

CLI는 `private/save-hook/launcher/HwpSaveHook.Launcher.csproj`입니다. 개발 중에는 `plan`으로 대상과 해시 결과를 읽고, `launch`로 실행할 수 있습니다.

```powershell
dotnet run --project private/save-hook/launcher -- plan --clr netfx --profiler private/save-hook/profiler/bin/HwpSaveHookProfiler.dll
```

설치 정보가 바뀌었으면 `--hwp PATH`로 실행 파일을 지정하세요.

CLI의 `launch`도 GUI와 같은 실행 경계를 사용합니다. 같은 세션에 한글이 실행 중이거나 다른 런처가 시작하는 중이면 `already-running`을 반환합니다. HWP 모드로 실행하면 로그를 `%LOCALAPPDATA%\HwpSaveHook\logs\`에 만들고, 결과 JSON의 `log`와 `profilingStatus`(`request-accepted`, `applied`, `failed`, `process-exited`, `not-observed`, `disabled`)로 상태를 알려 줍니다.

| 종료 코드 | 의미 |
|---|---|
| 0 | 계획 출력 또는 실행 성공(HWP 모드는 요청 접수 이상) |
| 2 | 옵션 값 누락·공백, 잘못된 SHA-256·`--clr` 등 입력 오류 |
| 3 | 읽을 수 있는 `Hwp.exe`를 찾지 못함(`hwp-not-found`) |
| 5 | `inspection-failed`, `already-running`, `profiling-unconfirmed`, `process-exited`, `launch-failed` |

문법상 올바르지만 존재하지 않는 DLL 경로는 입력 오류가 아니라 계획의 `profiler-missing`으로 보고합니다. 체크아웃 경로에 공백이나 한글이 있어도 빌드할 수 있습니다.
