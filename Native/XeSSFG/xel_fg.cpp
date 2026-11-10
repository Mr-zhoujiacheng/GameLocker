#include "xel_fg.h"
#include "frame_estimator.h"
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <xess_fg/xefg_swapchain_d3d12.h>
#include <xell/xell_d3d12.h>
#include <cmath>
#include <cstdio>
#include <memory>
#include <new>
#include <limits>

using Microsoft::WRL::ComPtr;
namespace {
thread_local char diagnostic[512] = {};
struct Failure { HRESULT hr; };
void fail(HRESULT hr, const char* operation, int sdk = 0) {
    sprintf_s(diagnostic, "%s: HRESULT=0x%08lX SDK=%d", operation, (unsigned long)hr, sdk);
    throw Failure{hr};
}
void check(HRESULT hr, const char* operation) { if (FAILED(hr)) fail(hr, operation); }
void fg(xefg_swapchain_result_t r, const char* op) { if (r < 0) fail(E_FAIL, op, r); }
void ll(xell_result_t r, const char* op) { if (r != XELL_RESULT_SUCCESS) fail(E_FAIL, op, r); }
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
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
    ComPtr<ID3D11Device> device11;
    ComPtr<ID3D11Device5> device115;
    ComPtr<ID3D11DeviceContext4> context11;
    ComPtr<ID3D11Texture2D> bridge11;
    ComPtr<ID3D11Fence> fence11;
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Resource> bridge12, motion, depth;
    ComPtr<ID3D12DescriptorHeap> dummyRtvs;
    ComPtr<ID3D12Fence> fence12;
    ComPtr<IDXGISwapChain3> swap;
    Handle event;
    xefg_swapchain_handle_t sdk = nullptr;
    xell_context_handle_t latency = nullptr;
    XelStats stats = {};

