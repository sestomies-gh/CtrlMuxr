// Proxy core: hooks the vtables of the real DirectInput COM objects and implements device hiding, axis masking,
// mapping of other devices into one device, axis shaping, identity/capability overrides, FFB fixes and logging.
// Config: dinput_proxy.ini next to the DLL (created with defaults if missing); see DEFAULT_INI below.
// Entry points (dinput_entry.cpp ...) call the Core_* functions in core.h.
#define DIRECTINPUT_VERSION 0x0700
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <dinput.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <stdlib.h>
#include <ctype.h>
#include <mmsystem.h>
#include "core.h"

static HINSTANCE g_self;
static CRITICAL_SECTION g_cs;
static FILE* g_log;

// ---------------------------------------------------------------- logging
// Two kinds of lines:
//  - Log():    normal lines. Written only while CtrlMuxr is running (it holds the named mutex APP_MUTEX) or when the INI has
//              [Log] Always=1. Otherwise nothing is formatted or written.
//  - LogErr(): errors. Always kept: in the normal log while logging is on, otherwise in dinput_errors.log (size-limited).
// A game must never be slowed down by logging, whatever it calls per frame (some old game may call something 2000 times a second):
// lines are rate-limited with a token bucket, written into a memory buffer and flushed to disk at most twice a second.
static const char APP_MUTEX[] = "Local\\CtrlMuxr_Running";
static const long LOG_MAX_BYTES = 5L * 1024 * 1024;     // one log file
static const int LOG_KEEP = 5;                          // log files kept in the folder (this run's included)
static const long ERRLOG_MAX_BYTES = 256L * 1024;       // dinput_errors.log is restarted when it reaches this
static const long LOG_RATE = 200, LOG_BURST = 500;      // normal lines per second (sustained) and the burst allowed
static const long ERR_RATE = 20, ERR_BURST = 40;        // the same for error lines
static const DWORD LOG_FLUSH_MS = 500;

static char g_logDir[MAX_PATH];  // folder of the proxy DLL (the logs and the INI are there)
static bool g_logModeInit;
static bool g_logAlways;         // [Log] Always=1: log also when CtrlMuxr is not running
static bool g_logOn;             // normal lines are being logged (app running or Always)
static DWORD g_logModeTick;      // when the state above was last checked (once a second)
static long g_logBytes;          // size of this process's log file
static bool g_logFull;           // the size limit was reached: nothing more is written
static bool g_logDirty;          // written but not yet flushed
static DWORD g_logFlushTick;
static FILE* g_errFile;
static long g_errBytes;
struct Bucket { long milli; DWORD last; DWORD dropped; };   // milli = tokens * 1000
static Bucket g_lineBucket, g_errBucket;
static DWORD g_noteTick;

// Takes one token; false (and a count of the dropped line) when the rate is exceeded.
static bool TakeToken(Bucket& b, long rate, long burst)
{
    DWORD now = GetTickCount();
    DWORD dt = now - b.last;
    if (dt > 10000) dt = 10000;          // long idle (or the first call): the bucket is simply full
    b.last = now;
    b.milli += (long)dt * rate;          // ms * lines/s = thousandths of a line
    if (b.milli > burst * 1000) b.milli = burst * 1000;
    if (b.milli < 1000) { b.dropped++; return false; }
    b.milli -= 1000;
    return true;
}

// Deletes the oldest dinput_*.log files of the folder so that, with the one being opened, LOG_KEEP remain.
static void TrimOldLogs(const char* dir, const char* current)
{
    static char names[256][MAX_PATH]; static FILETIME times[256];
    int n = 0;
    char pat[MAX_PATH + 16];
    _snprintf(pat, sizeof pat, "%s\\dinput_*.log", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (_stricmp(fd.cFileName, current) == 0 || n >= 256) continue;
        {   // only the proxy's own logs: dinput_<digits>.log
            const char* d = fd.cFileName + 7; bool ok = *d != 0;
            for (; *d && *d != '.'; d++) if (*d < '0' || *d > '9') ok = false;
            if (!ok) continue;
        }
        _snprintf(names[n], MAX_PATH, "%s\\%s", dir, fd.cFileName); times[n] = fd.ftLastWriteTime; n++;
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    while (n > LOG_KEEP - 1) {          // the oldest goes first
        int o = 0;
        for (int i = 1; i < n; i++) if (CompareFileTime(&times[i], &times[o]) < 0) o = i;
        DeleteFileA(names[o]);
        strcpy(names[o], names[n - 1]); times[o] = times[n - 1]; n--;
    }
}

// Decides whether normal lines are logged. Looks at the world at most once a second (g_cs held).
static void RefreshLogMode()
{
    DWORD now = GetTickCount();
    if (g_logModeInit && now - g_logModeTick < 1000) return;
    if (!g_logModeInit) {
        g_logModeInit = true;
        GetModuleFileNameA(g_self, g_logDir, MAX_PATH);
        char* s = strrchr(g_logDir, '\\');
        if (s) *s = 0;
        char ini[MAX_PATH + 24];
        _snprintf(ini, sizeof ini, "%s\\dinput_proxy.ini", g_logDir);
        g_logAlways = GetPrivateProfileIntA("Log", "Always", 0, ini) != 0;
    }
    g_logModeTick = now;
    HANDLE m = OpenMutexA(SYNCHRONIZE, FALSE, APP_MUTEX);
    if (m) CloseHandle(m);
    g_logOn = g_logAlways || m != NULL;
}

static void FlushLog(bool force)         // g_cs held
{
    if (!g_log || !g_logDirty) return;
    DWORD now = GetTickCount();
    if (!force && now - g_logFlushTick < LOG_FLUSH_MS) return;
    fflush(g_log);
    g_logDirty = false; g_logFlushTick = now;
}

static void OpenLog()                    // g_cs held
{
    char file[MAX_PATH + 32];
    _snprintf(file, sizeof file, "%s\\dinput_%lu.log", g_logDir, GetCurrentProcessId());
    char mine[48]; _snprintf(mine, sizeof mine, "dinput_%lu.log", GetCurrentProcessId());
    TrimOldLogs(g_logDir, mine);
    g_log = fopen(file, "a");
    if (g_log) {
        setvbuf(g_log, NULL, _IOFBF, 65536);
        fseek(g_log, 0, SEEK_END); g_logBytes = ftell(g_log);
        char exe[MAX_PATH];
        GetModuleFileNameA(NULL, exe, MAX_PATH);
        fprintf(g_log, "[%8lu] dinput_logger started, exe=%s\n", GetTickCount(), exe);
    }
}

// Writes one line to the normal log (g_cs held, no rate limit here).
static void WriteLogV(const char* fmt, va_list ap)
{
    if (g_logFull) return;
    if (!g_log) OpenLog();
    if (!g_log) return;
    int w1 = fprintf(g_log, "[%8lu] ", GetTickCount());
    int w2 = vfprintf(g_log, fmt, ap);
    fputc('\n', g_log);
    g_logBytes += (w1 > 0 ? w1 : 0) + (w2 > 0 ? w2 : 0) + 1;
    g_logDirty = true;
    if (g_logBytes >= LOG_MAX_BYTES) { fprintf(g_log, "[%8lu] log size limit (5 MB) reached: logging stopped\n", GetTickCount()); g_logFull = true; FlushLog(true); return; }
    FlushLog(false);
}

static void WriteLog(const char* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    WriteLogV(fmt, ap);
    va_end(ap);
}

static void Log(const char* fmt, ...)
{
    EnterCriticalSection(&g_cs);
    RefreshLogMode();
    if (g_logOn && !g_logFull && TakeToken(g_lineBucket, LOG_RATE, LOG_BURST)) {
        DWORD now = GetTickCount();
        if (g_lineBucket.dropped && now - g_noteTick >= 1000) {      // tell once a second that lines were left out
            WriteLog("(%lu lines left out: more than %ld per second)", g_lineBucket.dropped, LOG_RATE);
            g_lineBucket.dropped = 0; g_noteTick = now;
        }
        va_list ap;
        va_start(ap, fmt);
        WriteLogV(fmt, ap);
        va_end(ap);
    }
    LeaveCriticalSection(&g_cs);
}

// One line to dinput_errors.log with the time and the game's exe name (g_cs held). Restarted when it gets full.
static void WriteErrFile(const char* msg)
{
    char path[MAX_PATH + 32];
    _snprintf(path, sizeof path, "%s\\dinput_errors.log", g_logDir);
    if (!g_errFile) {
        g_errFile = fopen(path, "a");
        if (!g_errFile) return;
        fseek(g_errFile, 0, SEEK_END); g_errBytes = ftell(g_errFile);
    }
    if (g_errBytes >= ERRLOG_MAX_BYTES) {
        fclose(g_errFile);
        g_errFile = fopen(path, "w");
        if (!g_errFile) return;
        g_errBytes = 0;
        fprintf(g_errFile, "(file restarted: size limit %ld kB)\n", ERRLOG_MAX_BYTES / 1024);
    }
    char exe[MAX_PATH];
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    const char* base = strrchr(exe, '\\');
    base = base ? base + 1 : exe;
    SYSTEMTIME t;
    GetLocalTime(&t);
    int w = fprintf(g_errFile, "%04d-%02d-%02d %02d:%02d:%02d %s pid=%lu  %s\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond,
                    base, GetCurrentProcessId(), msg);
    if (w > 0) g_errBytes += w;
    fflush(g_errFile);
}

// A kept line (g_cs held, logging mode refreshed): to the normal log when that is on (errors marked ERROR), else to dinput_errors.log.
static void PutKept(bool err, const char* msg)
{
    if (g_logOn && !g_logFull) WriteLog(err ? "ERROR %s" : "%s", msg);
    else WriteErrFile(msg);
}

// Errors and the recoveries that follow them are always kept (LogErr / LogEvt), but a game that repeats one every frame must
// not flood anything: identical consecutive messages are counted (a summary line comes when the message changes and every
// 10 s) and the rate is limited.
static void KeepLine(bool err, const char* buf)
{
    static char last[512];
    static bool lastErr;
    static DWORD rep, repTick;
    EnterCriticalSection(&g_cs);
    RefreshLogMode();
    DWORD now = GetTickCount();
    if (strcmp(buf, last) == 0) {
        rep++;
        if (now - repTick >= 10000) {
            char note[600]; _snprintf(note, sizeof note, "%s  (repeated %lu more times)", last, rep);
            note[sizeof note - 1] = 0;
            PutKept(lastErr, note); rep = 0; repTick = now;
        }
    } else if (TakeToken(g_errBucket, ERR_RATE, ERR_BURST)) {
        if (rep) { char note[600]; _snprintf(note, sizeof note, "%s  (repeated %lu more times)", last, rep); note[sizeof note - 1] = 0; PutKept(lastErr, note); }
        PutKept(err, buf);
        strcpy(last, buf); lastErr = err; rep = 0; repTick = now;
    }
    LeaveCriticalSection(&g_cs);
}

static void LogErr(const char* fmt, ...)       // a problem
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf(buf, sizeof buf, fmt, ap);
    buf[sizeof buf - 1] = 0;
    va_end(ap);
    KeepLine(true, buf);
}

static void LogEvt(const char* fmt, ...)       // the problem is over (device back, Acquire works again): kept with the errors
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf(buf, sizeof buf, fmt, ap);
    buf[sizeof buf - 1] = 0;
    va_end(ap);
    KeepLine(false, buf);
}

// Called from the polling hooks (g_cs held): flushes lines that are still in the buffer, so the app's Log tab stays current.
static void LogTick() { FlushLog(false); }

// The INI may have changed while the game runs: [Log] Always (g_cs held).
static void ReloadLogMode(const char* ini)
{
    g_logAlways = GetPrivateProfileIntA("Log", "Always", 0, ini) != 0;
    g_logModeTick = GetTickCount() - 1000;      // look at the app again on the next line
}

