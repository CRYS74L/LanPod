// Derived from CRYS74L/ReTray native/agent/main.cpp at
// a57279b6375be9ea463bf9ee3f232487c795250e. GPL-3.0.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <atomic>
#include <string>

namespace {
constexpr UINT OwnCallback = WM_USER + 1024;
constexpr UINT NotifyMessage = WM_APP + 17;
constexpr UINT HostOpenMessage = WM_APP + 18;
constexpr UINT HostMenuMessage = WM_APP + 19;
constexpr UINT RegisterIconMessage = WM_APP + 20;
constexpr UINT_PTR RegistrationTimer = 1;
constexpr UINT RegistrationRetryMilliseconds = 1000;
std::atomic<HWND> window{};
std::atomic<bool> ownAdded{};
std::atomic<bool> stopping{};
HWND uiWindow{};
HICON ownImage{};
std::wstring tooltip;
std::wstring diagnosticPath;
bool iconPresent{};
unsigned registrationAttempts{};
std::wstring lastFailureStage;
constexpr wchar_t windowClass[] = L"LanPod.TrayRuntime";

void Log(const wchar_t* stage, DWORD detail = 0) {
    SYSTEMTIME now{};
    GetSystemTime(&now);
    wchar_t text[384]{};
    swprintf_s(text, L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ pid=%lu stage=%s detail=%lu attempts=%u\r\n",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
        now.wMilliseconds, GetCurrentProcessId(), stage, detail, registrationAttempts);
    OutputDebugStringW(text);
    if (diagnosticPath.empty()) return;
    const auto file = CreateFileW(diagnosticPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        OutputDebugStringW(L"LanPod: could not open tray diagnostic log.\n");
        return;
    }
    LARGE_INTEGER size{}, zero{};
    if (GetFileSizeEx(file, &size) && size.QuadPart > 256 * 1024) {
        SetFilePointerEx(file, zero, nullptr, FILE_BEGIN);
        SetEndOfFile(file);
    } else {
        SetFilePointerEx(file, zero, nullptr, FILE_END);
    }
    char utf8[768]{};
    const auto length = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8), nullptr, nullptr);
    DWORD written{};
    if (length == 0 || !WriteFile(file, utf8, length - 1, &written, nullptr))
        OutputDebugStringW(L"LanPod: could not write tray diagnostic log.\n");
    CloseHandle(file);
}

bool RegistrationFailed(const wchar_t* stage) {
    // Shell_NotifyIcon documents a BOOL result, not a GetLastError contract.
    // Log the failed operation once per transition and count every attempt.
    if (lastFailureStage != stage) Log(stage);
    lastFailureStage = stage;
    return false;
}

// ReTray SetOwnIcon: the host supplies its own image and tooltip.
bool SetOwnIcon() {
    ++registrationAttempts;
    NOTIFYICONDATAW d{};
    d.cbSize = sizeof(d);
    d.hWnd = window.load();
    d.uID = 1;
    d.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    d.uCallbackMessage = OwnCallback;
    d.hIcon = ownImage;
    wcsncpy_s(d.szTip, std::size(d.szTip), tooltip.c_str(), _TRUNCATE);
    // Reuse a partially registered icon. If the shell lost it, try ADD with
    // the same HWND/id; never create a second identity during recovery.
    if (!iconPresent || !Shell_NotifyIconW(NIM_MODIFY, &d)) {
        if (!Shell_NotifyIconW(NIM_ADD, &d)) return RegistrationFailed(L"NIM_ADD.failed");
        iconPresent = true;
    }
    d.uVersion = NOTIFYICON_VERSION_4;
    if (!Shell_NotifyIconW(NIM_SETVERSION, &d)) return RegistrationFailed(L"NIM_SETVERSION.failed");
    d.uFlags = NIF_TIP | NIF_SHOWTIP;
    if (!Shell_NotifyIconW(NIM_MODIFY, &d)) return RegistrationFailed(L"NIM_MODIFY.tip.failed");
    ownAdded = true;
    Log(L"icon.ready");
    lastFailureStage.clear();
    return true;
}

void RegisterIconOrRetry() {
    if (stopping || ownAdded) return;
    const auto hwnd = window.load();
    if (SetOwnIcon()) {
        KillTimer(hwnd, RegistrationTimer);
    } else if (!stopping && !SetTimer(hwnd, RegistrationTimer, RegistrationRetryMilliseconds, nullptr)) {
        const auto error = GetLastError();
        Log(L"SetTimer.failed", error);
        PostQuitMessage(error ? error : ERROR_GEN_FAILURE);
    }
}

// ReTray's embedded TrayMenu / DispatchPendingInterfaceMenu path.
void TrayMenu(const POINT* anchor = nullptr) {
    POINT point{};
    if (anchor) point = *anchor; else GetCursorPos(&point);
    DWORD pid{};
    if (!uiWindow || !GetWindowThreadProcessId(uiWindow, &pid) || pid != GetCurrentProcessId()) return;
    AllowSetForegroundWindow(pid);
    PostMessageW(uiWindow, HostMenuMessage,
        static_cast<WPARAM>(static_cast<INT_PTR>(point.x)), static_cast<LPARAM>(point.y));
}

