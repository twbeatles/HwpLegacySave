<#
  프로파일러를 대체 출력명으로 빌드하고(원본 DLL 잠김 회피), InstantiateProbe로
  CLR v4 인스턴스화 가능 여부를 한글 없이 검증한다.
  사용: powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-and-probe.ps1
#>
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proot = Split-Path -Parent $here          # profiler/
$incDir = Join-Path $proot 'inc'
$objDir = Join-Path $here 'obj'
$binDir = Join-Path $here 'bin'

# vcvars x86 흡수 (build.ps1과 동일 방식)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe를 찾을 수 없습니다: $vswhere" }

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    # 개별 컴포넌트 지정이 안 먹는 설치에서는 일단 최신 경로라도 받아본다.
    $vsPath = & $vswhere -latest -products * -property installationPath
}
if (-not $vsPath) { throw "MSVC(C++ 빌드 도구)가 설치되어 있지 않습니다." }

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat를 찾을 수 없습니다: $vcvars" }

$envDump = & "${env:ComSpec}" /c "`"$vcvars`" x86 >nul 2>&1 && set"
foreach ($line in $envDump) { if ($line -match '^(.*?)=(.*)$') { Set-Item -Path ("env:" + $matches[1]) -Value $matches[2] } }
$cl = (Get-Command cl.exe -ErrorAction SilentlyContinue)
if (-not $cl) { throw "cl.exe를 PATH에서 찾지 못했습니다(vcvars 설정 실패)." }
$cl = $cl.Source

New-Item -ItemType Directory -Force -Path $objDir, $binDir | Out-Null

# 1) 프로파일러를 대체 출력명으로 빌드 (bin/HwpSaveHookProfiler.probe.dll)
$probeDll = Join-Path $binDir 'HwpSaveHookProfiler.probe.dll'
$srcProfiler = Join-Path $proot 'HwpSaveHookProfiler.cpp'
$defProfiler = Join-Path $proot 'HwpSaveHookProfiler.def'
Write-Host "[probe] building profiler -> $probeDll"
& $cl /nologo /LD /EHsc /std:c++17 /W3 /utf-8 /O2 /MT `
    "/I$incDir" "/Fo$(Join-Path $objDir 'profiler.obj')" "/Fe$probeDll" "$srcProfiler" `
    /link "/DEF:$defProfiler" /MACHINE:X86 ole32.lib oleaut32.lib uuid.lib
if ($LASTEXITCODE -ne 0) { throw "profiler build failed ($LASTEXITCODE)" }

# 2) InstantiateProbe.exe 빌드
$probeExe = Join-Path $binDir 'InstantiateProbe.exe'
$srcProbe = Join-Path $here 'InstantiateProbe.cpp'
Write-Host "[probe] building probe -> $probeExe"
& $cl /nologo /EHsc /std:c++17 /W3 /utf-8 /O2 /MT `
    "/I$incDir" "/Fo$(Join-Path $objDir 'probe.obj')" "/Fe$probeExe" "$srcProbe" `
    /link /MACHINE:X86 ole32.lib oleaut32.lib uuid.lib
if ($LASTEXITCODE -ne 0) { throw "probe build failed ($LASTEXITCODE)" }

# 3) 실행
Write-Host "[probe] running probe against profiler DLL:" -ForegroundColor Cyan
& $probeExe $probeDll
$code = $LASTEXITCODE
Write-Host "[probe] exit $code"
exit $code
