// Interface between the proxy core (core.cpp) and a DLL entry point (dinput_entry.cpp, later dinput8_entry.cpp).
// The entry point owns the exports and loads the real system DLL; the core owns all hooks, config, mapping and FFB fixes.
#pragma once
#include <windows.h>

enum {
    CORE_DI_WIDE = 1,       // object uses wide-char (W) callbacks/structs: enumeration is not wrapped
    CORE_DI_V7   = 2,       // IDirectInput7: also hook CreateDeviceEx
    CORE_DI_V8   = 4,       // IDirectInput8: devices have the full IDirectInputDevice8 vtable (FFB slots hooked at creation)
};

// Call once from DllMain(DLL_PROCESS_ATTACH).
void Core_Attach(HINSTANCE self);

// Creates a real (unhooked) DirectInput object of any version; the core uses it to read devices hidden from the game.
typedef HRESULT (*Core_SourceFactory)(void** di);
void Core_SetSourceFactory(Core_SourceFactory f);

// Call with every DirectInput object the real DLL returned to the game.
void Core_PatchDI(void* di, unsigned flags);

// Writes a line to dinput_<pid>.log next to the DLL (only while CtrlMuxr is running or the INI has [Log] Always=1).
void Core_Log(const char* fmt, ...);
// An error line: always kept (dinput_errors.log when normal logging is off).
void Core_LogErr(const char* fmt, ...);

// Call from DllMain(DLL_PROCESS_DETACH): flushes the log buffer.
void Core_Detach();

