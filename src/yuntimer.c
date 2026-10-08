/* 定时关机助手（YunTimer 复刻版 · 原生 C 版）
 * 复刻自 www.yunguanji.com 的 YunTimer v2.0.1.9「定时关机助手」，原创实现。
 *
 * 与原版相同的技术形态：纯 Win32 API + GDI 自绘界面，无运行时、无 JIT，
 * 双击即开（数十毫秒级）。单文件源码，gcc(mingw-w64) 编译，约 45KB。
 * 界面结构对齐原版：紧凑时间头 + 指定时间/倒计时页签 + "HH : MM" 时间盒（真 EDIT 控件，
 * 支持输入/光标/选中）+ 单滑块控制焦点段 + 执行按钮。
 *
 * 构建（见 build.cmd）：
 *   windres src\app.rc -O coff -o src\app_res.o
 *   GUI 主程序  : gcc -O2 -municode -mwindows -o dist\定时关机助手.exe src\yuntimer.c src\app_res.o -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
 *   自检运行器  : gcc -O2 -DTEST_BUILD -o dist\selftest.exe src\yuntimer.c src\app_res.o -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
 *
 * 运行模式：
 *   定时关机助手.exe            正常模式（定时到达后真实执行）
 *   定时关机助手.exe --simulate 模拟模式（动作仅写入 simulate.log，绝不真正执行）
 *   selftest.exe --selftest     逻辑自检（无界面，无任何系统副作用）
 *   selftest.exe --uitest       窗口创建冒烟测试（不显示窗口）
 */

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <dwmapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifndef DWMWA_USE_IMMERSIVE_DARK_MODE
#define DWMWA_USE_IMMERSIVE_DARK_MODE 20
#endif
#ifndef DWMWA_CAPTION_COLOR
#define DWMWA_CAPTION_COLOR 35
#endif
#ifndef DWMWA_BORDER_COLOR
#define DWMWA_BORDER_COLOR 34
#endif

#define WM_TRAY (WM_APP + 1)

#define APP_TITLE   L"定时关机助手"
#define REPO_URL    L"https://github.com/118coder/YunTimerClone"
#define RUN_KEY     L"Software\\Microsoft\\Windows\\CurrentVersion\\Run"
#define RUN_VALUE   L"定时关机助手"
#define CLASS_MAIN  L"YunTimerMainWnd"
#define CLASS_DLG   L"YunTimerConfirmWnd"
#define IDC_EDIT_H  101
#define IDC_EDIT_M  102

/* ---------- 配色（Fluent 深色） ---------- */
#define COL_BG        RGB(0x1B,0x1B,0x1F)
#define COL_ACCENT    RGB(0x60,0xCD,0xFF)
#define COL_DANGER    RGB(0xDC,0x26,0x26)
#define COL_CARD      RGB(0x2D,0x2D,0x33)
#define COL_CARD_LINE RGB(0x3A,0x3A,0x41)
#define COL_SEG       RGB(0x2A,0x2A,0x30)
#define COL_HOVER     RGB(0x38,0x38,0x40)
#define COL_TRACK     RGB(0x3A,0x3A,0x41)
#define COL_TXT       RGB(0xFF,0xFF,0xFF)
#define COL_TXT2      RGB(0xC9,0xC9,0xCE)
#define COL_TXT3      RGB(0x8B,0x8B,0x92)
#define COL_DARKTXT   RGB(0x1B,0x1B,0x1F)
#define COL_SEGTXT    RGB(0xB3,0xB3,0xB8)
#define COL_EDIT_BG   RGB(0x21,0x21,0x26)
#define COL_EDIT_FOC  RGB(0x1E,0x3A,0x52)

/* ---------- 前向声明 ---------- */
typedef struct AppConfig_t {
    int action;
    int warnSeconds;
    int forceWait;
    int mode;          /* 0=指定时间 1=倒计时 */
    int hour;
    int minute;
} AppConfig_t;

static BOOL  autostart_is_set(void);
static void  config_path(wchar_t* out, size_t cch);
static void  config_save(const AppConfig_t* c, const wchar_t* path);

/* ---------- 核心逻辑 ---------- */
enum PowerAction { PA_SHUTDOWN = 0, PA_REBOOT = 1, PA_LOGOFF = 2, PA_HIBERNATE = 3, PA_LOCK = 4 };

static const wchar_t* action_name(int a)
{
    switch (a) {
        case PA_SHUTDOWN: return L"关机";
        case PA_REBOOT:   return L"重启";
        case PA_LOGOFF:   return L"注销";
        case PA_HIBERNATE:return L"休眠";
        case PA_LOCK:     return L"锁定";
    }
    return L"关机";
}

/* 纯函数：只生成命令行，不执行（供自检断言） */
static void get_command(int action, const wchar_t** exe, const wchar_t** args)
{
    switch (action) {
        case PA_SHUTDOWN:  *exe = L"shutdown";     *args = L"-s -t 0"; return;
        case PA_REBOOT:    *exe = L"shutdown";     *args = L"-r -t 0"; return;
        case PA_LOGOFF:    *exe = L"shutdown";     *args = L"-l"; return;
        case PA_HIBERNATE: *exe = L"rundll32.exe"; *args = L"powrprof.dll,SetSuspendState 0,1,0"; return;
        case PA_LOCK:      *exe = L"rundll32.exe"; *args = L"user32.dll,LockWorkStation"; return;
    }
    *exe = L"shutdown"; *args = L"-s -t 0";
}

/* ---------- 配置 ---------- */
static void config_defaults(AppConfig_t* c)
{
    c->action = PA_SHUTDOWN;
    c->warnSeconds = 60;
    c->forceWait = 30;
    c->mode = 0;
    c->hour = 23;
    c->minute = 30;
}

static BOOL file_writable(const wchar_t* path)
{
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return FALSE;
    CloseHandle(h);
    DeleteFileW(path);
    return TRUE;
}

static void config_path(wchar_t* out, size_t cch)
{
    wchar_t exeDir[MAX_PATH], probe[MAX_PATH];
    GetModuleFileNameW(NULL, exeDir, MAX_PATH);
    wchar_t* slash = wcsrchr(exeDir, L'\\');
    if (slash) *(slash + 1) = 0;
    _snwprintf(probe, MAX_PATH - 1, L"%s.write_test", exeDir);
    probe[MAX_PATH - 1] = 0;
    if (file_writable(probe)) {
        _snwprintf(out, cch - 1, L"%sconfig.ini", exeDir);
    } else {
        wchar_t appData[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH);
        if (n > 0 && n < MAX_PATH) {
            _snwprintf(out, cch - 1, L"%s\\定时关机助手", appData);
            out[cch - 1] = 0;
            CreateDirectoryW(out, NULL);
            _snwprintf(out, cch - 1, L"%s\\定时关机助手\\config.ini", appData);
        } else {
            _snwprintf(out, cch - 1, L"config.ini");
        }
    }
    out[cch - 1] = 0;
}

static void config_load(AppConfig_t* c, const wchar_t* path)
{
    config_defaults(c);
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return;
    c->action = GetPrivateProfileIntW(L"Config", L"Action", c->action, path);
    if (c->action < 0 || c->action > 4) c->action = PA_SHUTDOWN;
    c->warnSeconds = GetPrivateProfileIntW(L"Config", L"WarnSeconds", c->warnSeconds, path);
    if (c->warnSeconds < 10 || c->warnSeconds > 3600) c->warnSeconds = 60;
    c->forceWait = GetPrivateProfileIntW(L"Config", L"ForceWaitSeconds", c->forceWait, path);
    if (c->forceWait < 5 || c->forceWait > 3600) c->forceWait = 30;
    c->mode = GetPrivateProfileIntW(L"Config", L"Mode", c->mode, path);
    if (c->mode != 0 && c->mode != 1) c->mode = 0;
    c->hour = GetPrivateProfileIntW(L"Task", L"Hour", c->hour, path);
    if (c->hour < 0) c->hour = 0;
    c->minute = GetPrivateProfileIntW(L"Task", L"Minute", c->minute, path);
    if (c->minute < 0) c->minute = 0;
}

