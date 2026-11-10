#include "ls_bridge.h"
#include "xel_fg.h"
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <cstdarg>
#include <cmath>
#include <mutex>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif

namespace lsbridge {
namespace {
using Microsoft::WRL::ComPtr;
constexpr GUID shaderTag = {0xe03dc165, 0xc4b1, 0x4cb3, {0xb4, 0x75, 0x75, 0x0f, 0x72, 0x11, 0x68, 0x22}};
constexpr uint64_t conversionHash = 0x5cb211ca30438f98ull;
constexpr UINT updateWindow = WM_APP + 0x321;
struct ShaderTag { uint64_t hash; uint64_t size; };
// Exact bytecode identities observed in this LS build, excluding conversion and final copy.
constexpr ShaderTag fgShaders[] = {
    {0x93233956c02c9df8ull,6388}, {0xffed152a84367f04ull,15592},
    {0xa1253097bf65c4f0ull,28688}, {0x96a3a14ffe70f6ffull,55292},
    {0xfcec26bedac7fe65ull,33752}, {0x00de3f7c20f18a08ull,55292},
    {0x8f7c8c3e7d46f853ull,55292}, {0xbcae3282c1bd1d92ull,13848},
    {0xd95d2b5f02fa3a6cull,33752}, {0x42943a9b68cc97fcull,55292},
    {0xa6805ebfab3c195aull,55292}, {0x3fb394cdde674cb9ull,13848},
    {0x46908e610fc3081cull,15592}, {0x80ca636c1bb5373dull,15592},
    {0xc21b13e7873ca7b6ull,15592}, {0x7a0e1a5ba8f04cabull,9780},
    {0x5a80323715d42c7bull,10048}, {0xc6b3ecbc0842d44dull,34528},
    {0x93edb79f5e6042bcull,16904}, {0xb5d60d379446edb1ull,15592},
    {0xe334851db3f61e79ull,15592}, {0x75da9f7e8df55949ull,15592},
    {0xd6dc93a378d096e5ull,11076}, {0xe5533b22265437cdull,6012},
    {0xcd2b9454f7414535ull,3132}
};
bool isFg(const ShaderTag& tag) {
    for (const auto& known : fgShaders)
        if (known.hash == tag.hash && known.size == tag.size) return true;
    return false;
}
bool bypassRequested = false, bypassFrame = false, historyDirty = false;
bool fsr3Backend = false;
bool estimateInputsRequested = false;
uint64_t skippedDispatches = 0;
struct Api {
    decltype(&xelCreate) create = nullptr;
    HRESULT(__cdecl *estimate)(XelSession*, uint32_t) = nullptr;
    decltype(&xelSubmit) submit = nullptr;
    decltype(&xelGetStats) stats = nullptr;
    decltype(&xelDestroy) destroy = nullptr;
    decltype(&xelAdoptThread) adopt = nullptr;
    decltype(&xelGetLastError) error = nullptr;
} api;
std::once_flag startup;
std::atomic<bool> ready{false}, disabled{false}, wanted{false}, affinity{false};
std::atomic<HWND> outputWindow{nullptr}, sourceWindow{nullptr};
std::atomic<ULONGLONG> heartbeat{0};
std::atomic<DWORD> ownerThread{0};
HMODULE bridgeModule = nullptr;
LONGLONG frequency = 0;
SRWLOCK renderLock = SRWLOCK_INIT;
SRWLOCK logLock = SRWLOCK_INIT;
FILE* logFile = nullptr;
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> immediate;
ComPtr<ID3D11Texture2D> snapshot;
ComPtr<IDXGISwapChain> sourceChain;
XelSession* session = nullptr;
UINT width = 0, height = 0;
uint64_t rawSerial = 0, submittedSerial = 0, previousInterpolated = 0;
LONGLONG rawTicks = 0, submittedTicks = 0;
ULONGLONG successTick = 0;
bool interpolated = false;
std::atomic<bool> notified{false};

void log(const char* format, ...) {
    AcquireSRWLockExclusive(&logLock);
    if (logFile) {
        fprintf(logFile, "[%llu tid=%lu] ", GetTickCount64(), GetCurrentThreadId());
        va_list args;
        va_start(args, format);
        vfprintf(logFile, format, args);
        va_end(args);
        fputc('\n', logFile);
        fflush(logFile);
    }
    ReleaseSRWLockExclusive(&logLock);
}
void notifyWindow() {
    HWND window = outputWindow.load();
    if (window) PostMessageW(window, updateWindow, 0, 0);
}
void hide() {
    notified.store(false);
    if (wanted.exchange(false)) notifyWindow();
}
void disable(const char* reason) {
    if (!disabled.exchange(true)) {
        log("disabled: %s", reason);
        hide();
    } else {
        wanted.store(false);
    }
}
// Called only while holding renderLock, on the thread that owns the session.
void cleanup() {
    hide();
    if (GetCurrentThreadId() != ownerThread.load()) return;
    if (session) {
        HRESULT hr = api.destroy(&session);
        if (FAILED(hr)) {
            log("xelDestroy failed hr=%08lx: %s; retain handle for creator-thread retry", hr, api.error());
            return;
        }
    }
    // Keep capturing raw frames after bypass: LS temporal history is no longer valid.
    if (!historyDirty) {
        snapshot.Reset();
        sourceChain.Reset();
        immediate.Reset();
        device.Reset();
    }
}
struct RenderGuard {
    bool locked;
    RenderGuard() : locked(TryAcquireSRWLockExclusive(&renderLock) != FALSE) {}
    ~RenderGuard() { if (locked) ReleaseSRWLockExclusive(&renderLock); }
};
bool escapePressed() {
    if (GetAsyncKeyState(VK_ESCAPE) & 0x8000) disable("Escape");
    return disabled.load();
}
bool sourceRectangle(HWND source, RECT& rect) {
    if (!source || !IsWindow(source) || !IsWindowVisible(source) || IsIconic(source)) return false;
    if (!GetClientRect(source, &rect) || rect.right <= 0 || rect.bottom <= 0) return false;
    POINT origin = {0, 0};
    if (!ClientToScreen(source, &origin)) return false;
    OffsetRect(&rect, origin.x, origin.y);
    return true;
}
void refreshWindow(HWND window) {
    RECT rect = {};
    const bool valid = sourceRectangle(sourceWindow.load(), rect);
    const ULONGLONG beat = heartbeat.load();
    const bool show = !escapePressed() && affinity.load() && wanted.load() && valid &&
        beat != 0 && GetTickCount64() - beat <= 250;
    if (!show) notified.store(false);
    if (valid) {
        HWND toast = FindWindowW(L"GameLockerFloatingToast", L"GameLocker Toast");
        HWND above = toast && IsWindowVisible(toast) ? toast : HWND_TOPMOST;
        SetWindowPos(window, above, rect.left, rect.top,
            rect.right - rect.left, rect.bottom - rect.top,
            SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    } else {
        ShowWindow(window, SW_HIDE);
    }
}
LRESULT CALLBACK windowProc(HWND window, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_TIMER || message == updateWindow) {
        refreshWindow(window);
        return 0;
    }
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_NCHITTEST) return HTTRANSPARENT;
    if (message == WM_CLOSE) { disable("output window closed"); return 0; }
    if (message == WM_DESTROY) {
        outputWindow.store(nullptr);
        disable("output window destroyed");
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wp, lp);
}
DWORD WINAPI windowThread(void*) {
    WNDCLASSW wc = {};
    wc.lpfnWndProc = windowProc;
    wc.hInstance = bridgeModule;
    wc.lpszClassName = L"GameLocker.LSBridge.Output";
    if (!RegisterClassW(&wc)) { disable("RegisterClass failed"); return 0; }
    HWND window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
        wc.lpszClassName, L"GameLocker LS presentation bridge", WS_POPUP,
        0, 0, 1, 1, nullptr, nullptr, bridgeModule, nullptr);
    if (!window) { disable("CreateWindow failed"); return 0; }
    if (!SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE)) {
        log("capture exclusion failed error=%lu", GetLastError());
        disable("capture exclusion required");
        DestroyWindow(window);
        return 0;
    }
    DWORD actualAffinity = 0;
    if (!GetWindowDisplayAffinity(window, &actualAffinity) || actualAffinity != WDA_EXCLUDEFROMCAPTURE) {
        disable("capture exclusion not confirmed");
        DestroyWindow(window);
        return 0;
    }
    affinity.store(true);
    outputWindow.store(window);
    if (!SetTimer(window, 1, 50, nullptr)) {
        disable("watchdog timer failed");
        DestroyWindow(window);
        return 0;
    }
    log("output HWND ready, capture excluded; initially hidden");
    MSG message;
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}
bool sameIdentity(IUnknown* left, IUnknown* right) {
    if (!left || !right) return false;
    ComPtr<IUnknown> a, b;
    if (FAILED(left->QueryInterface(IID_PPV_ARGS(&a))) ||
        FAILED(right->QueryInterface(IID_PPV_ARGS(&b)))) return false;
    return a.Get() == b.Get();
}
bool textureShape(const D3D11_TEXTURE2D_DESC& desc, DXGI_FORMAT format) {
    return desc.Width && desc.Height && desc.Format == format && desc.MipLevels == 1 &&
        desc.ArraySize == 1 && desc.SampleDesc.Count == 1 && desc.SampleDesc.Quality == 0;
}
bool ownerCheck(ID3D11DeviceContext* context) {
    DWORD current = GetCurrentThreadId();
    DWORD owner = ownerThread.load();
    if (owner && owner != current) {
        hide();
        bypassFrame = false;
        if (session && FAILED(api.adopt(session))) {
            log("render thread transfer failed: %s", api.error());
            disable("render thread transfer failed");
            return false;
        }
        log("render thread transferred from %lu to %lu", owner, current);
        ownerThread.store(current);
        sourceChain.Reset();
        sourceWindow.store(nullptr);
        submittedTicks = 0;
        submittedSerial = 0;
        interpolated = false;
        successTick = 0;
    }
    if (immediate && !sameIdentity(immediate.Get(), context)) {
        hide();
        bypassFrame = false;
        if (session && FAILED(api.destroy(&session))) {
            log("capture session teardown failed: %s", api.error());
            disable("capture session teardown failed");
            return false;
        }
        snapshot.Reset();
        sourceChain.Reset();
        immediate.Reset();
        device.Reset();
        sourceWindow.store(nullptr);
        submittedTicks = 0;
        submittedSerial = 0;
        previousInterpolated = 0;
        rawTicks = 0;
        interpolated = false;
        successTick = 0;
        log("capture context recreated; old backend drained and destroyed");
    }
    return true;
}
} // namespace

