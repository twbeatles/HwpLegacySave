# 업데이트 매니페스트

`latest.json`은 릴리스 워크플로(`.github/workflows/release.yml`)가 `v*` 태그를 빌드할 때 만들어 main에 커밋합니다. 런처는 아래 주소에서 이 파일을 읽습니다.

```text
https://raw.githubusercontent.com/twbeatles/HwpLegacySave/main/updates/latest.json
```

형식은 평면 JSON입니다. `signature`는 아래 서명 대상 문자열에 대한 ECDSA P-256/SHA-256 서명(IEEE P1363, base64)입니다.

```text
hwpsavehook-update-v1
version=<버전>
artifact_url=<HTTPS ZIP 주소>
sha256=<소문자 16진>
size=<바이트>
expires_at=<UTC ISO-8601>
```

- 개인키는 저장소에 두지 않습니다. GitHub 저장소 Secret `HWPSAVEHOOK_UPDATE_PRIVATE_KEY`에만 등록합니다.
- 공개키는 `private/save-hook/core/UpdateInstaller.cs`의 `PublicKeyBase64`에 들어 있습니다. 키를 바꾸면 이전 버전 런처는 새 매니페스트를 받아들이지 않습니다. 그때는 수동 업데이트를 안내해야 합니다.
- 서명하지 않은 임시 매니페스트를 커밋하지 마세요.

직접 서명하려면 다음 명령을 씁니다.

```powershell
$env:HWPSAVEHOOK_UPDATE_PRIVATE_KEY = Get-Content <개인키 파일>
dotnet private/save-hook/launcher/bin/Release/net10.0-windows/win-x64/hwp-save-hook.dll update-sign `
  --version 1.2.0 --artifact HwpSaveHook-win-x64.zip `
  --url https://github.com/twbeatles/HwpLegacySave/releases/download/v1.2.0/HwpSaveHook-win-x64.zip `
  --out updates/latest.json
```
