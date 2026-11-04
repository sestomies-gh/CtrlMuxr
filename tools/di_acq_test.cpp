// di_acq_test.cpp - which DirectInput cooperative levels can this machine acquire a force-feedback wheel with?
// Opens its own window, then for DirectInput 5.00 and 7.00 (dinput.dll) and 8.00 (dinput8.dll) tries
// EXCLUSIVE|BACKGROUND, EXCLUSIVE|FOREGROUND and NONEXCLUSIVE|BACKGROUND on the first device whose name contains the given text (default "MOZA").
// Build: tools\build_di_acq_test.bat      Run: out\di_acq_test_x86.exe [name-part | VID:PID]   (output goes to stdout; the app runs it with VID:PID)
// The old (1-7) objects are used through the IDirectInput8A / IDirectInputDevice8A types: the slots this test calls
// (CreateDevice, EnumDevices; SetDataFormat, SetCooperativeLevel, Acquire, Unacquire, Release) are at the same vtable positions in every version.
#define DIRECTINPUT_VERSION 0x0800
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "dinput8.lib")
#pragma comment(lib, "dxguid.lib")

static char g_name[128] = "MOZA";
static unsigned g_vid, g_pid;        // when the argument is "VVVV:PPPP" the device is matched by VID:PID instead of by name
static bool g_byId;
static GUID g_guid;
static bool g_found;

static BOOL CALLBACK EnumCb(const DIDEVICEINSTANCEA* d, void*)
{
    if (g_byId) {
        if (d->guidProduct.Data1 == ((g_pid << 16) | g_vid)) { g_guid = d->guidInstance; g_found = true; printf("device: %s\n", d->tszProductName); return DIENUM_STOP; }
        return DIENUM_CONTINUE;
    }
    char up[260], want[128];
    strncpy(up, d->tszProductName, sizeof up - 1); up[sizeof up - 1] = 0; CharUpperA(up);
    strncpy(want, g_name, sizeof want - 1); want[sizeof want - 1] = 0; CharUpperA(want);
    if (strstr(up, want)) { g_guid = d->guidInstance; g_found = true; printf("device: %s\n", d->tszProductName); return DIENUM_STOP; }
    return DIENUM_CONTINUE;
}

static LRESULT CALLBACK Wp(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h, m, w, l); }

static const struct { DWORD f; const char* n; } LEVELS[3] = {
    { DISCL_EXCLUSIVE | DISCL_BACKGROUND, "EXCLUSIVE|BACKGROUND" },
    { DISCL_EXCLUSIVE | DISCL_FOREGROUND, "EXCLUSIVE|FOREGROUND" },
    { DISCL_NONEXCLUSIVE | DISCL_BACKGROUND, "NONEXCLUSIVE|BACKGROUND" },
};

static void RunLevels(HWND hw, const char* tag, IDirectInput8A* di)
{
    g_found = false;
    di->EnumDevices(DI8DEVCLASS_GAMECTRL, EnumCb, NULL, DIEDFL_ATTACHEDONLY);
    if (!g_found) { printf("[%s] no device matching \"%s\"\n", tag, g_name); fflush(stdout); return; }
    for (int i = 0; i < 3; i++) {
        IDirectInputDevice8A* dev = NULL;
        HRESULT hr = di->CreateDevice(g_guid, &dev, NULL);
        if (FAILED(hr)) { printf("[%s] CreateDevice hr=%08lX\n", tag, (unsigned long)hr); return; }
        dev->SetDataFormat(&c_dfDIJoystick);
        HRESULT hc = dev->SetCooperativeLevel(hw, LEVELS[i].f);
        HRESULT ha = dev->Acquire();
        printf("[%s] %-24s SetCoop=%08lX Acquire=%08lX %s\n", tag, LEVELS[i].n, (unsigned long)hc, (unsigned long)ha, SUCCEEDED(ha) ? "OK" : "FAILED");
        dev->Unacquire(); dev->Release();
    }
}

static void TestOld(HWND hw, DWORD ver)       // dinput.dll: DirectInputCreateA(version)
{
    char tag[16]; _snprintf(tag, sizeof tag, "DI %lX", ver);
    HMODULE m = LoadLibraryA("dinput.dll");
    typedef HRESULT (WINAPI *PFN)(HINSTANCE, DWORD, void**, IUnknown*);
    PFN f = m ? (PFN)GetProcAddress(m, "DirectInputCreateA") : NULL;
    if (!f) { printf("[%s] dinput.dll / DirectInputCreateA not available\n", tag); return; }
    void* pv = NULL;
    HRESULT hr = f(GetModuleHandleA(NULL), ver, &pv, NULL);
    if (FAILED(hr)) { printf("[%s] create failed hr=%08lX\n", tag, (unsigned long)hr); return; }
    IDirectInput8A* di = (IDirectInput8A*)pv;
    RunLevels(hw, tag, di);
    di->Release();
}

static void Test8(HWND hw)
{
    IDirectInput8A* di = NULL;
    HRESULT hr = DirectInput8Create(GetModuleHandleA(NULL), 0x0800, IID_IDirectInput8A, (void**)&di, NULL);
    if (FAILED(hr)) { printf("[DI 800] create failed hr=%08lX\n", (unsigned long)hr); return; }
    RunLevels(hw, "DI 800", di);
    di->Release();
}

int main(int argc, char** argv)
{
    if (argc > 1) {
        strncpy(g_name, argv[1], sizeof g_name - 1); g_name[sizeof g_name - 1] = 0;
        if (strlen(g_name) == 9 && g_name[4] == ':' && sscanf(g_name, "%4x:%4x", &g_vid, &g_pid) == 2) g_byId = true;
    }
    WNDCLASSA wc = { 0 };
    wc.lpfnWndProc = Wp; wc.hInstance = GetModuleHandleA(NULL); wc.lpszClassName = "di_acq_test"; wc.hCursor = LoadCursorA(NULL, IDC_ARROW);
    RegisterClassA(&wc);
    HWND hw = CreateWindowA("di_acq_test", "di_acq_test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 100, 100, 400, 200, NULL, NULL, wc.hInstance, NULL);
    ShowWindow(hw, SW_SHOW); SetForegroundWindow(hw);
    MSG msg;
    DWORD t0 = GetTickCount();
    while (GetTickCount() - t0 < 1500) { while (PeekMessageA(&msg, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageA(&msg); } Sleep(10); }
    printf("process: %s   foreground window is ours: %s\n", sizeof(void*) == 8 ? "x64" : "x86", GetForegroundWindow() == hw ? "yes" : "NO");
    TestOld(hw, 0x0500);
    TestOld(hw, 0x0700);
    Test8(hw);
    DestroyWindow(hw);
    return 0;
}
