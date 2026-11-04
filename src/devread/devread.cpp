// devread.dll - tiny native helper for the GUI: lists game controllers and reads their state through DirectInput 8.
// Plain C API (cdecl) for P/Invoke. Axis names are the DirectInput ones used in dinput_proxy.ini (X Y Z Rx Ry Rz S0 S1).
#define DIRECTINPUT_VERSION 0x0800
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <dinput.h>
#include <stdio.h>
#include <string.h>

struct DRDevInfo {
    char name[128];
    unsigned vid, pid;
    char guid[40];
    int axisMask;           // bit i = axis i present (X Y Z Rx Ry Rz S0 S1)
    int numButtons;
    int ffb;                // 1 = the device reports force feedback (DIDC_FORCEFEEDBACK)
};

#pragma pack(push, 1)
struct State176 {           // the first 176 bytes of DIJOYSTATE2: axes, POV, 128 buttons
    LONG ax[8];             // X Y Z Rx Ry Rz S0 S1 (lX lY lZ lRx lRy lRz slider0 slider1)
    DWORD pov[4];
    BYTE btn[128];
};
#pragma pack(pop)

static const int MAXDEV = 16;
static IDirectInput8A* g_di;
static IDirectInputDevice8A* g_dev[MAXDEV];
static GUID g_guid[MAXDEV];
static DRDevInfo g_info[MAXDEV];
static int g_n;

static DIOBJECTDATAFORMAT g_odf[8 + 4 + 128];
static DIDATAFORMAT g_df;

static void BuildFormat()
{
    if (g_df.dwSize) return;
    const GUID* ax[6] = { &GUID_XAxis, &GUID_YAxis, &GUID_ZAxis, &GUID_RxAxis, &GUID_RyAxis, &GUID_RzAxis };
    int n = 0;
    for (int i = 0; i < 6; i++, n++) { g_odf[n].pguid = ax[i]; g_odf[n].dwOfs = i * 4; g_odf[n].dwType = 0x80000000 | DIDFT_AXIS | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = DIDOI_ASPECTPOSITION; }
    for (int i = 0; i < 2; i++, n++) { g_odf[n].pguid = &GUID_Slider; g_odf[n].dwOfs = 24 + i * 4; g_odf[n].dwType = 0x80000000 | DIDFT_AXIS | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = DIDOI_ASPECTPOSITION; }
    for (int i = 0; i < 4; i++, n++) { g_odf[n].pguid = &GUID_POV; g_odf[n].dwOfs = 32 + i * 4; g_odf[n].dwType = 0x80000000 | DIDFT_POV | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = 0; }
    for (int i = 0; i < 128; i++, n++) { g_odf[n].pguid = NULL; g_odf[n].dwOfs = 48 + i; g_odf[n].dwType = 0x80000000 | DIDFT_BUTTON | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = 0; }
    g_df.dwSize = sizeof(DIDATAFORMAT); g_df.dwObjSize = sizeof(DIOBJECTDATAFORMAT);
    g_df.dwFlags = DIDF_ABSAXIS; g_df.dwDataSize = sizeof(State176); g_df.dwNumObjs = n; g_df.rgodf = g_odf;
}

static void CloseAll()
{
    for (int i = 0; i < g_n; i++) if (g_dev[i]) { g_dev[i]->Unacquire(); g_dev[i]->Release(); g_dev[i] = NULL; }
    g_n = 0;
}

struct EnumCtx { int n; };

