#include <windows.h>
#include <tlhelp32.h>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

static_assert(sizeof(void*) == 8, "Build probe_loader for Windows x64.");

namespace {
constexpr DWORD threadTimeoutMs = 30000;

[[noreturn]] void winError(const char* operation, DWORD code = GetLastError()) {
    throw std::runtime_error(std::string(operation) + " failed (Win32 error " + std::to_string(code) + ")");
}

struct Handle {
    HANDLE value;
    explicit Handle(HANDLE h) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

struct LocalModule {
    HMODULE value;
    ~LocalModule() { if (value) FreeLibrary(value); }
};

struct RemoteMemory {
    HANDLE process;
    void* address;
    bool mayFree = true;
    ~RemoteMemory() {
        if (address && mayFree && !VirtualFreeEx(process, address, 0, MEM_RELEASE))
            std::fprintf(stderr, "Warning: VirtualFreeEx failed (Win32 error %lu).\n", GetLastError());
    }
};

bool absolutePath(const std::wstring& path) {
    const bool drive = path.size() >= 3 &&
        ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
    const bool unc = path.size() > 2 && path[0] == L'\\' && path[1] == L'\\';
    return drive || unc;
}

std::wstring fullPath(const std::wstring& path) {
    DWORD needed = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (!needed) winError("GetFullPathNameW");
    std::vector<wchar_t> text(needed);
    DWORD length = GetFullPathNameW(path.c_str(), needed, text.data(), nullptr);
    if (!length || length >= needed) winError("GetFullPathNameW");
    return std::wstring(text.data(), length);
}

const wchar_t* basename(const std::wstring& path) {
    const auto pos = path.find_last_of(L"\\/");
    return path.c_str() + (pos == std::wstring::npos ? 0 : pos + 1);
}

DWORD parsePid(const wchar_t* text) {
    if (!*text) throw std::runtime_error("--pid must be a positive decimal DWORD.");
    DWORD result = 0;
    for (; *text; ++text) {
        if (*text < L'0' || *text > L'9' ||
            result > (std::numeric_limits<DWORD>::max() - (*text - L'0')) / 10)
            throw std::runtime_error("--pid must be a positive decimal DWORD.");
        result = result * 10 + (*text - L'0');
    }
    if (!result) throw std::runtime_error("--pid must be nonzero.");
    return result;
}

void requireX64(HANDLE process, const char* label) {
    using QueryArchitecture = BOOL (WINAPI*)(HANDLE, USHORT*, USHORT*);
    auto query = reinterpret_cast<QueryArchitecture>(
        GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "IsWow64Process2"));
    if (!query) winError("Resolve IsWow64Process2");
    USHORT machine = 0, nativeMachine = 0;
    if (!query(process, &machine, &nativeMachine)) winError("IsWow64Process2");
    if (machine != IMAGE_FILE_MACHINE_UNKNOWN || nativeMachine != IMAGE_FILE_MACHINE_AMD64)
        throw std::runtime_error(std::string(label) + " must be native AMD64 (x64), not WOW64/ARM64.");
}

MODULEENTRY32W findModule(DWORD pid, const std::wstring& path) {
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (unsigned attempt = 0; attempt < 8; ++attempt) {
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot != INVALID_HANDLE_VALUE || GetLastError() != ERROR_BAD_LENGTH) break;
    }
    Handle modules(snapshot);
    if (modules.value == INVALID_HANDLE_VALUE) winError("CreateToolhelp32Snapshot");
    MODULEENTRY32W entry = {};
    entry.dwSize = sizeof(entry);
    if (!Module32FirstW(modules.value, &entry)) winError("Module32FirstW");
    do {
        if (_wcsicmp(fullPath(entry.szExePath).c_str(), path.c_str()) == 0) return entry;
    } while (Module32NextW(modules.value, &entry));
    if (GetLastError() != ERROR_NO_MORE_FILES) winError("Module32NextW");
    throw std::runtime_error("Remote module not found by its absolute full path (not by thread exit code).");
}

