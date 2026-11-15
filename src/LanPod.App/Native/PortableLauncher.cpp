#include <windows.h>
#include <setupapi.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <sstream>
#include <cwctype>
#include <system_error>

namespace fs = std::filesystem;

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

static void Check(bool success) {
    if (!success) throw std::system_error(GetLastError(), std::system_category());
}

static std::string ResourceText(int id) {
    auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    Check(resource != nullptr);
    auto bytes = static_cast<const char*>(LockResource(LoadResource(nullptr, resource)));
    Check(bytes != nullptr);
    return std::string(bytes, SizeofResource(nullptr, resource));
}

static bool RuntimeComplete(const fs::path& directory) {
    if (!fs::is_regular_file(directory / L".complete")) return false;
    std::istringstream manifest(ResourceText(103));
    std::string line;
    while (std::getline(manifest, line)) {
        auto separator = line.find('\t');
        auto file = directory / fs::path(line.substr(separator + 1));
        if (!fs::is_regular_file(file) || fs::file_size(file) != std::stoull(line.substr(0, separator)))
            return false;
    }
    return true;
}

struct Extraction {
    fs::path directory;
    DWORD error = ERROR_SUCCESS;
};

static UINT CALLBACK ExtractFile(void* context, UINT message, UINT_PTR parameter1, UINT_PTR) {
    auto& extraction = *static_cast<Extraction*>(context);
    try {
        if (message == SPFILENOTIFY_FILEINCABINET) {
            auto file = reinterpret_cast<FILE_IN_CABINET_INFO_W*>(parameter1);
            fs::path relative(file->NameInCabinet);
            if (relative.is_absolute() || relative.has_root_name())
                throw std::system_error(ERROR_INVALID_DATA, std::system_category());
            for (const auto& component : relative)
                if (component == L"..")
                    throw std::system_error(ERROR_INVALID_DATA, std::system_category());
            // Cabinet paths are relative to our private extraction directory.
            // This also avoids the MAX_PATH-sized callback buffer for long roots.
            auto name = relative.wstring();
            if (name.size() >= MAX_PATH)
                throw std::system_error(ERROR_FILENAME_EXCED_RANGE, std::system_category());
            fs::create_directories((extraction.directory / relative).parent_path());
            wcscpy_s(file->FullTargetName, MAX_PATH, name.c_str());
            return FILEOP_DOIT;
        }
        if (message == SPFILENOTIFY_FILEEXTRACTED) {
            auto paths = reinterpret_cast<FILEPATHS_W*>(parameter1);
            extraction.error = paths->Win32Error;
            return paths->Win32Error;
        }
        return NO_ERROR;
    } catch (const std::system_error& error) {
        extraction.error = static_cast<DWORD>(error.code().value());
    } catch (...) { extraction.error = ERROR_INVALID_DATA; }
    SetLastError(extraction.error);
    return FILEOP_ABORT;
}

static fs::path PrepareRuntime(const fs::path& root) {
    auto identityText = ResourceText(102);
    auto identity = std::wstring(identityText.begin(), identityText.end());
    auto directory = root / L"Data" / L"Runtime" / identity;

    Handle mutex;
    // Serialize only extraction. The application's existing single-instance
    // service owns monitoring, activation and the window lifecycle.
    auto normalizedRoot = root.wstring();
    for (auto& character : normalizedRoot) character = std::towlower(character);
    auto mutexName = L"Local\\LanPod.Extract." + std::to_wstring(std::hash<std::wstring>{}(normalizedRoot));
    mutex.value = CreateMutexW(nullptr, FALSE, mutexName.c_str());
    Check(mutex.value != nullptr);
    auto wait = WaitForSingleObject(mutex.value, INFINITE);
    Check(wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED);
    struct Unlock { HANDLE mutex; ~Unlock() { ReleaseMutex(mutex); } } unlock{mutex.value};

    if (RuntimeComplete(directory)) return directory;

    auto staging = directory.parent_path() / (L".extract-" + std::to_wstring(GetCurrentProcessId()));
    fs::create_directories(staging);
    auto originalDirectory = fs::current_path();
    try {
        auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(101), RT_RCDATA);
        Check(resource != nullptr);
        auto size = SizeofResource(nullptr, resource);
        auto bytes = LockResource(LoadResource(nullptr, resource));
        Check(bytes != nullptr && size != 0);
        auto cabinet = staging / L"payload.cab";
        std::ofstream output(cabinet, std::ios::binary | std::ios::trunc);
        output.write(static_cast<const char*>(bytes), size);
        output.close();
        if (!output) throw std::system_error(ERROR_WRITE_FAULT, std::system_category());
        fs::current_path(staging);
        Extraction extraction{staging};
        auto extracted = SetupIterateCabinetW(cabinet.c_str(), 0, ExtractFile, &extraction);
        auto error = extraction.error ? extraction.error : GetLastError();
        fs::current_path(originalDirectory);
        if (!extracted || extraction.error) throw std::system_error(error, std::system_category());
        fs::remove(cabinet);
        if (!fs::exists(staging / L"LanPod.exe") || !fs::exists(staging / L"coreclr.dll"))
            throw std::system_error(ERROR_FILE_CORRUPT, std::system_category());
        std::ofstream marker(staging / L".complete");
        marker << "ready";
        marker.close();
        if (!marker) throw std::system_error(ERROR_WRITE_FAULT, std::system_category());
        if (!RuntimeComplete(staging))
            throw std::system_error(ERROR_FILE_CORRUPT, std::system_category());
        if (fs::exists(directory)) fs::remove_all(directory);
        fs::rename(staging, directory);
        return directory;
    } catch (...) {
        fs::current_path(originalDirectory);
        std::error_code cleanupError;
        fs::remove_all(staging, cleanupError);
        throw;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int) {
    try {
        std::wstring executable(32768, L'\0');
        auto length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
        Check(length > 0 && length < executable.size());
        executable.resize(length);
        auto root = fs::path(executable).parent_path();
        auto runtime = PrepareRuntime(root);
        Check(SetEnvironmentVariableW(L"LANPOD_LAUNCHER_PATH", executable.c_str()));
        auto childExecutable = runtime / L"LanPod.exe";
        std::wstring command = L"\"" + childExecutable.wstring() + L"\" " + arguments;
        STARTUPINFOW startup{sizeof(startup)};
        PROCESS_INFORMATION process{};
        Check(CreateProcessW(childExecutable.c_str(), command.data(), nullptr, nullptr, FALSE,
            0, nullptr, root.c_str(), &startup, &process));
        Handle thread{process.hThread};
        Handle child{process.hProcess};
        Check(WaitForSingleObject(child.value, INFINITE) == WAIT_OBJECT_0);
        DWORD exitCode = 1;
        Check(GetExitCodeProcess(child.value, &exitCode));
        return static_cast<int>(exitCode);
    } catch (const std::exception& error) {
        auto size = MultiByteToWideChar(CP_UTF8, 0, error.what(), -1, nullptr, 0);
        std::wstring detail(size, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, error.what(), -1, detail.data(), size);
        MessageBoxW(nullptr, (L"无法启动 LanPod。\n" + detail).c_str(), L"LanPod", MB_OK | MB_ICONERROR);
        return 1;
    }
}