static void config_save(const AppConfig_t* c, const wchar_t* path)
{
    wchar_t buf[32];
    wsprintfW(buf, L"%d", c->action);      WritePrivateProfileStringW(L"Config", L"Action", buf, path);
    wsprintfW(buf, L"%d", c->warnSeconds); WritePrivateProfileStringW(L"Config", L"WarnSeconds", buf, path);
    wsprintfW(buf, L"%d", c->forceWait);   WritePrivateProfileStringW(L"Config", L"ForceWaitSeconds", buf, path);
    WritePrivateProfileStringW(L"Config", L"AutoStart", autostart_is_set() ? L"1" : L"0", path);
    wsprintfW(buf, L"%d", c->mode);        WritePrivateProfileStringW(L"Config", L"Mode", buf, path);
    wsprintfW(buf, L"%d", c->hour);        WritePrivateProfileStringW(L"Task", L"Hour", buf, path);
    wsprintfW(buf, L"%d", c->minute);      WritePrivateProfileStringW(L"Task", L"Minute", buf, path);
}

/* ---------- 注册表自启动 ---------- */
static BOOL autostart_is_set(void)
{
    HKEY k;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, RUN_KEY, 0, KEY_QUERY_VALUE, &k) != ERROR_SUCCESS) return FALSE;
    DWORD type = 0, cb = 0;
    LONG r = RegQueryValueExW(k, RUN_VALUE, NULL, &type, NULL, &cb);
    RegCloseKey(k);
    return r == ERROR_SUCCESS;
}

static void autostart_set(BOOL on)
{
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, RUN_KEY, 0, NULL, 0, KEY_SET_VALUE, NULL, &k, NULL) != ERROR_SUCCESS) return;
    if (on) {
        wchar_t exe[MAX_PATH], quoted[MAX_PATH + 4];
        GetModuleFileNameW(NULL, exe, MAX_PATH);
        _snwprintf(quoted, MAX_PATH + 3, L"\"%s\"", exe);
        quoted[MAX_PATH + 3] = 0;
        RegSetValueExW(k, RUN_VALUE, 0, REG_SZ, (const BYTE*)quoted, (DWORD)((wcslen(quoted) + 1) * sizeof(wchar_t)));
    } else {
        RegDeleteValueW(k, RUN_VALUE);
    }
    RegCloseKey(k);
}

/* ---------- 时间工具 ---------- */
static __int64 st_to_100ns(const SYSTEMTIME* st)
{
    FILETIME ft;
    if (!SystemTimeToFileTime(st, &ft)) return 0;
    return ((__int64)ft.dwHighDateTime << 32) | (__int64)ft.dwLowDateTime;
}

static void ns100_to_st(__int64 v, SYSTEMTIME* st)
{
    FILETIME ft;
    ft.dwHighDateTime = (DWORD)((unsigned __int64)v >> 32);
    ft.dwLowDateTime = (DWORD)((unsigned __int64)v & 0xFFFFFFFFu);
    FileTimeToSystemTime(&ft, st);
}

static void st_add_seconds(const SYSTEMTIME* st, long seconds, SYSTEMTIME* out)
{
    ns100_to_st(st_to_100ns(st) + (__int64)seconds * 10000000, out);
}

static int st_cmp(const SYSTEMTIME* a, const SYSTEMTIME* b)
{
    __int64 x = st_to_100ns(a), y = st_to_100ns(b);
    if (x < y) return -1;
    if (x > y) return 1;
    return 0;
}

/* ---------- 定时任务 ---------- */
typedef struct {
    int isCountdown;   /* 0=指定时间 1=倒计时 */
    int hour, minute;
    int action;
} Task;

typedef struct {
    Task task;
    int armed;
    SYSTEMTIME fireAt;   /* 设定时刻即固定，避免固定任务“每次重算”永不触发 */
} Engine;

enum { VR_OK = 0, VR_BAD = 1, VR_PAST = 2 };

static int validate_fixed(int hour, int minute, const SYSTEMTIME* now)
{
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return VR_BAD;
    SYSTEMTIME t = *now;
    t.wHour = (WORD)hour; t.wMinute = (WORD)minute; t.wSecond = 0; t.wMilliseconds = 0;
    if (st_cmp(&t, now) <= 0) return VR_PAST;
    return VR_OK;
}

static int validate_count(int hour, int minute)
{
    if (hour < 0 || hour > 99 || minute < 0 || minute > 59) return VR_BAD;
    if (hour == 0 && minute == 0) return VR_BAD;
    return VR_OK;
}

static void next_fire(const Task* task, const SYSTEMTIME* now, SYSTEMTIME* out)
{
    if (task->isCountdown) {
        st_add_seconds(now, (long)task->hour * 3600 + (long)task->minute * 60, out);
        return;
    }
    SYSTEMTIME t = *now;
    t.wHour = (WORD)task->hour; t.wMinute = (WORD)task->minute; t.wSecond = 0; t.wMilliseconds = 0;
    if (st_cmp(&t, now) <= 0) st_add_seconds(&t, 24 * 3600, &t);
    *out = t;
}

static void engine_arm(Engine* e, const Task* task, const SYSTEMTIME* now)
{
    e->task = *task;
    e->armed = 1;
    next_fire(task, now, &e->fireAt);
}

static void engine_disarm(Engine* e)
{
    e->armed = 0;
}

static int engine_tick(Engine* e, const SYSTEMTIME* now)
{
    if (!e->armed) return 0;
    if (st_cmp(now, &e->fireAt) >= 0) {
        e->armed = 0;
        return 1;
    }
    return 0;
}

static long engine_remaining_seconds(const Engine* e, const SYSTEMTIME* now)
{
    if (!e->armed) return 0;
    __int64 d = (st_to_100ns(&e->fireAt) - st_to_100ns(now)) / 10000000;
    return (long)(d < 0 ? 0 : d);
}

/* ---------- 执行器 ---------- */
static wchar_t g_simLog[MAX_PATH];
static int g_simulate = 0;
static int g_forceWait = 30;

static void simulate_log(int action, const wchar_t* reason)
{
    if (!g_simLog[0]) return;
    FILE* f = _wfopen(g_simLog, L"a, ccs=UTF-8");
    if (!f) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    fwprintf(f, L"[%04d-%02d-%02d %02d:%02d:%02d] 【模拟模式】%s（%s）— 未真正执行\r\n",
        (int)st.wYear, (int)st.wMonth, (int)st.wDay, (int)st.wHour, (int)st.wMinute, (int)st.wSecond,
        action_name(action), reason);
    fclose(f);
}

static DWORD WINAPI force_thread(LPVOID param)
{
    Sleep((DWORD)(intptr_t)param * 1000);
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    wchar_t cmd[64];
    wcscpy(cmd, L"shutdown -s -f -t 0");
    ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi);
    if (pi.hProcess) CloseHandle(pi.hProcess);
    if (pi.hThread) CloseHandle(pi.hThread);
    return 0;
}

/* 返回 NULL 表示成功；否则返回错误文本（静态缓冲） */
static const wchar_t* execute_action(int action, const wchar_t* reason)
{
    if (g_simulate) {
        simulate_log(action, reason);
        return NULL;
    }
    const wchar_t *exe, *args;
    get_command(action, &exe, &args);
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    wchar_t cmd[256];
    _snwprintf(cmd, 255, L"%s %s", exe, args);
    cmd[255] = 0;
    ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        static wchar_t err[512];
        _snwprintf(err, 511, L"%s失败：\r\nCreateProcess 错误 %lu", action_name(action), (unsigned long)GetLastError());
        err[511] = 0;
        return err;
    }
    if (pi.hProcess) CloseHandle(pi.hProcess);
    if (pi.hThread) CloseHandle(pi.hThread);

    if (action == PA_SHUTDOWN || action == PA_REBOOT) {
        HANDLE t = CreateThread(NULL, 0, force_thread, (LPVOID)(intptr_t)g_forceWait, 0, NULL);
        if (t) CloseHandle(t);
    }
    return NULL;
}

/* ---------- 自检（TEST_BUILD 控制台） ---------- */
#ifdef TEST_BUILD
static int g_failed = 0;
static void check(const char* name, int ok)
{
    if (ok) printf("PASS  %s\n", name);
    else { g_failed++; printf("FAIL  %s\n", name); }
}