bool start(HMODULE module, bool bypass, bool fsr3, bool estimateInputs) {
    std::call_once(startup, [module, bypass, fsr3, estimateInputs]() {
        bypassRequested = bypass;
        fsr3Backend = fsr3;
        estimateInputsRequested = estimateInputs;
        wchar_t temp[MAX_PATH] = {}, path[MAX_PATH] = {};
        DWORD length = GetTempPathW(MAX_PATH, temp);
        if (length && length < MAX_PATH && swprintf_s(path, L"%sGameLocker-LS-bridge-%lu.log", temp, GetCurrentProcessId()) > 0)
            _wfopen_s(&logFile, path, L"a");
        log("opt-in presentation bridge starting backend=%s computeBypassRequested=%u estimateInputs=%u", fsr3 ? "FSR3" : "XeSS", bypass ? 1u : 0u, estimateInputs ? 1u : 0u);
        LARGE_INTEGER hz;
        if (!module || !QueryPerformanceFrequency(&hz) || hz.QuadPart <= 0) {
            disable("invalid module or QPC"); return;
        }
        frequency = hz.QuadPart;
        // Pin the hook module: this worker and its window procedure live until process exit.
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&start), &bridgeModule)) {
            disable("cannot pin module"); return;
        }
        wchar_t dllPath[32768] = {};
        DWORD count = GetModuleFileNameW(module, dllPath, 32768);
        if (!count || count >= 32768) { disable("module path unavailable"); return; }
        wchar_t* slash = wcsrchr(dllPath, L'\\');
        if (!slash || wcscpy_s(slash + 1, 32768 - static_cast<size_t>(slash + 1 - dllPath), fsr3 ? L"fsr_fg.dll" : L"xel_fg.dll")) {
            disable("sibling path invalid"); return;
        }
        HMODULE dll = LoadLibraryExW(dllPath, nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!dll) { log("LoadLibraryEx failed error=%lu", GetLastError()); disable("frame generation backend unavailable"); return; }
        // Intentionally retain the backend module; no unloading or thread teardown in DllMain.
        api.create = reinterpret_cast<decltype(api.create)>(GetProcAddress(dll, "xelCreate"));
        api.submit = reinterpret_cast<decltype(api.submit)>(GetProcAddress(dll, "xelSubmit"));
        api.stats = reinterpret_cast<decltype(api.stats)>(GetProcAddress(dll, "xelGetStats"));
        api.destroy = reinterpret_cast<decltype(api.destroy)>(GetProcAddress(dll, "xelDestroy"));
        api.error = reinterpret_cast<decltype(api.error)>(GetProcAddress(dll, "xelGetLastError"));
        api.adopt = reinterpret_cast<decltype(api.adopt)>(GetProcAddress(dll, "xelAdoptThread"));
        api.estimate = reinterpret_cast<decltype(api.estimate)>(GetProcAddress(dll, "xelSetEstimation"));
        if (!api.create || !api.submit || !api.stats || !api.destroy || !api.error || !api.adopt || !api.estimate) {
            disable("missing backend exports"); return;
        }
        HANDLE thread = CreateThread(nullptr, 0, windowThread, nullptr, 0, nullptr);
        if (!thread) { disable("UI thread creation failed"); return; }
        CloseHandle(thread);
        ready.store(true);
        log("bridge initialized; waiting for exact raw conversion shader");
    });
    return ready.load() && !disabled.load();
}

