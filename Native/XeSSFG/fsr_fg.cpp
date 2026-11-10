#include "xel_fg.h"
#include "frame_estimator.h"
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <ffx_api/ffx_api_loader.h>
#include <ffx_api/ffx_framegeneration.h>
#include <ffx_api/dx12/ffx_api_dx12.h>
#include <cmath>
#include <cstdio>
#include <memory>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
thread_local char diagnostic[512] = {};
struct Failure { HRESULT hr; };
void fail(HRESULT hr, const char* op, int sdk = 0) {
    sprintf_s(diagnostic, "%s: HRESULT=0x%08lX FFX=%d", op, (unsigned long)hr, sdk);
    throw Failure{hr};
}
void check(HRESULT hr, const char* op) { if (FAILED(hr)) fail(hr, op); }
void ffx(ffxReturnCode_t r, const char* op) { if (r != FFX_API_RETURN_OK) fail(E_FAIL, op, (int)r); }
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
};
struct Module {
    HMODULE value = nullptr;
    ~Module() { if (value) FreeLibrary(value); }
};
D3D12_RESOURCE_BARRIER barrier(ID3D12Resource* r, D3D12_RESOURCE_STATES a, D3D12_RESOURCE_STATES b) {
    D3D12_RESOURCE_BARRIER v = {};
    v.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    v.Transition = {r, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, a, b};
    return v;
}
template<class F> HRESULT boundary(F&& fn) noexcept {
    diagnostic[0] = 0;
    try { fn(); return S_OK; }
    catch (Failure e) { return e.hr; }
    catch (const std::bad_alloc&) { strcpy_s(diagnostic, "Out of memory"); return E_OUTOFMEMORY; }
    catch (...) { strcpy_s(diagnostic, "Unexpected native exception"); return E_UNEXPECTED; }
}
}

struct XelSession {
    DWORD thread = GetCurrentThreadId();
    UINT width = 0, height = 0;
    bool enabled = true, reset = true, poisoned = false, estimateInputs = false;
    std::unique_ptr<FrameEstimator> estimator;
    uint32_t id = 0;
    uint64_t fenceValue = 0;
    Module module;
    ffxFunctions api = {};
    ffxContext sdk = nullptr;
    // Keep the descriptors and their pNext chain alive for the context lifetime.
    ffxCreateBackendDX12Desc backend = {};
    ffxOverrideVersion version = {};
    ffxCreateContextDescFrameGeneration creation = {};
    ComPtr<ID3D11Device> device11;
    ComPtr<ID3D11Device5> device115;
    ComPtr<ID3D11DeviceContext4> context11;
    ComPtr<ID3D11Texture2D> bridge11;
    ComPtr<ID3D11Fence> fence11;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Resource> bridge12, motion, depth, output;
    ComPtr<ID3D12DescriptorHeap> dummyRtvs;
    ComPtr<ID3D12Fence> fence12;
    ComPtr<IDXGISwapChain3> swap;
    Handle event;
    XelStats stats = {};