std::uintptr_t exportRva(const std::wstring& dllPath) {
    LocalModule module{LoadLibraryExW(dllPath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES)};
    if (!module.value) winError("LoadLibraryExW(DONT_RESOLVE_DLL_REFERENCES)");
    const auto base = reinterpret_cast<std::uintptr_t>(module.value);
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0)
        throw std::runtime_error("Invalid probe DLL DOS header.");
    MEMORY_BASIC_INFORMATION region = {};
    if (!VirtualQuery(module.value, &region, sizeof(region))) winError("VirtualQuery");
    const auto ntOffset = static_cast<std::size_t>(dos->e_lfanew);
    if (ntOffset > region.RegionSize || sizeof(IMAGE_NT_HEADERS64) > region.RegionSize - ntOffset)
        throw std::runtime_error("Probe DLL NT header is outside its mapped header region.");
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + ntOffset);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        throw std::runtime_error("Probe DLL must be a valid AMD64 PE image.");
    const auto imageSize = nt->OptionalHeader.SizeOfImage;
    FARPROC start = GetProcAddress(module.value, "probeStart");
    if (!start) winError("GetProcAddress(probeStart)");
    const auto address = reinterpret_cast<std::uintptr_t>(start);
    if (address < base || address - base >= imageSize)
        throw std::runtime_error("probeStart export RVA is outside the probe image (forwarded exports are not supported).");
    const auto rva = address - base;
    const auto& exports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (rva >= exports.VirtualAddress && rva - exports.VirtualAddress < exports.Size)
        throw std::runtime_error("probeStart must not be a forwarded export.");
    return rva;
}

LPTHREAD_START_ROUTINE remoteLoadLibrary(DWORD pid) {
    FARPROC load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    if (!load) winError("GetProcAddress(LoadLibraryW)");
    HMODULE owner = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(load), &owner)) winError("GetModuleHandleExW(LoadLibraryW owner)");
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(owner, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) winError("GetModuleFileNameW(LoadLibraryW owner)");
    const auto remote = findModule(pid, fullPath(std::wstring(path.data(), length)));
    const auto rva = reinterpret_cast<std::uintptr_t>(load) - reinterpret_cast<std::uintptr_t>(owner);
    if (rva >= remote.modBaseSize) throw std::runtime_error("LoadLibraryW RVA is outside its remote system module.");
    return reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<std::uintptr_t>(remote.modBaseAddr) + rva);
}

void waitThread(HANDLE thread, const char* label) {
    DWORD wait = WaitForSingleObject(thread, threadTimeoutMs);
    if (wait == WAIT_TIMEOUT)
        throw std::runtime_error(std::string(label) + " timed out after 30 seconds; remote work may still be running. Restart the target before retrying.");
    if (wait != WAIT_OBJECT_0) winError("WaitForSingleObject");
}
}

