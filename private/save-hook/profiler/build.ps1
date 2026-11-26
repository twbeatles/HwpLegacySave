<#
  HwpSaveHookProfiler x86 빌드 스크립트 (설계 §8-1).
  - 대상 Hwp.exe가 x86이므로 프로파일러도 x86으로 빌드한다.
  - CLR 프로파일링 헤더(cor.h/corprof.h 등)는 저장소에 동봉하지 않는다.
    inc/README.md 따라 dotnet/runtime(MIT)에서 inc/ 로 벤더링한 뒤 실행하라.
  사용법:  powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$incDir = Join-Path $here 'inc'
$binDir = Join-Path $here 'bin'
$objDir = Join-Path $here 'obj'

# --- 1. 벤더링한 CLR 프로파일링 헤더 확인 ---
# corprof.h provides the profiler interfaces. cor.h includes the metadata and IL header definitions.
$requiredHeaders = @('corprof.h', 'cor.h', 'corhdr.h', 'corerror.h')
$missing = @()
foreach ($h in $requiredHeaders) {
    if (-not (Test-Path (Join-Path $incDir $h))) { $missing += $h }
}
if ($missing.Count -gt 0) {
    Write-Host "[build] 누락된 CLR 프로파일링 헤더: $($missing -join ', ')" -ForegroundColor Yellow
    Write-Host "[build] inc/README.md 를 따라 dotnet/runtime 에서 inc/ 로 복사한 뒤 다시 실행하세요." -ForegroundColor Yellow
    exit 2
}

# --- 2. MSVC x86 환경 구성 (vswhere + vcvarsall) ---
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe를 찾을 수 없습니다: $vswhere" }

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    # 구성요소 쿼리가 비면 설치 경로만이라도 받아본다.
    $vsPath = & $vswhere -latest -products * -property installationPath
}
if (-not $vsPath) { throw "MSVC(C++ 빌드 도구)가 설치되어 있지 않습니다." }

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat를 찾을 수 없습니다: $vcvars" }

# vcvarsall x86 환경변수를 이 프로세스로 흡수한다.
$envDump = & "${env:ComSpec}" /c "`"$vcvars`" x86 >nul 2>&1 && set"
foreach ($line in $envDump) {
    if ($line -match '^(.*?)=(.*)$') {
        Set-Item -Path ("env:" + $matches[1]) -Value $matches[2]
    }
}
$cl = (Get-Command cl.exe -ErrorAction SilentlyContinue)
if (-not $cl) { throw "cl.exe를 PATH에서 찾지 못했습니다(vcvars 흡수 실패)." }
Write-Host "[build] cl.exe: $($cl.Source)"

# --- 3. 컴파일 ---
New-Item -ItemType Directory -Force -Path $binDir, $objDir | Out-Null
$src = Join-Path $here 'HwpSaveHookProfiler.cpp'
$def = Join-Path $here 'HwpSaveHookProfiler.def'
$dll = Join-Path $binDir 'HwpSaveHookProfiler.dll'

$optFlags = if ($Configuration -eq 'Debug') { @('/Od', '/Zi', '/MTd') } else { @('/O2', '/MT') }

# Use an explicit object filename so the closing quote of a spaced path follows a filename.
$clArgs = @(
    '/nologo', '/LD', '/EHsc', '/std:c++17', '/W3', '/utf-8'
) + $optFlags + @(
    "/I$incDir",
    "/Fo$(Join-Path $objDir 'HwpSaveHookProfiler.obj')",
    "/Fe$dll",
    "$src"
) + @(
    '/link',
    "/DEF:$def",
    '/MACHINE:X86',
    'ole32.lib', 'oleaut32.lib', 'uuid.lib'
)

Write-Host "[build] compiling x86 -> $dll"
& $cl.Source @clArgs
if ($LASTEXITCODE -ne 0) { throw "cl.exe 빌드 실패(exit $LASTEXITCODE)." }

Write-Host "[build] OK: $dll" -ForegroundColor Green
Write-Host "[build] 런처는 --profiler `"$dll`" 로 가리키거나, 런처 exe 옆 profiler\ 에 두세요."
