// Entry point for the dinput.dll proxy (DirectInput 1-7): exports + loading of the real system dinput.dll.
// All hooking and logic is in core.cpp (see core.h).
#define DIRECTINPUT_VERSION 0x0700
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>
#include "core.h"

static HMODULE g_real;

static FARPROC Real(const char* name)
{
    if (!g_real) {
        char path[MAX_PATH];
        GetSystemDirectoryA(path, MAX_PATH);      // 32-bit process: SysWOW64; 64-bit: System32
        strcat(path, "\\dinput.dll");
        g_real = LoadLibraryA(path);
        (g_real ? Core_Log : Core_LogErr)("Loaded real %s -> %p", path, (void*)g_real);
    }
    return g_real ? GetProcAddress(g_real, name) : NULL;
}

typedef HRESULT (WINAPI *PFN_CreateA)(HINSTANCE, DWORD, void**, IUnknown*);
typedef HRESULT (WINAPI *PFN_CreateEx)(HINSTANCE, DWORD, const GUID*, void**, IUnknown*);

// Used by the core to read devices that are hidden from the game.
static HRESULT SourceFactory(void** di)
{
    PFN_CreateA f = (PFN_CreateA)Real("DirectInputCreateA");
    return f ? f(GetModuleHandleA(NULL), 0x0500, di, NULL) : E_FAIL;
}

extern "C" HRESULT WINAPI My_DirectInputCreateA(HINSTANCE h, DWORD ver, void** out, IUnknown* outer)
{
    PFN_CreateA f = (PFN_CreateA)Real("DirectInputCreateA");
    if (!f) return E_FAIL;
    HRESULT hr = f(h, ver, out, outer);
    (FAILED(hr) ? Core_LogErr : Core_Log)("DirectInputCreateA ver=%lX hr=%lX", ver, (unsigned long)hr);
    if (SUCCEEDED(hr) && out && *out) Core_PatchDI(*out, 0);
    return hr;
}

extern "C" HRESULT WINAPI My_DirectInputCreateW(HINSTANCE h, DWORD ver, void** out, IUnknown* outer)
{
    PFN_CreateA f = (PFN_CreateA)Real("DirectInputCreateW");
    if (!f) return E_FAIL;
    HRESULT hr = f(h, ver, out, outer);
    (FAILED(hr) ? Core_LogErr : Core_Log)("DirectInputCreateW ver=%lX hr=%lX", ver, (unsigned long)hr);
    if (SUCCEEDED(hr) && out && *out) Core_PatchDI(*out, CORE_DI_WIDE);
    return hr;
}

extern "C" HRESULT WINAPI My_DirectInputCreateEx(HINSTANCE h, DWORD ver, const GUID* riid, void** out, IUnknown* outer)
{
    PFN_CreateEx f = (PFN_CreateEx)Real("DirectInputCreateEx");
    if (!f) return E_FAIL;
    HRESULT hr = f(h, ver, riid, out, outer);
    (FAILED(hr) ? Core_LogErr : Core_Log)("DirectInputCreateEx ver=%lX hr=%lX", ver, (unsigned long)hr);
    if (SUCCEEDED(hr) && out && *out) {
        bool wide = riid && (IsEqualIID(*riid, IID_IDirectInputW) || IsEqualIID(*riid, IID_IDirectInput2W) ||
                             IsEqualIID(*riid, IID_IDirectInput7W));
        bool v7 = riid && (IsEqualIID(*riid, IID_IDirectInput7A) || IsEqualIID(*riid, IID_IDirectInput7W));
        Core_PatchDI(*out, (wide ? CORE_DI_WIDE : 0) | (v7 ? CORE_DI_V7 : 0));
    }
    return hr;
}

extern "C" HRESULT WINAPI My_DllGetClassObject(const GUID* a, const GUID* b, void** c)
{
    typedef HRESULT (WINAPI *F)(const GUID*, const GUID*, void**);
    F f = (F)Real("DllGetClassObject");
    return f ? f(a, b, c) : E_FAIL;
}

extern "C" HRESULT WINAPI My_DllCanUnloadNow() { return S_FALSE; }

extern "C" HRESULT WINAPI My_DllRegisterServer()
{
    typedef HRESULT (WINAPI *F)();
    F f = (F)Real("DllRegisterServer");
    return f ? f() : E_FAIL;
}

extern "C" HRESULT WINAPI My_DllUnregisterServer()
{
    typedef HRESULT (WINAPI *F)();
    F f = (F)Real("DllUnregisterServer");
    return f ? f() : E_FAIL;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        Core_Attach(inst);
        Core_SetSourceFactory(SourceFactory);
    } else if (reason == DLL_PROCESS_DETACH) {
        Core_Detach();
    }
    return TRUE;
}
