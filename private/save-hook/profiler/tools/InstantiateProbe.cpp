// Checks the CLR v4 startup interface and the ReJIT callback interface.

#include <windows.h>
#include <cor.h>
#include <corprof.h>
#include <cstdio>

// 프로파일러 CLSID (런처/프로파일러와 동일)
static const CLSID kClsid =
    { 0xE72ECB74, 0x8EC7, 0x43AE, { 0xB9, 0xF1, 0xC0, 0x3C, 0xAC, 0x1C, 0xAC, 0xD3 } };

typedef HRESULT (STDMETHODCALLTYPE *DllGetClassObjectFn)(REFCLSID, REFIID, void**);

static void ReportQI(IUnknown* unk, const char* name, REFIID iid)
{
    void* p = nullptr;
    HRESULT hr = unk->QueryInterface(iid, &p);
    printf("  QI %-24s -> 0x%08lX %s\n", name, (unsigned long)hr, SUCCEEDED(hr) ? "(OK)" : "(no)");
    if (SUCCEEDED(hr) && p) ((IUnknown*)p)->Release();
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        printf("usage: InstantiateProbe <profiler.dll>\n");
        return 2;
    }

    HMODULE mod = LoadLibraryW(argv[1]);
    if (!mod)
    {
        printf("LoadLibrary FAILED (GetLastError=%lu)\n", GetLastError());
        return 3;
    }
    printf("LoadLibrary OK\n");

    auto getClassObject = (DllGetClassObjectFn)GetProcAddress(mod, "DllGetClassObject");
    if (!getClassObject)
    {
        printf("GetProcAddress(DllGetClassObject) FAILED\n");
        return 3;
    }

    IClassFactory* factory = nullptr;
    HRESULT hr = getClassObject(kClsid, __uuidof(IClassFactory), (void**)&factory);
    printf("DllGetClassObject(IID_IClassFactory) -> 0x%08lX %s\n",
           (unsigned long)hr, SUCCEEDED(hr) ? "(OK)" : "(FAIL)");
    if (FAILED(hr) || !factory) return 4;

    // CLR v4가 요구하는 인터페이스로 생성 시도.
    ICorProfilerCallback3* cb3 = nullptr;
    hr = factory->CreateInstance(nullptr, __uuidof(ICorProfilerCallback3), (void**)&cb3);
    printf("CreateInstance(IID_ICorProfilerCallback3) -> 0x%08lX %s\n",
           (unsigned long)hr, SUCCEEDED(hr) ? "(OK)" : "(FAIL)");
    if (SUCCEEDED(hr) && cb3)
    {
        printf("Instantiation SUCCEEDED. Interface support:\n");
        ReportQI(cb3, "ICorProfilerCallback",  __uuidof(ICorProfilerCallback));
        ReportQI(cb3, "ICorProfilerCallback2", __uuidof(ICorProfilerCallback2));
        ReportQI(cb3, "ICorProfilerCallback3", __uuidof(ICorProfilerCallback3));
        ReportQI(cb3, "ICorProfilerCallback4", __uuidof(ICorProfilerCallback4));
        cb3->Release();
    }
    const bool startup = SUCCEEDED(hr) && cb3;
    ICorProfilerCallback4* cb4 = nullptr;
    hr = factory->CreateInstance(nullptr, __uuidof(ICorProfilerCallback4), (void**)&cb4);
    const bool rejit = SUCCEEDED(hr) && cb4;
    printf("CreateInstance(IID_ICorProfilerCallback4) -> 0x%08lX %s\n",
           (unsigned long)hr, rejit ? "(OK)" : "(FAIL)");
    if (cb4) cb4->Release();
    factory->Release();

    printf("RESULT: %s\n", startup && rejit ? "PASS" : "FAIL");
    return startup && rejit ? 0 : 1;
}