static BOOL CALLBACK EnumCb(const DIDEVICEINSTANCEA* d, void* ref)
{
    EnumCtx* c = (EnumCtx*)ref;
    if (c->n >= MAXDEV) return DIENUM_STOP;
    DRDevInfo& i = g_info[c->n];
    memset(&i, 0, sizeof i);
    _snprintf(i.name, sizeof i.name, "%s", d->tszProductName);
    i.vid = d->guidProduct.Data1 & 0xFFFF;
    i.pid = d->guidProduct.Data1 >> 16;
    const GUID& g = d->guidInstance;
    _snprintf(i.guid, sizeof i.guid, "%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X", g.Data1, g.Data2, g.Data3,
              g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    g_guid[c->n] = d->guidInstance;
    IDirectInputDevice8A* dv = NULL;      // capabilities are read here so the Add game dialog can tell the wheel (FFB) from the other controllers
    if (g_di && SUCCEEDED(g_di->CreateDevice(d->guidInstance, &dv, NULL)) && dv) {
        DIDEVCAPS caps;
        memset(&caps, 0, sizeof caps);
        caps.dwSize = sizeof caps;
        if (SUCCEEDED(dv->GetCapabilities(&caps))) { i.numButtons = (int)caps.dwButtons; i.ffb = (caps.dwFlags & DIDC_FORCEFEEDBACK) ? 1 : 0; }
        dv->Release();
    }
    c->n++;
    return DIENUM_CONTINUE;
}

struct ObjCtx { int mask; int sliders; };

static BOOL CALLBACK ObjCb(const DIDEVICEOBJECTINSTANCEA* o, void* ref)
{
    ObjCtx* c = (ObjCtx*)ref;
    int a = -1;
    if (IsEqualGUID(o->guidType, GUID_XAxis)) a = 0;
    else if (IsEqualGUID(o->guidType, GUID_YAxis)) a = 1;
    else if (IsEqualGUID(o->guidType, GUID_ZAxis)) a = 2;
    else if (IsEqualGUID(o->guidType, GUID_RxAxis)) a = 3;
    else if (IsEqualGUID(o->guidType, GUID_RyAxis)) a = 4;
    else if (IsEqualGUID(o->guidType, GUID_RzAxis)) a = 5;
    else if (IsEqualGUID(o->guidType, GUID_Slider) && c->sliders < 2) a = 6 + c->sliders++;
    if (a >= 0) c->mask |= 1 << a;
    return DIENUM_CONTINUE;
}

extern "C" {

// Lists attached game controllers; returns the count (0 on failure). Opens nothing yet.
__declspec(dllexport) int DR_Enum(DRDevInfo* out, int max)
{
    CloseAll();
    if (!g_di) {
        typedef HRESULT (WINAPI *PFN)(HINSTANCE, DWORD, const GUID*, void**, IUnknown*);
        HMODULE m = LoadLibraryA("dinput8.dll");
        PFN f = m ? (PFN)GetProcAddress(m, "DirectInput8Create") : NULL;
        if (!f || FAILED(f(GetModuleHandleA(NULL), DIRECTINPUT_VERSION, &IID_IDirectInput8A, (void**)&g_di, NULL))) return 0;
    }
    EnumCtx c = { 0 };
    g_di->EnumDevices(DI8DEVCLASS_GAMECTRL, EnumCb, &c, DIEDFL_ATTACHEDONLY);
    g_n = c.n;
    for (int i = 0; i < g_n && i < max; i++) out[i] = g_info[i];
    return g_n < max ? g_n : max;
}

// Opens device idx for reading (shared, works in the background). Returns 0 on success.
__declspec(dllexport) int DR_Open(int idx, HWND hwnd)
{
    if (idx < 0 || idx >= g_n || !g_di) return -1;
    if (g_dev[idx]) return 0;
    IDirectInputDevice8A* d = NULL;
    if (FAILED(g_di->CreateDevice(g_guid[idx], &d, NULL)) || !d) return -2;
    BuildFormat();
    if (FAILED(d->SetDataFormat(&g_df))) { d->Release(); return -3; }
    d->SetCooperativeLevel(hwnd, DISCL_NONEXCLUSIVE | DISCL_BACKGROUND);
    DIPROPRANGE r;
    memset(&r, 0, sizeof r);
    r.diph.dwSize = sizeof r; r.diph.dwHeaderSize = sizeof(DIPROPHEADER); r.diph.dwHow = DIPH_DEVICE; r.lMin = 0; r.lMax = 65535;
    d->SetProperty(DIPROP_RANGE, &r.diph);
    DIPROPDWORD dz;
    memset(&dz, 0, sizeof dz);
    dz.diph.dwSize = sizeof dz; dz.diph.dwHeaderSize = sizeof(DIPROPHEADER); dz.diph.dwHow = DIPH_DEVICE; dz.dwData = 0;
    d->SetProperty(DIPROP_DEADZONE, &dz.diph);
    ObjCtx oc = { 0, 0 };
    d->EnumObjects(ObjCb, &oc, DIDFT_AXIS);
    g_info[idx].axisMask = oc.mask;
    DIDEVCAPS caps;
    memset(&caps, 0, sizeof caps);
    caps.dwSize = sizeof caps;
    if (SUCCEEDED(d->GetCapabilities(&caps))) g_info[idx].numButtons = (int)caps.dwButtons;
    d->Acquire();
    g_dev[idx] = d;
    return 0;
}

__declspec(dllexport) int DR_AxisMask(int idx) { return (idx >= 0 && idx < g_n) ? g_info[idx].axisMask : 0; }
__declspec(dllexport) int DR_NumButtons(int idx) { return (idx >= 0 && idx < g_n) ? g_info[idx].numButtons : 0; }

// axes[8] (0..65535), pov[4], buttons[128] (0 / 128). Returns 0 on success.
__declspec(dllexport) int DR_Read(int idx, int* axes, int* pov, unsigned char* buttons)
{
    if (idx < 0 || idx >= g_n || !g_dev[idx]) return -1;
    State176 s;
    memset(&s, 0, sizeof s);
    g_dev[idx]->Poll();
    HRESULT hr = g_dev[idx]->GetDeviceState(sizeof s, &s);
    if (hr == DIERR_INPUTLOST || hr == DIERR_NOTACQUIRED) {
        g_dev[idx]->Acquire();
        hr = g_dev[idx]->GetDeviceState(sizeof s, &s);
    }
    if (FAILED(hr)) return -2;
    for (int i = 0; i < 8; i++) axes[i] = s.ax[i];
    for (int i = 0; i < 4; i++) pov[i] = (int)s.pov[i];
    memcpy(buttons, s.btn, 128);
    return 0;
}

__declspec(dllexport) void DR_CloseAll() { CloseAll(); }

}   // extern "C"
