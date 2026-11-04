// Entry point for the dinput8.dll proxy (DirectInput 8): exports + loading of the real system dinput8.dll.
// All hooking and logic is in core.cpp (see core.h).
#define DIRECTINPUT_VERSION 0x0800
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
        strcat(path, "\\dinput8.dll");
        g_real = LoadLibraryA(path);
        (g_real ? Core_Log : Core_LogErr)("Loaded real %s -> %p", path, (void*)g_real);
    }
    return g_real ? GetProcAddress(g_real, name) : NULL;
}

typedef HRESULT (WINAPI *PFN_Create8)(HINSTANCE, DWORD, const GUID*, void**, IUnknown*);

// Used by the core to read devices that are hidden from the game.
static HRESULT SourceFactory(void** di)
{
    PFN_Create8 f = (PFN_Create8)Real("DirectInput8Create");
    return f ? f(GetModuleHandleA(NULL), 0x0800, &IID_IDirectInput8A, di, NULL) : E_FAIL;
}

extern "C" HRESULT WINAPI My_DirectInput8Create(HINSTANCE h, DWORD ver, const GUID* riid, void** out, IUnknown* outer)
{
    PFN_Create8 f = (PFN_Create8)Real("DirectInput8Create");
    if (!f) return E_FAIL;
    HRESULT hr = f(h, ver, riid, out, outer);
    char rs[48] = "null";
    if (riid) _snprintf(rs, sizeof rs, "%08lX-%04X-%04X", riid->Data1, riid->Data2, riid->Data3);
    (FAILED(hr) ? Core_LogErr : Core_Log)("DirectInput8Create ver=%lX riid=%s hr=%lX", ver, rs, (unsigned long)hr);
    if (SUCCEEDED(hr) && out && *out) {
        bool wide = riid && IsEqualIID(*riid, IID_IDirectInput8W);
        Core_PatchDI(*out, CORE_DI_V8 | (wide ? CORE_DI_WIDE : 0));
    }
    return hr;
}

extern "C" const void* WINAPI My_GetdfDIJoystick()
{
    typedef const void* (WINAPI *F)();
    F f = (F)Real("GetdfDIJoystick");
    return f ? f() : NULL;
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