static void GuidStr(const GUID& g, char* out)
{
    sprintf(out, "%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X", g.Data1, g.Data2, g.Data3,
            g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
}

// ---------------------------------------------------------------- vtable patching
struct Patch { void** vt; int slot; void* orig; };
static Patch g_patch[128];
static int g_nPatch;

static void* Orig(void* obj, int slot)
{
    void** vt = *(void***)obj;
    for (int i = 0; i < g_nPatch; i++)
        if (g_patch[i].vt == vt && g_patch[i].slot == slot) return g_patch[i].orig;
    return NULL;
}

static void PatchSlot(void* obj, int slot, void* hook)
{
    void** vt = *(void***)obj;
    EnterCriticalSection(&g_cs);
    for (int i = 0; i < g_nPatch; i++)
        if (g_patch[i].vt == vt && g_patch[i].slot == slot) { LeaveCriticalSection(&g_cs); return; }
    if (g_nPatch < 128) {
        DWORD old;
        if (VirtualProtect(&vt[slot], sizeof(void*), PAGE_READWRITE, &old)) {
            g_patch[g_nPatch].vt = vt;
            g_patch[g_nPatch].slot = slot;
            g_patch[g_nPatch].orig = vt[slot];
            g_nPatch++;
            vt[slot] = hook;
            VirtualProtect(&vt[slot], sizeof(void*), old, &old);
        }
    }
    LeaveCriticalSection(&g_cs);
}

// ---------------------------------------------------------------- device records
struct Rec {
    void* dev;
    bool hasMap;            // this device is the destination of [AxisMap]/[ButtonMap] entries
    BYTE lastout[512];
    DWORD lastoutcb;
    DWORD lastoutlog;
    char name[80];
    DWORD n, lastcb, lastlog, dataCalls;
    BYTE last[256];
    DWORD vid, pid;
    unsigned maskBits;      // bit i = axis i overridden (X Y Z Rx Ry Rz S0 S1 = DIJOYSTATE longs 0..7)
    LONG neutral[8];
    LONG raw[8], rmin[8], rmax[8];
    bool seen;
    // layout of the state buffer the game selected with SetDataFormat (offsets into the GetDeviceState buffer, -1 = absent)
    bool fmtKnown;
    DWORD fmtSize;
    int axOfs[8];           // X Y Z Rx Ry Rz S0 S1
    int btnOfs[128];
    LONG axMin[8], axMax[8];    // axis ranges the game selected with DIPROP_RANGE (default 0..65535)
    DWORD devType, capsFlags, gameDz;   // device type / caps flags reported to the game, deadzone (0..10000) the game asked for
    HWND coopHwnd; DWORD coopFlags;     // last SetCooperativeLevel the game made on this device (flags as in effect), for the Acquire fallback
    bool coopFallback;                  // the device runs on the non-exclusive fallback level (no force feedback); TryUpgrade may restore exclusive
    DWORD coopWanted, nextUpgrade;      // the game's own level; GetTickCount() of the next upgrade attempt
    int upgradeTries;
    bool acqSeen; DWORD acqHr, acqRep;  // last Acquire result logged for this device and how many identical ones followed (log rate limit)
    DWORD seq;                          // creation order of the object this row tracks (RecycleRec reuses the oldest row of a device, never the newest)
};
#define MAX_REC 64                      // a game that recreates all its device objects on every device change makes 40+ per change
static Rec g_rec[MAX_REC];
static int g_nRec;
static DWORD g_recSeq;

static Rec* FindRec(void* dev)
{
    for (int i = 0; i < g_nRec; i++) if (g_rec[i].dev == dev) return &g_rec[i];
    return NULL;
}

static const char* DevName(void* dev)
{
    Rec* r = FindRec(dev);
    return r ? r->name : "?";
}

// ---------------------------------------------------------------- config / hotkey
static const char* const AXIS_NAMES[8] = { "X", "Y", "Z", "Rx", "Ry", "Rz", "S0", "S1" };
struct MaskEntry { DWORD vid, pid; unsigned bits; };
static MaskEntry g_mask[16];
static int g_nMask;
static DWORD g_hide[16][2];
static int g_nHide;
struct TypeSpoof { DWORD vid, pid, type; };   // [DevType] VID:PID=hex: report this DIDEVICEINSTANCE.dwDevType
static TypeSpoof g_typeSpoof[8];
static int g_nTypeSpoof;
static bool g_wide;                     // game uses the W (unicode) DirectInput interfaces: device structs are DIDEVICEINSTANCEW etc.
static bool g_di8;                      // game uses DirectInput 8: devices carry the full vtable incl. FFB slots
static LONG g_ffBoost = 100;            // [FFB] Boost percent
static bool g_blockSine;                // [FFB] BlockSine: periodic (sine) effects are never started (some wheels play them as a pull to one side; read at start)
static bool g_gainAsMag;                // [FFB] GainAsMagnitude: strength sent as magnitude instead of per-effect gain (read at start)
static bool g_ignoreDz;                 // [Caps] IgnoreDeadzone=1: drop DIPROP_DEADZONE the game sets (use [Shape] dz= instead)
static bool g_autoCoop = true;          // [Caps] AutoCoop=0 switches the Acquire fallback off (see H_Acquire)
static bool g_nonExcl;                  // [Caps] NonExclusive=1: joystick cooperative level -> NONEXCLUSIVE|BACKGROUND (Acquire E_ACCESSDENIED workaround; disables FFB)
static DWORD g_clearFlags;              // [Caps] ClearFlags (hex): DIDEVCAPS.dwFlags bits to hide from the game, e.g. 8 = DIDC_POLLEDDATAFORMAT
static DWORD g_maxButtons;              // [Caps] MaxButtons: clamp the button count GetCapabilities reports (0 = off)
static char g_ini[MAX_PATH];
static bool g_cfgLoaded;
static UINT g_hkVk;
static unsigned g_hkMods;               // 1 = ctrl, 2 = alt, 4 = shift
static bool g_hkDown;
static DWORD g_hkLastCheck;

static int AxisIndex(const char* s)
{
    for (int i = 0; i < 8; i++) if (_stricmp(s, AXIS_NAMES[i]) == 0) return i;
    return -1;
}

static void ParseHotkey(const char* s)
{
    char buf[64];
    _snprintf(buf, sizeof buf, "%s", s);
    g_hkVk = 0; g_hkMods = 0;
    for (char* t = strtok(buf, "+ "); t; t = strtok(NULL, "+ ")) {
        if (!_stricmp(t, "CTRL")) g_hkMods |= 1;
        else if (!_stricmp(t, "ALT")) g_hkMods |= 2;
        else if (!_stricmp(t, "SHIFT")) g_hkMods |= 4;
        else if ((t[0] == 'F' || t[0] == 'f') && atoi(t + 1) >= 1 && atoi(t + 1) <= 24) g_hkVk = VK_F1 + atoi(t + 1) - 1;
        else if (strlen(t) == 1) g_hkVk = (UINT)toupper((unsigned char)t[0]);
    }
}

static const char DEFAULT_INI[] =
    "; dinput_proxy.ini\r\n"
    "; [Mask]: VID:PID = axes to override (X Y Z Rx Ry Rz S0 S1). Overridden axes always report their neutral value.\r\n"
    "; [Neutral]: VID:PID.Axis = value (0..65535, default 32767). Written by the Learn hotkey, may be edited by hand.\r\n"
    "; [Hide]: VID:PID= devices hidden from the game's enumeration (the proxy can still read them for mapping).\r\n"
    "; [AxisMap]: destination = source [invert]   e.g. 346E:0002.Y=DDFD:5008.X   (axes X Y Z Rx Ry Rz S0 S1)\r\n"
    "; [ButtonMap]: destination = source          e.g. 346E:0002.B29=0079:0006.B11   (buttons B1..B32; several sources separated by spaces: any of them presses the button)\r\n"
    "[Hotkey]\r\n"
    "Learn=CTRL+ALT+F12\r\n"
    "[Mask]\r\n"
    "346E:0002=Y Rx Ry S1\r\n"
    "[Neutral]\r\n"
    "[Hide]\r\n"
    "0079:0006=\r\n"
    "DDFD:5008=\r\n"
    "[AxisMap]\r\n"
    "346E:0002.Y=DDFD:5008.X\r\n"
    "346E:0002.Rx=DDFD:5008.Y\r\n"
    "346E:0002.Ry=DDFD:5008.Z\r\n"
    "346E:0002.S1=DDFD:5008.Rx\r\n"
    "[ButtonMap]\r\n"
    "346E:0002.B1=0079:0006.B11\r\n"
    "346E:0002.B2=0079:0006.B10\r\n"
    "346E:0002.B3=0079:0006.B7\r\n"
    "[AxisButton]\r\n"
    "346E:0002.B4=DDFD:5008.Rx 20000\r\n";

// ---- mapping: read other (hidden) devices through our own DirectInput objects and write them into a destination's state
static Core_SourceFactory g_srcFactory;
static HWND g_hwnd;                     // game window, captured from SetCooperativeLevel

struct AMap { DWORD dvid, dpid; int dax; int src; int sax; bool inv; int half; };   // half: 0 = full axis, 1 = rest at centre, up to max; 2 = rest at centre, down to min
struct BMap { DWORD dvid, dpid; int dbtn; int src; int sbtn; };
struct Src { DWORD vid, pid; void* dev; bool ok, gone, lost; DWORD nextTry, nextRead; DIJOYSTATE st; BYTE prev[80]; DWORD lastlog; };
struct ABMap { DWORD dvid, dpid; int dbtn; int src; int sax; LONG thr; bool inv; };   // axis -> button by threshold
static ABMap g_abmap[16]; static int g_nAbmap;
static AMap g_amap[16]; static int g_nAmap;
static const int MAX_BMAP = 48;
static BMap g_bmap[MAX_BMAP]; static int g_nBmap;      // one entry per source: a game button can have several sources (any of them presses it)
static const int MAX_SRC = 8;           // source devices besides the wheel (9 devices in all); one more is reported as a bad config line
static Src g_src[MAX_SRC]; static int g_nSrc;

static int SrcIndex(DWORD vid, DWORD pid)
{
    for (int i = 0; i < g_nSrc; i++) if (g_src[i].vid == vid && g_src[i].pid == pid) return i;
    if (g_nSrc >= MAX_SRC) return -1;
    memset(&g_src[g_nSrc], 0, sizeof g_src[0]);
    g_src[g_nSrc].vid = vid; g_src[g_nSrc].pid = pid;
    return g_nSrc++;
}

static bool ParseRef(const char* s, DWORD* vid, DWORD* pid, char* tok)
{
    unsigned v, p;
    if (sscanf(s, " %x:%x.%15[A-Za-z0-9]", &v, &p, tok) != 3) return false;
    *vid = v; *pid = p;
    return true;
}

static void ParseMaps()
{
    static char sec[4096];
    GetPrivateProfileSectionA("AxisMap", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nAmap < 16; p += strlen(p) + 1) {
        const char* eq = strchr(p, '=');
        DWORD dv, dp, sv, sp;
        char dt[16], st[16], o1[16] = "", o2[16] = "";
        if (p[0] == ';' || !eq || !ParseRef(p, &dv, &dp, dt) || !ParseRef(eq + 1, &sv, &sp, st)) continue;
        sscanf(strchr(eq + 1, '.'), ".%*[A-Za-z0-9] %15s %15s", o1, o2);
        int da = AxisIndex(dt), sa = AxisIndex(st), si = SrcIndex(sv, sp);
        if (da < 0 || sa < 0 || si < 0) { LogErr("Config: bad AxisMap line \"%s\"", p); continue; }
        AMap& m = g_amap[g_nAmap++];
        m.dvid = dv; m.dpid = dp; m.dax = da; m.src = si; m.sax = sa; m.inv = false; m.half = 0;
        const char* opts[2] = { o1, o2 };
        for (int k = 0; k < 2; k++) {
            const char* o = opts[k];
            if (!_stricmp(o, "invert")) m.inv = true;
            else if (!_stricmp(o, "half")) m.half = 1;
            else if (!_stricmp(o, "halfneg")) m.half = 2;
            else if (!_stricmp(o, "splithi")) m.half = 3;
            else if (!_stricmp(o, "splitlo")) m.half = 4;
        }
        Log("Config: axis %04lX:%04lX.%s <- %04lX:%04lX.%s%s%s", dv, dp, dt, sv, sp, st, m.inv ? " (inverted)" : "",
            m.half == 1 ? " (half: centre..max)" : m.half == 2 ? " (half: centre..min)" : m.half == 3 ? " (split: upper half)" : m.half == 4 ? " (split: lower half)" : "");
    }
    GetPrivateProfileSectionA("ButtonMap", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nBmap < MAX_BMAP; p += strlen(p) + 1) {
        const char* eq = strchr(p, '=');
        DWORD dv, dp;
        char dt[16];
        if (p[0] == ';' || !eq || !ParseRef(p, &dv, &dp, dt)) continue;
        int db = (dt[0] == 'B' || dt[0] == 'b') ? atoi(dt + 1) - 1 : -1;   // the INI numbers buttons from 1 (B1..B32), internally 0..31
        if (db < 0 || db > 31) { LogErr("Config: bad ButtonMap line \"%s\"", p); continue; }
        // one or more sources, separated by spaces: the game button is pressed while any of them is
        for (const char* q = eq + 1; *q; ) {
            while (*q == ' ' || *q == '\t') q++;
            if (!*q) break;
            const char* e = q;
            while (*e && *e != ' ' && *e != '\t') e++;
            DWORD sv, sp;
            char st[16];
            int sb = -1, si = -1;
            if (ParseRef(q, &sv, &sp, st)) { sb = (st[0] == 'B' || st[0] == 'b') ? atoi(st + 1) - 1 : -1; si = SrcIndex(sv, sp); }
            if (sb < 0 || sb > 31 || si < 0 || g_nBmap >= MAX_BMAP) LogErr("Config: bad ButtonMap source in \"%s\"", p);
            else {
                BMap& m = g_bmap[g_nBmap++];
                m.dvid = dv; m.dpid = dp; m.dbtn = db; m.src = si; m.sbtn = sb;
                Log("Config: button %04lX:%04lX.B%d <- %04lX:%04lX.B%d", dv, dp, db + 1, sv, sp, sb + 1);
            }
            q = e;
        }
    }
    // [AxisButton]: dest button = source axis [threshold] [invert]; pressed when axis >= threshold (invert: <=)
    GetPrivateProfileSectionA("AxisButton", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nAbmap < 16; p += strlen(p) + 1) {
        const char* eq = strchr(p, '=');
        DWORD dv, dp, sv, sp;
        char dt[16], st[16], inv[16] = "";
        if (p[0] == ';' || !eq || !ParseRef(p, &dv, &dp, dt) || !ParseRef(eq + 1, &sv, &sp, st)) continue;
        long thr = 32768;
        sscanf(strchr(eq + 1, '.'), ".%*[A-Za-z0-9] %ld %15s", &thr, inv);
        if (inv[0] == 0) sscanf(strchr(eq + 1, '.'), ".%*[A-Za-z0-9] %15s", inv);   // "invert" without a threshold
        int db = (dt[0] == 'B' || dt[0] == 'b') ? atoi(dt + 1) - 1 : -1;   // the INI numbers buttons from 1 (B1..B32), internally 0..31
        int sa = AxisIndex(st), si = SrcIndex(sv, sp);
        if (db < 0 || db > 31 || sa < 0 || si < 0) { LogErr("Config: bad AxisButton line \"%s\"", p); continue; }
        ABMap& m = g_abmap[g_nAbmap++];
        m.dvid = dv; m.dpid = dp; m.dbtn = db; m.src = si; m.sax = sa; m.thr = thr; m.inv = !_stricmp(inv, "invert");
        Log("Config: button %04lX:%04lX.B%d <- axis %04lX:%04lX.%s %s %ld", dv, dp, db + 1, sv, sp, st, m.inv ? "<=" : ">=", thr);
    }
}

// [Shape]: VID:PID.Axis = invert dz=% expo=% gain=% adz=% center=raw
//   dz   own deadzone around center (input inside is ignored, the rest is rescaled)
//   expo blend towards a cubic curve (0 = linear, 100 = pure cubic): softer around center
//   gain output multiplier, clamped to full range
//   adz  anti-deadzone: compensates a deadzone built into the game (output jumps past it as soon as there is input)
struct Shape { DWORD vid, pid; int ax; double dz, expo, gain, adz, center; bool inv;
               double lead, prevN, prevT, vel; };   // lead: seconds of velocity feed-forward (cancels a game's input smoothing)
static Shape g_shape[16]; static int g_nShape;

static void ParseShape()
{
    static char sec[4096];
    GetPrivateProfileSectionA("Shape", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nShape < 16; p += strlen(p) + 1) {
        const char* eq = strchr(p, '=');
        DWORD dv, dp;
        char dt[16];
        if (p[0] == ';' || !eq || !ParseRef(p, &dv, &dp, dt)) continue;
        int ax = AxisIndex(dt);
        if (ax < 0) { LogErr("Config: bad Shape line \"%s\"", p); continue; }
        Shape& s = g_shape[g_nShape];
        s.vid = dv; s.pid = dp; s.ax = ax; s.dz = s.expo = s.adz = 0; s.gain = 1; s.center = 32767.5; s.inv = false;
        s.lead = s.prevN = s.prevT = s.vel = 0;
        char val[200];
        _snprintf(val, sizeof val, "%s", eq + 1);
        for (char* t = strtok(val, " ,"); t; t = strtok(NULL, " ,")) {
            char* v = strchr(t, '=');
            double x = v ? atof(v + 1) : 0;
            if (!_stricmp(t, "invert")) s.inv = true;
            else if (!_strnicmp(t, "dz=", 3)) s.dz = x / 100;
            else if (!_strnicmp(t, "expo=", 5)) s.expo = x / 100;
            else if (!_strnicmp(t, "gain=", 5)) s.gain = x / 100;
            else if (!_strnicmp(t, "adz=", 4)) s.adz = x / 100;
            else if (!_strnicmp(t, "center=", 7)) s.center = x;
            else if (!_strnicmp(t, "lead=", 5)) s.lead = x;
            else LogErr("Config: unknown Shape option \"%s\"", t);
        }
        g_nShape++;
        Log("Config: shape %04lX:%04lX.%s inv=%d dz=%.1f%% expo=%.1f%% gain=%.1f%% adz=%.1f%% center=%.1f lead=%.3fs", dv, dp, dt, s.inv,
            s.dz * 100, s.expo * 100, s.gain * 100, s.adz * 100, s.center, s.lead);
    }
}

// [Identity]: VID:PID = newVID:newPID Display name   -> what the game sees in DIDEVICEINSTANCE (name, product GUID)
struct IdSpoof { DWORD vid, pid, nvid, npid; char name[64]; };
static IdSpoof g_id[8]; static int g_nId;

// [AxisCombine]: dest axis = gasSrc.axis brakeSrc.axis [invert]  -> one axis, gas on one half, brake on the other
struct CMap { DWORD dvid, dpid; int dax; int src1, ax1, src2, ax2; bool inv; };
static CMap g_cmap[8]; static int g_nCmap;

static void ParseIdentity()
{
    static char sec[2048];
    GetPrivateProfileSectionA("Identity", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nId < 8; p += strlen(p) + 1) {
        unsigned v, pd, nv, np;
        int used = 0;
        if (p[0] == ';' || sscanf(p, "%x:%x=%x:%x %n", &v, &pd, &nv, &np, &used) < 4 || !used) continue;
        IdSpoof& s = g_id[g_nId++];
        s.vid = v; s.pid = pd; s.nvid = nv; s.npid = np;
        _snprintf(s.name, sizeof s.name, "%s", p + used);
        Log("Config: identity %04X:%04X -> %04X:%04X \"%s\"", v, pd, nv, np, s.name);
    }
}

static bool ApplyIdentity(DIDEVICEINSTANCEA* d, DWORD vid, DWORD pid)
{
    for (int i = 0; i < g_nId; i++) {
        if (g_id[i].vid != vid || g_id[i].pid != pid) continue;
        d->guidProduct.Data1 = (g_id[i].npid << 16) | (g_id[i].nvid & 0xFFFF);
        if (g_id[i].name[0]) {
            _snprintf(d->tszProductName, sizeof d->tszProductName, "%s", g_id[i].name);
            _snprintf(d->tszInstanceName, sizeof d->tszInstanceName, "%s", g_id[i].name);
        }
        return true;
    }
    return false;
}

static bool ApplyIdentityW(DIDEVICEINSTANCEW* d, DWORD vid, DWORD pid)
{
    for (int i = 0; i < g_nId; i++) {
        if (g_id[i].vid != vid || g_id[i].pid != pid) continue;
        d->guidProduct.Data1 = (g_id[i].npid << 16) | (g_id[i].nvid & 0xFFFF);
        if (g_id[i].name[0]) {
            MultiByteToWideChar(CP_ACP, 0, g_id[i].name, -1, d->tszProductName, MAX_PATH);
            MultiByteToWideChar(CP_ACP, 0, g_id[i].name, -1, d->tszInstanceName, MAX_PATH);
        }
        return true;
    }
    return false;
}

static void ParseCombine()
{
    static char sec[2048];
    GetPrivateProfileSectionA("AxisCombine", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nCmap < 8; p += strlen(p) + 1) {
        const char* eq = strchr(p, '=');
        DWORD dv, dp;
        char dt[16];
        unsigned v1, p1, v2, p2;
        char t1[16], t2[16], inv[16] = "";
        if (p[0] == ';' || !eq || !ParseRef(p, &dv, &dp, dt)) continue;
        if (sscanf(eq + 1, " %x:%x.%15[A-Za-z0-9] %x:%x.%15[A-Za-z0-9] %15s", &v1, &p1, t1, &v2, &p2, t2, inv) < 6) continue;
        int da = AxisIndex(dt), a1 = AxisIndex(t1), a2 = AxisIndex(t2), s1 = SrcIndex(v1, p1), s2 = SrcIndex(v2, p2);
        if (da < 0 || a1 < 0 || a2 < 0 || s1 < 0 || s2 < 0) { LogErr("Config: bad AxisCombine line \"%s\"", p); continue; }
        CMap& m = g_cmap[g_nCmap++];
        m.dvid = dv; m.dpid = dp; m.dax = da; m.src1 = s1; m.ax1 = a1; m.src2 = s2; m.ax2 = a2; m.inv = !_stricmp(inv, "invert");
        Log("Config: axis %04lX:%04lX.%s <- combine(%04X:%04X.%s, %04X:%04X.%s)%s", dv, dp, dt, v1, p1, t1, v2, p2, t2, m.inv ? " inverted" : "");
    }
}

static LONG ShapeValue(Shape& s, LONG v)
{
    double n = (v - s.center) / 32767.5;
    if (n > 1) n = 1; else if (n < -1) n = -1;
    if (s.inv) n = -n;
    if (s.lead > 0) {                              // n += lead * velocity (smoothed), clamped
        LARGE_INTEGER q, f;
        QueryPerformanceCounter(&q);
        QueryPerformanceFrequency(&f);
        double t = (double)q.QuadPart / (double)f.QuadPart, dt = t - s.prevT;
        if (s.prevT > 0 && dt > 0.0005 && dt < 0.25) s.vel = 0.5 * s.vel + 0.5 * ((n - s.prevN) / dt);
        else if (dt >= 0.25) s.vel = 0;
        if (dt > 0.0005 || s.prevT == 0) { s.prevN = n; s.prevT = t; }
        n += s.lead * s.vel;
        if (n > 1) n = 1; else if (n < -1) n = -1;
    }
    double sg = n < 0 ? -1 : 1, a = n < 0 ? -n : n;
    if (s.dz > 0) a = a < s.dz ? 0 : (a - s.dz) / (1 - s.dz);
    if (s.expo > 0) a = (1 - s.expo) * a + s.expo * a * a * a;
    a *= s.gain;
    if (a > 1) a = 1;
    if (s.adz > 0 && a > 0) a = s.adz + a * (1 - s.adz);
    double o = s.center + sg * a * 32767.5;
    return (LONG)(o < 0 ? 0 : o > 65535 ? 65535 : o + 0.5);
}

static bool HasMapFor(DWORD vid, DWORD pid)
{
    for (int i = 0; i < g_nShape; i++) if (g_shape[i].vid == vid && g_shape[i].pid == pid) return true;
    for (int i = 0; i < g_nCmap; i++) if (g_cmap[i].dvid == vid && g_cmap[i].dpid == pid) return true;
    for (int i = 0; i < g_nAmap; i++) if (g_amap[i].dvid == vid && g_amap[i].dpid == pid) return true;
    for (int i = 0; i < g_nBmap; i++) if (g_bmap[i].dvid == vid && g_bmap[i].dpid == pid) return true;
    for (int i = 0; i < g_nAbmap; i++) if (g_abmap[i].dvid == vid && g_abmap[i].dpid == pid) return true;
    return false;
}

static void* Slot(void* obj, int slot)    // original (unhooked) vtable function
{
    void* o = Orig(obj, slot);
    return o ? o : (*(void***)obj)[slot];
}

#ifndef DIDFT_OPTIONAL
#define DIDFT_OPTIONAL 0x80000000      // DX8 flag; matches the game's own format in the log (type=80FFFF03)
#endif
static DIOBJECTDATAFORMAT g_odf[44];
static DIDATAFORMAT g_df;

static void BuildFormat()                 // standard DIJOYSTATE layout, same as the game's own format
{
    if (g_df.dwSize) return;
    const GUID* ax[6] = { &GUID_XAxis, &GUID_YAxis, &GUID_ZAxis, &GUID_RxAxis, &GUID_RyAxis, &GUID_RzAxis };
    int n = 0;
    for (int i = 0; i < 6; i++, n++) { g_odf[n].pguid = ax[i]; g_odf[n].dwOfs = i * 4; g_odf[n].dwType = DIDFT_OPTIONAL | DIDFT_AXIS | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = DIDOI_ASPECTPOSITION; }
    for (int i = 0; i < 2; i++, n++) { g_odf[n].pguid = &GUID_Slider; g_odf[n].dwOfs = 24 + i * 4; g_odf[n].dwType = DIDFT_OPTIONAL | DIDFT_AXIS | DIDFT_ANYINSTANCE; g_odf[n].dwFlags = DIDOI_ASPECTPOSITION; }
    for (int i = 0; i < 4; i++, n++) { g_odf[n].pguid = &GUID_POV; g_odf[n].dwOfs = 32 + i * 4; g_odf[n].dwType = DIDFT_OPTIONAL | DIDFT_POV | DIDFT_ANYINSTANCE; }
    for (int i = 0; i < 32; i++, n++) { g_odf[n].pguid = NULL; g_odf[n].dwOfs = 48 + i; g_odf[n].dwType = DIDFT_OPTIONAL | DIDFT_BUTTON | DIDFT_ANYINSTANCE; }
    g_df.dwSize = sizeof(DIDATAFORMAT); g_df.dwObjSize = sizeof(DIOBJECTDATAFORMAT);
    g_df.dwFlags = DIDF_ABSAXIS; g_df.dwDataSize = sizeof(DIJOYSTATE); g_df.dwNumObjs = 44; g_df.rgodf = g_odf;
}

struct FindCtx { DWORD vid, pid; GUID g; bool found; };
static BOOL CALLBACK FindCb(const DIDEVICEINSTANCEA* d, void* ref)
{
    FindCtx* c = (FindCtx*)ref;
    if ((d->guidProduct.Data1 & 0xFFFF) == c->vid && (d->guidProduct.Data1 >> 16) == c->pid) {
        c->g = d->guidInstance; c->found = true;
        return DIENUM_STOP;
    }
    return DIENUM_CONTINUE;
}

typedef HRESULT (WINAPI *SFnEnum)(void*, DWORD, LPDIENUMDEVICESCALLBACKA, void*, DWORD);
typedef HRESULT (WINAPI *SFnCreateDev)(void*, const GUID*, void**, IUnknown*);
typedef HRESULT (WINAPI *SFnSetFmt)(void*, const DIDATAFORMAT*);
typedef HRESULT (WINAPI *SFnSetCoop)(void*, HWND, DWORD);
typedef HRESULT (WINAPI *SFnAcquire)(void*);
typedef HRESULT (WINAPI *SFnGetState)(void*, DWORD, void*);

struct WinFind { DWORD pid; HWND h; };
static BOOL CALLBACK EnumWinCb(HWND h, LPARAM lp)
{
    WinFind* f = (WinFind*)lp;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == f->pid && IsWindowVisible(h) && GetWindow(h, GW_OWNER) == NULL) { f->h = h; return FALSE; }
    return TRUE;
}

