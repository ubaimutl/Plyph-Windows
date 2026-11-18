#define UNICODE
#define _UNICODE
#include <windows.h>

static size_t wlen(const wchar_t *s) {
    size_t n = 0;
    while (s && s[n]) n++;
    return n;
}

static void wcopy(wchar_t *dst, const wchar_t *src, size_t *pos) {
    while (src && *src) dst[(*pos)++] = *src++;
}

static const wchar_t *skip_exe_arg(const wchar_t *cmd) {
    if (!cmd) return L"";
    const wchar_t *p = cmd;
    while (*p == L' ' || *p == L'\t') p++;
    if (*p == L'"') {
        p++;
        while (*p && *p != L'"') p++;
        if (*p == L'"') p++;
    } else {
        while (*p && *p != L' ' && *p != L'\t') p++;
    }
    while (*p == L' ' || *p == L'\t') p++;
    return p;
}

void WINAPI PlyphLauncherEntry(void) {
    HANDLE heap = GetProcessHeap();
    wchar_t *exePath = (wchar_t *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(wchar_t) * 32768);
    wchar_t *appDir = (wchar_t *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(wchar_t) * 32768);
    wchar_t *targetExe = (wchar_t *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(wchar_t) * 32768);
    STARTUPINFOW *si = (STARTUPINFOW *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(STARTUPINFOW));
    PROCESS_INFORMATION *pi = (PROCESS_INFORMATION *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(PROCESS_INFORMATION));
    if (!exePath || !appDir || !targetExe || !si || !pi) ExitProcess(1);

    DWORD len = GetModuleFileNameW(NULL, exePath, 32767);
    if (len == 0 || len >= 32767) ExitProcess(1);

    DWORD slash = len;
    while (slash > 0 && exePath[slash - 1] != L'\\') slash--;
    if (slash == 0) ExitProcess(1);
    exePath[slash - 1] = L'\0';

    size_t p1 = 0;
    wcopy(appDir, exePath, &p1);
    wcopy(appDir, L"\\app", &p1);
    appDir[p1] = L'\0';

    size_t p2 = 0;
    wcopy(targetExe, appDir, &p2);
    wcopy(targetExe, L"\\Plyph-Portable.exe", &p2);
    targetExe[p2] = L'\0';

    if (GetFileAttributesW(targetExe) == INVALID_FILE_ATTRIBUTES) {
        size_t mLen = 64 + p2;
        wchar_t *msg = (wchar_t *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(wchar_t) * mLen);
        if (msg) {
            size_t pm = 0;
            wcopy(msg, L"Missing application executable:\n", &pm);
            wcopy(msg, targetExe, &pm);
            MessageBoxW(NULL, msg, L"Plyph Portable", MB_OK | MB_ICONERROR);
            HeapFree(heap, 0, msg);
        }
        ExitProcess(1);
    }

    const wchar_t *args = skip_exe_arg(GetCommandLineW());
    size_t cmdLen = p2 + wlen(args) + 8;
    wchar_t *cmd = (wchar_t *)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(wchar_t) * cmdLen);
    if (!cmd) ExitProcess(1);

    size_t pc = 0;
    cmd[pc++] = L'"';
    wcopy(cmd, targetExe, &pc);
    cmd[pc++] = L'"';
    if (*args) {
        cmd[pc++] = L' ';
        wcopy(cmd, args, &pc);
    }
    cmd[pc] = L'\0';

    si->cb = sizeof(STARTUPINFOW);
    BOOL ok = CreateProcessW(targetExe, cmd, NULL, NULL, FALSE, 0, NULL, appDir, si, pi);
    HeapFree(heap, 0, cmd);
    if (!ok) {
        MessageBoxW(NULL, L"Failed to launch Plyph-Portable.exe inside app\\.", L"Plyph Portable", MB_OK | MB_ICONERROR);
        ExitProcess(1);
    }

    CloseHandle(pi->hThread);
    CloseHandle(pi->hProcess);
    ExitProcess(0);
}
