// Compile the production HWND/message loop against a synthetic shell. No
// Explorer process, installed application or real notification icon is changed.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <atomic>
#include <cstdio>
#include <stdexcept>
#include <string>

namespace {
std::atomic<int> failedOperation{}, failuresRemaining{}, addCalls{}, deleteCalls{};
std::atomic<bool> shellHasIcon{}, shellUsesV4{}, failWindow{}, failTimer{}, blockNotify{}, notifyEntered{};

BOOL WINAPI TestNotifyIcon(DWORD operation, PNOTIFYICONDATAW data) {
    notifyEntered = true;
    while (blockNotify) Sleep(1);
    int stage = static_cast<int>(operation);
    if (operation == NIM_MODIFY && data->uFlags == (NIF_TIP | NIF_SHOWTIP)) stage = 10;
    if (operation == NIM_ADD) ++addCalls;
    if (failedOperation == stage && failuresRemaining > 0) {
        --failuresRemaining;
        SetLastError(0x81234567); // The BOOL is the only Shell_NotifyIcon error contract.
        return FALSE;
    }
    if (operation == NIM_ADD) {
        if (shellHasIcon) return FALSE;
        shellHasIcon = true;
        shellUsesV4 = false;
    } else if (operation == NIM_SETVERSION) {
        if (!shellHasIcon) return FALSE;
        shellUsesV4 = data->uVersion == NOTIFYICON_VERSION_4;
    } else if (operation == NIM_MODIFY) {
        if (!shellHasIcon) return FALSE;
    } else if (operation == NIM_DELETE) {
        ++deleteCalls;
        shellHasIcon = false;
        shellUsesV4 = false;
    }
    return TRUE;
}

HWND WINAPI TestCreateWindow(LPCWSTR name, LPCWSTR title, DWORD style, int x, int y,
    int width, int height, HWND parent, HMENU menu, HINSTANCE instance, LPVOID parameter) {
    if (failWindow) { SetLastError(ERROR_ACCESS_DENIED); return nullptr; }
    return CreateWindowW(name, title, style, x, y, width, height, parent, menu, instance, parameter);
}

UINT_PTR WINAPI TestSetTimer(HWND hwnd, UINT_PTR id, UINT elapsed, TIMERPROC callback) {
    if (failTimer) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); return 0; }
    return SetTimer(hwnd, id, elapsed, callback);
}
}

#define Shell_NotifyIconW TestNotifyIcon
#undef CreateWindowW
#define CreateWindowW TestCreateWindow
#define SetTimer TestSetTimer
#include "../../src/LanDeviceMonitor.App/Native/TrayRuntime.cpp"
#undef Shell_NotifyIconW
#undef CreateWindowW
#undef SetTimer

extern "C" __declspec(dllexport) void WINAPI TestConfigure(int operation, int failures, BOOL windowFailure, BOOL blocked) {
    failedOperation = operation;
    failuresRemaining = failures;
    failWindow = windowFailure;
    failTimer = false;
    blockNotify = blocked;
    notifyEntered = false;
    addCalls = 0;
    deleteCalls = 0;
    shellHasIcon = false;
    shellUsesV4 = false;
}
extern "C" __declspec(dllexport) void WINAPI TestReleaseShell() { blockNotify = false; }
extern "C" __declspec(dllexport) int WINAPI TestAddCalls() { return addCalls.load(); }

#ifndef TRAY_TEST_LIBRARY
namespace {
void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

struct Runtime {
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread{};
    const wchar_t* logPath{};

    explicit Runtime(const wchar_t* path = nullptr) : logPath(path) {
        thread = CreateThread(nullptr, 0, [](void* state) -> DWORD {
            auto* runtime = static_cast<Runtime*>(state);
            return LanTrayRunWithDiagnostics(LoadIconW(nullptr, IDI_APPLICATION), L"Synthetic tray",
                nullptr, runtime->ready, runtime->logPath);
        }, this, 0, nullptr);
        Require(thread && ready, "Cannot create isolated tray thread/event.");
    }

    void WaitReady(bool expectWindow = true) {
        Require(WaitForSingleObject(ready, 1000) == WAIT_OBJECT_0, "Startup waited for the shell.");
        if (expectWindow) Require(LanTrayHandle() && WaitForSingleObject(thread, 0) == WAIT_TIMEOUT,
            "Native tray exited when shell registration failed.");
    }

    void WaitForIcon() {
        const auto deadline = GetTickCount64() + 6000;
        while (!LanTrayIsAdded() && GetTickCount64() < deadline) Sleep(10);
        Require(LanTrayIsAdded() && shellHasIcon && shellUsesV4, "Tray did not recover with V4 callbacks.");
        Require(WaitForSingleObject(thread, 0) == WAIT_TIMEOUT, "Message loop stopped after recovery.");
    }

    void Stop(DWORD expectedError = 0) {
        LanTrayStop();
        Require(WaitForSingleObject(thread, 2000) == WAIT_OBJECT_0, "Pending registration prevented exit.");
        DWORD code{};
        GetExitCodeThread(thread, &code);
        Require(code == expectedError && !LanTrayHandle() && !shellHasIcon && !LanTrayIsAdded(),
            "Exit code was wrong or tray exit left an HWND/icon behind.");
    }