static int run_selftest(void)
{
    printf("===== 定时关机助手 逻辑自检（模拟状态，不执行任何系统动作） =====\n");
    const wchar_t *exe, *args;
    get_command(PA_SHUTDOWN, &exe, &args);
    check("cmd.Shutdown", wcscmp(exe, L"shutdown") == 0 && wcscmp(args, L"-s -t 0") == 0);
    get_command(PA_REBOOT, &exe, &args);
    check("cmd.Reboot", wcscmp(exe, L"shutdown") == 0 && wcscmp(args, L"-r -t 0") == 0);
    get_command(PA_LOGOFF, &exe, &args);
    check("cmd.Logoff", wcscmp(exe, L"shutdown") == 0 && wcscmp(args, L"-l") == 0);
    get_command(PA_HIBERNATE, &exe, &args);
    check("cmd.Hibernate", wcscmp(exe, L"rundll32.exe") == 0 && wcsstr(args, L"SetSuspendState") != NULL);
    get_command(PA_LOCK, &exe, &args);
    check("cmd.Lock", wcscmp(exe, L"rundll32.exe") == 0 && wcsstr(args, L"LockWorkStation") != NULL);

    SYSTEMTIME now;
    now.wYear = 2026; now.wMonth = 10; now.wDay = 8; now.wDayOfWeek = 4;
    now.wHour = 10; now.wMinute = 0; now.wSecond = 0; now.wMilliseconds = 0;
    check("validate.Fixed.ok", validate_fixed(10, 5, &now) == VR_OK);
    check("validate.Fixed.past", validate_fixed(9, 0, &now) == VR_PAST);
    check("validate.Fixed.equalNow", validate_fixed(10, 0, &now) == VR_PAST);
    check("validate.Fixed.badHour", validate_fixed(24, 0, &now) == VR_BAD);
    check("validate.Fixed.badMin", validate_fixed(10, 60, &now) == VR_BAD);
    check("validate.Count.ok", validate_count(0, 1) == VR_OK);
    check("validate.Count.zero", validate_count(0, 0) == VR_BAD);
    check("validate.Count.bad", validate_count(99, 60) == VR_BAD);

    Task t1; t1.isCountdown = 0; t1.hour = 10; t1.minute = 5; t1.action = PA_SHUTDOWN;
    SYSTEMTIME f1; next_fire(&t1, &now, &f1);
    check("nextFire.sameDay", f1.wDay == 8 && f1.wHour == 10 && f1.wMinute == 5);
    SYSTEMTIME late = now; late.wHour = 23;
    Task t2; t2.isCountdown = 0; t2.hour = 0; t2.minute = 30; t2.action = PA_SHUTDOWN;
    SYSTEMTIME f2; next_fire(&t2, &late, &f2);
    check("nextFire.tomorrow", f2.wDay == 9 && f2.wHour == 0 && f2.wMinute == 30);
    Task t3; t3.isCountdown = 1; t3.hour = 0; t3.minute = 90; t3.action = PA_SHUTDOWN;
    SYSTEMTIME f3; next_fire(&t3, &now, &f3);
    check("nextFire.countdown90min", f3.wHour == 11 && f3.wMinute == 30);

    /* 关键回归：固定任务到点必须触发 */
    Engine e; ZeroMemory(&e, sizeof(e));
    Task t4; t4.isCountdown = 0; t4.hour = 10; t4.minute = 2; t4.action = PA_SHUTDOWN;
    engine_arm(&e, &t4, &now);
    int fired = 0;
    SYSTEMTIME cur = now;
    int i;
    for (i = 0; i < 130; i++) { st_add_seconds(&cur, 1, &cur); fired += engine_tick(&e, &cur); }
    check("engine.fixedFiresOnce", fired == 1 && e.armed == 0);
    fired += engine_tick(&e, &cur);
    check("engine.noRefire", fired == 1);

    Engine e2; ZeroMemory(&e2, sizeof(e2));
    Task t5; t5.isCountdown = 1; t5.hour = 0; t5.minute = 2; t5.action = PA_SHUTDOWN;
    engine_arm(&e2, &t5, &now);
    fired = 0;
    cur = now;
    for (i = 0; i < 60; i++) { st_add_seconds(&cur, 1, &cur); fired += engine_tick(&e2, &cur); }
    check("engine.notFiredEarly", fired == 0 && e2.armed == 1);
    for (i = 0; i < 70; i++) { st_add_seconds(&cur, 1, &cur); fired += engine_tick(&e2, &cur); }
    check("engine.firedOnce", fired == 1 && e2.armed == 0);

    /* 延迟 10 分钟 = 重新武装 */
    Task t6; t6.isCountdown = 1; t6.hour = 0; t6.minute = 10; t6.action = PA_SHUTDOWN;
    engine_arm(&e2, &t6, &cur);
    long rem = engine_remaining_seconds(&e2, &cur);
    check("decision.delayRearms", e2.armed == 1 && rem > 9 * 60 && rem <= 10 * 60);
    engine_disarm(&e2);
    check("decision.cancelDisarms", e2.armed == 0);

    /* 模拟执行器日志 */
    wchar_t tmpLog[MAX_PATH];
    GetTempPathW(MAX_PATH, tmpLog);
    wcscat(tmpLog, L"yuntimer_selftest_simulate.log");
    DeleteFileW(tmpLog);
    {
        wchar_t old[MAX_PATH];
        wcscpy(old, g_simLog);
        wcscpy(g_simLog, tmpLog);
        simulate_log(PA_SHUTDOWN, L"自检");
        simulate_log(PA_REBOOT, L"自检2");
        wcscpy(g_simLog, old);
    }
    check("sim.log", GetFileAttributesW(tmpLog) != INVALID_FILE_ATTRIBUTES);
    {
        FILE* f = _wfopen(tmpLog, L"r, ccs=UTF-8");
        wchar_t line[256]; int hits = 0;
        if (f) {
            while (fgetws(line, 256, f)) if (wcsstr(line, L"关机") || wcsstr(line, L"重启")) hits++;
            fclose(f);
        }
        check("sim.content", hits >= 2);
    }
    DeleteFileW(tmpLog);

    /* 配置读写往返 + 缺失/损坏 */
    wchar_t tmpCfg[MAX_PATH];
    GetTempPathW(MAX_PATH, tmpCfg);
    wcscat(tmpCfg, L"yuntimer_selftest_config.ini");
    DeleteFileW(tmpCfg);
    AppConfig_t c1; config_defaults(&c1);
    c1.action = PA_REBOOT; c1.warnSeconds = 120; c1.mode = 1; c1.hour = 5; c1.minute = 45;
    config_save(&c1, tmpCfg);
    AppConfig_t c2; config_load(&c2, tmpCfg);
    check("config.roundtrip", c2.action == PA_REBOOT && c2.warnSeconds == 120 && c2.mode == 1 && c2.hour == 5 && c2.minute == 45);
    AppConfig_t c3; config_load(&c3, L"Z:\\no_such_path\\no_file.ini");
    check("config.missing", c3.warnSeconds == 60 && c3.action == PA_SHUTDOWN);
    FILE* bad = _wfopen(tmpCfg, L"w, ccs=UTF-8");
    if (bad) { fwprintf(bad, L"garbage [[ == bad"); fclose(bad); }
    AppConfig_t c4; config_load(&c4, tmpCfg);
    check("config.corrupt", c4.warnSeconds == 60 && c4.action == PA_SHUTDOWN);
    DeleteFileW(tmpCfg);

    printf("===== 完成：失败 %d 项 =====\n", g_failed);
    return g_failed == 0 ? 0 : 1;
}
#endif /* TEST_BUILD */

/* ---------- UI ---------- */
enum {
    R_NONE = 0, R_SEG_FIXED, R_SEG_COUNT, R_SLIDER, R_TIMEBOX,
    R_CHIP0, R_BTN_OK, R_BTN_STOP, R_BTN_RUN, R_TOGGLE, R_LINK
};
#define R_CHIP_LAST (R_CHIP0 + 4)