static HWND FindGameWindow()
{
    WinFind f = { GetCurrentProcessId(), NULL };
    EnumWindows(EnumWinCb, (LPARAM)&f);
    return f.h;
}

static void InitSource(Src* s)            // g_cs held; retried every 3 s on failure (not when the device is missing: s->gone)
{
    s->nextTry = GetTickCount() + 3000;
    if (!g_hwnd) g_hwnd = FindGameWindow();     // winmm-only games never call DirectInput's SetCooperativeLevel
    if (!g_srcFactory || !g_hwnd) return;
    void* di = NULL;
    if (FAILED(g_srcFactory(&di)) || !di) { LogErr("Source %04lX:%04lX: DirectInputCreate failed", s->vid, s->pid); return; }
    FindCtx fc = { s->vid, s->pid };
    ((SFnEnum)Slot(di, 4))(di, DIDEVTYPE_JOYSTICK, FindCb, &fc, DIEDFL_ATTACHEDONLY);
    if (!fc.found) {        // not connected when the game started: never poll again (every attempt enumerates all devices under g_cs and stalls the game's input call)
        s->gone = true;
        LogErr("Source %04lX:%04lX: device not connected at start - not polled again (connect it and restart the game)", s->vid, s->pid);
        return;
    }
    void* dev = NULL;
    if (FAILED(((SFnCreateDev)Slot(di, 3))(di, &fc.g, &dev, NULL)) || !dev) { LogErr("Source %04lX:%04lX: CreateDevice failed", s->vid, s->pid); return; }
    BuildFormat();
    HRESULT h1 = ((SFnSetFmt)Slot(dev, 11))(dev, &g_df);
    HRESULT h2 = ((SFnSetCoop)Slot(dev, 13))(dev, g_hwnd, DISCL_NONEXCLUSIVE | DISCL_BACKGROUND);
    HRESULT h3 = ((SFnAcquire)Slot(dev, 7))(dev);
    Log("Source %04lX:%04lX opened: SetDataFormat=%lX SetCoop=%lX Acquire=%lX", s->vid, s->pid, (unsigned long)h1, (unsigned long)h2, (unsigned long)h3);
    if (FAILED(h1)) return;
    // Right after Acquire a HID device reports a centered default (all axes 32767) until its first report arrives.
    // The game takes its first read as the axis baseline, so wait for real data before the first state is returned.
    DIJOYSTATE warm;
    for (int i = 0; i < 6; i++) { Sleep(10); ((SFnGetState)Slot(dev, 9))(dev, sizeof warm, &warm); }
    s->dev = dev;
    s->ok = true;
}

static void ReadSource(Src* s)            // g_cs held
{
    if (!s->ok) {
        if (s->gone || GetTickCount() < s->nextTry) return;
        InitSource(s);
        if (!s->ok) return;
    }
    DWORD now = GetTickCount();
    if (s->lost && (LONG)(now - s->nextRead) < 0) return;      // device unplugged: retry only every 250 ms, keep the last (button-cleared) state
    SFnGetState gs = (SFnGetState)Slot(s->dev, 9);
    HRESULT hr = gs(s->dev, sizeof(DIJOYSTATE), &s->st);
    if (FAILED(hr)) {
        ((SFnAcquire)Slot(s->dev, 7))(s->dev);
        hr = gs(s->dev, sizeof(DIJOYSTATE), &s->st);
    }
    if (FAILED(hr)) {
        if (!s->lost) {
            s->lost = true;
            memset(s->st.rgbButtons, 0, sizeof s->st.rgbButtons);   // a button held at unplug time must not stay pressed
            LogErr("Source %04lX:%04lX: lost (read failed hr=%lX) - retrying every 250 ms", s->vid, s->pid, (unsigned long)hr);
        }
        s->nextRead = now + 250;
    } else if (s->lost) {
        s->lost = false;
        LogEvt("Source %04lX:%04lX: back", s->vid, s->pid);
    }
    if (memcmp(s->prev, &s->st, 80) != 0 && now - s->lastlog >= 250) {   // log source changes (rate-limited)
        char line[400];
        int p = _snprintf(line, sizeof line, "Src %04lX:%04lX dw:", s->vid, s->pid);
        const LONG* a = (const LONG*)&s->st;
        for (int i = 0; i < 8; i++) p += _snprintf(line + p, sizeof line - p, " %ld", a[i]);
        p += _snprintf(line + p, sizeof line - p, " | btn:");
        for (int i = 0; i < 32; i++)
            if (s->st.rgbButtons[i] & 0x80) p += _snprintf(line + p, sizeof line - p, " B%d", i + 1);
        Log("%s", line);
        memcpy(s->prev, &s->st, 80);
        s->lastlog = now;
    }
}

static LONG* AxisPtr(const Rec* r, BYTE* data, int a)
{
    int o = r->axOfs[a];
    return (o >= 0 && (DWORD)o + 4 <= r->fmtSize) ? (LONG*)(data + o) : NULL;
}

static BYTE* ButtonPtr(const Rec* r, BYTE* data, int b)
{
    int o = (b >= 0 && b < 128) ? r->btnOfs[b] : -1;
    return (o >= 0 && (DWORD)o < r->fmtSize) ? data + o : NULL;
}

// Axis values are processed in the common 0..65535 domain; the game may have selected another range (DIPROP_RANGE).
static LONG ToCommon(const Rec* r, int a, LONG v)
{
    if (r->axMin[a] == 0 && r->axMax[a] == 65535) return v;
    double span = (double)r->axMax[a] - (double)r->axMin[a];     // double: a +-2^30 range overflows LONG
    if (span == 0) return 32767;
    double c = ((double)v - (double)r->axMin[a]) * 65535.0 / span;
    return (LONG)(c < 0 ? 0 : c > 65535 ? 65535 : c + 0.5);
}

static LONG FromCommon(const Rec* r, int a, LONG c)
{
    if (r->axMin[a] == 0 && r->axMax[a] == 65535) return c;
    double span = (double)r->axMax[a] - (double)r->axMin[a];
    return (LONG)((double)r->axMin[a] + (double)c * span / 65535.0 + (span >= 0 ? 0.5 : -0.5));
}

// Applies all mappings of destination device r to its state buffer, using the offsets of the game's own data format.
static void ApplyMaps(const Rec* r, BYTE* data)    // g_cs held
{
    bool done[MAX_SRC] = { false };
    for (int i = 0; i < g_nAmap; i++) {
        const AMap& m = g_amap[i];
        if (m.dvid != r->vid || m.dpid != r->pid) continue;
        if (!done[m.src]) { ReadSource(&g_src[m.src]); done[m.src] = true; }
        LONG v = ((const LONG*)&g_src[m.src].st)[m.sax];
        if (m.inv) v = 65535 - v;
        if (m.half == 1) v = (LONG)(32767.5 + v / 2.0);
        else if (m.half == 2) v = (LONG)(32767.5 - v / 2.0);
        else if (m.half == 3) { double c = (v - 32767.5) * 2.0; v = (LONG)(c < 0 ? 0 : c > 65535 ? 65535 : c); }       // upper half -> 0..max
        else if (m.half == 4) { double c = (32767.5 - v) * 2.0; v = (LONG)(c < 0 ? 0 : c > 65535 ? 65535 : c); }       // lower half -> 0..max
        if (LONG* d = AxisPtr(r, data, m.dax)) *d = v;
    }
    // a game button can have several sources ([ButtonMap] and [AxisButton]): it is pressed while any of them is
    for (int i = 0; i < g_nBmap; i++) {
        const BMap& m = g_bmap[i];
        if (m.dvid == r->vid && m.dpid == r->pid) if (BYTE* d = ButtonPtr(r, data, m.dbtn)) *d = 0;
    }
    for (int i = 0; i < g_nAbmap; i++) {
        const ABMap& m = g_abmap[i];
        if (m.dvid == r->vid && m.dpid == r->pid) if (BYTE* d = ButtonPtr(r, data, m.dbtn)) *d = 0;
    }
    for (int i = 0; i < g_nBmap; i++) {
        const BMap& m = g_bmap[i];
        if (m.dvid != r->vid || m.dpid != r->pid) continue;
        if (!done[m.src]) { ReadSource(&g_src[m.src]); done[m.src] = true; }
        if (g_src[m.src].st.rgbButtons[m.sbtn] & 0x80) if (BYTE* d = ButtonPtr(r, data, m.dbtn)) *d = 0x80;
    }
    for (int i = 0; i < g_nAbmap; i++) {
        const ABMap& m = g_abmap[i];
        if (m.dvid != r->vid || m.dpid != r->pid) continue;
        if (!done[m.src]) { ReadSource(&g_src[m.src]); done[m.src] = true; }
        LONG v = ((const LONG*)&g_src[m.src].st)[m.sax];
        if (m.inv ? v <= m.thr : v >= m.thr) if (BYTE* d = ButtonPtr(r, data, m.dbtn)) *d = 0x80;
    }
    for (int i = 0; i < g_nCmap; i++) {           // two axes -> one: gas pulls towards 0, brake towards 65535 (invert swaps)
        const CMap& m = g_cmap[i];
        if (m.dvid != r->vid || m.dpid != r->pid) continue;
        if (!done[m.src1]) { ReadSource(&g_src[m.src1]); done[m.src1] = true; }
        if (!done[m.src2]) { ReadSource(&g_src[m.src2]); done[m.src2] = true; }
        double g = ((const LONG*)&g_src[m.src1].st)[m.ax1], b = ((const LONG*)&g_src[m.src2].st)[m.ax2];
        double o = 32767.5 + (m.inv ? (g - b) : (b - g)) / 2;
        if (LONG* d = AxisPtr(r, data, m.dax)) *d = (LONG)(o < 0 ? 0 : o > 65535 ? 65535 : o + 0.5);
    }
    for (int i = 0; i < g_nShape; i++) {          // shaping runs last, on the final axis value
        Shape& s = g_shape[i];
        if (s.vid != r->vid || s.pid != r->pid) continue;
        if (LONG* d = AxisPtr(r, data, s.ax)) *d = ShapeValue(s, *d);
    }
}

