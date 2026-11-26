// Startup CLR profiler for the verified Hwp.exe save path (design §8-3).
#include <windows.h>
#include <cor.h>
#include <corprof.h>
#include <atomic>
#include <cstring>
#include <cstdio>
#include <cstdarg>
#include <mutex>
#include <string>

#include "Target.h"

// ----------------------------------------------------------------------------
// 로깅: 환경변수 HWP_SAVEHOOK_LOG, 없으면 %TEMP%\hwp-save-hook-profiler.log
// ----------------------------------------------------------------------------
static std::mutex g_logMutex;

static std::wstring LogPath()
{
    constexpr DWORD maxPath = 32768;
    DWORD size = GetEnvironmentVariableW(L"HWP_SAVEHOOK_LOG", nullptr, 0);
    if (size > 0 && size <= maxPath)
    {
        std::wstring configured(size, L'\0');
        DWORD count = GetEnvironmentVariableW(L"HWP_SAVEHOOK_LOG", configured.data(), size);
        if (count > 0 && count < size)
        {
            configured.resize(count);
            return configured;
        }
    }

    std::wstring temp(MAX_PATH + 1, L'\0');
    DWORD count = GetTempPathW(static_cast<DWORD>(temp.size()), temp.data());
    if (count >= temp.size() && count <= maxPath)
    {
        temp.resize(count);
        count = GetTempPathW(static_cast<DWORD>(temp.size()), temp.data());
    }
    if (count == 0 || count >= temp.size()) return {};
    temp.resize(count);
    return temp + L"hwp-save-hook-profiler.log";
}

static void Log(const char* fmt, ...) noexcept
try
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    static std::wstring path = LogPath();
    if (path.empty()) return;
    FILE* fp = nullptr;
    if (_wfopen_s(&fp, path.c_str(), L"a+, ccs=UTF-8") != 0 || fp == nullptr)
    {
        return;
    }

    SYSTEMTIME st;
    GetLocalTime(&st);
    fwprintf(fp, L"%04d-%02d-%02d %02d:%02d:%02d.%03d [pid %lu] ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
             GetCurrentProcessId());

    char line[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(line, sizeof(line), fmt, args);
    va_end(args);

    // UTF-8 로그 스트림에 ANSI 문자열을 그대로 흘려도 ASCII 범위에선 안전.
    fwprintf(fp, L"%hs\n", line);
    fclose(fp);
}
catch (...) { /* Diagnostics must not throw into the CLR. */ }

static void LogRejitState(const char* state)
{
    Log("ReJITState: token=0x%08lX state=%s", (ULONG)TARGET_METHOD_TOKEN, state);
}