static HWND g_wnd, g_editH, g_editM;
static HINSTANCE g_inst;
static HFONT g_fHeader, g_fLabel, g_fBtn, g_fBtnSmall, g_fSmall, g_fSec, g_fWarn, g_fTime;
static HBRUSH g_brEditIdle, g_brEditFocus;
static AppConfig_t g_cfg;
static Engine g_eng;
static int g_hour = 23, g_minute = 30;
static int g_mode = 0;
static int g_focusSeg = 1;          /* 1=小时段 2=分钟段（滑块与高亮跟随） */
static int g_updating = 0;
static int g_hover = R_NONE, g_dragSlider = 0;
static NOTIFYICONDATAW g_nid;
static BOOL g_created = FALSE;
static BOOL g_forceExit = FALSE;
static BOOL g_autoStart = FALSE;
static int g_dlgAction = PA_SHUTDOWN, g_dlgRemain = 60, g_dlgTotal = 60;
static int g_dlgDecision = 2;
static int g_uiTestMode = 0;
static HBITMAP g_trayBmp_unused = NULL;

/* 布局（客户区 430x560） */
static const RECT RC_SEG     = {56, 56, 374, 94};
static const RECT RC_CARD    = {16, 106, 414, 254};
static const RECT RC_TIMEBOX = {52, 124, 378, 186};
static const RECT RC_TRACK   = {44, 222, 386, 222};
static const RECT RC_BTNOK   = {16, 312, 414, 356};
static const RECT RC_BTNSTP  = {16, 366, 414, 404};
static const RECT RC_TOGGLE  = {16, 474, 58, 496};
static const RECT RC_BTNRUN  = {296, 470, 414, 502};
static const RECT RC_LINK    = {16, 512, 414, 532};

static RECT mkrect(int l, int t, int r, int b)
{
    RECT rc; rc.left = l; rc.top = t; rc.right = r; rc.bottom = b;
    return rc;
}

static BOOL pt_in(const RECT* r, int x, int y)
{
    return x >= r->left && x < r->right && y >= r->top && y < r->bottom;
}

static int hour_max(void) { return g_mode == 0 ? 23 : 99; }

static int hit_test(int x, int y)
{
    if (pt_in(&RC_SEG, x, y)) return x < (RC_SEG.left + RC_SEG.right) / 2 ? R_SEG_FIXED : R_SEG_COUNT;
    if (pt_in(&RC_CARD, x, y)) {
        if (pt_in(&RC_TIMEBOX, x, y)) return R_TIMEBOX;
        if (y >= RC_TRACK.top - 12 && y <= RC_TRACK.top + 12 && x >= RC_TRACK.left - 4 && x <= RC_TRACK.right + 4) return R_SLIDER;
        return R_NONE;
    }
    if (y >= 264 && y < 296 && x >= 84 && x < 400) {
        int idx = (x - 84) / 64;
        if (idx > 4) idx = 4;
        return R_CHIP0 + idx;
    }
    if (pt_in(&RC_BTNOK, x, y)) return R_BTN_OK;
    if (pt_in(&RC_BTNSTP, x, y)) return R_BTN_STOP;
    if (pt_in(&RC_TOGGLE, x, y)) return R_TOGGLE;
    if (pt_in(&RC_BTNRUN, x, y)) return R_BTN_RUN;
    if (pt_in(&RC_LINK, x, y) && x > RC_LINK.right - 160) return R_LINK;
    return R_NONE;
}

/* ---------- 绘制 ---------- */
static void draw_round(HDC dc, const RECT* r, int rad, COLORREF fill, COLORREF line)
{
    HBRUSH b = CreateSolidBrush(fill);
    HPEN p = line == 0 ? (HPEN)GetStockObject(NULL_PEN) : CreatePen(PS_SOLID, 1, line);
    HBRUSH ob = (HBRUSH)SelectObject(dc, b);
    HPEN op = (HPEN)SelectObject(dc, p);
    RoundRect(dc, r->left, r->top, r->right + 1, r->bottom + 1, rad * 2, rad * 2);
    SelectObject(dc, ob);
    SelectObject(dc, op);
    DeleteObject(b);
    if (line) DeleteObject(p);
}

static void draw_text_r(HDC dc, const wchar_t* s, const RECT* r, HFONT f, COLORREF col, UINT align)
{
    HFONT of = (HFONT)SelectObject(dc, f);
    SetTextColor(dc, col);
    SetBkMode(dc, TRANSPARENT);
    RECT rr = *r;
    DrawTextW(dc, s, -1, &rr, align | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS);
    SelectObject(dc, of);
}

static void fill_circle(HDC dc, int cx, int cy, int rad, COLORREF col)
{
    HBRUSH b = CreateSolidBrush(col);
    HPEN op = (HPEN)SelectObject(dc, (HPEN)GetStockObject(NULL_PEN));
    HBRUSH ob = (HBRUSH)SelectObject(dc, b);
    Ellipse(dc, cx - rad, cy - rad, cx + rad, cy + rad);
    SelectObject(dc, ob);
    SelectObject(dc, op);
    DeleteObject(b);
}

static void draw_slider(HDC dc, int value, int vmax, int hover)
{
    double frac = vmax > 0 ? (double)value / vmax : 0.0;
    double w = (RC_TRACK.right - RC_TRACK.left) - 14;
    if (w < 0) w = 0;
    RECT t = mkrect(RC_TRACK.left, RC_TRACK.top - 2, RC_TRACK.right, RC_TRACK.top + 2);
    draw_round(dc, &t, 2, COL_TRACK, 0);
    RECT f = mkrect(RC_TRACK.left, RC_TRACK.top - 2, RC_TRACK.left + (int)(7 + frac * w), RC_TRACK.top + 2);
    draw_round(dc, &f, 2, COL_ACCENT, 0);
    int cx = RC_TRACK.left + (int)(7 + frac * w);
    fill_circle(dc, cx, RC_TRACK.top, 7, hover ? RGB(0xE8,0xF6,0xFF) : RGB(0xFF,0xFF,0xFF));
}

static void draw_toggle(HDC dc)
{
    RECT pill = RC_TOGGLE;
    draw_round(dc, &pill, 11, g_autoStart ? COL_ACCENT : RGB(0x4A,0x4A,0x52), 0);
    int thumbX = g_autoStart ? RC_TOGGLE.right - 18 : RC_TOGGLE.left + 4;
    fill_circle(dc, thumbX + 7, (RC_TOGGLE.top + RC_TOGGLE.bottom) / 2, 7, RGB(0xFF,0xFF,0xFF));
}