// [Winmm]: Enabled=1, Wheel=VID:PID (destination device), Id=0 (joystick id the game reads), Axes=N, Buttons=N (caps overrides),
// Source=auto (the data comes from the winmm joystick whose VID:PID is Wheel, shown to the game as Id; without it Id is also the real joystick id)
static bool g_wmEnabled;
static DWORD g_wmVid, g_wmPid;
static UINT g_wmId;
static int g_wmAxes, g_wmButtons;
static bool g_wmAuto;                     // Source=auto
static volatile LONG g_wmReal = -1;       // real winmm joystick id of the wheel (-1: not found)
static DWORD g_wmResolveTick;

static void ParseWinmm()
{
    g_wmEnabled = GetPrivateProfileIntA("Winmm", "Enabled", 0, g_ini) != 0;
    char w[32];
    GetPrivateProfileStringA("Winmm", "Wheel", "", w, sizeof w, g_ini);
    unsigned v, p;
    if (sscanf(w, "%x:%x", &v, &p) == 2) { g_wmVid = v; g_wmPid = p; } else g_wmEnabled = false;
    g_wmId = (UINT)GetPrivateProfileIntA("Winmm", "Id", 0, g_ini);
    char src[16];
    GetPrivateProfileStringA("Winmm", "Source", "", src, sizeof src, g_ini);
    g_wmAuto = _stricmp(src, "auto") == 0;
    g_wmReal = -1; g_wmResolveTick = 0;
    g_wmAxes = GetPrivateProfileIntA("Winmm", "Axes", 0, g_ini);
    g_wmButtons = GetPrivateProfileIntA("Winmm", "Buttons", 0, g_ini);
    if (g_wmEnabled) Log("Config: winmm hook enabled, wheel %04lX:%04lX = joystick id %u%s, caps axes=%d buttons=%d", g_wmVid, g_wmPid, g_wmId, g_wmAuto ? " (data from the joystick with this VID:PID)" : "", g_wmAxes, g_wmButtons);
}

static void ParseMask()
{
    static char sec[4096];
    GetPrivateProfileSectionA("Mask", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nMask < 16; p += strlen(p) + 1) {
        unsigned vid, pid;
        int used = 0;
        if (sscanf(p, "%x:%x=%n", &vid, &pid, &used) < 2 || !used) continue;
        char list[256];
        _snprintf(list, sizeof list, "%s", p + used);
        MaskEntry& m = g_mask[g_nMask];
        m.vid = vid; m.pid = pid; m.bits = 0;
        for (char* t = strtok(list, ", "); t; t = strtok(NULL, ", ")) {
            int a = AxisIndex(t);
            if (a >= 0) m.bits |= 1u << a;
            else LogErr("Config: unknown axis \"%s\" for %04X:%04X", t, vid, pid);
        }
        g_nMask++;
    }
}

static FILETIME g_iniTime;           // last write time of the INI when it was parsed (live reload)
static void RememberIniTime()
{
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (GetFileAttributesExA(g_ini, GetFileExInfoStandard, &fa)) g_iniTime = fa.ftLastWriteTime;
}

static void LoadConfig()      // call with g_cs held
{
    if (g_cfgLoaded) return;
    g_cfgLoaded = true;
    GetModuleFileNameA(g_self, g_ini, MAX_PATH);
    char* s = strrchr(g_ini, '\\');
    if (s) *s = 0;
    strcat(g_ini, "\\dinput_proxy.ini");
    if (GetFileAttributesA(g_ini) == INVALID_FILE_ATTRIBUTES) {
        FILE* f = fopen(g_ini, "wb");
        if (f) { fwrite(DEFAULT_INI, 1, sizeof DEFAULT_INI - 1, f); fclose(f); }
        Log("Config: created default %s", g_ini);
    }
    char hk[64];
    GetPrivateProfileStringA("Hotkey", "Learn", "CTRL+ALT+F12", hk, sizeof hk, g_ini);
    ParseHotkey(hk);
    Log("Config: %s learn hotkey=%s (vk=%02X mods=%u)", g_ini, hk, g_hkVk, g_hkMods);

    static char sec[4096];
    ParseMask();

    GetPrivateProfileSectionA("Hide", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nHide < 16; p += strlen(p) + 1) {
        unsigned vid, pid;
        if (p[0] == ';' || sscanf(p, "%x:%x", &vid, &pid) < 2) continue;
        g_hide[g_nHide][0] = vid; g_hide[g_nHide][1] = pid; g_nHide++;
        Log("Config: hide %04X:%04X from enumeration", vid, pid);
    }
    GetPrivateProfileSectionA("DevType", sec, sizeof sec, g_ini);
    for (const char* p = sec; *p && g_nTypeSpoof < 8; p += strlen(p) + 1) {
        unsigned vid, pid, type;
        if (p[0] == ';' || sscanf(p, "%x:%x=%x", &vid, &pid, &type) != 3) continue;
        g_typeSpoof[g_nTypeSpoof].vid = vid; g_typeSpoof[g_nTypeSpoof].pid = pid; g_typeSpoof[g_nTypeSpoof].type = type;
        g_nTypeSpoof++;
        Log("Config: device %04X:%04X reports dwDevType=%X", vid, pid, type);
    }
    {
        char cf[16];
        GetPrivateProfileStringA("Caps", "ClearFlags", "0", cf, sizeof cf, g_ini);
        g_clearFlags = strtoul(cf, NULL, 16);
        if (g_clearFlags) Log("Config: ClearFlags=%lX", g_clearFlags);
    }
    g_ffBoost = GetPrivateProfileIntA("FFB", "Boost", 100, g_ini);
    if (g_ffBoost != 100) Log("Config: FFB Boost=%ld%%", g_ffBoost);
    g_gainAsMag = GetPrivateProfileIntA("FFB", "GainAsMagnitude", 0, g_ini) != 0;
    if (g_gainAsMag) Log("Config: FFB GainAsMagnitude=1");
    g_blockSine = GetPrivateProfileIntA("FFB", "BlockSine", 0, g_ini) != 0;
    if (g_blockSine) Log("Config: FFB BlockSine=1");
    g_ignoreDz = GetPrivateProfileIntA("Caps", "IgnoreDeadzone", 0, g_ini) != 0;
    if (g_ignoreDz) Log("Config: IgnoreDeadzone=1");
    g_nonExcl = GetPrivateProfileIntA("Caps", "NonExclusive", 0, g_ini) != 0;
    if (g_nonExcl) Log("Config: NonExclusive=1");
    g_autoCoop = GetPrivateProfileIntA("Caps", "AutoCoop", 1, g_ini) != 0;
    if (!g_autoCoop) Log("Config: AutoCoop=0");
    g_maxButtons = GetPrivateProfileIntA("Caps", "MaxButtons", 0, g_ini);
    if (g_maxButtons) Log("Config: MaxButtons=%lu", g_maxButtons);
    ParseMaps();
    ParseShape();
    ParseIdentity();
    ParseCombine();
    ParseWinmm();
    RememberIniTime();
}

static DWORD SpoofType(DWORD vid, DWORD pid, DWORD orig)
{
    for (int i = 0; i < g_nTypeSpoof; i++)
        if (g_typeSpoof[i].vid == vid && g_typeSpoof[i].pid == pid) return g_typeSpoof[i].type;
    return orig;
}

static bool IsHidden(DWORD vid, DWORD pid)
{
    for (int i = 0; i < g_nHide; i++) if (g_hide[i][0] == vid && g_hide[i][1] == pid) return true;
    return false;
}

static void LearnNeutral()    // call with g_cs held
{
    int learned = 0;
    for (int i = 0; i < g_nRec; i++) {
        Rec* r = &g_rec[i];
        if (!r->maskBits || !r->seen) continue;
        for (int a = 0; a < 8; a++) {
            if (!(r->maskBits & (1u << a))) continue;
            if (r->rmax[a] - r->rmin[a] <= 256) {
                Log("Learn: %s %s is static (raw %ld), skipped; edit [Neutral] by hand if needed", r->name, AXIS_NAMES[a], r->raw[a]);
                continue;
            }
            char key[32], val[16];
            _snprintf(key, sizeof key, "%04lX:%04lX.%s", r->vid, r->pid, AXIS_NAMES[a]);
            _snprintf(val, sizeof val, "%ld", r->raw[a]);
            WritePrivateProfileStringA("Neutral", key, val, g_ini);
            r->neutral[a] = r->raw[a];
            learned++;
            Log("Learn: %s %s neutral=%ld", r->name, AXIS_NAMES[a], r->raw[a]);
        }
    }
    Log("Learn: %d axes stored", learned);
    MessageBeep(learned ? MB_OK : MB_ICONEXCLAMATION);
}

static void MaybeReload();    // live reload of the INI (defined after the winmm record)

static void CheckHotkey()     // call with g_cs held
{
    MaybeReload();
    DWORD now = GetTickCount();
    if (!g_hkVk || now - g_hkLastCheck < 20) return;
    g_hkLastCheck = now;
    bool down = (GetAsyncKeyState(g_hkVk) & 0x8000) != 0;
    if (down && (g_hkMods & 1) && !(GetAsyncKeyState(VK_CONTROL) & 0x8000)) down = false;
    if (down && (g_hkMods & 2) && !(GetAsyncKeyState(VK_MENU) & 0x8000)) down = false;
    if (down && (g_hkMods & 4) && !(GetAsyncKeyState(VK_SHIFT) & 0x8000)) down = false;
    if (down && !g_hkDown) LearnNeutral();
    g_hkDown = down;
}

// ---------------------------------------------------------------- live state for the GUI (shared memory, no disk traffic)
// "Local\CONTROLLAPP_live_<pid>": the final state the game receives plus what the game asked for. Throttled to ~20 Hz.
#pragma pack(push, 1)
struct LiveBlock {
    DWORD magic;                // 'CLIV'
    volatile DWORD seq;         // odd while being written
    DWORD pid;
    DWORD vid, pidDev;
    LONG axes[8];               // common domain 0..65535, after mask/map/shape
    LONG axMin[8], axMax[8];    // ranges the game selected
    BYTE btn[128];              // 0 / 128
    DWORD devType, capsFlags, gameDz, fmtSize;
    char name[64];
};
#pragma pack(pop)
static LiveBlock* g_live;
static const Rec* g_liveOwner;
static DWORD g_liveLast;

static void LiveInit()
{
    char nm[64];
    _snprintf(nm, sizeof nm, "Local\\CONTROLLAPP_live_%lu", GetCurrentProcessId());
    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(LiveBlock), nm);
    if (!m) return;
    g_live = (LiveBlock*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LiveBlock));
    if (g_live) { memset(g_live, 0, sizeof(LiveBlock)); g_live->magic = 0x564C4943; g_live->pid = GetCurrentProcessId(); }
}

static void LivePublish(const Rec* r, BYTE* data, DWORD cb)     // g_cs held; data = the state the game receives
{
    if (!g_live) return;
    if (!g_liveOwner || (r->hasMap && !g_liveOwner->hasMap)) g_liveOwner = r;
    if (g_liveOwner != r) return;
    DWORD now = GetTickCount();
    if (now - g_liveLast < 50) return;
    g_liveLast = now;
    g_live->seq++;
    g_live->vid = r->vid; g_live->pidDev = r->pid;
    for (int a = 0; a < 8; a++) {
        const LONG* ap = AxisPtr(r, data, a);
        g_live->axes[a] = ap ? ToCommon(r, a, *ap) : -1;
        g_live->axMin[a] = r->axMin[a]; g_live->axMax[a] = r->axMax[a];
    }
    for (int b = 0; b < 128; b++) {
        const BYTE* bp = ButtonPtr(r, data, b);
        g_live->btn[b] = bp ? (*bp & 0x80) : 0;
    }
    g_live->devType = r->devType; g_live->capsFlags = r->capsFlags; g_live->gameDz = r->gameDz; g_live->fmtSize = r->fmtSize;
    _snprintf(g_live->name, sizeof g_live->name, "%s", r->name);
    g_live->seq++;
}

// Mask bits, neutral values and mapping flag of a destination record, from the config.
static void ConfigureRec(Rec* r)    // g_cs held
{
    LoadConfig();
    r->maskBits = 0;
    for (int i = 0; i < g_nMask; i++)
        if (g_mask[i].vid == r->vid && g_mask[i].pid == r->pid) r->maskBits |= g_mask[i].bits;
    r->hasMap = HasMapFor(r->vid, r->pid);
    if (r->hasMap) Log("  mapping active for this device");
    for (int a = 0; a < 8; a++) {
        char key[32];
        _snprintf(key, sizeof key, "%04lX:%04lX.%s", r->vid, r->pid, AXIS_NAMES[a]);
        r->neutral[a] = (LONG)GetPrivateProfileIntA("Neutral", key, 32767, g_ini);
        if (r->maskBits & (1u << a)) Log("  mask %s = %ld", AXIS_NAMES[a], r->neutral[a]);
    }
}

// Mask + mappings + output log on a state buffer laid out as described by r (DirectInput or the winmm view).
static void ProcessOutput(Rec* r, BYTE* data, DWORD cb)    // g_cs held
{
    if (!r->maskBits && !r->hasMap) return;
    for (int a = 0; a < 8; a++)
        if (LONG* ap = AxisPtr(r, data, a)) *ap = ToCommon(r, a, *ap);
    for (int a = 0; a < 8; a++)
        if (LONG* ap = AxisPtr(r, data, a))
            if (r->maskBits & (1u << a)) *ap = r->neutral[a];
    if (r->hasMap) ApplyMaps(r, data);
    LONG common[8];                                // for the output log (common domain)
    for (int a = 0; a < 8; a++) {
        LONG* ap = AxisPtr(r, data, a);
        common[a] = ap ? *ap : -1;
        if (ap) *ap = FromCommon(r, a, *ap);
    }
    DWORD now2 = GetTickCount();
    DWORD ocmp = cb < sizeof r->lastout ? cb : (DWORD)sizeof r->lastout;
    if ((r->lastoutcb != cb || memcmp(r->lastout, data, ocmp) != 0) && (r->n <= 3 || now2 - r->lastoutlog >= 250)) {
        char line[512];
        int p = _snprintf(line, sizeof line, "Out %s dw:", r->name);
        for (int a = 0; a < 8; a++) {
            p += _snprintf(line + p, sizeof line - p, " %ld", common[a]);
        }
        p += _snprintf(line + p, sizeof line - p, " | btn:");
        for (int b = 0; b < 128 && p < 480; b++) {
            const BYTE* bp = ButtonPtr(r, data, b);
            if (bp && (*bp & 0x80)) p += _snprintf(line + p, sizeof line - p, " B%d", b + 1);
        }
        Log("%s", line);
        memcpy(r->lastout, data, ocmp);
        r->lastoutcb = cb;
        r->lastoutlog = now2;
    }
}

// ---------------------------------------------------------------- device hooks
typedef HRESULT (WINAPI *PFN_GetState)(void*, DWORD, void*);
typedef HRESULT (WINAPI *PFN_GetData)(void*, DWORD, DIDEVICEOBJECTDATA*, DWORD*, DWORD);
typedef HRESULT (WINAPI *PFN_SetFmt)(void*, const DIDATAFORMAT*);
typedef HRESULT (WINAPI *PFN_SetCoop)(void*, HWND, DWORD);
typedef HRESULT (WINAPI *PFN_SetProp)(void*, const GUID*, const DIPROPHEADER*);
typedef HRESULT (WINAPI *PFN_Acquire)(void*);
typedef HRESULT (WINAPI *PFN_GetInfo)(void*, DIDEVICEINSTANCEA*);