// ----------------------------------------------------------------------------
// 프로파일러 콜백. CLR v4(.NET Framework 4.x)는 ICorProfilerCallback2까지만 구현한
// 프로파일러를 "이전 버전 CLR용(V2 레거시)"으로 보고 기본 차단한다(.NET Runtime 이벤트
// 1022, 메시지 0x2517). 현재 버전 프로파일러로 인정받으려면 ICorProfilerCallback3를
// 구현해야 한다. (v1만이면 "COM 인스턴스화 예외" 0x250f.)
class CProfiler : public ICorProfilerCallback4
{
public:
    ~CProfiler() { if (m_info) m_info->Release(); }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        // __uuidof 사용: corguids.lib 없이 벤더 헤더만으로 빌드되게 한다.
        if (riid == __uuidof(IUnknown) ||
            riid == __uuidof(ICorProfilerCallback) ||
            riid == __uuidof(ICorProfilerCallback2) ||
            riid == __uuidof(ICorProfilerCallback3) ||
            riid == __uuidof(ICorProfilerCallback4))
        {
            *ppv = static_cast<ICorProfilerCallback4*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&m_refCount); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG c = InterlockedDecrement(&m_refCount);
        if (c == 0) delete this;
        return c;
    }

    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* runtime) override
    {
        const DWORD length = GetModuleFileNameW(nullptr, m_executable, ARRAYSIZE(m_executable));
        if (length == 0 || length >= ARRAYSIZE(m_executable)) return E_FAIL;

        const WCHAR* separator = wcsrchr(m_executable, L'\\');
        if (_wcsicmp(separator ? separator + 1 : m_executable, TARGET_MODULE_BASENAME) != 0) return S_OK;

        HRESULT hr = runtime->QueryInterface(__uuidof(ICorProfilerInfo4), (void**)&m_info);
        if (FAILED(hr))
        {
            Log("Initialize: ICorProfilerInfo4 unavailable, hr=0x%08lX.", (ULONG)hr);
            return hr;
        }

        ICorProfilerInfo5* info5 = nullptr;
        hr = runtime->QueryInterface(__uuidof(ICorProfilerInfo5), (void**)&info5);
        if (FAILED(hr))
        {
            Log("Initialize: ICorProfilerInfo5 unavailable, hr=0x%08lX.", (ULONG)hr);
            return hr;
        }

        const DWORD mask = COR_PRF_MONITOR_MODULE_LOADS | COR_PRF_MONITOR_JIT_COMPILATION |
            COR_PRF_DISABLE_INLINING | COR_PRF_ENABLE_REJIT | COR_PRF_DISABLE_ALL_NGEN_IMAGES;
        hr = info5->SetEventMask2(mask, 0);
        info5->Release();
        Log("Initialize: SetEventMask2(0x%08lX, 0) hr=0x%08lX.", mask, (ULONG)hr);
        return hr;
    }

    HRESULT STDMETHODCALLTYPE Shutdown() override
    {
        m_targetModule.store(0);
        if (m_info) Log("Shutdown.");
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID moduleId, HRESULT status) override
    {
        if (FAILED(status) || !m_info || m_targetModule.load() || !VerifyTarget(moduleId))
        {
            return S_OK;
        }

        ModuleID empty = 0;
        if (!m_targetModule.compare_exchange_strong(empty, moduleId)) return S_OK;
        Log("ModuleLoadFinished: verified target module (moduleId=%p).",
            (void*)moduleId);

        // RequestReJIT may suspend the CLR. Return from this callback without waiting for it.
        AddRef();
        HANDLE thread = CreateThread(nullptr, 0, RequestOnWorker, this, 0, nullptr);
        if (thread)
        {
            CloseHandle(thread);
        }
        else
        {
            const DWORD error = GetLastError();
            m_targetModule.store(0);
            Release();
            Log("ModuleLoadFinished: could not create ReJIT worker (error=%lu).", error);
            LogRejitState("failed");
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID moduleId) override
    {
        m_targetModule.compare_exchange_strong(moduleId, 0);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID functionId, BOOL) override
    {
        if (IsTargetFunction(functionId))
        {
            Log("JITCompilationStarted: original target version.");
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID moduleId, mdMethodDef methodId,
        ICorProfilerFunctionControl* control) override
    {
        if (!IsTarget(moduleId, methodId)) return S_OK;

        // Return false (no conversion) without writing to any argument.
        constexpr BYTE code[] = { 0x16, 0x2A }; // ldc.i4.0; ret
        constexpr IMAGE_COR_ILMETHOD_TINY header = {
            (sizeof(code) << (CorILMethod_FormatShift - 1)) | CorILMethod_TinyFormat
        };
        const BYTE body[] = { header.Flags_CodeSize, code[0], code[1] };
        const HRESULT hr = control->SetILFunctionBody(sizeof(body), body);
        Log("GetReJITParameters: SetILFunctionBody for token 0x%08lX hr=0x%08lX.",
            (ULONG)methodId, (ULONG)hr);
        LogRejitState(SUCCEEDED(hr) ? "il-installed" : "failed");
        return hr;
    }

    HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID functionId, ReJITID rejitId, BOOL) override
    {
        if (IsTargetFunction(functionId))
        {
            Log("ReJITCompilationStarted: target rejitId=%p.", (void*)rejitId);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID functionId, ReJITID rejitId,
        HRESULT status, BOOL) override
    {
        if (IsTargetFunction(functionId))
        {
            Log("ReJITCompilationFinished: target rejitId=%p hr=0x%08lX.", (void*)rejitId, (ULONG)status);
            LogRejitState(SUCCEEDED(status) ? "compiled" : "failed");
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ReJITError(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId,
        HRESULT status) override
    {
        if (IsTarget(moduleId, methodId))
        {
            Log("ReJITError: target moduleId=%p functionId=%p hr=0x%08lX.",
                (void*)moduleId, (void*)functionId, (ULONG)status);
            LogRejitState("failed");
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE MovedReferences2(ULONG, ObjectID[], ObjectID[], SIZE_T[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE SurvivingReferences2(ULONG, ObjectID[], SIZE_T[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE JITInlining(FunctionID, FunctionID, BOOL*) override { return S_OK; }

    // --- 이하 S_OK 스텁(ICorProfilerCallback v1 전체 vtable) ---
    HRESULT STDMETHODCALLTYPE AppDomainCreationStarted(AppDomainID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainCreationFinished(AppDomainID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainShutdownStarted(AppDomainID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainShutdownFinished(AppDomainID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyLoadStarted(AssemblyID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyLoadFinished(AssemblyID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyUnloadStarted(AssemblyID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyUnloadFinished(AssemblyID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleLoadStarted(ModuleID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleUnloadFinished(ModuleID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleAttachedToAssembly(ModuleID, AssemblyID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassLoadStarted(ClassID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassLoadFinished(ClassID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassUnloadStarted(ClassID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassUnloadFinished(ClassID, HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE FunctionUnloadStarted(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCompilationFinished(FunctionID, HRESULT, BOOL) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID, BOOL*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID, COR_PRF_JIT_CACHE) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadCreated(ThreadID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadDestroyed(ThreadID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadAssignedToOSThread(ThreadID, DWORD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationStarted() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientSendingMessage(GUID*, BOOL) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientReceivingReply(GUID*, BOOL) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationFinished() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerReceivingMessage(GUID*, BOOL) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationStarted() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationReturned() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerSendingReply(GUID*, BOOL) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE UnmanagedToManagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ManagedToUnmanagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeResumeStarted() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeResumeFinished() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeThreadSuspended(ThreadID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeThreadResumed(ThreadID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE MovedReferences(ULONG, ObjectID[], ObjectID[], ULONG[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID, ClassID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectsAllocatedByClass(ULONG, ClassID[], ULONG[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectReferences(ObjectID, ClassID, ULONG, ObjectID[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RootReferences(ULONG, ObjectID[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionThrown(ObjectID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionEnter(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionLeave() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterEnter(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterLeave() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchCatcherFound(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerEnter(UINT_PTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerLeave(UINT_PTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionEnter(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionLeave() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyEnter(FunctionID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyLeave() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCatcherEnter(FunctionID, ObjectID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCatcherLeave() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE COMClassicVTableCreated(ClassID, REFGUID, void*, ULONG) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE COMClassicVTableDestroyed(ClassID, REFGUID, void*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherFound() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherExecute() override { return S_OK; }

    // --- ICorProfilerCallback2 추가분(전부 S_OK 스텁; .NET Framework 4.x 로드 요건) ---
    HRESULT STDMETHODCALLTYPE ThreadNameChanged(ThreadID, ULONG, WCHAR[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int, BOOL[], COR_PRF_GC_REASON) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG, ObjectID[], ULONG[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GarbageCollectionFinished() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE FinalizeableObjectQueued(DWORD, ObjectID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE RootReferences2(ULONG, ObjectID[], COR_PRF_GC_ROOT_KIND[], COR_PRF_GC_ROOT_FLAGS[], UINT_PTR[]) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE HandleCreated(GCHandleID, ObjectID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE HandleDestroyed(GCHandleID) override { return S_OK; }

    // --- ICorProfilerCallback3 추가분(CLR v4 "현재 버전" 인정 요건) ---
    // 시작 시 로드에는 호출되지 않지만(InitializeForAttach는 런타임 attach용), 인터페이스
    // 구현 자체가 필요하다. attach 경로를 쓸 일이 없으므로 로깅만 하고 S_OK.
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown*, void*, UINT) override
    {
        Log("InitializeForAttach called (unexpected for startup load); ignoring.");
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded() override { return S_OK; }

private:
    bool VerifyTarget(ModuleID moduleId) const
    {
        WCHAR path[1024];
        ULONG length = 0;
        if (m_info->GetModuleInfo(moduleId, nullptr, ARRAYSIZE(path), &length, path, nullptr) != S_OK ||
            length == 0 || length > ARRAYSIZE(path))
        {
            return false;
        }

        if (_wcsicmp(path, m_executable) != 0) return false;

        IMetaDataImport* metadata = nullptr;
        // IID_IMetaDataImport from cor.h, which declares the IID without a __uuidof attribute.
        constexpr IID metadataImportId =
            { 0x7DAC8207, 0xD3AE, 0x4C75, { 0x9B, 0x67, 0x92, 0x80, 0x1A, 0x49, 0x7D, 0x44 } };
        HRESULT hr = m_info->GetModuleMetaData(moduleId, ofRead, metadataImportId, (IUnknown**)&metadata);
        if (hr != S_OK || !metadata)
        {
            Log("ModuleLoadFinished: Hwp.exe metadata unavailable, hr=0x%08lX.", (ULONG)hr);
            return false;
        }

        GUID mvid{};
        PCCOR_SIGNATURE signature = nullptr;
        ULONG signatureSize = 0;
        const bool verified = metadata->GetScopeProps(nullptr, 0, nullptr, &mvid) == S_OK &&
            mvid == TARGET_MODULE_MVID &&
            metadata->GetMethodProps(TARGET_METHOD_TOKEN, nullptr, nullptr, 0, nullptr, nullptr,
                &signature, &signatureSize, nullptr, nullptr) == S_OK &&
            signature && signatureSize == sizeof(TARGET_METHOD_SIGNATURE) &&
            std::memcmp(signature, TARGET_METHOD_SIGNATURE, signatureSize) == 0;
        metadata->Release();
        if (!verified) Log("ModuleLoadFinished: Hwp.exe MVID or target signature mismatch.");
        return verified;
    }

    bool IsTarget(ModuleID moduleId, mdToken token) const
    {
        return moduleId != 0 && moduleId == m_targetModule.load() && token == TARGET_METHOD_TOKEN;
    }

    bool IsTargetFunction(FunctionID functionId) const
    {
        ModuleID moduleId = 0;
        mdToken token = 0;
        return m_targetModule.load() && m_info &&
            m_info->GetFunctionInfo(functionId, nullptr, &moduleId, &token) == S_OK &&
            IsTarget(moduleId, token);
    }

    static DWORD WINAPI RequestOnWorker(void* state)
    {
        auto* profiler = static_cast<CProfiler*>(state);
        ModuleID moduleId = profiler->m_targetModule.load();
        if (moduleId)
        {
            mdMethodDef method = TARGET_METHOD_TOKEN;
            ICorProfilerInfo4* info = profiler->m_info;
            const HRESULT hr = info->InitializeCurrentThread();
            if (SUCCEEDED(hr))
            {
                const HRESULT request = info->RequestReJIT(1, &moduleId, &method);
                Log("RequestReJIT: target moduleId=%p token=0x%08lX hr=0x%08lX.",
                    (void*)moduleId, (ULONG)method, (ULONG)request);
                LogRejitState(request == S_OK ? "request-accepted" : "failed");
            }
            else
            {
                Log("RequestReJIT: InitializeCurrentThread failed, hr=0x%08lX.", (ULONG)hr);
                LogRejitState("failed");
            }
        }
        profiler->Release();
        return 0;
    }

    LONG m_refCount = 1;
    ICorProfilerInfo4* m_info = nullptr;
    std::atomic<ModuleID> m_targetModule{0};
    WCHAR m_executable[1024]{};
};

// ----------------------------------------------------------------------------
// ClassFactory
// ----------------------------------------------------------------------------
class CClassFactory : public IClassFactory
{
public:
    CClassFactory() : m_refCount(1) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&m_refCount); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG c = InterlockedDecrement(&m_refCount);
        if (c == 0) delete this;
        return c;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;
        CProfiler* profiler = new (std::nothrow) CProfiler();
        if (profiler == nullptr) return E_OUTOFMEMORY;
        HRESULT hr = profiler->QueryInterface(riid, ppv);
        profiler->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

private:
    LONG m_refCount;
};

// ----------------------------------------------------------------------------
// DLL 내보내기
// ----------------------------------------------------------------------------
extern "C" HRESULT STDMETHODCALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    if (rclsid != CLSID_HwpSaveHookProfiler)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    CClassFactory* factory = new (std::nothrow) CClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT STDMETHODCALLTYPE DllCanUnloadNow()
{
    return S_OK;
}
