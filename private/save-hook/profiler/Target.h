// 대상 식별 상수. 설계 §3·§6.
// 이 프로파일러는 오직 아래 대상에만 작용한다. 식별 불일치면 아무 것도 하지 않는다(fail-safe).
#pragma once

#include <windows.h>

// 프로파일러 COM 클래스 식별자. 런처의 SaveHookLauncher.ProfilerClsid와 반드시 일치.
// {E72ECB74-8EC7-43AE-B9F1-C03CAC1CACD3}
// clang-format off
const CLSID CLSID_HwpSaveHookProfiler =
    { 0xE72ECB74, 0x8EC7, 0x43AE, { 0xB9, 0xF1, 0xC0, 0x3C, 0xAC, 0x1C, 0xAC, 0xD3 } };
// clang-format on

// 대상 메서드: instance bool Hwp.HwpFrame::ConvertToHwpxPath(ref string, ref string)
// 토큰은 빌드마다 달라질 수 있어 이름과 시그니처로 찾는다.
#define TARGET_MODULE_BASENAME L"Hwp.exe"
#define TARGET_TYPE_NAME L"Hwp.HwpFrame"
#define TARGET_METHOD_NAME L"ConvertToHwpxPath"
constexpr BYTE TARGET_METHOD_SIGNATURE[] = {
    IMAGE_CEE_CS_CALLCONV_HASTHIS, 2, ELEMENT_TYPE_BOOLEAN,
    ELEMENT_TYPE_BYREF, ELEMENT_TYPE_STRING, ELEMENT_TYPE_BYREF, ELEMENT_TYPE_STRING
};

// 실기 검증한 빌드. 런처의 HwpBuildCatalog.Verified와 같은 값을 유지한다.
// verified 모드에서는 모듈 MVID와 찾은 토큰이 이 표의 한 행과 모두 일치해야 한다.
struct VerifiedTarget
{
    GUID mvid;
    mdMethodDef token;
};

// clang-format off
constexpr VerifiedTarget VERIFIED_TARGETS[] = {
    // 한글 2022 교육기관용 12.0.0.4645 x86
    { { 0x065AAA74, 0x990A, 0x4F2E, { 0xAF, 0x36, 0x49, 0x53, 0xDE, 0xF7, 0x47, 0xEE } }, 0x06000244 },
};
// clang-format on