// After the Acquire fallback to a non-exclusive level (see H_Acquire) the game window often was not yet in the foreground at start-up.
// Once a window of the game is in the foreground, try the exclusive levels again so force feedback works.
// Called from GetDeviceState: at most every 1.5 s and 8 times per device.
static void TryUpgrade(void* self)
{
    HWND hw = NULL;
    DWORD want = 0;
    bool go = false;
    EnterCriticalSection(&g_cs);
    Rec* r = FindRec(self);
    DWORD now = GetTickCount();
    if (r && r->coopFallback && r->upgradeTries < 8 && (int)(now - r->nextUpgrade) >= 0) {
        HWND fg = GetForegroundWindow();
        DWORD pid = 0;
        if (fg) GetWindowThreadProcessId(fg, &pid);
        if (pid == GetCurrentProcessId()) { r->nextUpgrade = now + 1500; r->upgradeTries++; hw = r->coopHwnd; want = r->coopWanted; go = true; }
    }
    LeaveCriticalSection(&g_cs);
    if (!go || !hw) return;
    typedef HRESULT (WINAPI *PFN_Unacq)(void*);
    PFN_SetCoop sc = (PFN_SetCoop)Slot(self, 13);        // Slot(), not Orig(): Unacquire (8) is not hooked, so Orig() would return NULL for it
    PFN_Acquire ac = (PFN_Acquire)Slot(self, 7);
    PFN_Unacq un = (PFN_Unacq)Slot(self, 8);
    if (!sc || !ac || !un) return;
    DWORD levels[2] = { want, 5 };               // the game's own level, then exclusive+foreground
    un(self);
    for (int i = 0; i < 2; i++) {
        if (i == 1 && levels[1] == levels[0]) break;
        if (FAILED(sc(self, hw, levels[i]))) continue;
        HRESULT h = ac(self);
        Log("Acquire %s upgrade level=%lX hr=%lX%s", DevName(self), levels[i], (unsigned long)h, SUCCEEDED(h) ? " (exclusive again: force feedback available)" : "");
        if (SUCCEEDED(h)) {
            EnterCriticalSection(&g_cs);
            Rec* r2 = FindRec(self);
            if (r2) { r2->coopFallback = false; r2->coopFlags = levels[i]; }
            LeaveCriticalSection(&g_cs);
            return;
        }
    }
    sc(self, hw, 0xA);                           // stay non-exclusive
    HRESULT h2 = ac(self);
    LogErr("Acquire %s upgrade failed, back on level=A hr=%lX", DevName(self), (unsigned long)h2);
}

static HRESULT WINAPI H_GetDeviceState(void* self, DWORD cb, void* data)
{
    TryUpgrade(self);
    HRESULT hr = ((PFN_GetState)Orig(self, 9))(self, cb, data);
    if (FAILED(hr) || !data) return hr;
    EnterCriticalSection(&g_cs);
    CheckHotkey();
    Rec* r = FindRec(self);
    if (r) {
        bool fmt = r->fmtKnown && cb == r->fmtSize;      // state buffer has the layout the game selected
        if (fmt) {                                       // track raw axes (before masking)
            bool track = r->n >= 3;                      // first reads are a startup transient (e.g. 32767 -> 0)
            for (int a = 0; a < 8; a++) {
                const LONG* ap = AxisPtr(r, (BYTE*)data, a);
                if (!ap) continue;
                LONG cv = ToCommon(r, a, *ap);
                if (track) {
                    if (!r->seen || cv < r->rmin[a]) r->rmin[a] = cv;
                    if (!r->seen || cv > r->rmax[a]) r->rmax[a] = cv;
                }
                r->raw[a] = cv;
            }
            if (track) r->seen = true;
        }
        if (fmt && r->lastcb == cb) {                    // every raw button edge (before mask/map)
            const BYTE* nb = (const BYTE*)data;
            for (int b = 0; b < 128; b++) {
                int o = r->btnOfs[b];
                if (o < 0 || (DWORD)o >= cb || (DWORD)o >= sizeof r->last) continue;
                if ((nb[o] ^ r->last[o]) & 0x80)
                    Log("Button %s B%d %s", r->name, b + 1, (nb[o] & 0x80) ? "down" : "up");
            }
        }
        DWORD cmp = cb < sizeof r->last ? cb : (DWORD)sizeof r->last;
        bool changed = r->lastcb != cb || memcmp(r->last, data, cmp) != 0;
        DWORD now = GetTickCount();
        r->n++;
        if (r->n <= 3 || (changed && now - r->lastlog >= 250)) {
            char line[512];
            int p = _snprintf(line, sizeof line, "State %s cb=%lu n=%lu dw:", r->name, cb, r->n);
            const LONG* w = (const LONG*)data;
            DWORD nw = cb / 4 < 12 ? cb / 4 : 12;
            for (DWORD i = 0; i < nw && p < 400; i++) p += _snprintf(line + p, sizeof line - p, " %ld", w[i]);
            if (cb > 48) {
                p += _snprintf(line + p, sizeof line - p, " | nonzero bytes>=48:");
                const BYTE* b = (const BYTE*)data;
                for (DWORD i = 48; i < cb && p < 480; i++)
                    if (b[i]) p += _snprintf(line + p, sizeof line - p, " %lu=%02X", i, b[i]);
            }
            Log("%s", line);
            r->lastlog = now;
        }
        memcpy(r->last, data, cmp);
        r->lastcb = cb;
        if (fmt) {
            ProcessOutput(r, (BYTE*)data, cb);
            if (r->hasMap || r->maskBits || (r->axOfs[0] >= 0 && r->fmtSize >= 80)) LivePublish(r, (BYTE*)data, cb);
        }
    }
    LeaveCriticalSection(&g_cs);
    return hr;
}

static HRESULT WINAPI H_GetDeviceData(void* self, DWORD cbObj, DIDEVICEOBJECTDATA* d, DWORD* pdw, DWORD flags)
{
    HRESULT hr = ((PFN_GetData)Orig(self, 10))(self, cbObj, d, pdw, flags);
    EnterCriticalSection(&g_cs);
    Rec* r = FindRec(self);
    if (r) {
        r->dataCalls++;
        if (r->dataCalls <= 3)
            Log("GetDeviceData %s cbObj=%lu in=%lu out=%lu flags=%lX hr=%lX", r->name, cbObj,
                pdw ? (d ? *pdw : 0) : 0, pdw ? *pdw : 0, flags, (unsigned long)hr);
    }
    LeaveCriticalSection(&g_cs);
    return hr;
}

static HRESULT WINAPI H_SetDataFormat(void* self, const DIDATAFORMAT* f)
{
    if (f) {
        Log("SetDataFormat %s dwSize=%lu objSize=%lu flags=%lX dataSize=%lu numObjs=%lu", DevName(self),
            f->dwSize, f->dwObjSize, f->dwFlags, f->dwDataSize, f->dwNumObjs);
        for (DWORD i = 0; i < f->dwNumObjs && i < 80; i++) {
            const DIOBJECTDATAFORMAT& o = f->rgodf[i];
            char g[48] = "null";
            if (o.pguid) GuidStr(*o.pguid, g);
            Log("  obj[%lu] guid=%s ofs=%lu type=%08lX flags=%lX", i, g, o.dwOfs, o.dwType, o.dwFlags);
        }
    }
    if (f && f->rgodf) {
        EnterCriticalSection(&g_cs);
        Rec* r = FindRec(self);
        if (r) {
            r->fmtSize = f->dwDataSize;
            for (int a = 0; a < 8; a++) r->axOfs[a] = -1;
            for (int b = 0; b < 128; b++) r->btnOfs[b] = -1;
            int nextBtn = 0, nSlider = 0;
            for (DWORD i = 0; i < f->dwNumObjs; i++) {
                const DIOBJECTDATAFORMAT& o = f->rgodf[i];
                DWORD t = o.dwType & 0xFF;
                if ((t & 0x0C) && !(t & 0x03)) {                    // button: instance if given, else in order
                    DWORD inst = (o.dwType >> 8) & 0xFFFF;
                    int idx = inst != 0xFFFF ? (int)inst : nextBtn;
                    nextBtn++;
                    if (idx >= 0 && idx < 128) r->btnOfs[idx] = (int)o.dwOfs;
                } else if ((t & 0x03) && !(t & 0x0C) && o.pguid) {  // axis: identified by GUID
                    int a = -1;
                    if (IsEqualGUID(*o.pguid, GUID_XAxis)) a = 0;
                    else if (IsEqualGUID(*o.pguid, GUID_YAxis)) a = 1;
                    else if (IsEqualGUID(*o.pguid, GUID_ZAxis)) a = 2;
                    else if (IsEqualGUID(*o.pguid, GUID_RxAxis)) a = 3;
                    else if (IsEqualGUID(*o.pguid, GUID_RyAxis)) a = 4;
                    else if (IsEqualGUID(*o.pguid, GUID_RzAxis)) a = 5;
                    else if (IsEqualGUID(*o.pguid, GUID_Slider) && nSlider < 2) a = 6 + nSlider++;
                    if (a >= 0 && r->axOfs[a] < 0) r->axOfs[a] = (int)o.dwOfs;
                }
            }
            r->fmtKnown = true;
            Log("  layout: size=%lu axes ofs: %d %d %d %d %d %d %d %d, first buttons ofs: %d %d %d", r->fmtSize, r->axOfs[0], r->axOfs[1],
                r->axOfs[2], r->axOfs[3], r->axOfs[4], r->axOfs[5], r->axOfs[6], r->axOfs[7], r->btnOfs[0], r->btnOfs[1], r->btnOfs[2]);
        }
        LeaveCriticalSection(&g_cs);
    }
    return ((PFN_SetFmt)Orig(self, 11))(self, f);
}

static HRESULT WINAPI H_SetCooperativeLevel(void* self, HWND hwnd, DWORD flags)
{
    if (hwnd) g_hwnd = hwnd;
    DWORD use = flags;
    EnterCriticalSection(&g_cs);
    Rec* r = FindRec(self);
    if (g_nonExcl && (flags & 1) && r && (r->devType & 0xFF) == 4) use = 0xA;      // DIDEVTYPE_JOYSTICK only; keyboard/mouse untouched
    if (r) { r->coopHwnd = hwnd; r->coopFlags = use; }
    LeaveCriticalSection(&g_cs);
    HRESULT hr = ((PFN_SetCoop)Orig(self, 13))(self, hwnd, use);
    (FAILED(hr) ? LogErr : Log)("SetCooperativeLevel %s hwnd=%p flags=%lX%s hr=%lX", DevName(self), (void*)hwnd, flags, use != flags ? " (-> A)" : "", (unsigned long)hr);
    return hr;
}

static HRESULT WINAPI H_SetProperty(void* self, const GUID* prop, const DIPROPHEADER* h)
{
    DIPROPDWORD dz;
    const DIPROPHEADER* use = h;
    if (g_ignoreDz && h && (ULONG_PTR)prop == 5 && h->dwSize == sizeof(DIPROPDWORD)) {   // DIPROP_DEADZONE
        dz = *(const DIPROPDWORD*)h;
        dz.dwData = 0;
        use = &dz.diph;
        Log("SetProperty %s: game deadzone %lu -> 0 (Caps.IgnoreDeadzone)", DevName(self), ((const DIPROPDWORD*)h)->dwData);
    }
    HRESULT hr = ((PFN_SetProp)Orig(self, 6))(self, prop, use);
    if (SUCCEEDED(hr) && h && (ULONG_PTR)prop == 5 && h->dwSize == sizeof(DIPROPDWORD)) {     // remember the deadzone the game asked for
        EnterCriticalSection(&g_cs);
        Rec* me = FindRec(self);
        for (int i = 0; me && i < g_nRec; i++)
            if (g_rec[i].vid == me->vid && g_rec[i].pid == me->pid) g_rec[i].gameDz = ((const DIPROPDWORD*)h)->dwData;
        LeaveCriticalSection(&g_cs);
    }
    if (h) {
        unsigned long id = (unsigned long)(ULONG_PTR)prop;
        if (SUCCEEDED(hr) && (ULONG_PTR)prop == 4 && h->dwSize == sizeof(DIPROPRANGE)) {   // DIPROP_RANGE
            const DIPROPRANGE* pr = (const DIPROPRANGE*)h;
            EnterCriticalSection(&g_cs);
            Rec* me = FindRec(self);
            for (int i = 0; me && i < g_nRec; i++) {
                Rec* r = &g_rec[i];
                if (r->vid != me->vid || r->pid != me->pid) continue;
                for (int a = 0; a < 8; a++) {
                    bool hit = h->dwHow == DIPH_DEVICE || (h->dwHow == DIPH_BYOFFSET && r->axOfs[a] >= 0 && (DWORD)r->axOfs[a] == h->dwObj);
                    if (h->dwHow == DIPH_BYID && r->axOfs[a] >= 0) {          // resolve id -> offset in the selected data format
                        DIDEVICEOBJECTINSTANCEA oi;
                        DIDEVICEOBJECTINSTANCEW oiw;
                        memset(&oi, 0, sizeof oi);
                        memset(&oiw, 0, sizeof oiw);
                        oi.dwSize = sizeof(DIDEVICEOBJECTINSTANCE_DX3A);
                        oiw.dwSize = sizeof(DIDEVICEOBJECTINSTANCE_DX3W);
                        typedef HRESULT (WINAPI *PFN_GetObjInfo)(void*, DIDEVICEOBJECTINSTANCEA*, DWORD, DWORD);
                        typedef HRESULT (WINAPI *PFN_GetObjInfoW)(void*, DIDEVICEOBJECTINSTANCEW*, DWORD, DWORD);
                        void* goi = Slot(self, 14);
                        if (g_wide) {
                            if (goi && SUCCEEDED(((PFN_GetObjInfoW)goi)(self, &oiw, h->dwObj, DIPH_BYID))) hit = (int)oiw.dwOfs == r->axOfs[a];
                        } else if (goi && SUCCEEDED(((PFN_GetObjInfo)goi)(self, &oi, h->dwObj, DIPH_BYID))) hit = (int)oi.dwOfs == r->axOfs[a];
                    }
                    if (hit) { r->axMin[a] = pr->lMin; r->axMax[a] = pr->lMax; }
                }
            }
            LeaveCriticalSection(&g_cs);
        }
        if ((ULONG_PTR)prop < 0x10000 && h->dwSize == sizeof(DIPROPRANGE))
            Log("SetProperty %s prop=%lu how=%lu obj=%lX range=[%ld,%ld] hr=%lX", DevName(self), id, h->dwHow,
                h->dwObj, ((const DIPROPRANGE*)h)->lMin, ((const DIPROPRANGE*)h)->lMax, (unsigned long)hr);
        else if ((ULONG_PTR)prop < 0x10000 && h->dwSize == sizeof(DIPROPDWORD))
            Log("SetProperty %s prop=%lu how=%lu obj=%lX data=%lu hr=%lX", DevName(self), id, h->dwHow, h->dwObj,
                ((const DIPROPDWORD*)h)->dwData, (unsigned long)hr);
        else
            Log("SetProperty %s prop=%lu size=%lu hr=%lX", DevName(self), id, h->dwSize, (unsigned long)hr);
    }
    return hr;
}

// Some games ask for an exclusive background level and Windows answers E_ACCESSDENIED (DIERR_OTHERAPPHASPRIO) although nothing else holds the
// device. When that happens on a joystick the proxy tries two other levels so the game gets the device anyway:
// exclusive+foreground (keeps force feedback), then non-exclusive+background (no force feedback). The game's own level is restored if both fail.
static HRESULT WINAPI H_Acquire(void* self)
{
    HRESULT hr = ((PFN_Acquire)Orig(self, 7))(self);
    if (hr == (HRESULT)0x80070005L && g_autoCoop) {
        HWND hw = NULL;
        DWORD fl = 0;
        bool joy = false;
        EnterCriticalSection(&g_cs);
        Rec* r = FindRec(self);
        if (r) { hw = r->coopHwnd; fl = r->coopFlags; joy = (r->devType & 0xFF) == 4; }
        LeaveCriticalSection(&g_cs);
        if (joy && hw && (fl & 1) && (fl & 8)) {          // only "exclusive + background": that failure is not about the window being in the background
            PFN_SetCoop sc = (PFN_SetCoop)Orig(self, 13);
            static const DWORD tries[2] = { 5, 0xA };       // EXCLUSIVE|FOREGROUND, NONEXCLUSIVE|BACKGROUND
            for (int i = 0; i < 2; i++) {
                if (FAILED(sc(self, hw, tries[i]))) continue;
                HRESULT h2 = ((PFN_Acquire)Orig(self, 7))(self);
                Log("Acquire %s fallback level=%lX hr=%lX%s", DevName(self), tries[i], (unsigned long)h2,
                    SUCCEEDED(h2) && tries[i] == 0xA ? " (non-exclusive: no force feedback)" : "");
                if (SUCCEEDED(h2)) {
                    hr = h2;
                    EnterCriticalSection(&g_cs);
                    Rec* r2 = FindRec(self);
                    if (r2) {
                        r2->coopFlags = tries[i];
                        r2->coopFallback = tries[i] == 0xA;              // non-exclusive: TryUpgrade will try to get exclusive back later
                        r2->coopWanted = fl; r2->upgradeTries = 0; r2->nextUpgrade = GetTickCount() + 1000;
                    }
                    LeaveCriticalSection(&g_cs);
                    break;
                }
            }
            if (FAILED(hr)) sc(self, hw, fl);               // give the game its own level back
        }
    }
    // Games call Acquire on every frame: log only when the result changes (a repeat count is written when it does), else the 5 MB log fills in minutes
    bool quiet = false;
    DWORD rep = 0, prevHr = 0;
    EnterCriticalSection(&g_cs);
    Rec* ra = FindRec(self);
    if (ra) {
        if (ra->acqSeen && ra->acqHr == (DWORD)hr) { ra->acqRep++; quiet = true; }
        else { rep = ra->acqRep; prevHr = ra->acqHr; ra->acqRep = 0; ra->acqHr = (DWORD)hr; ra->acqSeen = true; }
    }
    LeaveCriticalSection(&g_cs);
    if (!quiet) {
        if (rep) Log("Acquire %s hr=%lX (repeated %lu more times)", DevName(self), (unsigned long)prevHr, (unsigned long)rep);
        if (FAILED(hr)) LogErr("Acquire %s hr=%lX", DevName(self), (unsigned long)hr);
        else if (FAILED((HRESULT)prevHr)) LogEvt("Acquire %s hr=%lX (works again after hr=%lX)", DevName(self), (unsigned long)hr, (unsigned long)prevHr);     // the game got the device back
        else Log("Acquire %s hr=%lX", DevName(self), (unsigned long)hr);
    }
    return hr;
}