    void require(bool usable = true) {
        if (thread != GetCurrentThreadId()) fail(RPC_E_WRONG_THREAD, "Session must be used on creating thread");
        if (usable && poisoned) fail(E_UNEXPECTED, "Session poisoned; destroy/recreate required");
    }
    void wait(uint64_t value) {
        if (fence12->GetCompletedValue() == UINT64_MAX) fail(DXGI_ERROR_DEVICE_REMOVED, "Shared fence device removed");
        if (fence12->GetCompletedValue() < value) {
            check(fence12->SetEventOnCompletion(value, event.value), "SetEventOnCompletion");
            DWORD result = WaitForSingleObject(event.value, 30000);
            if (result != WAIT_OBJECT_0) fail(result == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(WAIT_TIMEOUT) : HRESULT_FROM_WIN32(GetLastError()), "GPU fence wait");
        }
        check(device12->GetDeviceRemovedReason(), "D3D12 device removed");
    }
    void drain() {
        if (!queue || !fence12 || !event.value) return;
        // XeSS submits its work in Present. A queue signal AFTER Present covers
        // the SDK's use of tagged resources, as required by its lifetime contract.
        check(queue->Signal(fence12.Get(), ++fenceValue), "Queue drain signal");
        wait(fenceValue);
    }
    HRESULT cleanup() noexcept {
        HRESULT drained = boundary([&] { drain(); });
        // Never release resources still in flight after a timeout. Device loss
        // permits teardown because no further GPU execution can occur.
        if (FAILED(drained) && device12 && SUCCEEDED(device12->GetDeviceRemovedReason())) return drained;
        swap.Reset();
        if (sdk) {
            auto result = xefgSwapChainDestroy(sdk);
            if (result < 0) {
                sprintf_s(diagnostic, "xefgSwapChainDestroy SDK=%d", (int)result);
                return E_FAIL;
            }
            sdk = nullptr;
        }
        if (latency) {
            auto result = xellDestroyContext(latency);
            if (result != XELL_RESULT_SUCCESS) {
                sprintf_s(diagnostic, "xellDestroyContext SDK=%d", (int)result);
                return E_FAIL;
            }
            latency = nullptr;
        }
        return S_OK;
    }
    void createDummy(DXGI_FORMAT format, ComPtr<ID3D12Resource>& output) {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width; desc.Height = height;
        desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = format; desc.SampleDesc.Count = 1;
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        check(device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&output)), "Create dummy input");
    }
    void initialize(ID3D11Device* d, ID3D11DeviceContext* c, HWND hwnd, UINT w, UINT h) {
        width = w; height = h; device11 = d;
        check(d->QueryInterface(IID_PPV_ARGS(&device115)), "Requires ID3D11Device5");
        check(c->QueryInterface(IID_PPV_ARGS(&context11)), "Requires ID3D11DeviceContext4");
        if (c->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE) fail(E_INVALIDARG, "Immediate context required");
        ComPtr<ID3D11Device> owner;
        c->GetDevice(&owner);
        ComPtr<ID3D11DeviceContext> actual;
        d->GetImmediateContext(&actual);
        if (owner.Get() != d || actual.Get() != c) fail(E_INVALIDARG, "Device/context mismatch");
        ComPtr<IDXGIDevice> dxgi;
        check(d->QueryInterface(IID_PPV_ARGS(&dxgi)), "Query DXGI device");
        ComPtr<IDXGIAdapter> adapter;
        check(dxgi->GetAdapter(&adapter), "Get caller adapter");
        check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device12)), "Create same-adapter D3D12 device");
        D3D12_COMMAND_QUEUE_DESC q = {};
        q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
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
        ComPtr<IDXGIResource1> sharedResource;
        check(bridge11.As(&sharedResource), "Query shared color");
        Handle colorHandle;
        check(sharedResource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &colorHandle.value), "Share color NT handle");
        check(device12->OpenSharedHandle(colorHandle.value, IID_PPV_ARGS(&bridge12)), "Open color in D3D12");
        check(device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create allocator");
        check(device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&commands)), "Create command list");
        createDummy(DXGI_FORMAT_R16G16_FLOAT, motion);
        createDummy(DXGI_FORMAT_R32_FLOAT, depth);
        D3D12_DESCRIPTOR_HEAP_DESC hd = {};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; hd.NumDescriptors = 2;
        check(device12->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&dummyRtvs)), "Create dummy RTV heap");
        auto cpu = dummyRtvs->GetCPUDescriptorHandleForHeapStart();
        device12->CreateRenderTargetView(motion.Get(), nullptr, cpu);
        const float zero[4] = {};
        commands->ClearRenderTargetView(cpu, zero, 0, nullptr);
        cpu.ptr += device12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        device12->CreateRenderTargetView(depth.Get(), nullptr, cpu);
        const float flat[4] = {1, 1, 1, 1};
        commands->ClearRenderTargetView(cpu, flat, 0, nullptr);
        D3D12_RESOURCE_BARRIER b[] = {
            barrier(motion.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
            barrier(depth.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)};
        commands->ResourceBarrier(2, b);
        check(commands->Close(), "Close dummy initialization");
        ID3D12CommandList* lists[] = {commands.Get()};
        queue->ExecuteCommandLists(1, lists);
        drain();
        ll(xellD3D12CreateContext(device12.Get(), &latency), "xellD3D12CreateContext");
        xell_sleep_params_t sleep = {}; sleep.bLowLatencyMode = 1;
        ll(xellSetSleepMode(latency, &sleep), "xellSetSleepMode");
        fg(xefgSwapChainD3D12CreateContext(device12.Get(), &sdk), "xefgSwapChainD3D12CreateContext");
        fg(xefgSwapChainSetLatencyReduction(sdk, latency), "SetLatencyReduction");
        ComPtr<IDXGIFactory2> factory;
        check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "Create factory");
        DXGI_SWAP_CHAIN_DESC1 sd = {};
        sd.Width = w; sd.Height = h; sd.Format = color.Format;
        sd.SampleDesc.Count = 1; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = 3; sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        sd.Scaling = DXGI_SCALING_STRETCH; sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        xefg_swapchain_d3d12_init_params_t init = {};
        init.maxInterpolatedFrames = 1; init.uiMode = XEFG_SWAPCHAIN_UI_MODE_NONE;
        fg(xefgSwapChainD3D12InitFromSwapChainDesc(sdk, hwnd, &sd, nullptr, queue.Get(), factory.Get(), &init), "Initialize XeSS proxy swapchain");
        fg(xefgSwapChainD3D12GetSwapChainPtr(sdk, IID_PPV_ARGS(&swap)), "Get proxy swapchain");
        check(factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER), "Disable fullscreen shortcut");
        fg(xefgSwapChainSetEnabled(sdk, 1), "Enable XeSS-FG");
    }
    void mark(xell_latency_marker_type_t type) {
        ll(xellAddMarkerData(latency, id, type), "XeLL frame marker");
    }
    void tag(ID3D12Resource* r, xefg_swapchain_resource_type_t type) {
        xefg_swapchain_d3d12_resource_data_t data = {};
        data.type = type; data.validity = XEFG_SWAPCHAIN_RV_UNTIL_NEXT_PRESENT;
        data.resourceSize = {width, height}; data.pResource = r;
        data.incomingState = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        fg(xefgSwapChainD3D12TagFrameResource(sdk, nullptr, id, &data), "Tag XeSS input");
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
        ++id;
        poisoned = true;
        ll(xellSleep(latency, id), "xellSleep");
        mark(XELL_SIMULATION_START); mark(XELL_SIMULATION_END); mark(XELL_RENDERSUBMIT_START);
        context11->CopyResource(bridge11.Get(), source);
        check(context11->Signal(fence11.Get(), ++fenceValue), "D3D11 color signal");
        context11->Flush();
        check(queue->Wait(fence12.Get(), fenceValue), "D3D12 wait for color");
        check(allocator->Reset(), "Reset allocator");
        check(commands->Reset(allocator.Get(), nullptr), "Reset command list");
        if (estimator) check(estimator->record(commands.Get(), bridge12.Get(), motion.Get(), depth.Get(),
            estimateInputs && enabled, reset || forceReset), "Estimate screen inputs");
        ComPtr<ID3D12Resource> back;
        check(swap->GetBuffer(swap->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&back)), "Get proxy backbuffer");
        D3D12_RESOURCE_BARRIER start[] = {
            barrier(bridge12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE),
            barrier(back.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST)};
        commands->ResourceBarrier(2, start);
        commands->CopyResource(back.Get(), bridge12.Get());
        D3D12_RESOURCE_BARRIER finish[] = {
            barrier(bridge12.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON),
            barrier(back.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT)};
        commands->ResourceBarrier(2, finish);
        check(commands->Close(), "Close frame commands");
        ID3D12CommandList* lists[] = {commands.Get()}; queue->ExecuteCommandLists(1, lists);
        if (enabled) {
            tag(motion.Get(), XEFG_SWAPCHAIN_RES_MOTION_VECTOR);
            tag(depth.Get(), XEFG_SWAPCHAIN_RES_DEPTH);
            xefg_swapchain_frame_constant_data_t constants = {};
            for (int i = 0; i < 16; i += 5) constants.viewMatrix[i] = constants.projectionMatrix[i] = 1;
            if (estimateInputs) {
                // Synthetic camera matches the pseudo-depth encoding, not the game's camera.
                constants.projectionMatrix[0] = 1.7320508f * (float)height / width;
                constants.projectionMatrix[5] = 1.7320508f;
                constants.projectionMatrix[10] = 1000.0f / 999.9f;
                constants.projectionMatrix[11] = 1;
                constants.projectionMatrix[14] = -100.0f / 999.9f;
                constants.projectionMatrix[15] = 0;
            }
            constants.motionVectorScaleX = constants.motionVectorScaleY = 1;
            constants.frameRenderTime = ms; constants.resetHistory = reset || forceReset;
            fg(xefgSwapChainTagFrameConstants(sdk, id, &constants), "Tag frame constants");
        }
        fg(xefgSwapChainSetPresentId(sdk, id), "Set present ID");
        mark(XELL_RENDERSUBMIT_END); mark(XELL_PRESENT_START);
        HRESULT presented = swap->Present(1, 0);
        mark(XELL_PRESENT_END);
        check(presented, "Proxy Present");
        drain();
        check(context11->Wait(fence11.Get(), fenceValue), "D3D11 wait for SDK completion");
        context11->Flush();
        xefg_swapchain_present_status_t status = {};
        auto query = xefgSwapChainGetLastPresentStatus(sdk, &status);
        stats.lastStatusQueryResult = query;
        stats.flags = enabled ? XEL_STATUS_REQUESTED_ENABLED : 0;
        stats.lastPresentId = id; stats.lastFramesSent = 0;
        stats.lastFrameGenerationResult = query;
        ++stats.submittedFrames;
        if (query == XEFG_SWAPCHAIN_RESULT_SUCCESS) {
            stats.flags |= XEL_STATUS_VALID;
            stats.lastFramesSent = status.framesPresented;
            stats.lastFrameGenerationResult = status.frameGenResult;
            if (status.isFrameGenEnabled) stats.flags |= XEL_STATUS_SDK_ENABLED;
            stats.sdkFramesSent += status.framesPresented;
            if (status.frameGenResult < 0) fail(E_FAIL, "SDK interpolation failed", status.frameGenResult);
            if (status.frameGenResult > 0) ++stats.statusWarnings;
            if (status.isFrameGenEnabled && status.frameGenResult == XEFG_SWAPCHAIN_RESULT_SUCCESS && status.framesPresented > 1) {
                stats.flags |= XEL_STATUS_LAST_INTERPOLATED;
                stats.interpolatedFramesSent += status.framesPresented - 1;
                ++stats.interpolatingPresents;
            }
        } else {
            ++stats.statusWarnings;
            fg(query, "GetLastPresentStatus");
        }
        reset = false; poisoned = false;
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
            if (FAILED(cleanup)) {
                // Preserve a recoverable handle when safe cleanup could not finish.
                *out = owned.release();
                (*out)->poisoned = true;
            }
            strcpy_s(diagnostic, saved);
            throw;
        }
        *out = owned.release();
    });
}
HRESULT __cdecl xelSubmit(XelSession* s, ID3D11Texture2D* src, float ms, uint32_t reset) {
    return boundary([&] { session(s).submit(src, ms, reset != 0); });
}
HRESULT __cdecl xelSetEnabled(XelSession* s, uint32_t enabled) {
    return boundary([&] { auto& v = session(s); fg(xefgSwapChainSetEnabled(v.sdk, enabled != 0), "SetEnabled"); v.enabled = enabled != 0; v.reset = true;
        v.stats.flags = (v.stats.flags & ~XEL_STATUS_REQUESTED_ENABLED) | (v.enabled ? XEL_STATUS_REQUESTED_ENABLED : 0); });
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
        s->drain();
        s->thread = GetCurrentThreadId();
        s->reset = true;
    });
}
HRESULT __cdecl xelResize(XelSession* s, uint32_t, uint32_t) { return boundary([&] { session(s); fail(E_NOTIMPL, "Resize unsupported: destroy and recreate session"); }); }
HRESULT __cdecl xelGetStats(XelSession* s, XelStats* out) {
    return boundary([&] { auto& v = session(s, false); if (!out) fail(E_POINTER, "Null stats output"); *out = v.stats; if (v.poisoned) out->flags |= XEL_STATUS_POISONED; });
}
HRESULT __cdecl xelDestroy(XelSession** s) {
    return boundary([&] { if (!s) fail(E_POINTER, "Null session address"); if (!*s) return;
        (*s)->require(false); HRESULT hr = (*s)->cleanup(); if (FAILED(hr)) throw Failure{hr}; delete *s; *s = nullptr; });
}
const char* __cdecl xelGetLastError(void) { return diagnostic; }