void registerShader(ID3D11ComputeShader* shader, uint64_t hash, SIZE_T size) {
    if (!ready.load() || !shader ||
        !((hash == conversionHash && size == 1216) || isFg({hash, static_cast<uint64_t>(size)}))) return;
    const ShaderTag tag = {hash, static_cast<uint64_t>(size)};
    HRESULT hr = shader->SetPrivateData(shaderTag, sizeof(tag), &tag);
    if (FAILED(hr)) disable("cannot tag conversion shader");
    else log("classified shader hash=%016llx size=%llu role=%s", tag.hash, tag.size,
        hash == conversionHash ? "raw" : "LSFG");
}

void afterDispatch(ID3D11DeviceContext* context, UINT x, UINT y, UINT z) {
    if (!ready.load() || !context) return;
    if (disabled.load()) {
        RenderGuard guard;
        if (!guard.locked) return;
        cleanup();
        if (!historyDirty) return;
    }
    if (context->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE) return;
    ComPtr<ID3D11ComputeShader> shader;
    context->CSGetShader(&shader, nullptr, nullptr);
    ShaderTag tag = {};
    UINT bytes = sizeof(tag);
    if (!shader || FAILED(shader->GetPrivateData(shaderTag, &bytes, &tag)) || bytes != sizeof(tag) ||
        tag.hash != conversionHash || tag.size != 1216) return;
    RenderGuard guard;
    if (!guard.locked) { disable("concurrent raw dispatch"); return; }
    if (!ownerCheck(context)) { cleanup(); return; }
    if (escapePressed()) { cleanup(); if (!historyDirty) return; }
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11UnorderedAccessView> uav;
    context->CSGetShaderResources(0, 1, &srv);
    context->CSGetUnorderedAccessViews(0, 1, &uav);
    if (!srv || !uav) { disable("conversion views missing"); cleanup(); return; }
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    srv->GetDesc(&sd);
    uav->GetDesc(&ud);
    ComPtr<ID3D11Resource> inputResource, outputResource;
    srv->GetResource(&inputResource);
    uav->GetResource(&outputResource);
    ComPtr<ID3D11Texture2D> input, output;
    if (FAILED(inputResource.As(&input)) || FAILED(outputResource.As(&output))) {
        disable("conversion resources not 2D textures"); cleanup(); return;
    }
    D3D11_TEXTURE2D_DESC in = {}, out = {};
    input->GetDesc(&in);
    output->GetDesc(&out);
    if (sd.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D || sd.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
        sd.Texture2D.MostDetailedMip != 0 || sd.Texture2D.MipLevels != 1 ||
        ud.ViewDimension != D3D11_UAV_DIMENSION_TEXTURE2D || ud.Format != DXGI_FORMAT_R8G8B8A8_UNORM ||
        ud.Texture2D.MipSlice != 0 || !textureShape(in, DXGI_FORMAT_B8G8R8A8_UNORM) ||
        !textureShape(out, DXGI_FORMAT_R8G8B8A8_UNORM) || in.Width != out.Width || in.Height != out.Height ||
        out.Usage != D3D11_USAGE_DEFAULT || out.CPUAccessFlags != 0 ||
        x != (out.Width + 7) / 8 || y != (out.Height + 7) / 8 || z != 1) {
        disable("conversion descriptor or dispatch dimensions mismatch"); cleanup(); return;
    }
    ComPtr<ID3D11Device> currentDevice, resourceDevice, inputDevice;
    context->GetDevice(&currentDevice);
    output->GetDevice(&resourceDevice);
    input->GetDevice(&inputDevice);
    if (!sameIdentity(currentDevice.Get(), inputDevice.Get()) ||
        !sameIdentity(currentDevice.Get(), resourceDevice.Get()) ||
        (device && !sameIdentity(device.Get(), currentDevice.Get()))) {
        disable("raw device changed"); cleanup(); return;
    }
    if (snapshot && (width != out.Width || height != out.Height)) {
        disable("raw size changed"); cleanup(); return;
    }
    if (!snapshot) {
        ownerThread.store(GetCurrentThreadId());
        device = currentDevice;
        immediate = context;
        width = out.Width;
        height = out.Height;
        D3D11_TEXTURE2D_DESC copy = out;
        copy.BindFlags = 0;
        copy.MiscFlags = 0;
        HRESULT hr = device->CreateTexture2D(&copy, nullptr, &snapshot);
        if (FAILED(hr)) { log("snapshot allocation failed hr=%08lx", hr); disable("snapshot allocation"); cleanup(); return; }
        log("raw snapshot active: %ux%u, owner tid=%lu", width, height, ownerThread.load());
    }
    if (rawSerial == UINT64_MAX) { disable("raw serial exhausted"); cleanup(); return; }
    LARGE_INTEGER now;
    if (!QueryPerformanceCounter(&now)) { disable("QPC failed"); cleanup(); return; }
    context->CopyResource(snapshot.Get(), output.Get());
    rawTicks = now.QuadPart;
    ++rawSerial;
    bypassFrame = bypassRequested && !disabled.load() && interpolated && wanted.load() &&
        successTick && GetTickCount64() - successTick <= 250;
}

