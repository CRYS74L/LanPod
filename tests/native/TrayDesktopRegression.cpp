#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <cstdio>
#include <stdexcept>
#include <string>

using Run = DWORD(WINAPI*)(HICON, const wchar_t*, HWND, HANDLE, const wchar_t*);
using Stop = void(WINAPI*)();
using Handle = HWND(WINAPI*)();
using IsAdded = BOOL(WINAPI*)();

namespace {
void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

struct Context {
    Run run;
    HANDLE ready;
    HDESK desktop;
    const wchar_t* logPath;
};

DWORD WINAPI RunOnDesktop(void* state) {
    const auto& context = *static_cast<Context*>(state);
    if (context.desktop && !SetThreadDesktop(context.desktop)) return GetLastError();
    return context.run(LoadIconW(nullptr, IDI_APPLICATION), L"LAN startup regression",
        nullptr, context.ready, context.logPath);
}

void VerifyDesktop(Run run, Stop stop, Handle handle, IsAdded isAdded, const wchar_t* logPath, bool isolated) {
    const auto name = L"LanDeviceMonitor.Tests." + std::to_wstring(GetCurrentProcessId());
    HDESK desktop = isolated ? CreateDesktopW(name.c_str(), nullptr, nullptr, 0,
        DESKTOP_CREATEWINDOW | DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS, nullptr) : nullptr;
    Require(!isolated || desktop, "Cannot create isolated desktop.");
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Context context{run, ready, desktop, logPath};
    HANDLE thread = CreateThread(nullptr, 0, RunOnDesktop, &context, 0, nullptr);
    try {
        Require(ready && thread, "Cannot create runtime thread/event.");
        Require(WaitForSingleObject(ready, 1000) == WAIT_OBJECT_0, "Desktop runtime readiness blocked on the shell.");
        const auto hwnd = handle();
        Require(hwnd && !IsWindowVisible(hwnd), "Native tray window is missing or visible.");
        if (isolated) {
            Sleep(3200);
            Require(!isAdded() && WaitForSingleObject(thread, 0) == WAIT_TIMEOUT,
                "Missing system tray stopped the runtime or falsely reported registration.");
            std::puts("PASS production DLL remains running for 3 retry intervals on a desktop without a system tray");
        } else {
            const auto deadline = GetTickCount64() + 10000;
            while (!isAdded() && GetTickCount64() < deadline) Sleep(10);
            Require(isAdded(), "Production DLL did not register with the real shell.");
            NOTIFYICONIDENTIFIER id{};
            id.cbSize = sizeof(id); id.hWnd = hwnd; id.uID = 1;
            RECT rect{};
            Require(SUCCEEDED(Shell_NotifyIconGetRect(&id, &rect)), "Real shell did not contain the icon.");
            std::puts("PASS production DLL registers a queryable icon on the normal desktop");
        }
        stop();
        Require(WaitForSingleObject(thread, 3000) == WAIT_OBJECT_0, "Desktop runtime did not exit.");
        DWORD result{};
        GetExitCodeThread(thread, &result);
        Require(result == 0 && !handle() && !isAdded() && !IsWindow(hwnd),
            "Desktop runtime exit leaked a window or returned an error.");
    } catch (...) {
        stop();
        if (WaitForSingleObject(thread, 3000) != WAIT_OBJECT_0) ExitProcess(2);
        CloseHandle(thread); CloseHandle(ready);
        if (desktop) CloseDesktop(desktop);
        throw;
    }
    CloseHandle(thread); CloseHandle(ready);
    if (desktop) CloseDesktop(desktop);
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    const auto library = LoadLibraryW(argv[1]);
    if (!library) return 3;
    int result = 0;
    try {
        const auto run = reinterpret_cast<Run>(GetProcAddress(library, "LanTrayRunWithDiagnostics"));
        const auto stop = reinterpret_cast<Stop>(GetProcAddress(library, "LanTrayStop"));
        const auto handle = reinterpret_cast<Handle>(GetProcAddress(library, "LanTrayHandle"));
        const auto isAdded = reinterpret_cast<IsAdded>(GetProcAddress(library, "LanTrayIsAdded"));
        Require(run && stop && handle && isAdded, "Production tray exports are missing.");
        VerifyDesktop(run, stop, handle, isAdded, argv[2], true);
        VerifyDesktop(run, stop, handle, isAdded, argv[2], false);
        std::puts("Tray desktop regression PASS.");
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL %s\n", error.what());
        result = 1;
    }
    FreeLibrary(library);
    return result;
}