static void paint_main(HDC dc)
{
    wchar_t buf[128];
    /* 紧凑时间头：日期居左，当前时间居右（对齐原版） */
    SYSTEMTIME st;
    GetLocalTime(&st);
    RECT hd = mkrect(24, 20, 220, 44);
    swprintf(buf, 127, L"%d年%d月%d日", st.wYear, st.wMonth, st.wDay);
    draw_text_r(dc, buf, &hd, g_fHeader, COL_TXT2, DT_LEFT);
    RECT ht = mkrect(210, 20, 406, 44);
    swprintf(buf, 127, L"%02d时%02d分%02d秒", st.wHour, st.wMinute, st.wSecond);
    draw_text_r(dc, buf, &ht, g_fHeader, COL_TXT2, DT_RIGHT);

    /* 页签：指定时间 / 倒计时 */
    RECT seg = RC_SEG;
    draw_round(dc, &seg, 6, COL_SEG, 0);
    RECT l = mkrect(RC_SEG.left + 3, RC_SEG.top + 3, (RC_SEG.left + RC_SEG.right) / 2 - 1, RC_SEG.bottom - 3);
    RECT r = mkrect((RC_SEG.left + RC_SEG.right) / 2 + 1, RC_SEG.top + 3, RC_SEG.right - 3, RC_SEG.bottom - 3);
    draw_round(dc, &l, 4, g_mode == 0 ? COL_ACCENT : (g_hover == R_SEG_FIXED ? COL_HOVER : COL_SEG), 0);
    draw_round(dc, &r, 4, g_mode == 1 ? COL_ACCENT : (g_hover == R_SEG_COUNT ? COL_HOVER : COL_SEG), 0);
    RECT lt = l, rt = r;
    lt.bottom -= 3; rt.bottom -= 3;
    draw_text_r(dc, L"指定时间", &lt, g_fLabel, g_mode == 0 ? COL_DARKTXT : COL_SEGTXT, DT_CENTER);
    draw_text_r(dc, L"倒计时", &rt, g_fLabel, g_mode == 1 ? COL_DARKTXT : COL_SEGTXT, DT_CENTER);

    /* 卡片：时间盒 + 单滑块 */
    RECT card = RC_CARD;
    draw_round(dc, &card, 8, COL_CARD, COL_CARD_LINE);
    RECT box = RC_TIMEBOX;
    draw_round(dc, &box, 8, COL_EDIT_BG, (GetFocus() == g_editH || GetFocus() == g_editM) ? COL_ACCENT : COL_CARD_LINE);
    /* 冒号分隔（两 EDIT 控件之间，由父窗体绘制） */
    RECT colon = mkrect(188, RC_TIMEBOX.top, 242, RC_TIMEBOX.bottom);
    draw_text_r(dc, L":", &colon, g_fTime, COL_TXT2, DT_CENTER);

    /* 单滑块控制焦点段 */
    int val = g_focusSeg == 1 ? g_hour : g_minute;
    int vmax = g_focusSeg == 1 ? hour_max() : 59;
    draw_slider(dc, val, vmax, g_hover == R_SLIDER || g_dragSlider);

    /* 动作芯片 */
    RECT la = mkrect(16, 268, 60, 290);
    draw_text_r(dc, L"执行", &la, g_fLabel, COL_TXT2, DT_LEFT);
    {
        static const wchar_t* names[5] = { L"关机", L"重启", L"注销", L"休眠", L"锁定" };
        int i;
        for (i = 0; i < 5; i++) {
            RECT ch = mkrect(84 + i * 64, 264, 84 + i * 64 + 60, 296);
            draw_round(dc, &ch, 5, g_cfg.action == i ? COL_ACCENT : (g_hover == R_CHIP0 + i ? COL_HOVER : COL_CARD), 0);
            RECT cht = ch; cht.bottom -= 2;
            draw_text_r(dc, names[i], &cht, g_fLabel, g_cfg.action == i ? COL_DARKTXT : COL_TXT, DT_CENTER);
        }
    }

    /* 执行 / 取消定时 */
    RECT ok = RC_BTNOK;
    draw_round(dc, &ok, 5, COL_ACCENT, 0);
    RECT okt = ok; okt.bottom -= 2;
    draw_text_r(dc, L"执  行", &okt, g_fBtn, COL_DARKTXT, DT_CENTER);
    RECT stp = RC_BTNSTP;
    draw_round(dc, &stp, 5, g_eng.armed ? COL_HOVER : COL_CARD, g_eng.armed ? 0 : COL_CARD_LINE);
    RECT stpt = stp; stpt.bottom -= 2;
    draw_text_r(dc, L"取消定时", &stpt, g_fBtnSmall, g_eng.armed ? COL_TXT : COL_TXT3, DT_CENTER);

    /* 状态 */
    if (g_eng.armed) {
        swprintf(buf, 127, L"将于 %d点%d分%d秒%s", g_eng.fireAt.wHour, g_eng.fireAt.wMinute, g_eng.fireAt.wSecond, action_name(g_eng.task.action));
        RECT s1 = mkrect(0, 414, 430, 436);
        draw_text_r(dc, buf, &s1, g_fLabel, COL_TXT2, DT_CENTER);
        SYSTEMTIME now;
        GetLocalTime(&now);
        long rem = engine_remaining_seconds(&g_eng, &now);
        swprintf(buf, 127, L"剩余 %02d:%02d:%02d", (int)(rem / 3600), (int)((rem / 60) % 60), (int)(rem % 60));
        RECT s2 = mkrect(0, 438, 430, 462);
        draw_text_r(dc, buf, &s2, g_fBtn, COL_ACCENT, DT_CENTER);
    } else {
        RECT s1 = mkrect(0, 414, 430, 436);
        draw_text_r(dc, L"未设置定时任务", &s1, g_fLabel, COL_TXT2, DT_CENTER);
    }

    draw_toggle(dc);
    RECT tl = mkrect(66, 474, 180, 496);
    draw_text_r(dc, L"开机自启动", &tl, g_fLabel, COL_TXT2, DT_LEFT);

    RECT run = RC_BTNRUN;
    draw_round(dc, &run, 5, COL_DANGER, 0);
    RECT runt = run; runt.bottom -= 2;
    draw_text_r(dc, L"立即执行", &runt, g_fBtnSmall, COL_TXT, DT_CENTER);

    RECT lk = RC_LINK;
    draw_text_r(dc, L"项目主页 v2.0.1.9", &lk, g_fSmall, g_hover == R_LINK ? COL_ACCENT : COL_TXT3, DT_RIGHT);
}

static void invalidate_ui(void)
{
    if (g_wnd) InvalidateRect(g_wnd, NULL, FALSE);
}

/* ---------- 托盘（图标来自 exe 内嵌资源） ---------- */
static void tray_add(void)
{
    ZeroMemory(&g_nid, sizeof(g_nid));
    g_nid.cbSize = sizeof(g_nid);
    g_nid.hWnd = g_wnd;
    g_nid.uID = 1;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.uCallbackMessage = WM_TRAY;
    g_nid.hIcon = (HICON)LoadImageW(g_inst, MAKEINTRESOURCEW(1), IMAGE_ICON, 32, 32, LR_DEFAULTCOLOR);
    if (!g_nid.hIcon) g_nid.hIcon = LoadIconW(NULL, IDI_APPLICATION);
    wcscpy(g_nid.szTip, L"定时关机助手");
    Shell_NotifyIconW(NIM_ADD, &g_nid);
}

static void tray_balloon(const wchar_t* title, const wchar_t* text)
{
    UINT of = g_nid.uFlags;
    g_nid.uFlags = NIF_INFO;
    lstrcpynW(g_nid.szInfoTitle, title, 64);
    lstrcpynW(g_nid.szInfo, text, 256);
    g_nid.dwInfoFlags = NIIF_NONE;
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);
    g_nid.uFlags = of;
}

static void tray_remove(void)
{
    Shell_NotifyIconW(NIM_DELETE, &g_nid);
}

/* ---------- 业务动作 ---------- */
static void do_execute(HWND owner, int action, const wchar_t* reason)
{
    const wchar_t* err = execute_action(action, reason);
    if (err) MessageBoxW(owner, err, L"关机失败", MB_OK | MB_ICONERROR);
}

static void set_edit_value(int which, int v)
{
    g_updating = 1;
    wchar_t buf[8];
    swprintf(buf, 7, L"%02d", v);
    SetWindowTextW(which == 1 ? g_editH : g_editM, buf);
    g_updating = 0;
}

static void normalize_segment(int which)
{
    int v = which == 1 ? g_hour : g_minute;
    int vmax = which == 1 ? hour_max() : 59;
    if (v < 0) v = 0;
    if (v > vmax) v = vmax;
    if (which == 1) g_hour = v; else g_minute = v;
    set_edit_value(which, v);
}

static void set_mode(int mode)
{
    g_mode = mode;
    if (g_hour > hour_max()) g_hour = hour_max();
    set_edit_value(1, g_hour);
    invalidate_ui();
}

static void arm_task(HWND w)
{
    Task t;
    t.isCountdown = g_mode;
    t.hour = g_hour;
    t.minute = g_minute;
    t.action = g_cfg.action;
    SYSTEMTIME now;
    GetLocalTime(&now);
    engine_arm(&g_eng, &t, &now);
    g_cfg.hour = g_hour;
    g_cfg.minute = g_minute;
    wchar_t path[MAX_PATH];
    config_path(path, MAX_PATH);
    config_save(&g_cfg, path);
    {
        wchar_t msg[128];
        swprintf(msg, 127, L"已设置在 %d点%d分%d秒%s，若要更改，请在此图标上单击右键。",
            g_eng.fireAt.wHour, g_eng.fireAt.wMinute, g_eng.fireAt.wSecond, action_name(g_eng.task.action));
        tray_balloon(APP_TITLE, msg);
    }
    if (g_mode == 1) ShowWindow(w, SW_MINIMIZE);
}

static void on_ok(HWND w)
{
    int vr;
    if (g_mode == 0) {
        SYSTEMTIME now;
        GetLocalTime(&now);
        vr = validate_fixed(g_hour, g_minute, &now);
    } else {
        vr = validate_count(g_hour, g_minute);
    }
    if (vr == VR_BAD) {
        MessageBoxW(w, L"亲，这个是火星文嘛？看不懂啊", L"提示", MB_OK | MB_ICONWARNING);
        return;
    }
    if (vr == VR_PAST) {
        SYSTEMTIME now;
        GetLocalTime(&now);
        wchar_t buf[128];
        swprintf(buf, 127, L"亲，现在已经%d点%d分了，想现在就关机的话，请点右下角“立即执行”！",
            (int)now.wHour, (int)now.wMinute);
        MessageBoxW(w, buf, L"提示", MB_OK | MB_ICONWARNING);
        return;
    }
    arm_task(w);
    invalidate_ui();
}

