#include <windows.h>
#include <shlobj.h>
#include <propkey.h>
#include <propvarutil.h>

extern "C" __declspec(dllexport) HRESULT __cdecl LanEnsureNotificationShortcut(
    PCWSTR shortcutPath,
    PCWSTR executablePath,
    PCWSTR workingDirectory,
    PCWSTR appUserModelId,
    const GUID* activatorClsid)
{
    IShellLinkW* link = nullptr;
    IPropertyStore* properties = nullptr;
    IPersistFile* file = nullptr;
    PROPVARIANT value{};

    HRESULT result = CoCreateInstance(
        CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link));
    if (SUCCEEDED(result)) result = link->QueryInterface(IID_PPV_ARGS(&properties));
    if (SUCCEEDED(result)) result = link->QueryInterface(IID_PPV_ARGS(&file));

    // Load an existing shortcut so unrelated user properties remain intact.
    const bool existed = GetFileAttributesW(shortcutPath) != INVALID_FILE_ATTRIBUTES;
    if (SUCCEEDED(result) && existed)
        result = file->Load(shortcutPath, STGM_READWRITE);

    if (SUCCEEDED(result)) result = link->SetPath(executablePath);
    if (SUCCEEDED(result)) result = link->SetWorkingDirectory(workingDirectory);
    if (SUCCEEDED(result)) result = link->SetIconLocation(executablePath, 0);
    if (SUCCEEDED(result)) result = InitPropVariantFromString(appUserModelId, &value);
    if (SUCCEEDED(result)) result = properties->SetValue(PKEY_AppUserModel_ID, value);
    PropVariantClear(&value);

    if (SUCCEEDED(result)) result = InitPropVariantFromCLSID(*activatorClsid, &value);
    if (SUCCEEDED(result)) result = properties->SetValue(PKEY_AppUserModel_ToastActivatorCLSID, value);
    PropVariantClear(&value);

    if (SUCCEEDED(result)) result = properties->Commit();
    if (SUCCEEDED(result)) result = file->Save(shortcutPath, TRUE);

    if (file) file->Release();
    if (properties) properties->Release();
    if (link) link->Release();

    if (SUCCEEDED(result))
        SHChangeNotify(existed ? SHCNE_UPDATEITEM : SHCNE_CREATE,
            SHCNF_PATHW | SHCNF_FLUSH, shortcutPath, nullptr);
    return result;
}
