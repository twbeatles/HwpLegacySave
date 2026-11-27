[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$saveHook = Join-Path $PSScriptRoot 'private/save-hook'
$sdk = Join-Path $PSScriptRoot 'private/toolchain/dotnet-10.0.404/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $sdk) { $sdk } else { (Get-Command dotnet.exe).Source }
$output = Join-Path $saveHook 'dist/win-x64'
$localPackages = Join-Path $PSScriptRoot 'private/toolchain/nuget-cache'
$restoreArgs = @()
if (Test-Path -LiteralPath $localPackages) {
    $restoreArgs += "-p:RestorePackagesPath=$localPackages"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$native = Join-Path $saveHook 'profiler/build.ps1'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
& $windowsPowerShell -NoProfile -File $native
if ($LASTEXITCODE -ne 0) { throw "The x86 profiler build failed ($LASTEXITCODE)." }

$gui = Join-Path $saveHook 'gui/HwpSaveHook.Gui.csproj'
& $dotnet publish $gui -c Release -p:DebugType=None -o $output @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "The GUI publish failed ($LASTEXITCODE)." }

$nativeOutput = Join-Path $output 'profiler'
New-Item -ItemType Directory -Force -Path $nativeOutput | Out-Null
Copy-Item -LiteralPath (Join-Path $saveHook 'profiler/bin/HwpSaveHookProfiler.dll') -Destination $nativeOutput
foreach ($notice in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice) -Destination $output
}

$cli = Join-Path $saveHook 'launcher/HwpSaveHook.Launcher.csproj'
& $dotnet build $cli -c Release -r win-x64 @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "The CLI build failed ($LASTEXITCODE)." }

$tests = Join-Path $saveHook 'launcher.tests/HwpSaveHook.Launcher.Tests.csproj'
$cliAssembly = Join-Path $saveHook 'launcher/bin/Release/net10.0-windows/win-x64/hwp-save-hook.dll'
& $dotnet run --project $tests -c Release @restoreArgs -- --cli $dotnet $cliAssembly
if ($LASTEXITCODE -ne 0) { throw "The launcher tests failed ($LASTEXITCODE)." }

Write-Host ("GUI: " + (Join-Path $output 'HwpSaveHook.exe'))