int wmain(int argc, wchar_t** argv) {
    bool remoteLoadStarted = false;
    try {
        DWORD pid = 0;
        bool bridge = false, bypass = false;
        std::wstring dllPath;
        for (int i = 1; i < argc; ++i) {
            if (!std::wcscmp(argv[i], L"--pid") && i + 1 < argc && !pid) pid = parsePid(argv[++i]);
            else if (!std::wcscmp(argv[i], L"--dll") && i + 1 < argc && dllPath.empty()) dllPath = argv[++i];
            else if (!std::wcscmp(argv[i], L"--bridge") && !bridge) bridge = true;
            else if (!std::wcscmp(argv[i], L"--bypass") && !bypass) bypass = true;
            else throw std::runtime_error("Usage: probe_loader --pid <integer> --dll <absolute DLL path> [--bridge] [--bypass]");
        }
        if (!pid || dllPath.empty()) throw std::runtime_error("Usage: probe_loader --pid <integer> --dll <absolute DLL path>");
        if (bypass) bridge = true;
        if (!absolutePath(dllPath)) throw std::runtime_error("--dll must be an absolute path.");
        dllPath = fullPath(dllPath);
        DWORD attributes = GetFileAttributesW(dllPath.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) winError("GetFileAttributesW(probe DLL)");
        if (attributes & FILE_ATTRIBUTE_DIRECTORY) throw std::runtime_error("--dll must identify a file, not a directory.");
        requireX64(GetCurrentProcess(), "Loader");
        Handle process(OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION |
            PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid));
        if (!process.value) winError("OpenProcess (no automatic elevation is attempted)");
        std::vector<wchar_t> exe(32768);
        DWORD exeLength = static_cast<DWORD>(exe.size());
        if (!QueryFullProcessImageNameW(process.value, 0, exe.data(), &exeLength)) winError("QueryFullProcessImageNameW");
        const std::wstring exePath(exe.data(), exeLength);
        if (_wcsicmp(basename(exePath), L"LosslessScaling.exe") != 0)
            throw std::runtime_error("Refusing injection: target executable basename must be LosslessScaling.exe.");
        requireX64(process.value, "Target");
        const auto startRva = exportRva(dllPath);
        const auto loadLibrary = remoteLoadLibrary(pid);
        std::fwprintf(stdout, L"Explicit diagnostic injection: PID %lu, target %ls, DLL %ls\n", pid, exePath.c_str(), dllPath.c_str());
        const SIZE_T bytes = (dllPath.size() + 1) * sizeof(wchar_t);
        RemoteMemory argument{process.value, VirtualAllocEx(process.value, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)};
        if (!argument.address) winError("VirtualAllocEx");
        SIZE_T written = 0;
        if (!WriteProcessMemory(process.value, argument.address, dllPath.c_str(), bytes, &written)) winError("WriteProcessMemory");
        if (written != bytes) throw std::runtime_error("WriteProcessMemory wrote an incomplete DLL path.");
        Handle loadThread(CreateRemoteThread(process.value, nullptr, 0, loadLibrary, argument.address, 0, nullptr));
        if (!loadThread.value) winError("CreateRemoteThread(LoadLibraryW)");
        remoteLoadStarted = true;
        argument.mayFree = false;
        try {
            waitThread(loadThread.value, "LoadLibraryW");
        } catch (...) {
            std::fprintf(stderr, "Remote DLL path allocation retained: the load thread may still use it.\n");
            throw;
        }
        argument.mayFree = true;
        // A thread exit code is only DWORD: it cannot carry an x64 HMODULE.
        const auto remote = findModule(pid, dllPath);
        if (startRva >= remote.modBaseSize) throw std::runtime_error("probeStart RVA is outside the remote image.");
        const auto start = reinterpret_cast<LPTHREAD_START_ROUTINE>(
            reinterpret_cast<std::uintptr_t>(remote.modBaseAddr) + startRva);
        std::fprintf(stdout, "Mode: %s\n", bypass ? "XeSS takeover + exact LSFG compute bypass; Escape uses raw fallback; restart to restore LSFG" :
            bridge ? "XeSS presentation takeover; Escape disables; LSFG compute retained" : "observe-only diagnostic");
        Handle startThread(CreateRemoteThread(process.value, nullptr, 0, start,
            bypass ? reinterpret_cast<void*>(2) : bridge ? reinterpret_cast<void*>(1) : nullptr, 0, nullptr));
        if (!startThread.value) winError("CreateRemoteThread(probeStart)");
        waitThread(startThread.value, "probeStart");
        DWORD result = 0;
        if (!GetExitCodeThread(startThread.value, &result)) winError("GetExitCodeThread(probeStart)");
        if (result != 0) throw std::runtime_error("probeStart returned nonzero status " + std::to_string(result) + ".");
        std::fwprintf(stdout, L"probeStart succeeded (0). Probe remains loaded because hooks are live; restart LosslessScaling.exe to unload.\n");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "probe_loader: %s\n", error.what());
        if (remoteLoadStarted)
            std::fprintf(stderr, "No remote FreeLibrary is performed; restart LosslessScaling.exe to unload any probe/hooks before retrying.\n");
        return 1;
    }
}