static void on_run_now(HWND w)
{
    int action = g_cfg.action;
    wchar_t buf[96];
    swprintf(buf, 95, L"确定要立即%s吗？", action_name(action));
    if (MessageBoxW(w, buf, L"确认", MB_YESNO | MB_ICONQUESTION) != IDYES) return;
    do_execute(w, action, L"手动立即执行");
    if (g_simulate)
        MessageBoxW(w, L"【模拟模式】已写入 simulate.log（未真正执行）", L"模拟模式", MB_OK | MB_ICONINFORMATION);
}

/* ---------- 确认弹窗 ---------- */
static int g_dlgDone = 0;

static LRESULT CALLBACK dlg_proc(HWND w, UINT m, WPARAM wp, LPARAM lp)
{
    switch (m) {
        case WM_CREATE:
            SetTimer(w, 1, 1000, NULL);
            return 0;
        case WM_TIMER:
            if (wp != 1) return 0;
            g_dlgRemain--;
            if (g_dlgRemain <= 0) {
                g_dlgDecision = 0;
                g_dlgDone = 1;
                DestroyWindow(w);
                return 0;
            }
            InvalidateRect(w, NULL, FALSE);
            return 0;
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(w, &ps);
            RECT cl; GetClientRect(w, &cl);
            HDC mem = CreateCompatibleDC(dc);
            HBITMAP bmp = CreateCompatibleBitmap(dc, cl.right, cl.bottom);
            HBITMAP ob = (HBITMAP)SelectObject(mem, bmp);
            {
                HBRUSH b = CreateSolidBrush(COL_BG);
                HBRUSH ob2 = (HBRUSH)SelectObject(mem, b);
                PatBlt(mem, 0, 0, cl.right, cl.bottom, PATCOPY);
                SelectObject(mem, ob2);
                DeleteObject(b);
            }
            wchar_t buf[128];
            RECT warn = mkrect(0, 22, cl.right, 54);
            draw_text_r(mem, L"关机提醒：请注意保存文件！", &warn, g_fWarn, COL_DANGER, DT_CENTER);
            /* 倒计时圆环 */
            int cx = cl.right / 2, cy = 116, r = 48;
            HBRUSH nul = (HBRUSH)GetStockObject(NULL_BRUSH);
            HPEN trackp = CreatePen(PS_SOLID, 6, COL_TRACK);
            HPEN arcp = CreatePen(PS_SOLID, 6, COL_ACCENT);
            HBRUSH obn = (HBRUSH)SelectObject(mem, nul);
            HPEN opn = (HPEN)SelectObject(mem, trackp);
            Ellipse(mem, cx - r, cy - r, cx + r, cy + r);
            double frac = (double)g_dlgRemain / g_dlgTotal;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            if (frac >= 0.999) {
                SelectObject(mem, arcp);
                Ellipse(mem, cx - r, cy - r, cx + r, cy + r);
            } else if (frac > 0.001) {
                double a = 2 * 3.14159265358979 * frac;
                double sx = cx + r * sin(a), sy = cy - r * cos(a);
                SelectObject(mem, arcp);
                Arc(mem, cx - r, cy - r, cx + r, cy + r, (int)floor(sx + 0.5), (int)floor(sy + 0.5), cx, cy - r);
            }
            SelectObject(mem, obn);
            SelectObject(mem, opn);
            DeleteObject(trackp);
            DeleteObject(arcp);
            swprintf(buf, 31, L"%d", g_dlgRemain);
            RECT rs = mkrect(cx - 60, cy - 22, cx + 60, cy + 22);
            draw_text_r(mem, buf, &rs, g_fSec, COL_TXT, DT_CENTER);
            swprintf(buf, 127, L"系统将在 %d 秒后%s。", g_dlgRemain, action_name(g_dlgAction));
            RECT body = mkrect(0, 176, cl.right, 200);
            draw_text_r(mem, buf, &body, g_fLabel, COL_TXT2, DT_CENTER);
            RECT b1 = mkrect(40, 214, 140, 252), b2 = mkrect(150, 214, 250, 252), b3 = mkrect(260, 214, 360, 252);
            draw_round(mem, &b1, 5, COL_DANGER, 0);
            draw_text_r(mem, L"执  行", &b1, g_fBtnSmall, COL_TXT, DT_CENTER);
            draw_round(mem, &b2, 5, COL_HOVER, 0);
            draw_text_r(mem, L"延迟 10 分钟", &b2, g_fBtnSmall, COL_TXT, DT_CENTER);
            draw_round(mem, &b3, 5, COL_HOVER, 0);
            draw_text_r(mem, L"取  消", &b3, g_fBtnSmall, COL_TXT2, DT_CENTER);
            BitBlt(dc, 0, 0, cl.right, cl.bottom, mem, 0, 0, SRCCOPY);
            SelectObject(mem, ob);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(w, &ps);
            return 0;
        }
        case WM_LBUTTONDOWN: {
            POINT p; GetCursorPos(&p); ScreenToClient(w, &p);
            if (p.y >= 214 && p.y < 252) {
                if (p.x >= 40 && p.x < 140) g_dlgDecision = 0;
                else if (p.x >= 150 && p.x < 250) g_dlgDecision = 1;
                else if (p.x >= 260 && p.x < 360) g_dlgDecision = 2;
                else return 0;
                g_dlgDone = 1;
                DestroyWindow(w);
            }
            return 0;
        }
        case WM_NCHITTEST: {
            POINT p; GetCursorPos(&p); ScreenToClient(w, &p);
            if (p.y > 8 && p.y < 200) return HTCAPTION;
            return HTCLIENT;
        }
        case WM_DESTROY:
            KillTimer(w, 1);
            return 0;
    }
    return DefWindowProcW(w, m, wp, lp);
}

/* 模态运行确认弹窗，返回决策：0 执行 1 延迟 2 取消 */
static int show_confirm(HWND owner, int action, int warnSeconds)
{
    g_dlgAction = action;
    g_dlgTotal = warnSeconds < 1 ? 1 : warnSeconds;
    g_dlgRemain = g_dlgTotal;
    g_dlgDecision = 2;
    g_dlgDone = 0;

    EnableWindow(owner, FALSE);
    DWORD style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
    RECT wr = mkrect(0, 0, 400, 280);
    AdjustWindowRect(&wr, style, FALSE);
    int ww = wr.right - wr.left, wh = wr.bottom - wr.top;
    int px = (GetSystemMetrics(SM_CXSCREEN) - ww) / 2;
    int py = (GetSystemMetrics(SM_CYSCREEN) - wh) / 2;
    HWND dlg = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, CLASS_DLG,
        g_simulate ? L"关机提示（模拟）" : L"关机提示", style,
        px, py, ww, wh, owner, NULL, g_inst, NULL);
    ShowWindow(dlg, SW_SHOW);
    UpdateWindow(dlg);
    MSG msg;
    while (IsWindow(dlg) && GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    EnableWindow(owner, TRUE);
    SetActiveWindow(owner);
    return g_dlgDecision;
}

/* ---------- 主窗口 ---------- */
static void show_main(void)
{
    ShowWindow(g_wnd, SW_RESTORE);
    ShowWindow(g_wnd, SW_SHOW);
    SetForegroundWindow(g_wnd);
    InvalidateRect(g_wnd, NULL, FALSE);
}

static void exit_app(void)
{
    g_forceExit = TRUE;
    tray_remove();
    if (g_wnd) DestroyWindow(g_wnd);
}

static void on_fire(HWND w, int action)
{
    int dec = show_confirm(w, action, g_cfg.warnSeconds);
    if (dec == 0) {
        do_execute(w, action, L"定时到达");
        tray_balloon(APP_TITLE, L"正在执行定时任务。");
    } else if (dec == 1) {
        Task t;
        t.isCountdown = 1; t.hour = 0; t.minute = 10; t.action = action;
        SYSTEMTIME now; GetLocalTime(&now);
        engine_arm(&g_eng, &t, &now);
        tray_balloon(APP_TITLE, L"已延迟 10 分钟执行。");
    } else {
        tray_balloon(APP_TITLE, L"已取消定时任务。");
    }
    invalidate_ui();
}