    ~Runtime() {
        blockNotify = false;
        LanTrayStop();
        if (WaitForSingleObject(thread, 2000) != WAIT_OBJECT_0) ExitProcess(2);
        CloseHandle(thread);
        CloseHandle(ready);
    }
};
}

int wmain(int argc, wchar_t** argv) {
    const wchar_t* logPath = argc > 1 ? argv[1] : nullptr;
    try {
        TestConfigure(0, 0, FALSE, FALSE);
        { Runtime runtime(logPath); runtime.WaitReady(); runtime.WaitForIcon(); runtime.Stop(); }
        std::puts("PASS immediate shell registration and cleanup");

        TestConfigure(NIM_ADD, 3, FALSE, FALSE);
        {
            Runtime runtime(logPath);
            runtime.WaitReady();
            runtime.WaitForIcon();
            Require(addCalls == 4 && deleteCalls == 0, "Delayed registration did not retry exactly three times.");
            runtime.Stop();
        }
        std::puts("PASS delayed shell recovers without TaskbarCreated");

        for (int operation : {static_cast<int>(NIM_SETVERSION), 10}) {
            TestConfigure(operation, 2, FALSE, FALSE);
            Runtime runtime(logPath);
            runtime.WaitReady();
            const auto deadline = GetTickCount64() + 1000;
            while (failuresRemaining == 2 && GetTickCount64() < deadline) Sleep(1);
            Require(shellHasIcon && !LanTrayIsAdded(), "Partial registration was reported as ready.");
            runtime.WaitForIcon();
            Require(addCalls == 1, "Partial registration duplicated the icon.");
            runtime.Stop();
            Require(deleteCalls == 1, "Partial registration leaked an icon.");
        }
        std::puts("PASS version/tooltip failures preserve one identity and recover");

        TestConfigure(0, 0, FALSE, FALSE);
        {
            Runtime runtime(logPath);
            runtime.WaitReady(); runtime.WaitForIcon();
            shellHasIcon = false; shellUsesV4 = false;
            failedOperation = NIM_ADD; failuresRemaining = 2;
            SendMessageW(LanTrayHandle(), RegisterWindowMessageW(L"TaskbarCreated"), 0, 0);
            Require(!LanTrayIsAdded(), "Taskbar recreation retained stale ready state.");
            runtime.WaitForIcon(); runtime.Stop();
        }
        std::puts("PASS taskbar recreation tolerates repeated registration failures");

        TestConfigure(0, 0, FALSE, FALSE);
        {
            Runtime runtime(logPath);
            runtime.WaitReady(); runtime.WaitForIcon();
            SendMessageW(LanTrayHandle(), RegisterWindowMessageW(L"TaskbarCreated"), 0, 0);
            runtime.WaitForIcon();
            Require(addCalls == 1, "Repeated TaskbarCreated duplicated an existing identity.");
            runtime.Stop();
        }
        std::puts("PASS repeated taskbar broadcast reuses an existing icon");

        TestConfigure(NIM_SETVERSION, 1, FALSE, FALSE);
        {
            Runtime runtime(logPath);
            runtime.WaitReady();
            while (!shellHasIcon) Sleep(1);
            shellHasIcon = false; // Lost partial registration without a broadcast.
            runtime.WaitForIcon();
            Require(addCalls == 2, "Lost partial icon was not added again.");
            runtime.Stop();
        }
        std::puts("PASS lost partial registration is re-added");

        TestConfigure(NIM_ADD, 100, FALSE, FALSE);
        { Runtime runtime(logPath); runtime.WaitReady(); Sleep(50); runtime.Stop(); }
        std::puts("PASS exit while the shell remains unavailable");

        TestConfigure(0, 0, FALSE, TRUE);
        {
            Runtime runtime(logPath); runtime.WaitReady();
            const auto deadline = GetTickCount64() + 1000;
            while (!notifyEntered && GetTickCount64() < deadline) Sleep(1);
            Require(notifyEntered && !LanTrayIsAdded(), "Slow shell fixture did not block registration.");
            TestReleaseShell(); runtime.WaitForIcon(); runtime.Stop();
        }
        std::puts("PASS readiness precedes a blocking Shell_NotifyIcon call");

        TestConfigure(0, 0, TRUE, FALSE);
        { Runtime runtime(logPath); runtime.WaitReady(false); runtime.Stop(ERROR_ACCESS_DENIED); }
        std::puts("PASS window failure preserves its actual error");

        TestConfigure(NIM_ADD, 100, FALSE, FALSE);
        failTimer = true;
        { Runtime runtime(logPath); runtime.WaitReady(false); Sleep(50); runtime.Stop(ERROR_NOT_ENOUGH_MEMORY); }
        std::puts("PASS timer failure remains a real runtime error");

        TestConfigure(NIM_ADD, 100, FALSE, FALSE);
        DWORD handlesBefore{}, handlesAfter{};
        GetProcessHandleCount(GetCurrentProcess(), &handlesBefore);
        for (int i = 0; i < 50; ++i) {
            Runtime runtime; runtime.WaitReady(); runtime.Stop();
        }
        GetProcessHandleCount(GetCurrentProcess(), &handlesAfter);
        Require(handlesAfter <= handlesBefore + 1, "Repeated start/stop leaked kernel handles.");
        std::puts("PASS 50 immediate start/stop cycles retain handle count");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL %s\n", error.what());
        return 1;
    }
}
#endif