bool skipDispatch(ID3D11DeviceContext* context) {
    if (!ready.load() || !bypassRequested || !context ||
        GetCurrentThreadId() != ownerThread.load()) return false;
    RenderGuard guard;
    if (!guard.locked || !bypassFrame || disabled.load() ||
        !sameIdentity(immediate.Get(), context)) return false;
    ComPtr<ID3D11ComputeShader> shader;
    context->CSGetShader(&shader, nullptr, nullptr);
    ShaderTag tag = {};
    UINT bytes = sizeof(tag);
    if (!shader || FAILED(shader->GetPrivateData(shaderTag, &bytes, &tag)) ||
        bytes != sizeof(tag) || !isFg(tag)) return false;
    historyDirty = true;
    if (++skippedDispatches == 1 || skippedDispatches % 12000 == 0)
        log("LSFG dispatch bypassed=%llu hash=%016llx raw=%llu", skippedDispatches, tag.hash, rawSerial);
    return true;
}

bool fallbackPresent(IDXGISwapChain* swapchain, UINT flags) {
    if (!ready.load() || !bypassRequested || !swapchain || (flags & DXGI_PRESENT_TEST)) return false;
    DXGI_SWAP_CHAIN_DESC desc = {};
    if (FAILED(swapchain->GetDesc(&desc)) || desc.OutputWindow != sourceWindow.load()) return false;
    RenderGuard guard;
    if (!guard.locked) return true;
    if (!historyDirty) return false;
    // Never restore LS temporal output after skipping its history updates. A restart
    // restores LSFG; until then, a failed XeSS takeover presents only validated raw.
    if (GetCurrentThreadId() != ownerThread.load() || !snapshot || !immediate) return true;
    ComPtr<ID3D11Device> current;
    ComPtr<ID3D11Texture2D> back;
    D3D11_TEXTURE2D_DESC bd = {};
    if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&current))) ||
        !sameIdentity(device.Get(), current.Get()) ||
        FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&back)))) return true;
    back->GetDesc(&bd);
    LARGE_INTEGER now;
    if (!textureShape(bd, DXGI_FORMAT_R8G8B8A8_UNORM) || bd.Width != width || bd.Height != height ||
        !QueryPerformanceCounter(&now) || now.QuadPart < rawTicks ||
        1000.0 * static_cast<double>(now.QuadPart - rawTicks) / frequency > 250.0) return true;
    immediate->CopyResource(back.Get(), snapshot.Get());
    static uint64_t rawFallbacks = 0;
    if (++rawFallbacks == 1 || rawFallbacks % 240 == 0)
        log("raw fallback Present=%llu; LSFG history invalid until process restart", rawFallbacks);
    return false;
}