// Force feedback / capability logging. Slots 18+ exist only on IDirectInputDevice2 and later,
// so PatchFF must be called only for objects obtained via QueryInterface(Device2/7).
typedef HRESULT (WINAPI *PFN_QI)(void*, const GUID*, void**);
typedef HRESULT (WINAPI *PFN_GetCaps)(void*, DIDEVCAPS*);
typedef HRESULT (WINAPI *PFN_CreateEffect)(void*, const GUID*, const DIEFFECT*, void**, IUnknown*);
typedef HRESULT (WINAPI *PFN_EnumEffects)(void*, LPDIENUMEFFECTSCALLBACKA, void*, DWORD);
typedef HRESULT (WINAPI *PFN_FFCmd)(void*, DWORD);

static HRESULT WINAPI H_QueryInterface(void*, const GUID*, void**);

static HRESULT WINAPI H_GetCapabilities(void* self, DIDEVCAPS* c)
{
    HRESULT hr = ((PFN_GetCaps)Orig(self, 3))(self, c);
    if (c && SUCCEEDED(hr) && c->dwSize >= 24) {
        Rec* r = FindRec(self);
        if (r) c->dwDevType = SpoofType(r->vid, r->pid, c->dwDevType);
    }
    if (c && SUCCEEDED(hr) && c->dwSize >= 24 && g_clearFlags && (c->dwFlags & g_clearFlags)) {
        Log("GetCapabilities %s: flags %lX -> %lX (Caps.ClearFlags)", DevName(self), c->dwFlags, c->dwFlags & ~g_clearFlags);
        c->dwFlags &= ~g_clearFlags;
    }
    if (c && SUCCEEDED(hr) && c->dwSize >= 24 && g_maxButtons && c->dwButtons > g_maxButtons) {
        Log("GetCapabilities %s: buttons %lu -> %lu (Caps.MaxButtons)", DevName(self), c->dwButtons, g_maxButtons);
        c->dwButtons = g_maxButtons;
    }
    if (c && SUCCEEDED(hr) && c->dwSize >= sizeof(DIDEVCAPS)) {
        Rec* rr = FindRec(self);
        if (rr) { rr->devType = c->dwDevType; rr->capsFlags = c->dwFlags; }
        Log("GetCapabilities %s flags=%lX (FFB=%s) devType=%lX axes=%lu buttons=%lu povs=%lu ffPeriod=%lu ffMinRes=%lu ffDriver=%lX",
            DevName(self), c->dwFlags, (c->dwFlags & DIDC_FORCEFEEDBACK) ? "yes" : "no", c->dwDevType, c->dwAxes,
            c->dwButtons, c->dwPOVs, c->dwFFSamplePeriod, c->dwFFMinTimeResolution, c->dwFFDriverVersion);
    } else if (c)
        Log("GetCapabilities %s dwSize=%lu hr=%lX", DevName(self), c->dwSize, (unsigned long)hr);
    return hr;
}

static void LogEffect(const char* dev, const DIEFFECT* e)
{
    if (!e) { Log("  DIEFFECT %s = null", dev); return; }
    __try {
        Log("  DIEFFECT %s: dwSize=%lu flags=%lX duration=%lu samplePeriod=%lu gain=%lu trigBtn=%lX trigRepeat=%lu cAxes=%lu",
            dev, e->dwSize, e->dwFlags, e->dwDuration, e->dwSamplePeriod, e->dwGain, e->dwTriggerButton,
            e->dwTriggerRepeatInterval, e->cAxes);
        char b[200];
        int p = 0;
        for (DWORD i = 0; i < e->cAxes && i < 4 && e->rgdwAxes; i++)
            p += _snprintf(b + p, sizeof b - p, " %08lX", e->rgdwAxes[i]);
        Log("    axes:%s", p ? b : " (null)");
        p = 0;
        for (DWORD i = 0; i < e->cAxes && i < 4 && e->rglDirection; i++)
            p += _snprintf(b + p, sizeof b - p, " %ld", e->rglDirection[i]);
        Log("    direction:%s", p ? b : " (null)");
        if (e->lpEnvelope)
            Log("    envelope: size=%lu attackLevel=%lu attackTime=%lu fadeLevel=%lu fadeTime=%lu", e->lpEnvelope->dwSize,
                e->lpEnvelope->dwAttackLevel, e->lpEnvelope->dwAttackTime, e->lpEnvelope->dwFadeLevel, e->lpEnvelope->dwFadeTime);
        else
            Log("    envelope: null");
        p = 0;
        const BYTE* t = (const BYTE*)e->lpvTypeSpecificParams;
        for (DWORD i = 0; t && i < e->cbTypeSpecificParams && i < 24; i++) p += _snprintf(b + p, sizeof b - p, " %02X", t[i]);
        Log("    typeSpecific: cb=%lu bytes:%s", e->cbTypeSpecificParams, p ? b : " (null)");
        if (e->dwSize >= sizeof(DIEFFECT)) Log("    startDelay=%lu", e->dwStartDelay);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("  DIEFFECT %s: unreadable", dev);
    }
}

// The game (1997) passes polar coordinates with a single axis; current DirectInput rejects that with
// E_INVALIDARG. Copy the effect and switch it to cartesian (one axis, direction value unchanged).
static bool FixEffect(const DIEFFECT* in, DIEFFECT* out)
{
    if (!in || in->dwSize < sizeof(DIEFFECT_DX5) || in->cAxes != 1) return false;
    bool polar = (in->dwFlags & DIEFF_POLAR) != 0;
    // condition effects (spring/damper/...): the game supplies one DICONDITION (24 bytes) per axis of a 2-axis stick
    bool cond = in->cbTypeSpecificParams > sizeof(DICONDITION) && in->cbTypeSpecificParams % sizeof(DICONDITION) == 0;
    if (!polar && !cond) return false;
    memset(out, 0, sizeof *out);
    memcpy(out, in, in->dwSize < sizeof *out ? in->dwSize : sizeof *out);
    if (polar) out->dwFlags = (in->dwFlags & ~(DWORD)(DIEFF_POLAR | DIEFF_SPHERICAL)) | DIEFF_CARTESIAN;
    if (cond) out->cbTypeSpecificParams = sizeof(DICONDITION);
    return true;
}

// [FFB] Boost (percent): scales the type-specific magnitudes of effects before they reach the driver (clamped to +-10000).
static LONG ScaleMag(LONG v) { LONGLONG r = (LONGLONG)v * g_ffBoost / 100; return (LONG)(r > 10000 ? 10000 : r < -10000 ? -10000 : r); }

static const DIEFFECT* BoostEffect(const DIEFFECT* in, DIEFFECT* out, BYTE* tsp /* 64 bytes */)
{
    if (g_ffBoost == 100 || !in || in->dwSize < sizeof(DIEFFECT_DX5) || !in->lpvTypeSpecificParams) return in;
    DWORD cb = in->cbTypeSpecificParams;
    if (cb != 4 && cb != 8 && cb != 16 && cb != 24) return in;
    memset(out, 0, sizeof *out);
    memcpy(out, in, in->dwSize < sizeof *out ? in->dwSize : sizeof *out);
    memcpy(tsp, in->lpvTypeSpecificParams, cb);
    LONG* l = (LONG*)tsp;
    if (cb == 4) l[0] = ScaleMag(l[0]);                                    // DICONSTANTFORCE.lMagnitude
    else if (cb == 8) { l[0] = ScaleMag(l[0]); l[1] = ScaleMag(l[1]); }      // DIRAMPFORCE start/end
    else if (cb == 16) l[0] = ScaleMag(l[0]);                              // DIPERIODIC.dwMagnitude (0..10000)
    else { l[1] = ScaleMag(l[1]); l[2] = ScaleMag(l[2]); }                 // DICONDITION coefficients
    out->lpvTypeSpecificParams = tsp;
    return out;
}

// [FFB] GainAsMagnitude: some games (Colin McRae Rally 1/2) change the strength of a running constant-force / periodic effect only
// through its gain (0..10000), and the Moza R9 driver ignores gain changes of a running effect: the effect keeps its start strength
// (the wheel is pulled to one side at full force). Instead the device gain is fixed at 10000 and the strength (magnitude * gain) is sent
// as the effect's magnitude, which the driver does follow.
struct EffRec { void* eff; GUID guid; DWORD gain; LONG baseMag; DIPERIODIC per; bool havePer; };
static const int MAX_EFFS = 64;
static EffRec g_effs[MAX_EFFS]; static int g_nEffs, g_nextEff;
static int g_nGainConv;

static bool IsConstGuid(const GUID& g) { return IsEqualGUID(g, GUID_ConstantForce) != 0; }
static bool IsPeriodicGuid(const GUID& g)
{
    return IsEqualGUID(g, GUID_Sine) || IsEqualGUID(g, GUID_Square) || IsEqualGUID(g, GUID_Triangle) ||
           IsEqualGUID(g, GUID_SawtoothUp) || IsEqualGUID(g, GUID_SawtoothDown);
}

static EffRec* FindEff(void* eff)
{
    for (int i = 0; i < g_nEffs; i++) if (g_effs[i].eff == eff) return &g_effs[i];
    return NULL;
}

