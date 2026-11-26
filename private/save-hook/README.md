# 한글 HWP 저장 런처 소스

`HwpSaveHook.slnx`에는 WPF UI, 공용 런처 코어, CLI, 런처 테스트가 있습니다. x86 CLR 프로파일러는 `profiler/`의 MSVC 빌드 스크립트로 컴파일합니다.

저장소 루트에서 `Build-Local.ps1`을 실행하면 `dist/win-x64/`에 UI와 프로파일러가 게시됩니다. UI 토글은 Windows 사용자별로 저장합니다. CLI 옵션은 해당 실행에만 적용됩니다.

[사용 안내](../../README.md) · [빌드와 실행](../../docs/BUILD.md) · [검증 기록](../../docs/superpowers/specs/2026-11-26-hwp-save-hook-rejit-handoff.md)