bool present(IDXGISwapChain* swapchain, UINT flags) {
    if (!ready.load() || !swapchain || (flags & DXGI_PRESENT_TEST)) return false;
    // XeSS presents its D3D12 chain from SDK worker threads too: a thread-local
    // recursion guard alone cannot distinguish those calls from the LS renderer.
    DXGI_SWAP_CHAIN_DESC desc = {};
    if (FAILED(swapchain->GetDesc(&desc))) return false;
    const HWND bridgeWindow = outputWindow.load();
    if (bridgeWindow && desc.OutputWindow == bridgeWindow) return false;
    ComPtr<ID3D11Device> presentDevice;
    if (FAILED(swapchain->GetDevice(IID_PPV_ARGS(&presentDevice)))) return false;
    RenderGuard guard;
    if (!guard.locked) { disable("concurrent present"); return false; }
    if (escapePressed()) { cleanup(); return false; }
    if (!snapshot) return false;
    if (!sameIdentity(device.Get(), presentDevice.Get())) return false;
    if (GetCurrentThreadId() != ownerThread.load()) { disable("Present thread changed"); return false; }
    ComPtr<ID3D11DeviceContext> presentContext;
    presentDevice->GetImmediateContext(&presentContext);
    if (!sameIdentity(immediate.Get(), presentContext.Get())) {
        disable("Present context changed"); cleanup(); return false;
    }
    if (sourceChain && !sameIdentity(sourceChain.Get(), swapchain)) {
        hide();
        bypassFrame = false;
        submittedTicks = 0;
        interpolated = false;
        log("capture swapchain recreated; restarting output confirmation");
    }
    HWND output = outputWindow.load();
    if (!output || !affinity.load()) { hide(); return false; }
    RECT rect = {};
    if (desc.OutputWindow == output || !sourceRectangle(desc.OutputWindow, rect)) { hide(); return false; }
    // Use the actual backbuffer as DXGI permits zero width/height in creation descriptors.
    ComPtr<ID3D11Texture2D> backbuffer;
    D3D11_TEXTURE2D_DESC backDesc = {};
    if (FAILED(swapchain->GetBuffer(0, IID_PPV_ARGS(&backbuffer)))) {
        disable("Present backbuffer unavailable"); cleanup(); return false;
    }
    backbuffer->GetDesc(&backDesc);
    if (backDesc.Width != width || backDesc.Height != height ||
        rect.right - rect.left != static_cast<LONG>(width) || rect.bottom - rect.top != static_cast<LONG>(height)) {
        disable("Present/client size differs from raw snapshot"); cleanup(); return false;
    }
    if (sourceWindow.load() && sourceWindow.load() != desc.OutputWindow) {
        hide();
        bypassFrame = false;
        submittedTicks = 0;
        interpolated = false;
        log("capture HWND recreated; restarting output confirmation");
    }
    sourceChain = swapchain;
    sourceWindow.store(desc.OutputWindow);
    notifyWindow();
    RECT outputRect = {};
    if (!GetClientRect(output, &outputRect) || outputRect.right != static_cast<LONG>(width) ||
        outputRect.bottom != static_cast<LONG>(height)) { hide(); return false; }
    LARGE_INTEGER now;
    if (!QueryPerformanceCounter(&now) || now.QuadPart < rawTicks ||
        1000.0 * static_cast<double>(now.QuadPart - rawTicks) / static_cast<double>(frequency) > 250.0) {
        hide(); return false;
    }
    if (rawSerial != submittedSerial) {
        if (!submittedTicks) {
            // Establish a real raw-frame interval before the first backend submission.
            submittedTicks = rawTicks;
            submittedSerial = rawSerial;
            hide();
            return false;
        }
        if (!session) {
            HRESULT hr = api.create(device.Get(), immediate.Get(), output, width, height, &session);
            if (FAILED(hr) || !session) {
                log("xelCreate failed hr=%08lx: %s", hr, api.error());
                disable("backend create failed"); cleanup(); return false;
            }
            hr = api.estimate(session, estimateInputsRequested ? 1u : 0u);
            if (FAILED(hr)) {
                log("xelSetEstimation failed hr=%08lx: %s", hr, api.error());
                disable("backend estimation setup failed"); cleanup(); return false;
            }
            log("backend session created on render thread");
        }
        const double milliseconds =
            1000.0 * static_cast<double>(rawTicks - submittedTicks) / static_cast<double>(frequency);
        if (!std::isfinite(milliseconds) || milliseconds <= 0.0) {
            disable("nonmonotonic raw QPC timestamps"); cleanup(); return false;
        }
        const bool reset = milliseconds > 250.0;
        HRESULT hr = api.submit(session, snapshot.Get(), static_cast<float>(milliseconds), reset ? 1u : 0u);
        if (FAILED(hr)) {
            log("xelSubmit failed hr=%08lx: %s", hr, api.error());
            disable("backend submit failed"); cleanup(); return false;
        }
        submittedSerial = rawSerial;
        submittedTicks = rawTicks;
        XelStats stats = {};
        hr = api.stats(session, &stats);
        if (FAILED(hr) || (stats.flags & XEL_STATUS_POISONED)) {
            log("xelGetStats failed/poisoned hr=%08lx flags=%x", hr, stats.flags);
            disable("backend stats invalid"); cleanup(); return false;
        }
        constexpr uint32_t required = XEL_STATUS_VALID | XEL_STATUS_SDK_ENABLED | XEL_STATUS_LAST_INTERPOLATED;
        interpolated = (stats.flags & required) == required && stats.lastFramesSent > 1 &&
            stats.interpolatedFramesSent > previousInterpolated;
        previousInterpolated = stats.interpolatedFramesSent;
        if (stats.submittedFrames == 1 || stats.submittedFrames % 120 == 0) {
            log("stats raw=%llu submitted=%llu SDKsent=%llu extra=%llu flags=%x last=%u interpolation=%u",
                rawSerial, stats.submittedFrames, stats.sdkFramesSent, stats.interpolatedFramesSent,
                stats.flags, stats.lastFramesSent, interpolated ? 1u : 0u);
        }
        if (!interpolated) { hide(); return false; }
        successTick = GetTickCount64();
    }
    const ULONGLONG tick = GetTickCount64();
    // SDK submission is blocking: recheck age/visibility and Escape after it returns.
    if (!interpolated || !successTick || tick - successTick > 250 ||
        !QueryPerformanceCounter(&now) || now.QuadPart < rawTicks ||
        1000.0 * static_cast<double>(now.QuadPart - rawTicks) / static_cast<double>(frequency) > 250.0 ||
        escapePressed() || !sourceRectangle(desc.OutputWindow, rect) ||
        rect.right - rect.left != static_cast<LONG>(width) || rect.bottom - rect.top != static_cast<LONG>(height)) {
        hide(); if (disabled.load()) cleanup(); return false;
    }
    heartbeat.store(successTick); // Duplicate Presents never extend the watchdog deadline.
    wanted.store(true);
    notifyWindow();
    const bool take = !disabled.load() && affinity.load() && wanted.load() && IsWindowVisible(output) != FALSE;
    static uint64_t suppressed = 0;
    if (take && (++suppressed == 1 || suppressed % 240 == 0))
        log("original Present suppressed=%llu; %s output active (not scanout measurement)", suppressed, fsr3Backend ? "FSR3" : "XeSS");
    if (take) {
        if (!notified.load()) {
            wchar_t name[96] = {};
            swprintf_s(name, L"Local\\GameLocker-%s-Active-%lu", fsr3Backend ? L"FSR3" : L"XeSS", GetCurrentProcessId());
            HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
            if (event) {
                notified.store(SetEvent(event) != FALSE);
                CloseHandle(event);
            }
        }
    }
    return take;
}
#ifdef XEL_PROBE_SELFTEST
bool testBypass(ID3D11Device* d, ID3D11DeviceContext* c, ID3D11ComputeShader* shader,
    ID3D11Texture2D* raw, IDXGISwapChain* swap) {
    if (!ready.load()) return false;
    bypassRequested = true;
    ownerThread.store(GetCurrentThreadId());
    immediate = c; device = d; snapshot = raw;
    D3D11_TEXTURE2D_DESC td = {}; raw->GetDesc(&td);
    width = td.Width; height = td.Height;
    DXGI_SWAP_CHAIN_DESC sd = {}; swap->GetDesc(&sd);
    sourceWindow.store(sd.OutputWindow);
    LARGE_INTEGER now; QueryPerformanceCounter(&now); rawTicks = now.QuadPart;
    bypassFrame = true;
    bool pass = !skipDispatch(c); // Unknown bytecode must always run.
    registerShader(shader, fgShaders[0].hash, fgShaders[0].size);
    bypassFrame = false;
    pass = pass && !skipDispatch(c); // No takeover/frame boundary authorization.
    bypassFrame = true;
    pass = pass && skipDispatch(c) && historyDirty;
    registerShader(shader, conversionHash, 1216);
    pass = pass && !skipDispatch(c); // Raw conversion must survive.
    pass = pass && !fallbackPresent(swap, 0) && !fallbackPresent(swap, DXGI_PRESENT_TEST);
    // Leave raw fallback valid for caller readback; no XeSS session is created.
    disabled.store(true);
    pass = pass && !skipDispatch(c) && !fallbackPresent(swap, 0);
    log("bypass state/fallback selftest=%s (synthetic tags, not LS integration)", pass ? "PASS" : "FAIL");
    return pass;
}
#endif
} // namespace lsbridge
