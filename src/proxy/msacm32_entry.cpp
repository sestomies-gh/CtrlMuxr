// Entry point for a msacm32.dll proxy: for games that read the joystick through winmm only (no DirectInput) and ship
// no proxy-able DLL. The game imports msacm32.dll (acmMetrics only), so a local msacm32.dll gets loaded; every export is
// forwarded to the real system DLL (generated headers, see gen_forwarders.py) and the core installs the winmm IAT hook.
#define DIRECTINPUT_VERSION 0x0500
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>
#include "core.h"

#ifdef _WIN64
#include "msacm32_fwd_x64.h"
#else
#include "msacm32_fwd_x86.h"
#endif

typedef HRESULT (WINAPI *PFN_CreateA)(HINSTANCE, DWORD, void**, IUnknown*);

// Used by the core to read the devices that are mapped into the wheel (through the system dinput.dll).
static HRESULT SourceFactory(void** di)
{
    static HMODULE m;
    if (!m) {
        char path[MAX_PATH];
        GetSystemDirectoryA(path, MAX_PATH);
        strcat(path, "\\dinput.dll");
        m = LoadLibraryA(path);
    }
    PFN_CreateA f = m ? (PFN_CreateA)GetProcAddress(m, "DirectInputCreateA") : NULL;
    return f ? f(GetModuleHandleA(NULL), 0x0500, di, NULL) : E_FAIL;
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
