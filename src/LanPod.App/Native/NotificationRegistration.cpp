#include <windows.h>
#include <shlobj.h>
#include <propkey.h>

extern "C" __declspec(dllexport) HRESULT __cdecl LanRemoveLegacyNotificationShortcut(
    PCWSTR shortcutPath, PCWSTR appUserModelId)
{
    auto attributes = GetFileAttributesW(shortcutPath);
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        auto error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ? S_FALSE : HRESULT_FROM_WIN32(error);
    }
    if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) return S_FALSE;
    IShellLinkW* link = nullptr;
    IPropertyStore* properties = nullptr;
    IPersistFile* file = nullptr;
    PROPVARIANT value{};

    HRESULT result = CoCreateInstance(
        CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link));
    if (SUCCEEDED(result)) result = link->QueryInterface(IID_PPV_ARGS(&properties));
    if (SUCCEEDED(result)) result = link->QueryInterface(IID_PPV_ARGS(&file));

    if (SUCCEEDED(result)) result = file->Load(shortcutPath, STGM_READ);
    if (SUCCEEDED(result)) result = properties->GetValue(PKEY_AppUserModel_ID, &value);
    const bool owned = SUCCEEDED(result) && value.vt == VT_LPWSTR &&
        value.pwszVal && wcscmp(value.pwszVal, appUserModelId) == 0;
    PropVariantClear(&value);

    if (file) file->Release();
    if (properties) properties->Release();
    if (link) link->Release();

    if (SUCCEEDED(result) && !owned) return S_FALSE;
    if (SUCCEEDED(result)) {
        if (!DeleteFileW(shortcutPath)) return HRESULT_FROM_WIN32(GetLastError());
        SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW | SHCNF_FLUSH, shortcutPath, nullptr);
    }
    return result;
}