    void require(bool usable = true) {
        if (thread != GetCurrentThreadId()) fail(RPC_E_WRONG_THREAD, "Session must be used on creating thread");
        if (usable && poisoned) fail(E_UNEXPECTED, "Session poisoned; destroy/recreate required");
    }
    void wait(uint64_t value) {
        if (fence12->GetCompletedValue() == UINT64_MAX) fail(DXGI_ERROR_DEVICE_REMOVED, "Shared fence device removed");
        if (fence12->GetCompletedValue() < value) {
            check(fence12->SetEventOnCompletion(value, event.value), "SetEventOnCompletion");
            DWORD r = WaitForSingleObject(event.value, 30000);
            if (r != WAIT_OBJECT_0) fail(r == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(WAIT_TIMEOUT) : HRESULT_FROM_WIN32(GetLastError()), "GPU fence wait");
        }
        check(device12->GetDeviceRemovedReason(), "D3D12 device removed");
    }
    void drain() {
        if (!queue || !fence12 || !event.value) return;
        check(queue->Signal(fence12.Get(), ++fenceValue), "Queue drain signal");
        wait(fenceValue);
    }
    HRESULT cleanup() noexcept {
        HRESULT hr = boundary([&] { drain(); });
        if (FAILED(hr) && device12 && SUCCEEDED(device12->GetDeviceRemovedReason())) return hr;
        if (sdk) {
            auto r = api.DestroyContext(&sdk, nullptr);
            if (r != FFX_API_RETURN_OK) {
                sprintf_s(diagnostic, "ffxDestroyContext FFX=%u", r);
                return E_FAIL;
            }
            sdk = nullptr;
        }
        swap.Reset();
        return S_OK;
    }
    void texture(DXGI_FORMAT format, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state, ComPtr<ID3D12Resource>& out) {
        D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width; desc.Height = height;
        desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = format; desc.SampleDesc.Count = 1; desc.Flags = flags;
        check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr, IID_PPV_ARGS(&out)), "Create FSR input/output");
    }
    void loadSdk() {
        HMODULE self = nullptr;
        check(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&xelCreate), &self) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "Locate fsr_fg module");
        wchar_t path[32768];
        DWORD n = GetModuleFileNameW(self, path, (DWORD)_countof(path));
        if (!n || n >= _countof(path)) fail(E_FAIL, "Resolve fsr_fg module path");
        std::wstring file(path, n);
        file.erase(file.find_last_of(L"\\/") + 1);
        file += L"amd_fidelityfx_dx12.dll";
        module.value = LoadLibraryExW(file.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module.value) fail(HRESULT_FROM_WIN32(GetLastError()), "Load adjacent official FSR DLL");
        ffxLoadFunctions(&api, module.value);
        if (!api.CreateContext || !api.DestroyContext || !api.Configure || !api.Query || !api.Dispatch)
            fail(E_NOINTERFACE, "Official FFX API exports missing");
    }
    void initialize(ID3D11Device* d, ID3D11DeviceContext* c, HWND hwnd, UINT w, UINT h) {
        width = w; height = h; device11 = d;
        check(d->QueryInterface(IID_PPV_ARGS(&device115)), "Requires ID3D11Device5");
        check(c->QueryInterface(IID_PPV_ARGS(&context11)), "Requires ID3D11DeviceContext4");
        if (c->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE) fail(E_INVALIDARG, "Immediate context required");
        ComPtr<ID3D11Device> owner; c->GetDevice(&owner);
        ComPtr<ID3D11DeviceContext> actual; d->GetImmediateContext(&actual);
        if (owner.Get() != d || actual.Get() != c) fail(E_INVALIDARG, "Device/context mismatch");
        ComPtr<IDXGIDevice> dxgi; check(d->QueryInterface(IID_PPV_ARGS(&dxgi)), "Query DXGI device");
        ComPtr<IDXGIAdapter> adapter; check(dxgi->GetAdapter(&adapter), "Get caller adapter");
        check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device12)), "Create same-adapter D3D12 device");
        D3D12_COMMAND_QUEUE_DESC q = {}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        check(device12->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)), "Create queue");
        check(device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12)), "Create shared fence");
        Handle sharedFence;
        check(device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &sharedFence.value), "Share fence");
        check(device115->OpenSharedFence(sharedFence.value, IID_PPV_ARGS(&fence11)), "Open D3D11 shared fence");
        event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!event.value) fail(HRESULT_FROM_WIN32(GetLastError()), "Create fence event");
        D3D11_TEXTURE2D_DESC color = {};
        color.Width = w; color.Height = h; color.MipLevels = 1; color.ArraySize = 1;
        color.Format = DXGI_FORMAT_R8G8B8A8_UNORM; color.SampleDesc.Count = 1;
        color.Usage = D3D11_USAGE_DEFAULT;
        color.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        color.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        check(d->CreateTexture2D(&color, nullptr, &bridge11), "Create shared color bridge");
        ComPtr<IDXGIResource1> shared; check(bridge11.As(&shared), "Query shared color");
        Handle colorHandle;
        check(shared->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &colorHandle.value), "Share color NT handle");
        check(device12->OpenSharedHandle(colorHandle.value, IID_PPV_ARGS(&bridge12)), "Open color in D3D12");
        check(device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create allocator");
        check(device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&commands)), "Create command list");
        texture(DXGI_FORMAT_R16G16_FLOAT, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RENDER_TARGET, motion);
        texture(DXGI_FORMAT_R32_FLOAT, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RENDER_TARGET, depth);
        texture(color.Format, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, output);
        D3D12_DESCRIPTOR_HEAP_DESC hd = {}; hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; hd.NumDescriptors = 2;
        check(device12->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&dummyRtvs)), "Create dummy RTV heap");
        auto cpu = dummyRtvs->GetCPUDescriptorHandleForHeapStart();
        device12->CreateRenderTargetView(motion.Get(), nullptr, cpu);
        const float zero[4] = {}; commands->ClearRenderTargetView(cpu, zero, 0, nullptr);
        cpu.ptr += device12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        device12->CreateRenderTargetView(depth.Get(), nullptr, cpu);
        const float flat[4] = {1, 1, 1, 1}; commands->ClearRenderTargetView(cpu, flat, 0, nullptr);
        D3D12_RESOURCE_BARRIER b[] = {
            barrier(motion.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            barrier(depth.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)};
        commands->ResourceBarrier(2, b);
        execute();
        loadSdk();
        // The standalone API provider is the interpolation-only path: it creates
        // OpticalFlow + FrameInterpolation contexts, never an upscaler.
        uint64_t count = 0;
        ffxQueryDescGetVersions query = {};
        query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
        query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        query.device = device12.Get(); query.outputCount = &count;
        ffx(api.Query(nullptr, &query.header), "Query FSR provider count");
        std::vector<uint64_t> ids((size_t)count);
        std::vector<const char*> names((size_t)count);
        query.versionIds = ids.data(); query.versionNames = names.data();
        ffx(api.Query(nullptr, &query.header), "Query FSR providers");
        // Pin the SDK's built-in provider; do not silently choose a driver upgrade.
        bool found = false;
        for (size_t i = 0; i < ids.size(); ++i) {
            if (names[i] && !strcmp(names[i], "1.1.3")) { version.versionId = ids[i]; found = true; break; }
        }
        if (!found) fail(E_NOINTERFACE, "FSR 3.1.4 SDK FrameInterpolation 1.1.3 provider missing");
        version.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.header.pNext = &version.header; backend.device = device12.Get();
        creation.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        creation.header.pNext = &backend.header;
        creation.flags = FFX_FRAMEGENERATION_ENABLE_DISPLAY_RESOLUTION_MOTION_VECTORS;
        creation.displaySize = creation.maxRenderSize = {w, h};
        creation.backBufferFormat = FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
        ffx(api.CreateContext(&sdk, &creation.header, nullptr), "Create interpolation-only FSR context");
        ComPtr<IDXGIFactory2> factory; check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "Create factory");
        DXGI_SWAP_CHAIN_DESC1 sd = {};
        sd.Width = w; sd.Height = h; sd.Format = color.Format; sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 3;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; sd.Scaling = DXGI_SCALING_STRETCH;
        sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> created;
        check(factory->CreateSwapChainForHwnd(queue.Get(), hwnd, &sd, nullptr, nullptr, &created), "Create ordinary swapchain");
        check(created.As(&swap), "Query swapchain3");
        check(factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER), "Disable fullscreen shortcut");
    }
    void begin() {
        check(allocator->Reset(), "Reset allocator");
        check(commands->Reset(allocator.Get(), nullptr), "Reset command list");
    }
    void execute() {
        check(commands->Close(), "Close commands");
        ID3D12CommandList* lists[] = {commands.Get()}; queue->ExecuteCommandLists(1, lists);
        drain();
    }
    bool present(ID3D12Resource* src, D3D12_RESOURCE_STATES state) {
        begin();
        ComPtr<ID3D12Resource> back;
        check(swap->GetBuffer(swap->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&back)), "Get backbuffer");
        D3D12_RESOURCE_BARRIER b[] = {barrier(src, state, D3D12_RESOURCE_STATE_COPY_SOURCE),
            barrier(back.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST)};
        commands->ResourceBarrier(2, b); commands->CopyResource(back.Get(), src);
        D3D12_RESOURCE_BARRIER end[] = {barrier(src, D3D12_RESOURCE_STATE_COPY_SOURCE, state),
            barrier(back.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT)};
        commands->ResourceBarrier(2, end); execute();
        HRESULT hr = swap->Present(1, 0);
        check(hr, "Present");
        drain();
        // Occluded/non-S_OK success statuses are not evidence of a sent frame.
        if (hr != S_OK) { ++stats.statusWarnings; return false; }
        ++stats.lastFramesSent; ++stats.sdkFramesSent;
        return true;
    }
    void submit(ID3D11Texture2D* source, float ms, bool forceReset) {
        require();
        if (!source || !std::isfinite(ms) || ms <= 0) fail(E_INVALIDARG, "Invalid texture/frame milliseconds");
        ComPtr<ID3D11Device> owner; source->GetDevice(&owner);
        D3D11_TEXTURE2D_DESC desc = {}; source->GetDesc(&desc);
        if (owner.Get() != device11.Get() || desc.Width != width || desc.Height != height ||
            desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM || desc.SampleDesc.Count != 1 || desc.SampleDesc.Quality != 0 ||
            desc.MipLevels != 1 || desc.ArraySize != 1 || desc.Usage != D3D11_USAGE_DEFAULT || desc.CPUAccessFlags)
            fail(E_INVALIDARG, "Texture must match device, dimensions, RGBA8, single sample/mip/slice and DEFAULT usage");
        if (id == UINT32_MAX) fail(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW), "Present IDs exhausted; recreate session");
        ++id; poisoned = true;
        stats.lastPresentId = id; stats.lastFramesSent = 0;
        stats.flags = enabled ? XEL_STATUS_REQUESTED_ENABLED : 0;
        // No SDK status-query equivalent exists: report E_NOTIMPL, not invented success.
        stats.lastStatusQueryResult = E_NOTIMPL;
        stats.lastFrameGenerationResult = E_NOTIMPL;
        context11->CopyResource(bridge11.Get(), source);
        check(context11->Signal(fence11.Get(), ++fenceValue), "D3D11 color signal"); context11->Flush();
        check(queue->Wait(fence12.Get(), fenceValue), "D3D12 wait for color");
        const bool resetFrame = reset || forceReset;
        if (estimator) {
            begin();
            check(estimator->record(commands.Get(), bridge12.Get(), motion.Get(), depth.Get(),
                estimateInputs && enabled, resetFrame), "Estimate screen inputs");
            execute();
        }
        if (enabled) {
            ffxConfigureDescFrameGeneration config = {};
            config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
            config.frameGenerationEnabled = true;
            config.flags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
            config.frameID = id;
            ffx(api.Configure(&sdk, &config.header), "Configure manual frame generation");
            begin();
            auto b = barrier(bridge12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            commands->ResourceBarrier(1, &b);
            ffxDispatchDescFrameGenerationPrepareCameraInfo camera = {};
            camera.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_CAMERAINFO;
            camera.cameraUp[1] = camera.cameraRight[0] = camera.cameraForward[2] = 1;
            ffxDispatchDescFrameGenerationPrepare prep = {};
            prep.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE;
            prep.header.pNext = &camera.header; prep.frameID = id; prep.commandList = commands.Get();
            prep.renderSize = {width, height}; prep.motionVectorScale = {1.0f, 1.0f};
            prep.frameTimeDelta = ms; prep.cameraNear = 0.1f; prep.cameraFar = 1000;
            prep.cameraFovAngleVertical = 1.04719755f; prep.viewSpaceToMetersFactor = 1;
            prep.depth = ffxApiGetResourceDX12(depth.Get()); prep.motionVectors = ffxApiGetResourceDX12(motion.Get());
            auto r = api.Dispatch(&sdk, &prep.header); stats.lastFrameGenerationResult = (int32_t)r;
            ffx(r, "FSR prepare");
            ffxDispatchDescFrameGeneration gen = {};
            gen.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
            gen.commandList = commands.Get(); gen.presentColor = ffxApiGetResourceDX12(bridge12.Get());
            gen.outputs[0] = ffxApiGetResourceDX12(output.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
            gen.numGeneratedFrames = 1; gen.reset = resetFrame; gen.frameID = id;
            gen.backbufferTransferFunction = FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB;
            gen.minMaxLuminance[0] = 0; gen.minMaxLuminance[1] = 100;
            gen.generationRect = {0, 0, (int32_t)width, (int32_t)height};
            r = api.Dispatch(&sdk, &gen.header); stats.lastFrameGenerationResult = (int32_t)r;
            ffx(r, "FSR optical flow / frame interpolation");
            b = barrier(bridge12.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
            commands->ResourceBarrier(1, &b); execute();
            stats.flags |= XEL_STATUS_SDK_ENABLED;
            // Reset dispatch seeds history; its copied output is NOT an interpolated frame.
            if (!resetFrame && present(output.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS)) {
                stats.flags |= XEL_STATUS_LAST_INTERPOLATED;
                ++stats.interpolatedFramesSent; ++stats.interpolatingPresents;
            }
        }
        present(bridge12.Get(), D3D12_RESOURCE_STATE_COMMON);
        check(context11->Wait(fence11.Get(), fenceValue), "D3D11 wait for completion"); context11->Flush();
        ++stats.submittedFrames; stats.flags |= XEL_STATUS_VALID;
        reset = !enabled; poisoned = false;
    }
};

namespace {
XelSession& session(XelSession* s, bool usable = true) {
    if (!s) fail(E_POINTER, "Null session");
    s->require(usable); return *s;
}
}
HRESULT __cdecl xelCreate(ID3D11Device* d, ID3D11DeviceContext* c, HWND hwnd, uint32_t w, uint32_t h, XelSession** out) {
    return boundary([&] {
        if (!out) fail(E_POINTER, "Null output");
        *out = nullptr;
        if (!d || !c || !IsWindow(hwnd) || !w || !h || w > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || h > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
            fail(E_INVALIDARG, "Invalid device/context/window/size");
        auto owned = std::make_unique<XelSession>();
        try { owned->initialize(d, c, hwnd, w, h); }
        catch (...) {
            char saved[512]; strcpy_s(saved, diagnostic);
            HRESULT cleanup = owned->cleanup();
            if (FAILED(cleanup)) { *out = owned.release(); (*out)->poisoned = true; }
            strcpy_s(diagnostic, saved); throw;
        }
        *out = owned.release();
    });
}
HRESULT __cdecl xelSubmit(XelSession* s, ID3D11Texture2D* src, float ms, uint32_t reset) {
    return boundary([&] { session(s).submit(src, ms, reset != 0); });
}
HRESULT __cdecl xelSetEnabled(XelSession* s, uint32_t enabled) {
    return boundary([&] { auto& v = session(s); v.enabled = enabled != 0; v.reset = true;
        v.stats.flags &= ~(XEL_STATUS_REQUESTED_ENABLED | XEL_STATUS_SDK_ENABLED | XEL_STATUS_LAST_INTERPOLATED);
        if (v.enabled) v.stats.flags |= XEL_STATUS_REQUESTED_ENABLED; });
}
HRESULT __cdecl xelSetEstimation(XelSession* s, uint32_t enabled) {
    return boundary([&] {
        auto& v = session(s);
        if (enabled && !v.estimator) {
            auto estimator = std::make_unique<FrameEstimator>();
            check(estimator->initialize(v.device12.Get(), v.width, v.height), "Initialize screen estimator");
            v.estimator = std::move(estimator);
        }
        if (v.estimateInputs != (enabled != 0)) { v.estimateInputs = enabled != 0; v.reset = true; }
    });
}
HRESULT __cdecl xelResetHistory(XelSession* s) { return boundary([&] { session(s).reset = true; }); }
HRESULT __cdecl xelAdoptThread(XelSession* s) {
    return boundary([&] {
        if (!s) fail(E_POINTER, "Null session");
        if (s->poisoned) fail(E_UNEXPECTED, "Cannot transfer poisoned session");
        s->drain(); s->thread = GetCurrentThreadId(); s->reset = true;
    });
}
HRESULT __cdecl xelResize(XelSession* s, uint32_t, uint32_t) {
    return boundary([&] { session(s); fail(E_NOTIMPL, "Resize unsupported: destroy and recreate session"); });
}
HRESULT __cdecl xelGetStats(XelSession* s, XelStats* out) {
    return boundary([&] { auto& v = session(s, false); if (!out) fail(E_POINTER, "Null stats output");
        *out = v.stats; if (v.poisoned) out->flags |= XEL_STATUS_POISONED; });
}
HRESULT __cdecl xelDestroy(XelSession** s) {
    return boundary([&] { if (!s) fail(E_POINTER, "Null session address"); if (!*s) return;
        (*s)->require(false); HRESULT hr = (*s)->cleanup(); if (FAILED(hr)) throw Failure{hr}; delete *s; *s = nullptr; });
}
const char* __cdecl xelGetLastError(void) { return diagnostic; }