static void apply_dark_chrome(HWND w)
{
    HMODULE dwm = GetModuleHandleW(L"dwmapi.dll");
    if (!dwm) dwm = LoadLibraryW(L"dwmapi.dll");
    if (!dwm) return;
    typedef HRESULT (WINAPI *DwmSetWindowAttributeFn)(HWND, DWORD, LPCVOID, DWORD);
    DwmSetWindowAttributeFn set = (DwmSetWindowAttributeFn)GetProcAddress(dwm, "DwmSetWindowAttribute");
    if (!set) return;
    int on = 1;
    if (set(w, DWMWA_USE_IMMERSIVE_DARK_MODE, &on, sizeof(on)) != S_OK)
        set(w, 19, &on, sizeof(on));
    COLORREF chrome = RGB(0x1B, 0x1B, 0x1F);
    set(w, DWMWA_CAPTION_COLOR, &chrome, sizeof(chrome));
    set(w, DWMWA_BORDER_COLOR, &chrome, sizeof(chrome));
}

static void slider_set_from_x(int x)
{
    double w = (RC_TRACK.right - RC_TRACK.left) - 14;
    if (w < 1) w = 1;
    double frac = (x - RC_TRACK.left - 7) / w;
    if (frac < 0) frac = 0;
    if (frac > 1) frac = 1;
    int vmax = g_focusSeg == 1 ? hour_max() : 59;
    int v = (int)(vmax * frac + 0.5);
    if (g_focusSeg == 1) {
        if (v != g_hour) { g_hour = v; set_edit_value(1, g_hour); invalidate_ui(); }
    } else {
        if (v != g_minute) { g_minute = v; set_edit_value(2, g_minute); invalidate_ui(); }
    }
}