struct Notification { const wchar_t* title; const wchar_t* body; };
LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    static UINT taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    if (taskbarCreated && msg == taskbarCreated) {
        ownAdded = false;
        // Probe our old identity with MODIFY before ADD. A repeated broadcast
        // (including a DPI change) can arrive while that identity still exists.
        registrationAttempts = 0;
        lastFailureStage.clear();
        Log(L"TaskbarCreated");
        RegisterIconOrRetry();
        return 0;
    }
    if (msg == RegisterIconMessage || (msg == WM_TIMER && wp == RegistrationTimer)) {
        RegisterIconOrRetry();
        return 0;
    }
    if (msg == OwnCallback) {
        if (!ownAdded || stopping) return 0;
        auto event = LOWORD(lp);
        if (event == WM_CONTEXTMENU) {
            POINT point{GET_X_LPARAM(wp), GET_Y_LPARAM(wp)};
            TrayMenu(point.x == -1 && point.y == -1 ? nullptr : &point);
        } else if (event == NIN_SELECT || event == NIN_KEYSELECT || event == WM_LBUTTONUP || event == WM_LBUTTONDBLCLK) {
            // The host coalesces activation and preserves its single-click behavior.
            PostMessageW(uiWindow, HostOpenMessage, event, 0);
        }
        return 0;
    }
    if (msg == NotifyMessage) {
        if (!ownAdded || stopping) return FALSE;
        auto* info = reinterpret_cast<Notification*>(lp);
        NOTIFYICONDATAW d{};
        d.cbSize = sizeof(d); d.hWnd = hwnd; d.uID = 1; d.uFlags = NIF_INFO;
        wcsncpy_s(d.szInfoTitle, std::size(d.szInfoTitle), info->title, _TRUNCATE);
        wcsncpy_s(d.szInfo, std::size(d.szInfo), info->body, _TRUNCATE);
        const auto result = Shell_NotifyIconW(NIM_MODIFY, &d);
        if (!result) Log(L"NIM_MODIFY.notification.failed");
        return result;
    }
    if (msg == WM_CLOSE) {
        EndMenu(); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}
void Cleanup() {
    auto hwnd = window.exchange(nullptr);
    ownAdded = false;
    if (hwnd) KillTimer(hwnd, RegistrationTimer);
    if (iconPresent) {
        NOTIFYICONDATAW d{}; d.cbSize=sizeof(d); d.hWnd=hwnd; d.uID=1;
        if (!Shell_NotifyIconW(NIM_DELETE, &d)) Log(L"NIM_DELETE.failed");
        iconPresent = false;
    }
    if (hwnd) DestroyWindow(hwnd);
}
}
extern "C" __declspec(dllexport) HWND WINAPI LanTrayHandle() { return window.load(); }
extern "C" __declspec(dllexport) BOOL WINAPI LanTrayIsAdded() { return ownAdded.load(); }
extern "C" __declspec(dllexport) void WINAPI LanTrayStop() {
    stopping = true;
    if (auto hwnd=window.load()) PostMessageW(hwnd,WM_CLOSE,0,0);
}
extern "C" __declspec(dllexport) BOOL WINAPI LanTrayNotify(const wchar_t* title,const wchar_t* body) {
    Notification info{title,body};
    auto hwnd=window.load();
    return hwnd && SendMessageW(hwnd,NotifyMessage,0,reinterpret_cast<LPARAM>(&info));
}
extern "C" __declspec(dllexport) DWORD WINAPI LanTrayRunWithDiagnostics(
    HICON image, const wchar_t* tip, HWND host, HANDLE ready, const wchar_t* logPath) {
    // Match ReTray NativeRuntime: blocking native message loop on its own STA.
    ownImage=image; tooltip=tip; uiWindow=host;
    diagnosticPath = logPath ? logPath : L"";
    stopping = false;
    registrationAttempts = 0;
    lastFailureStage.clear();
    WNDCLASSW wc{};
    wc.hInstance=GetModuleHandleW(nullptr); wc.lpfnWndProc=WindowProc; wc.lpszClassName=windowClass;
    if (!RegisterClassW(&wc)) {
        const auto error = GetLastError();
        if (error != ERROR_CLASS_ALREADY_EXISTS) {
            Log(L"RegisterClass.failed", error);
            SetEvent(ready);
            return error;
        }
    }
    window=CreateWindowW(wc.lpszClassName,L"LanPod",WS_OVERLAPPED,0,0,0,0,
        nullptr,nullptr,wc.hInstance,nullptr);
    if (!window) {
        const auto error = GetLastError();
        Log(L"CreateWindow.failed", error);
        SetEvent(ready);
        return error ? error : ERROR_GEN_FAILURE;
    }
    if (!PostMessageW(window, RegisterIconMessage, 0, 0)) {
        const auto error = GetLastError();
        Log(L"PostMessage.failed", error);
        Cleanup();
        SetEvent(ready);
        return error ? error : ERROR_GEN_FAILURE;
    }
    // Publish the HWND before touching the shell. Shell_NotifyIcon may block
    // during logon; the WPF dispatcher and monitoring can proceed immediately.
    Log(L"runtime.ready");
    SetEvent(ready);
    MSG message{}; BOOL result{};
    while ((result=GetMessageW(&message,nullptr,0,0))>0) {
        TranslateMessage(&message); DispatchMessageW(&message);
    }
    DWORD error=result<0?GetLastError():static_cast<DWORD>(message.wParam);
    if (error) Log(L"message-loop.failed", error);
    Cleanup();
    Log(L"runtime.stopped", error);
    return error;
}

extern "C" __declspec(dllexport) DWORD WINAPI LanTrayRun(HICON image, const wchar_t* tip, HWND host, HANDLE ready) {
    return LanTrayRunWithDiagnostics(image, tip, host, ready, nullptr);
}
