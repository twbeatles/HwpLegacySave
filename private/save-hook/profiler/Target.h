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

// 대상 식별값은 지원 빌드에만 유효하다. 다른 빌드는 런처의 해시 게이트가 먼저 막고,
// 프로파일러도 모듈 식별로 이중 확인한다.
const mdMethodDef TARGET_METHOD_TOKEN = 0x06000244;

// Verified build identity. See docs/superpowers/specs/2026-11-26-hwp-save-hook-design.md.
#define TARGET_MODULE_BASENAME L"Hwp.exe"
constexpr GUID TARGET_MODULE_MVID =
    { 0x065AAA74, 0x990A, 0x4F2E, { 0xAF, 0x36, 0x49, 0x53, 0xDE, 0xF7, 0x47, 0xEE } };
constexpr BYTE TARGET_METHOD_SIGNATURE[] = {
    IMAGE_CEE_CS_CALLCONV_HASTHIS, 2, ELEMENT_TYPE_BOOLEAN,
    ELEMENT_TYPE_BYREF, ELEMENT_TYPE_STRING, ELEMENT_TYPE_BYREF, ELEMENT_TYPE_STRING
};