static LRESULT CALLBACK main_proc(HWND w, UINT m, WPARAM wp, LPARAM lp)
{
    switch (m) {
        case WM_CREATE: {
            g_created = TRUE;
            apply_dark_chrome(w);
            g_fHeader    = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fLabel     = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fBtn       = CreateFontW(-15, 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fBtnSmall  = CreateFontW(-13, 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fSmall     = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fSec       = CreateFontW(-26, 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fWarn      = CreateFontW(-17, 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_fTime      = CreateFontW(-28, 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Consolas");
            g_brEditIdle  = CreateSolidBrush(COL_EDIT_BG);
            g_brEditFocus = CreateSolidBrush(COL_EDIT_FOC);

            /* 时间段输入：真 EDIT 控件（光标/选中/方向键/数字键盘全套原生行为） */
            g_editH = CreateWindowExW(0, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_CENTER | ES_NUMBER,
                74, 132, 114, 46, w, (HMENU)IDC_EDIT_H, g_inst, NULL);
            g_editM = CreateWindowExW(0, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_CENTER | ES_NUMBER,
                242, 132, 114, 46, w, (HMENU)IDC_EDIT_M, g_inst, NULL);
            SendMessageW(g_editH, WM_SETFONT, (WPARAM)g_fTime, TRUE);
            SendMessageW(g_editM, WM_SETFONT, (WPARAM)g_fTime, TRUE);
            SendMessageW(g_editH, EM_SETLIMITTEXT, 2, 0);
            SendMessageW(g_editM, EM_SETLIMITTEXT, 2, 0);
            set_edit_value(1, g_hour);
            set_edit_value(2, g_minute);

            SetTimer(w, 1, 500, NULL);
            SetTimer(w, 2, 1000, NULL);
            g_autoStart = autostart_is_set();
            if (!g_uiTestMode) tray_add();
            invalidate_ui();
            return 0;
        }
        case WM_CTLCOLOREDIT: {
            HDC hdcE = (HDC)wp;
            HWND ctl = (HWND)lp;
            if (ctl == g_editH || ctl == g_editM) {
                int which = ctl == g_editH ? 1 : 2;
                SetTextColor(hdcE, COL_TXT);
                SetBkColor(hdcE, g_focusSeg == which ? COL_EDIT_FOC : COL_EDIT_BG);
                return (LRESULT)(g_focusSeg == which ? g_brEditFocus : g_brEditIdle);
            }
            break;
        }
        case WM_COMMAND: {
            HWND ctl = (HWND)lp;
            if (ctl == g_editH || ctl == g_editM) {
                int which = ctl == g_editH ? 1 : 2;
                WORD code = HIWORD(wp);
                if (code == EN_SETFOCUS) {
                    g_focusSeg = which;
                    invalidate_ui();
                } else if (code == EN_KILLFOCUS) {
                    normalize_segment(which);   /* 失焦校验+补零（对齐原版 09 : 26 样式） */
                    invalidate_ui();
                } else if (code == EN_CHANGE && !g_updating) {
                    wchar_t txt[8];
                    GetWindowTextW(ctl, txt, 8);
                    int v = _wtoi(txt);
                    if (which == 1) g_hour = v; else g_minute = v;
                    invalidate_ui();
                }
                return 0;
            }
            break;
        }
        case WM_ACTIVATE: {
            if (LOWORD(wp) != WA_INACTIVE && g_editH) {
                SetFocus(g_focusSeg == 1 ? g_editH : g_editM);
            }
            return 0;
        }
        case WM_TIMER: {
            if (wp == 1) {
                invalidate_ui();
            } else if (wp == 2) {
                SYSTEMTIME now;
                GetLocalTime(&now);
                if (engine_tick(&g_eng, &now)) {
                    if (g_uiTestMode) {
                        simulate_log(g_eng.task.action, L"定时到达(UI测试)");
                    } else {
                        on_fire(w, g_eng.task.action);
                    }
                }
            }
            return 0;
        }
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(w, &ps);
            RECT cl; GetClientRect(w, &cl);
            HDC mem = CreateCompatibleDC(dc);
            HBITMAP bmp = CreateCompatibleBitmap(dc, cl.right, cl.bottom);
            HBITMAP ob = (HBITMAP)SelectObject(mem, bmp);
            {
                HBRUSH b = CreateSolidBrush(COL_BG);
                HBRUSH ob2 = (HBRUSH)SelectObject(mem, b);
                PatBlt(mem, 0, 0, cl.right, cl.bottom, PATCOPY);
                SelectObject(mem, ob2);
                DeleteObject(b);
            }
            paint_main(mem);
            BitBlt(dc, 0, 0, cl.right, cl.bottom, mem, 0, 0, SRCCOPY);
            SelectObject(mem, ob);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(w, &ps);
            return 0;
        }
        case WM_MOUSEMOVE: {
            POINT p; GetCursorPos(&p); ScreenToClient(w, &p);
            if (g_dragSlider) {
                slider_set_from_x(p.x);
            }
            {
                int h = hit_test(p.x, p.y);
                if (h != g_hover) {
                    g_hover = h;
                    SetCursor(LoadCursor(NULL, h != R_NONE ? IDC_HAND : IDC_ARROW));
                    invalidate_ui();
                }
            }
            return 0;
        }
        case WM_LBUTTONDOWN: {
            POINT p; GetCursorPos(&p); ScreenToClient(w, &p);
            int h = hit_test(p.x, p.y);
            if (h == R_SLIDER) { g_dragSlider = 1; SetCapture(w); slider_set_from_x(p.x); }
            else if (h == R_TIMEBOX) {
                /* 点击时间盒空白处：聚焦更近的时间段 */
                SetFocus(p.x < (RC_TIMEBOX.left + RC_TIMEBOX.right) / 2 ? g_editH : g_editM);
            }
            else if (h == R_SEG_FIXED) set_mode(0);
            else if (h == R_SEG_COUNT) set_mode(1);
            else if (h >= R_CHIP0 && h <= R_CHIP_LAST) {
                if (g_cfg.action != h - R_CHIP0) { g_cfg.action = h - R_CHIP0; invalidate_ui(); }
            }
            else if (h == R_TOGGLE) {
                g_autoStart = !g_autoStart;
                autostart_set(g_autoStart);
                {
                    wchar_t path[MAX_PATH];
                    config_path(path, MAX_PATH);
                    config_save(&g_cfg, path);
                }
                invalidate_ui();
            }
            else if (h == R_BTN_OK) on_ok(w);
            else if (h == R_BTN_STOP) {
                engine_disarm(&g_eng);
                tray_balloon(APP_TITLE, L"已取消定时任务。");
                invalidate_ui();
            }
            else if (h == R_BTN_RUN) on_run_now(w);
            else if (h == R_LINK) {
                ShellExecuteW(w, L"open", REPO_URL, NULL, NULL, SW_SHOWNORMAL);
            }
            return 0;
        }
        case WM_LBUTTONUP: {
            if (g_dragSlider) { g_dragSlider = 0; ReleaseCapture(); invalidate_ui(); }
            return 0;
        }
        case WM_TRAY: {
            if (lp == WM_LBUTTONDBLCLK) show_main();
            else if (lp == WM_RBUTTONUP || lp == WM_CONTEXTMENU) {
                SetForegroundWindow(w);
                HMENU menu = CreatePopupMenu();
                AppendMenuW(menu, MF_STRING, 1001, L"显示主界面");
                AppendMenuW(menu, MF_STRING, 1002, L"取消定时并退出");
                AppendMenuW(menu, MF_STRING, 1003, L"退出");
                POINT p; GetCursorPos(&p);
                int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, p.x, p.y, 0, w, NULL);
                DestroyMenu(menu);
                if (cmd == 1001) show_main();
                else if (cmd == 1002) { engine_disarm(&g_eng); exit_app(); }
                else if (cmd == 1003) exit_app();
            }
            return 0;
        }
        case WM_SIZE: {
            if (wp == SIZE_MINIMIZED && g_eng.armed) {
                ShowWindow(w, SW_HIDE);
                tray_balloon(APP_TITLE, L"定时任务运行中，请在此图标上单击右键管理。");
            }
            return 0;
        }
        case WM_CLOSE: {
            if (!g_forceExit && g_eng.armed) {
                if (MessageBoxW(w, L"已设置定时关机任务，确定取消任务并退出应用程序？",
                    L"确认", MB_YESNO | MB_ICONQUESTION) == IDYES) {
                    engine_disarm(&g_eng);
                    exit_app();
                    return 0;
                }
                ShowWindow(w, SW_HIDE);
                tray_balloon(APP_TITLE, L"定时任务仍在运行，请在此图标上单击右键管理。");
                return 0;
            }
            DestroyWindow(w);
            return 0;
        }
        case WM_DESTROY: {
            KillTimer(w, 1);
            KillTimer(w, 2);
            tray_remove();
            PostQuitMessage(0);
            return 0;
        }
    }
    return DefWindowProcW(w, m, wp, lp);
}

static void register_classes(void)
{
    WNDCLASSW wc;
    ZeroMemory(&wc, sizeof(wc));
    wc.lpfnWndProc = main_proc;
    wc.hInstance = g_inst;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hIcon = LoadIconW(g_inst, MAKEINTRESOURCEW(1));   /* exe 内嵌图标 */
    wc.lpszClassName = CLASS_MAIN;
    RegisterClassW(&wc);

    WNDCLASSW wd;
    ZeroMemory(&wd, sizeof(wd));
    wd.lpfnWndProc = dlg_proc;
    wd.hInstance = g_inst;
    wd.hCursor = LoadCursor(NULL, IDC_ARROW);
    wd.hIcon = LoadIconW(g_inst, MAKEINTRESOURCEW(1));
    wd.lpszClassName = CLASS_DLG;
    RegisterClassW(&wd);
}

#ifdef TEST_BUILD
/* 冒烟测试：隐藏创建主窗口（不显示） */
static int run_uitest(void)
{
    printf("===== 定时关机助手 UI 冒烟测试（不显示窗口、模拟执行） =====\n");
    int failed = 0;
    g_uiTestMode = 1;
    register_classes();
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
    HWND w = CreateWindowExW(0, CLASS_MAIN, APP_TITLE, style,
        0, 0, 446, 599, NULL, NULL, g_inst, NULL);
    check("main.createHidden", w != NULL && g_created);
    /* 时间段 EDIT 控件存在且输入联动生效 */
    HWND eh = GetDlgItem(w, IDC_EDIT_H);
    HWND em = GetDlgItem(w, IDC_EDIT_M);
    check("ui.edits", eh != NULL && em != NULL);
    SendMessageW(eh, WM_SETTEXT, 0, (LPARAM)L"17");
    check("ui.editToValue", g_hour == 17);
    g_updating = 1;
    SendMessageW(em, WM_SETTEXT, 0, (LPARAM)L"59");
    g_updating = 0;
    check("ui.editGuard", g_minute == 30);   /* 守卫期内不联动（模拟程序内部写回） */
    /* 引擎触发路径（模拟模式写日志） */
    wchar_t tmpLog[MAX_PATH];
    GetTempPathW(MAX_PATH, tmpLog);
    wcscat(tmpLog, L"yuntimer_uitest_simulate.log");
    DeleteFileW(tmpLog);
    wcscpy(g_simLog, tmpLog);
    {
        Task t;
        t.isCountdown = 1; t.hour = 0; t.minute = 0; t.action = PA_SHUTDOWN;
        SYSTEMTIME past;
        GetLocalTime(&past);
        st_add_seconds(&past, -2, &past);
        engine_arm(&g_eng, &t, &past);
        SYSTEMTIME now2;
        GetLocalTime(&now2);
        int fired = engine_tick(&g_eng, &now2);
        check("ui.firePath", fired == 1);
        simulate_log(PA_SHUTDOWN, L"定时到达(UI测试)");
        check("ui.simLog", GetFileAttributesW(tmpLog) != INVALID_FILE_ATTRIBUTES);
    }
    DeleteFileW(tmpLog);
    DestroyWindow(w);
    printf("===== 完成：失败 %d 项 =====\n", failed);
    return failed == 0 ? 0 : 1;
}
#endif

/* ---------- 入口 ---------- */
#ifndef TEST_BUILD
int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmdline, int show)
{
    (void)prev;
    g_inst = inst;
    if (wcsstr(cmdline, L"--simulate") || wcsstr(cmdline, L"/simulate")) g_simulate = 1;

    if (g_simulate) {
        wchar_t exeDir[MAX_PATH];
        GetModuleFileNameW(NULL, exeDir, MAX_PATH);
        wchar_t* slash = wcsrchr(exeDir, L'\\');
        if (slash) *(slash + 1) = 0;
        _snwprintf(g_simLog, MAX_PATH - 1, L"%ssimulate.log", exeDir);
        g_simLog[MAX_PATH - 1] = 0;
    }

    {
        wchar_t cfgPath[MAX_PATH];
        config_path(cfgPath, MAX_PATH);
        config_load(&g_cfg, cfgPath);
    }
    g_forceWait = g_cfg.forceWait;
    g_hour = g_cfg.hour < 0 ? 0 : (g_cfg.hour > 99 ? 99 : g_cfg.hour);
    g_minute = g_cfg.minute < 0 ? 0 : (g_cfg.minute > 59 ? 59 : g_cfg.minute);
    g_mode = g_cfg.mode;
    if (g_mode == 0 && g_hour > 23) g_hour = 23;

    register_classes();
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
    RECT wr = mkrect(0, 0, 430, 560);
    AdjustWindowRect(&wr, style, FALSE);
    int ww = wr.right - wr.left, wh = wr.bottom - wr.top;
    int px = (GetSystemMetrics(SM_CXSCREEN) - ww) / 2;
    int py = (GetSystemMetrics(SM_CYSCREEN) - wh) / 2;
    g_wnd = CreateWindowExW(0, CLASS_MAIN,
        g_simulate ? APP_TITLE L"（模拟模式）" : APP_TITLE,
        style, px, py, ww, wh, NULL, NULL, g_inst, NULL);
    if (!g_wnd) return 1;
    ShowWindow(g_wnd, show);
    UpdateWindow(g_wnd);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}

#else /* TEST_BUILD */

int main(int argc, char* argv[])
{
    g_inst = GetModuleHandleW(NULL);
    int i;
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--simulate") == 0) g_simulate = 1;
    }
    if (argc >= 2 && strcmp(argv[1], "--uitest") == 0) return run_uitest();
    return run_selftest();
}

#endif