// what the game has set on the effect so far (the values it means, before any conversion)
static void RecordEffect(EffRec* r, const DIEFFECT* e, DWORD flags)
{
    if (!r || !e) return;
    __try {
        if (flags & DIEP_GAIN) r->gain = e->dwGain;
        if ((flags & DIEP_TYPESPECIFICPARAMS) && e->lpvTypeSpecificParams) {
            if (IsConstGuid(r->guid) && e->cbTypeSpecificParams >= sizeof(DICONSTANTFORCE))
                r->baseMag = ((const DICONSTANTFORCE*)e->lpvTypeSpecificParams)->lMagnitude;
            else if (IsPeriodicGuid(r->guid) && e->cbTypeSpecificParams >= sizeof(DIPERIODIC)) {
                r->per = *(const DIPERIODIC*)e->lpvTypeSpecificParams; r->havePer = true;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static EffRec* NewEff(void* eff, const GUID& g)
{
    EffRec* r = FindEff(eff);
    if (!r) {
        if (g_nEffs < MAX_EFFS) r = &g_effs[g_nEffs++];
        else { r = &g_effs[g_nextEff]; g_nextEff = (g_nextEff + 1) % MAX_EFFS; }
    }
    memset(r, 0, sizeof *r);
    r->eff = eff; r->guid = g; r->gain = 10000;
    return r;
}

// Returns `in` unchanged unless the effect is a constant-force / periodic one and gain or type-specific parameters are being set.
// Otherwise a copy with gain 10000 and magnitude * gain/10000 (flags get DIEP_GAIN | DIEP_TYPESPECIFICPARAMS).
static const DIEFFECT* GainToMag(EffRec* r, const DIEFFECT* in, DWORD* flags, DIEFFECT* out, BYTE* tsp /* 16 bytes */)
{
    if (!g_gainAsMag || !r || !in || in->dwSize < sizeof(DIEFFECT_DX5)) return in;
    bool cf = IsConstGuid(r->guid), per = IsPeriodicGuid(r->guid);
    if (!cf && !per) return in;
    if (!(*flags & (DIEP_GAIN | DIEP_TYPESPECIFICPARAMS))) return in;
    if (per && !r->havePer) return in;
    double f = r->gain / 10000.0; if (f > 1.0) f = 1.0;
    memset(out, 0, sizeof *out);
    memcpy(out, in, in->dwSize < sizeof *out ? in->dwSize : sizeof *out);
    if (cf) {
        DICONSTANTFORCE c; c.lMagnitude = (LONG)(r->baseMag * f);
        memcpy(tsp, &c, sizeof c); out->cbTypeSpecificParams = sizeof c;
    } else {
        DIPERIODIC p = r->per; p.dwMagnitude = (DWORD)(p.dwMagnitude * f);
        memcpy(tsp, &p, sizeof p); out->cbTypeSpecificParams = sizeof p;
    }
    out->lpvTypeSpecificParams = tsp;
    out->dwGain = 10000;
    *flags |= DIEP_GAIN | DIEP_TYPESPECIFICPARAMS;
    if (g_nGainConv++ < 8) Log("FFB GainAsMagnitude: gain %lu -> magnitude %ld (base %ld)", (unsigned long)r->gain, cf ? ((DICONSTANTFORCE*)tsp)->lMagnitude : (LONG)((DIPERIODIC*)tsp)->dwMagnitude, cf ? r->baseMag : (LONG)r->per.dwMagnitude);
    return out;
}

typedef HRESULT (WINAPI *PFN_EffSetParams)(void*, const DIEFFECT*, DWORD);
typedef HRESULT (WINAPI *PFN_EffStart)(void*, DWORD, DWORD);
static int g_nEffSet, g_nEffStart;

static HRESULT WINAPI H_EffSetParameters(void* self, const DIEFFECT* e, DWORD flags)
{
    DIEFFECT fixed, conv, boosted;
    BYTE tsp[64], tspc[16];
    bool fix = FixEffect(e, &fixed);
    EffRec* rec = (g_gainAsMag || g_blockSine) ? FindEff(self) : NULL;
    if (rec) RecordEffect(rec, e, flags);
    DWORD fl = flags;
    if (rec && g_blockSine && IsPeriodicGuid(rec->guid)) fl &= ~(DWORD)DIEP_START;       // a blocked effect is never started
    const DIEFFECT* use = BoostEffect(GainToMag(rec, fix ? &fixed : e, &fl, &conv, tspc), &boosted, tsp);
    if (g_nEffSet++ < 5) {
        Log("Effect SetParameters flags=%lX%s", flags, fix ? " (1-axis fix)" : "");
        LogEffect("effect", e);
    } else if (e && e->lpvTypeSpecificParams && (flags & DIEP_TYPESPECIFICPARAMS)) {
        // compact log of what the game really sends while driving (rate-limited per size)
        static DWORD last[5];
        int k = e->cbTypeSpecificParams == 4 ? 0 : e->cbTypeSpecificParams == 8 ? 1 : e->cbTypeSpecificParams == 16 ? 2 : e->cbTypeSpecificParams == 24 ? 3 : 4;
        DWORD now = GetTickCount();
        if (now - last[k] >= 500) {
            const LONG* l = (const LONG*)e->lpvTypeSpecificParams;
            Log("FFB cb=%lu params: %ld %ld %ld %ld -> sent %ld", e->cbTypeSpecificParams, l[0],
                e->cbTypeSpecificParams >= 8 ? l[1] : 0, e->cbTypeSpecificParams >= 12 ? l[2] : 0,
                e->cbTypeSpecificParams >= 16 ? l[3] : 0, ((const LONG*)use->lpvTypeSpecificParams)[0]);
            last[k] = now;
        }
    }
    HRESULT hr = ((PFN_EffSetParams)Orig(self, 6))(self, use, fl);
    if (FAILED(hr)) LogErr("Effect SetParameters -> hr=%lX", (unsigned long)hr);
    else if (g_nEffSet <= 5) Log("Effect SetParameters -> hr=%lX", (unsigned long)hr);
    return hr;
}

static HRESULT WINAPI H_EffStart(void* self, DWORD iterations, DWORD flags)
{
    if (g_blockSine) {
        EffRec* r = FindEff(self);
        if (r && IsPeriodicGuid(r->guid)) {
            static int nBlocked;
            if (nBlocked++ < 5) Log("Effect Start blocked (BlockSine)");
            return DI_OK;
        }
    }
    HRESULT hr = ((PFN_EffStart)Orig(self, 7))(self, iterations, flags);
    if (g_nEffStart++ < 10 || FAILED(hr))
        Log("Effect Start iterations=%lu flags=%lX hr=%lX", iterations, flags, (unsigned long)hr);
    return hr;
}

static HRESULT WINAPI H_CreateEffect(void* self, const GUID* g, const DIEFFECT* e, void** out, IUnknown* outer)
{
    LogEffect(DevName(self), e);
    DIEFFECT fixed, conv, boosted;
    BYTE tsp[64], tspc[16];
    bool fix = FixEffect(e, &fixed);
    if (fix) Log("  CreateEffect: adjusted for 1 axis (polar->cartesian, conditions trimmed to 1)");
    // GainAsMagnitude: the effect is created with its strength in the magnitude (the record is bound to the effect object afterwards)
    EffRec tmp; memset(&tmp, 0, sizeof tmp);
    const DIEFFECT* use = fix ? &fixed : e;
    if (g_gainAsMag && g && e) {
        tmp.guid = *g; tmp.gain = 10000;
        RecordEffect(&tmp, e, DIEP_GAIN | DIEP_TYPESPECIFICPARAMS);
        DWORD cf = DIEP_GAIN | DIEP_TYPESPECIFICPARAMS;
        use = GainToMag(&tmp, use, &cf, &conv, tspc);
    }
    HRESULT hr = ((PFN_CreateEffect)Orig(self, 18))(self, g, BoostEffect(use, &boosted, tsp), out, outer);
    if (SUCCEEDED(hr) && out && *out) {
        PatchSlot(*out, 6, (void*)H_EffSetParameters);
        PatchSlot(*out, 7, (void*)H_EffStart);
        if ((g_gainAsMag || g_blockSine) && g) { EffRec* r = NewEff(*out, *g); r->gain = tmp.gain; r->baseMag = tmp.baseMag; r->per = tmp.per; r->havePer = tmp.havePer; }
    }
    char gs[48] = "null";
    if (g) GuidStr(*g, gs);
    (FAILED(hr) ? LogErr : Log)("CreateEffect %s guid=%s hr=%lX", DevName(self), gs, (unsigned long)hr);
    return hr;
}

struct EffCtx { LPDIENUMEFFECTSCALLBACKA cb; void* ref; };

static BOOL CALLBACK EffCb(const DIEFFECTINFOA* e, void* ref)
{
    EffCtx* c = (EffCtx*)ref;
    char gs[48];
    GuidStr(e->guid, gs);
    Log("  Effect: \"%s\" guid=%s type=%lX static=%lX dynamic=%lX", e->tszName, gs, e->dwEffType,
        e->dwStaticParams, e->dwDynamicParams);
    return c->cb(e, c->ref);
}

static HRESULT WINAPI H_EnumEffects(void* self, LPDIENUMEFFECTSCALLBACKA cb, void* ref, DWORD type)
{
    Log("EnumEffects %s type=%lX", DevName(self), type);
    EffCtx c = { cb, ref };
    HRESULT hr = ((PFN_EnumEffects)Orig(self, 19))(self, cb ? EffCb : NULL, &c, type);
    Log("EnumEffects %s -> hr=%lX", DevName(self), (unsigned long)hr);
    return hr;
}

static HRESULT WINAPI H_SendForceFeedbackCommand(void* self, DWORD cmd)
{
    HRESULT hr = ((PFN_FFCmd)Orig(self, 22))(self, cmd);
    (FAILED(hr) ? LogErr : Log)("SendForceFeedbackCommand %s cmd=%lX hr=%lX", DevName(self), cmd, (unsigned long)hr);
    return hr;
}

static void PatchFF(void* dev)
{
    PatchSlot(dev, 18, (void*)H_CreateEffect);
    PatchSlot(dev, 19, (void*)H_EnumEffects);
    PatchSlot(dev, 22, (void*)H_SendForceFeedbackCommand);
}

static HRESULT WINAPI H_GetDeviceInfo(void* self, DIDEVICEINSTANCEA* di)
{
    HRESULT hr = ((PFN_GetInfo)Orig(self, 15))(self, di);
    Rec* r = FindRec(self);
    if (SUCCEEDED(hr) && di && r) ApplyIdentity(di, r->vid, r->pid);
    return hr;
}

typedef HRESULT (WINAPI *PFN_GetInfoW)(void*, DIDEVICEINSTANCEW*);

static HRESULT WINAPI H_GetDeviceInfoW(void* self, DIDEVICEINSTANCEW* di)
{
    HRESULT hr = ((PFN_GetInfoW)Orig(self, 15))(self, di);
    Rec* r = FindRec(self);
    if (SUCCEEDED(hr) && di && r) ApplyIdentityW(di, r->vid, r->pid);
    return hr;
}

static void PatchDeviceSlots(void* dev)
{
    PatchSlot(dev, 15, g_wide ? (void*)H_GetDeviceInfoW : (void*)H_GetDeviceInfo);
    PatchSlot(dev, 0, (void*)H_QueryInterface);
    PatchSlot(dev, 3, (void*)H_GetCapabilities);
    PatchSlot(dev, 6, (void*)H_SetProperty);
    PatchSlot(dev, 7, (void*)H_Acquire);
    PatchSlot(dev, 9, (void*)H_GetDeviceState);
    PatchSlot(dev, 10, (void*)H_GetDeviceData);
    PatchSlot(dev, 11, (void*)H_SetDataFormat);
    PatchSlot(dev, 13, (void*)H_SetCooperativeLevel);
}

static HRESULT WINAPI H_QueryInterface(void* self, const GUID* riid, void** ppv)
{
    HRESULT hr = ((PFN_QI)Orig(self, 0))(self, riid, ppv);
    char gs[48] = "null";
    if (riid) GuidStr(*riid, gs);
    bool dev2 = riid && (IsEqualIID(*riid, IID_IDirectInputDevice2A) || IsEqualIID(*riid, IID_IDirectInputDevice2W) ||
                         IsEqualIID(*riid, IID_IDirectInputDevice7A) || IsEqualIID(*riid, IID_IDirectInputDevice7W));
    Log("QueryInterface %s riid=%s hr=%lX%s", DevName(self), gs, (unsigned long)hr, dev2 ? " (Device2+)" : "");
    if (SUCCEEDED(hr) && ppv && *ppv && dev2) {
        EnterCriticalSection(&g_cs);
        Rec* r = FindRec(self);
        if (r && !FindRec(*ppv) && g_nRec < MAX_REC) {
            g_rec[g_nRec] = *r;
            g_rec[g_nRec].dev = *ppv;
            g_rec[g_nRec].seq = ++g_recSeq;
            g_nRec++;
        }
        LeaveCriticalSection(&g_cs);
        PatchDeviceSlots(*ppv);
        PatchFF(*ppv);
    }
    return hr;
}

// Installed only once the record table has been full (see RecycleRec), so games that never fill it are not affected.
// When the object is destroyed (refcount 0) its row is freed (dev = NULL) and PatchDevice reuses it.
typedef ULONG (WINAPI *PFN_Release)(void*);
static ULONG WINAPI H_Release(void* self)
{
    PFN_Release orig = (PFN_Release)Orig(self, 2);
    if (!orig) return 0;
    ULONG n = orig(self);
    if (n == 0) {
        EnterCriticalSection(&g_cs);
        Rec* r = FindRec(self);
        if (r) {
            r->dev = NULL;
            static int logged;
            if (logged < 10) { logged++; Log("Release: row %d (%04lX:%04lX) freed", (int)(r - g_rec), r->vid, r->pid); }
        }
        LeaveCriticalSection(&g_cs);
    }
    return n;
}

static void ResetRec(Rec* r, void* dev)
{
    memset(r, 0, sizeof *r); r->dev = dev; r->seq = ++g_recSeq;
    for (int a = 0; a < 8; a++) r->axOfs[a] = -1;
    for (int b = 0; b < 128; b++) r->btnOfs[b] = -1;
    for (int a = 0; a < 8; a++) { r->axMin[a] = 0; r->axMax[a] = 65535; }
}

// g_cs held; the table is full (the game recreates its device objects on every device change and objects are never released here).
// Reuse the oldest row of the same physical device (VID/PID): its old object has most likely been replaced. Other devices' rows are left alone.
static Rec* RecycleRec(void* dev)
{
    PatchSlot(dev, 2, (void*)H_Release);        // from now on destroyed objects free their rows (idempotent per vtable)
    DWORD vid = 0, pid = 0; bool got = false;
    if (g_wide) {
        DIDEVICEINSTANCEW diw; memset(&diw, 0, sizeof diw); diw.dwSize = sizeof(DIDEVICEINSTANCE_DX3W);
        PFN_GetInfoW giw = (PFN_GetInfoW)Slot(dev, 15);
        if (giw && SUCCEEDED(giw(dev, &diw))) { vid = diw.guidProduct.Data1 & 0xFFFF; pid = diw.guidProduct.Data1 >> 16; got = true; }
    } else {
        DIDEVICEINSTANCEA di; memset(&di, 0, sizeof di); di.dwSize = sizeof(DIDEVICEINSTANCE_DX3A);
        PFN_GetInfo gi = (PFN_GetInfo)Slot(dev, 15);
        if (gi && SUCCEEDED(gi(dev, &di))) { vid = di.guidProduct.Data1 & 0xFFFF; pid = di.guidProduct.Data1 >> 16; got = true; }
    }
    if (got) {
        int old = -1;                           // the oldest row of this device: the game's live object is the newest one, not the first in the table
        for (int i = 0; i < g_nRec; i++)
            if (g_rec[i].vid == vid && g_rec[i].pid == pid && g_rec[i].dev != dev && (old < 0 || (LONG)(g_rec[i].seq - g_rec[old].seq) < 0)) old = i;
        if (old >= 0) {
            LogErr("Rec table full: row %d (%04lX:%04lX) reused for the new object %p", old, vid, pid, dev);
            ResetRec(&g_rec[old], dev);
            return &g_rec[old];
        }
    }
    static bool warned;
    if (!warned) { warned = true; LogErr("Rec table full: new device object %p (%04lX:%04lX) is not tracked", dev, vid, pid); }
    return NULL;
}

static void PatchDevice(void* dev, const GUID& guid)
{
    PatchDeviceSlots(dev);
    if (g_di8) PatchFF(dev);

    char gs[48];
    GuidStr(guid, gs);
    EnterCriticalSection(&g_cs);
    Rec* r = FindRec(dev);
    if (!r)
        for (int i = 0; i < g_nRec; i++) if (!g_rec[i].dev) { r = &g_rec[i]; ResetRec(r, dev); break; }   // row freed by H_Release
    if (!r && g_nRec < MAX_REC) { r = &g_rec[g_nRec++]; ResetRec(r, dev); }
    else if (!r) r = RecycleRec(dev);
    if (r) {
        bool gotInfo = false;
        if (g_wide) {
            DIDEVICEINSTANCEW diw;
            memset(&diw, 0, sizeof diw);
            diw.dwSize = sizeof(DIDEVICEINSTANCE_DX3W);
            PFN_GetInfoW giw = (PFN_GetInfoW)Slot(dev, 15);   // original, so vid/pid/name stay the real ones
            if (giw && SUCCEEDED(giw(dev, &diw))) {
                WideCharToMultiByte(CP_ACP, 0, diw.tszProductName, -1, r->name, sizeof r->name, NULL, NULL);
                r->vid = diw.guidProduct.Data1 & 0xFFFF;
                r->pid = diw.guidProduct.Data1 >> 16;
                gotInfo = true;
            }
        } else {
            DIDEVICEINSTANCEA di;
            memset(&di, 0, sizeof di);
            di.dwSize = sizeof(DIDEVICEINSTANCE_DX3A);
            PFN_GetInfo gi = (PFN_GetInfo)Slot(dev, 15);
            if (gi && SUCCEEDED(gi(dev, &di))) {
                _snprintf(r->name, sizeof r->name, "%s", di.tszProductName);
                r->vid = di.guidProduct.Data1 & 0xFFFF;
                r->pid = di.guidProduct.Data1 >> 16;
                gotInfo = true;
            }
        }
        if (!gotInfo) _snprintf(r->name, sizeof r->name, "%s", gs);
        Log("Device ready: %s ptr=%p guidInstance=%s", r->name, dev, gs);

        ConfigureRec(r);
    }
    LeaveCriticalSection(&g_cs);
}

// ---------------------------------------------------------------- DirectInput object hooks
typedef HRESULT (WINAPI *PFN_CreateDevice)(void*, const GUID*, void**, IUnknown*);
typedef HRESULT (WINAPI *PFN_CreateDeviceEx)(void*, const GUID*, const GUID*, void**, IUnknown*);
typedef HRESULT (WINAPI *PFN_Enum)(void*, DWORD, LPDIENUMDEVICESCALLBACKA, void*, DWORD);

static HRESULT WINAPI H_CreateDevice(void* self, const GUID* g, void** dev, IUnknown* outer)
{
    HRESULT hr = ((PFN_CreateDevice)Orig(self, 3))(self, g, dev, outer);
    char gs[48] = "null";
    if (g) GuidStr(*g, gs);
    (FAILED(hr) ? LogErr : Log)("CreateDevice guid=%s hr=%lX", gs, (unsigned long)hr);
    if (SUCCEEDED(hr) && dev && *dev && g) PatchDevice(*dev, *g);
    return hr;
}

static HRESULT WINAPI H_CreateDeviceEx(void* self, const GUID* g, const GUID* riid, void** dev, IUnknown* outer)
{
    HRESULT hr = ((PFN_CreateDeviceEx)Orig(self, 9))(self, g, riid, dev, outer);
    char gs[48] = "null";
    if (g) GuidStr(*g, gs);
    (FAILED(hr) ? LogErr : Log)("CreateDeviceEx guid=%s hr=%lX", gs, (unsigned long)hr);
    if (SUCCEEDED(hr) && dev && *dev && g) { PatchDevice(*dev, *g); PatchFF(*dev); }   // Device7 has the FFB slots
    return hr;
}

struct EnumCtx { LPDIENUMDEVICESCALLBACKA cb; void* ref; };

static BOOL CALLBACK EnumCb(const DIDEVICEINSTANCEA* d, void* ref)
{
    EnumCtx* c = (EnumCtx*)ref;
    char gi[48], gp[48];
    GuidStr(d->guidInstance, gi);
    GuidStr(d->guidProduct, gp);
    Log("  Enum: \"%s\" type=%lX VID=%04lX PID=%04lX guidProduct=%s guidInstance=%s", d->tszProductName,
        d->dwDevType, d->guidProduct.Data1 & 0xFFFF, d->guidProduct.Data1 >> 16, gp, gi);
    if (IsHidden(d->guidProduct.Data1 & 0xFFFF, d->guidProduct.Data1 >> 16)) {
        Log("    -> hidden from game");
        return DIENUM_CONTINUE;
    }
    DWORD vid = d->guidProduct.Data1 & 0xFFFF, pid = d->guidProduct.Data1 >> 16;
    if (d->dwSize <= sizeof(DIDEVICEINSTANCEA)) {
        DIDEVICEINSTANCEA tmp;
        memcpy(&tmp, d, d->dwSize);
        bool mod = false;
        if (SpoofType(vid, pid, d->dwDevType) != d->dwDevType) { tmp.dwDevType = SpoofType(vid, pid, d->dwDevType); mod = true; }
        if (ApplyIdentity(&tmp, vid, pid)) mod = true;
        if (mod) return c->cb(&tmp, c->ref);
    }
    return c->cb(d, c->ref);
}

static HRESULT WINAPI H_EnumDevices(void* self, DWORD type, LPDIENUMDEVICESCALLBACKA cb, void* ref, DWORD flags)
{
    EnterCriticalSection(&g_cs);
    LoadConfig();
    LeaveCriticalSection(&g_cs);
    Log("EnumDevices type=%lu flags=%lX", type, flags);
    EnumCtx c = { cb, ref };
    return ((PFN_Enum)Orig(self, 4))(self, type, cb ? EnumCb : NULL, &c, flags);
}

// ---------------------------------------------------------------- winmm joystick hook (games that read joyGetPosEx/joyGetPos)
// The game's IAT entries are redirected; the real call runs first, then the state of the configured wheel is
// converted to the common state layout, run through mask/map/shape, and converted back.
typedef MMRESULT (WINAPI *PFN_joyGetPosEx)(UINT, LPJOYINFOEX);
typedef MMRESULT (WINAPI *PFN_joyGetPos)(UINT, LPJOYINFO);
typedef MMRESULT (WINAPI *PFN_joyGetDevCapsA)(UINT_PTR, LPJOYCAPSA, UINT);
static PFN_joyGetPosEx o_joyGetPosEx;
static PFN_joyGetPos o_joyGetPos;
static PFN_joyGetDevCapsA o_joyGetDevCapsA;
static Rec g_wmRec;

// Re-reads the settings that can change while the game runs: masks, neutrals, mappings, shaping, FFB boost.
// (Hide, device type, identity and capability overrides apply to devices as the game creates them: restart needed.)
static void MaybeReload()     // g_cs held
{
    static DWORD lastCheck;
    DWORD now = GetTickCount();
    LogTick();
    if (!g_cfgLoaded || now - lastCheck < 500) return;
    lastCheck = now;
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (!GetFileAttributesExA(g_ini, GetFileExInfoStandard, &fa)) return;
    if (CompareFileTime(&fa.ftLastWriteTime, &g_iniTime) == 0) return;
    g_iniTime = fa.ftLastWriteTime;
    g_nMask = g_nAmap = g_nBmap = g_nAbmap = g_nShape = g_nCmap = 0;
    ParseMask();
    ParseMaps();
    ParseShape();
    ParseCombine();
    g_ffBoost = GetPrivateProfileIntA("FFB", "Boost", 100, g_ini);
    ReloadLogMode(g_ini);
    for (int i = 0; i < g_nRec; i++) ConfigureRec(&g_rec[i]);
    if (g_wmEnabled) ConfigureRec(&g_wmRec);
    Log("Config reloaded (masks, mappings, shaping, FFB boost)");
}

// winmm axes X Y Z R U V = common axes X Y Z Rz S0 S1 (Rx/Ry have no winmm field).
static void WmToState(BYTE* st, LONG x, LONG y, LONG z, LONG r, LONG u, LONG v, DWORD buttons)
{
    memset(st, 0, 80);
    LONG* ax = (LONG*)st;
    ax[0] = x; ax[1] = y; ax[2] = z; ax[3] = 32767; ax[4] = 32767; ax[5] = r; ax[6] = u; ax[7] = v;
    for (int i = 0; i < 32; i++) if ((buttons >> i) & 1) st[48 + i] = 0x80;
}

static DWORD WmButtons(const BYTE* st)
{
    DWORD b = 0;
    for (int i = 0; i < 32; i++) if (st[48 + i] & 0x80) b |= 1u << i;
    return b;
}

// Diagnostic: logs what the game asks from every joystick id and what it gets (raw, before the wheel is rewritten);
// a line is written when id/flags/buttons change, at most 300 lines.
static void WmTrace(const char* fn, UINT id, MMRESULT res, DWORD flags, DWORD buttons, DWORD x, DWORD y)
{
    static struct { UINT id; DWORD flags, buttons; bool used; } last[16];
    static int lines;
    if (id >= 16 || lines >= 300) return;
    if (last[id].used && last[id].flags == flags && last[id].buttons == buttons) return;
    last[id].used = true; last[id].flags = flags; last[id].buttons = buttons; lines++;
    Log("winmm TRACE %s id=%u rc=%u flags=%lX raw btn=%08lX X=%lu Y=%lu", fn, id, (unsigned)res, flags, buttons, x, y);
}

// Source=auto: the real winmm joystick id of the chosen wheel, found by VID:PID (asked from the original joyGetDevCapsA, so
// identity overrides do not matter). Not found: -1, and the game gets "unplugged" - no other joystick is guessed in its place.
static UINT WmRealId(UINT id)
{
    if (!g_wmAuto || id != g_wmId) return id;
    DWORD now = GetTickCount();
    if (g_wmReal < 0 && (g_wmResolveTick == 0 || now - g_wmResolveTick > 2000)) {
        g_wmResolveTick = now ? now : 1;
        PFN_joyGetDevCapsA f = o_joyGetDevCapsA;
        if (!f) f = (PFN_joyGetDevCapsA)GetProcAddress(GetModuleHandleA("winmm.dll"), "joyGetDevCapsA");
        LONG found = -1;
        for (UINT i = 0; f && i < 16; i++) {
            JOYCAPSA c;
            if (f(i, &c, sizeof c) == JOYERR_NOERROR && c.wMid == g_wmVid && c.wPid == g_wmPid) { found = (LONG)i; break; }
        }
        static LONG lastLogged = -2;
        if (found != lastLogged) {
            bool wasMissing = lastLogged == -1;          // it was not found before: this is a recovery
            lastLogged = found;
            if (found >= 0) (wasMissing ? LogEvt : Log)("winmm: wheel %04lX:%04lX is joystick %ld (the game sees it as id %u)", g_wmVid, g_wmPid, found, g_wmId);
            else LogErr("winmm: wheel %04lX:%04lX NOT FOUND among the joysticks - the game gets no joystick. Make a new profile with the controller you use now.", g_wmVid, g_wmPid);
        }
        g_wmReal = found;
    }
    return g_wmReal < 0 ? (UINT)-1 : (UINT)g_wmReal;
}

static MMRESULT WINAPI H_joyGetPosEx(UINT id, LPJOYINFOEX p)
{
    UINT real = WmRealId(id);
    if (real == (UINT)-1) return JOYERR_UNPLUGGED;
    MMRESULT res = o_joyGetPosEx(real, p);
    if (res != JOYERR_NOERROR && g_wmAuto && id == g_wmId) g_wmReal = -1;      // the device went away: look again
    WmTrace("joyGetPosEx", real, res, p ? p->dwFlags : 0, p ? p->dwButtons : 0, p ? p->dwXpos : 0, p ? p->dwYpos : 0);
    if (res != JOYERR_NOERROR || !p || id != g_wmId || p->dwSize < sizeof(JOYINFOEX)) return res;
    static DWORD lastFlags = 0xFFFFFFFF;
    if (p->dwFlags != lastFlags) { lastFlags = p->dwFlags; Log("winmm joyGetPosEx id=%u flags=%lX", id, p->dwFlags); }
    BYTE st[80];
    WmToState(st, p->dwXpos, p->dwYpos, p->dwZpos, p->dwRpos, p->dwUpos, p->dwVpos, p->dwButtons);
    EnterCriticalSection(&g_cs);
    MaybeReload();
    g_wmRec.n++;
    ProcessOutput(&g_wmRec, st, 80);
    LivePublish(&g_wmRec, st, 80);
    LeaveCriticalSection(&g_cs);
    const LONG* ax = (const LONG*)st;
    DWORD f = p->dwFlags;
    if (f & JOY_RETURNX) p->dwXpos = ax[0];
    if (f & JOY_RETURNY) p->dwYpos = ax[1];
    if (f & JOY_RETURNZ) p->dwZpos = ax[2];
    if (f & JOY_RETURNR) p->dwRpos = ax[5];
    if (f & JOY_RETURNU) p->dwUpos = ax[6];
    if (f & JOY_RETURNV) p->dwVpos = ax[7];
    if (f & JOY_RETURNBUTTONS) {
        p->dwButtons = WmButtons(st);
        DWORD n = 0;
        for (DWORD b = p->dwButtons; b; b &= b - 1) n++;
        p->dwButtonNumber = n;
    }
    return res;
}

static MMRESULT WINAPI H_joyGetPos(UINT id, LPJOYINFO p)
{
    UINT real = WmRealId(id);
    if (real == (UINT)-1) return JOYERR_UNPLUGGED;
    MMRESULT res = o_joyGetPos(real, p);
    if (res != JOYERR_NOERROR && g_wmAuto && id == g_wmId) g_wmReal = -1;
    if (res != JOYERR_NOERROR || !p || id != g_wmId) return res;
    static bool logged;
    if (!logged) { logged = true; Log("winmm joyGetPos id=%u (first call)", (unsigned)id); }
    BYTE st[80];
    WmToState(st, p->wXpos, p->wYpos, p->wZpos, 32767, 32767, 32767, p->wButtons & 0xF);
    EnterCriticalSection(&g_cs);
    g_wmRec.n++;
    ProcessOutput(&g_wmRec, st, 80);
    LeaveCriticalSection(&g_cs);
    const LONG* ax = (const LONG*)st;
    p->wXpos = ax[0]; p->wYpos = ax[1]; p->wZpos = ax[2];
    p->wButtons = WmButtons(st) & 0xF;          // joyGetPos only carries 4 buttons
    return res;
}

static MMRESULT WINAPI H_joyGetDevCapsA(UINT_PTR id, LPJOYCAPSA p, UINT cb)
{
    UINT real = WmRealId((UINT)id);
    if (real == (UINT)-1) return JOYERR_UNPLUGGED;
    MMRESULT res = o_joyGetDevCapsA(real, p, cb);
    if (p && res == JOYERR_NOERROR && cb >= sizeof(JOYCAPSA)) Log("winmm TRACE joyGetDevCapsA id=%u: mid=%04X pid=%04X axes=%u buttons=%u caps=%X", (unsigned)id, p->wMid, p->wPid, p->wNumAxes, p->wNumButtons, p->wCaps);
    if (res != JOYERR_NOERROR || !p || id != g_wmId || cb < sizeof(JOYCAPSA)) return res;
    if (g_wmAxes > 0) {
        p->wNumAxes = g_wmAxes;
        if (g_wmAxes >= 3) p->wCaps |= JOYCAPS_HASZ;
        if (g_wmAxes >= 4) p->wCaps |= JOYCAPS_HASR;
        if (g_wmAxes >= 5) p->wCaps |= JOYCAPS_HASU;
        if (g_wmAxes >= 6) p->wCaps |= JOYCAPS_HASV;
        if (!p->wZmax) { p->wZmin = p->wXmin; p->wZmax = p->wXmax; }
        if (!p->wRmax) { p->wRmin = p->wXmin; p->wRmax = p->wXmax; }
        if (!p->wUmax) { p->wUmin = p->wXmin; p->wUmax = p->wXmax; }
        if (!p->wVmax) { p->wVmin = p->wXmin; p->wVmax = p->wXmax; }
    }
    if (g_wmButtons > 0) { p->wNumButtons = g_wmButtons; if (p->wMaxButtons < (UINT)g_wmButtons) p->wMaxButtons = g_wmButtons; }
    EnterCriticalSection(&g_cs);
    for (int i = 0; i < g_nId; i++)
        if (g_id[i].vid == g_wmVid && g_id[i].pid == g_wmPid) {
            p->wMid = (WORD)g_id[i].nvid; p->wPid = (WORD)g_id[i].npid;
            if (g_id[i].name[0]) _snprintf(p->szPname, sizeof p->szPname, "%s", g_id[i].name);
        }
    LeaveCriticalSection(&g_cs);
    Log("winmm joyGetDevCaps id=%u: axes=%u buttons=%u caps=%X", (unsigned)id, p->wNumAxes, p->wNumButtons, p->wCaps);
    return res;
}

// Redirects the import "func" of "dll" in module mod to hook; returns true and the original pointer on success.
static bool IatHook(HMODULE mod, const char* dll, const char* func, void* hook, void** orig)
{
    BYTE* base = (BYTE*)mod;
    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return false;
    for (IMAGE_IMPORT_DESCRIPTOR* d = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); d->Name; d++) {
        if (_stricmp((const char*)(base + d->Name), dll) != 0) continue;
        IMAGE_THUNK_DATA* ot = (IMAGE_THUNK_DATA*)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA* ft = (IMAGE_THUNK_DATA*)(base + d->FirstThunk);
        for (; ot->u1.AddressOfData; ot++, ft++) {
            if (IMAGE_SNAP_BY_ORDINAL(ot->u1.Ordinal)) continue;
            const IMAGE_IMPORT_BY_NAME* ibn = (const IMAGE_IMPORT_BY_NAME*)(base + ot->u1.AddressOfData);
            if (strcmp((const char*)ibn->Name, func) != 0) continue;
            DWORD old;
            if (!VirtualProtect(&ft->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) return false;
            *orig = (void*)ft->u1.Function;
            ft->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&ft->u1.Function, sizeof(void*), old, &old);
            return true;
        }
    }
    return false;
}

static void WinmmInstall()
{
    EnterCriticalSection(&g_cs);
    LoadConfig();
    if (!g_wmEnabled) { LeaveCriticalSection(&g_cs); return; }
    memset(&g_wmRec, 0, sizeof g_wmRec);
    g_wmRec.vid = g_wmVid; g_wmRec.pid = g_wmPid;
    _snprintf(g_wmRec.name, sizeof g_wmRec.name, "winmm:%04lX:%04lX", g_wmVid, g_wmPid);
    g_wmRec.fmtKnown = true; g_wmRec.fmtSize = 80;
    for (int a = 0; a < 8; a++) { g_wmRec.axMin[a] = 0; g_wmRec.axMax[a] = 65535; }
    for (int a = 0; a < 8; a++) g_wmRec.axOfs[a] = a * 4;
    for (int b = 0; b < 128; b++) g_wmRec.btnOfs[b] = b < 32 ? 48 + b : -1;
    ConfigureRec(&g_wmRec);
    HMODULE exe = GetModuleHandleA(NULL);
    bool a = IatHook(exe, "WINMM.dll", "joyGetPosEx", (void*)H_joyGetPosEx, (void**)&o_joyGetPosEx);
    bool b = IatHook(exe, "WINMM.dll", "joyGetPos", (void*)H_joyGetPos, (void**)&o_joyGetPos);
    bool c = IatHook(exe, "WINMM.dll", "joyGetDevCapsA", (void*)H_joyGetDevCapsA, (void**)&o_joyGetDevCapsA);
    Log("winmm IAT hooks in game module: joyGetPosEx=%d joyGetPos=%d joyGetDevCapsA=%d", a, b, c);
    LeaveCriticalSection(&g_cs);
}

// W (unicode) enumeration: same filtering as the A version (hide, device type, identity) on DIDEVICEINSTANCEW
typedef HRESULT (WINAPI *PFN_EnumW)(void*, DWORD, LPDIENUMDEVICESCALLBACKW, void*, DWORD);
struct EnumCtxW { LPDIENUMDEVICESCALLBACKW cb; void* ref; };

static BOOL CALLBACK EnumCbW(const DIDEVICEINSTANCEW* d, void* ref)
{
    EnumCtxW* c = (EnumCtxW*)ref;
    char name[128];
    WideCharToMultiByte(CP_ACP, 0, d->tszProductName, -1, name, sizeof name, NULL, NULL);
    DWORD vid = d->guidProduct.Data1 & 0xFFFF, pid = d->guidProduct.Data1 >> 16;
    Log("  EnumW: \"%s\" type=%lX VID=%04lX PID=%04lX", name, d->dwDevType, vid, pid);
    if (IsHidden(vid, pid)) {
        Log("    -> hidden from game");
        return DIENUM_CONTINUE;
    }
    if (d->dwSize <= sizeof(DIDEVICEINSTANCEW)) {
        DIDEVICEINSTANCEW tmp;
        memcpy(&tmp, d, d->dwSize);
        bool mod = false;
        if (SpoofType(vid, pid, d->dwDevType) != d->dwDevType) { tmp.dwDevType = SpoofType(vid, pid, d->dwDevType); mod = true; }
        if (ApplyIdentityW(&tmp, vid, pid)) mod = true;
        if (mod) return c->cb(&tmp, c->ref);
    }
    return c->cb(d, c->ref);
}

static HRESULT WINAPI H_EnumDevicesW(void* self, DWORD type, LPDIENUMDEVICESCALLBACKW cb, void* ref, DWORD flags)
{
    EnterCriticalSection(&g_cs);
    LoadConfig();
    LeaveCriticalSection(&g_cs);
    Log("EnumDevices(W) type=%lu flags=%lX", type, flags);
    EnumCtxW c = { cb, ref };
    return ((PFN_EnumW)Orig(self, 4))(self, type, cb ? EnumCbW : NULL, &c, flags);
}

// ---------------------------------------------------------------- public interface (core.h)
void Core_PatchDI(void* di, unsigned flags)
{
    if (flags & CORE_DI_WIDE) g_wide = true;
    PatchSlot(di, 3, (void*)H_CreateDevice);
    PatchSlot(di, 4, (flags & CORE_DI_WIDE) ? (void*)H_EnumDevicesW : (void*)H_EnumDevices);
    if (flags & CORE_DI_V7) PatchSlot(di, 9, (void*)H_CreateDeviceEx);     // IDirectInput7 only
    if (flags & CORE_DI_V8) g_di8 = true;
}

void Core_SetSourceFactory(Core_SourceFactory f) { g_srcFactory = f; }

void Core_Attach(HINSTANCE self)
{
    g_self = self;
    InitializeCriticalSection(&g_cs);
    LiveInit();
    WinmmInstall();
}

void Core_Log(const char* fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf(buf, sizeof buf, fmt, ap);
    buf[sizeof buf - 1] = 0;
    va_end(ap);
    Log("%s", buf);
}

void Core_LogErr(const char* fmt, ...)
{
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf(buf, sizeof buf, fmt, ap);
    buf[sizeof buf - 1] = 0;
    va_end(ap);
    LogErr("%s", buf);
}

// DLL_PROCESS_DETACH: whatever is still in the log buffer goes to disk (no waiting for a lock a dead thread may hold).
void Core_Detach()
{
    if (TryEnterCriticalSection(&g_cs)) { FlushLog(true); LeaveCriticalSection(&g_cs); }
}

