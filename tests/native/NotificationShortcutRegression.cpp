#include <windows.h>
#include <shlobj.h>
#include <propkey.h>
#include <propvarutil.h>
#include <filesystem>
#include <iostream>
#include <stdexcept>

namespace fs = std::filesystem;
using RemoveShortcut = HRESULT (__cdecl*)(PCWSTR, PCWSTR);

static void Check(HRESULT result) {
    if (FAILED(result)) throw std::runtime_error("Shortcut API failed");
}

static void CreateShortcut(const fs::path& path, PCWSTR identity) {
    IShellLinkW* link = nullptr;
    IPropertyStore* properties = nullptr;
    IPersistFile* file = nullptr;
    PROPVARIANT value{};
    Check(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)));
    Check(link->QueryInterface(IID_PPV_ARGS(&properties)));
    Check(link->QueryInterface(IID_PPV_ARGS(&file)));
    Check(link->SetPath(L"C:\\Fictional\\LanPod.exe"));
    if (identity) {
        Check(InitPropVariantFromString(identity, &value));
        Check(properties->SetValue(PKEY_AppUserModel_ID, value));
        PropVariantClear(&value);
    }
    Check(properties->Commit());
    Check(file->Save(path.c_str(), TRUE));
    file->Release(); properties->Release(); link->Release();
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    auto directory = fs::path(argv[2]) / (L"shortcut-" + std::to_wstring(GetCurrentProcessId()));
    HMODULE module = nullptr;
    try {
        Check(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED));
        module = LoadLibraryW(argv[1]);
        if (!module) throw std::runtime_error("Cannot load native notification module");
        auto remove = reinterpret_cast<RemoveShortcut>(GetProcAddress(module, "LanRemoveLegacyNotificationShortcut"));
        if (!remove) throw std::runtime_error("Missing cleanup export");
        auto shortcut = directory / L"LanPod.lnk";
        if (remove(shortcut.c_str(), L"CRYS74L.LanPod") != S_FALSE || fs::exists(directory))
            throw std::runtime_error("Clean launch created an external shortcut directory");
        fs::create_directories(directory);
        CreateShortcut(shortcut, L"CRYS74L.LanPod");
        Check(remove(shortcut.c_str(), L"CRYS74L.LanPod"));
        if (fs::exists(shortcut)) throw std::runtime_error("Owned legacy shortcut was retained");
        CreateShortcut(shortcut, L"OtherVendor.Application");
        if (remove(shortcut.c_str(), L"CRYS74L.LanPod") != S_FALSE || !fs::exists(shortcut))
            throw std::runtime_error("Foreign shortcut was removed");
        fs::remove(shortcut);
        CreateShortcut(shortcut, nullptr);
        if (remove(shortcut.c_str(), L"CRYS74L.LanPod") != S_FALSE || !fs::exists(shortcut))
            throw std::runtime_error("Unmarked user shortcut was removed");
        fs::remove_all(directory);
        FreeLibrary(module); CoUninitialize();
        std::cout << "Notification shortcut PASS: no new entry, owned legacy cleanup, foreign and unmarked shortcuts preserved.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        std::error_code ignored;
        fs::remove_all(directory, ignored);
        if (module) FreeLibrary(module);
        CoUninitialize();
        return 1;
    }
}
