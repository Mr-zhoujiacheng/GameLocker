#include "fsr_sr.h"
#include "frame_estimator.h"
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <ffx_api.h>
#include <ffx_upscale.h>
#include <dx12/ffx_api_dx12.h>
#include <cstdio>
#include <memory>
#include <string>
#include <vector>
#include <chrono>

using Microsoft::WRL::ComPtr;
namespace {
char diagnostic[1024] = "FSR SR not initialized; estimated inputs experimental; INT8 unverified";
struct Failure {};
void fail(const char* op, HRESULT hr = E_FAIL, unsigned sdk = 0) {
    sprintf_s(diagnostic, "%s: HRESULT=0x%08lX FFX=%u", op, (unsigned long)hr, sdk);
    throw Failure{};
}
void check(HRESULT hr, const char* op) { if (FAILED(hr)) fail(op, hr); }
void ffx(ffxReturnCode_t rc, const char* op) { if (rc != FFX_API_RETURN_OK) fail(op, E_FAIL, rc); }
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
};
D3D12_RESOURCE_BARRIER barrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
    return b;
}

// Restrict the compatibility edit to this loaded upscaler image, never the disk,
// system identity, driver, signature verification, or another module.
struct CompatibilityEdit {
    unsigned char* address = nullptr;
    unsigned char original[4] = {};
    bool active = false;
    void write(const unsigned char* bytes) {
        DWORD protection = 0;
        if (!VirtualProtect(address, 4, PAGE_EXECUTE_READWRITE, &protection)) fail("Compatibility VirtualProtect", HRESULT_FROM_WIN32(GetLastError()));
        memcpy(address, bytes, 4);
        BOOL flushed = FlushInstructionCache(GetCurrentProcess(), address, 4);
        DWORD ignored = 0;
        BOOL restored = VirtualProtect(address, 4, protection, &ignored);
        if (!flushed || !restored) fail("Compatibility instruction cache/protection restore");
    }
    void apply(HMODULE module) {
        const int pattern[] = {0x48,0x83,0xEC,0x48,0x48,0x8B,0xC2,0x48,0x85,0xD2,0x74,0x54,0x48,0x8D,0x54,0x24,0x20,0x48,0x8B,0xC8,0xE8,-1,-1,-1,-1,0x81,0x7C,0x24,0x34,0x91,0,0,0,0x44,0x0F,0xB6,0xC0,0x75,0x23,0x8B,0x54,0x24,0x30,0x8D,0x4A,0xFF,0x83,0xF9,0x0E,0x76,0x13,0x8D,0x4A,0xE0,0x81,0xF9,0xDE,0,0,0,0x76,0x08,0x8D,0x4A,0xF0,0x83,0xF9,0x0F};
        auto base = reinterpret_cast<unsigned char*>(module);
        auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) fail("Invalid upscaler DOS image");
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) fail("Invalid upscaler PE image");
        auto sections = IMAGE_FIRST_SECTION(nt);
        unsigned matches = 0;
        for (unsigned s = 0; s < nt->FileHeader.NumberOfSections; ++s) {
            if (!(sections[s].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
            size_t begin = sections[s].VirtualAddress, size = sections[s].Misc.VirtualSize;
            if (begin > nt->OptionalHeader.SizeOfImage || size > nt->OptionalHeader.SizeOfImage - begin) fail("Invalid executable section bounds");
            for (size_t i = 0; i + _countof(pattern) <= size; ++i) {
                bool match = true;
                for (size_t j = 0; j < _countof(pattern); ++j)
                    if (pattern[j] >= 0 && base[begin + i + j] != pattern[j]) { match = false; break; }
                if (match) { ++matches; address = base + begin + i; }
            }
        }
        if (matches != 1) fail("Upscaler 68-byte compatibility signature not unique");
        memcpy(original, address, 4);
        const unsigned char patch[] = {0xB0, 0x01, 0xC3, 0x90};
        active = true;
        write(patch);
    }
    void restore() { if (active) { write(original); active = false; } }
    ~CompatibilityEdit() { if (active) { try { restore(); } catch (...) {} } }
};

struct ComputeState {
    ID3D11DeviceContext* c;
    ComPtr<ID3D11ComputeShader> shader;
    ID3D11ClassInstance* instances[256] = {};
    UINT count = 256;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11UnorderedAccessView> uav;
    ComPtr<ID3D11Buffer> cb;
    explicit ComputeState(ID3D11DeviceContext* context) : c(context) {
        c->CSGetShader(&shader, instances, &count);
        c->CSGetShaderResources(0, 1, &srv);
        c->CSGetUnorderedAccessViews(0, 1, &uav);
        c->CSGetConstantBuffers(0, 1, &cb);
    }
    ~ComputeState() {
        ID3D11UnorderedAccessView* emptyUav = nullptr;
        ID3D11ShaderResourceView* emptySrv = nullptr;
        UINT keep = UINT(-1);
        c->CSSetUnorderedAccessViews(0, 1, &emptyUav, &keep);
        c->CSSetShaderResources(0, 1, &emptySrv);
        c->CSSetShader(shader.Get(), instances, count);
        auto s = srv.Get(); auto u = uav.Get(); auto b = cb.Get();
        c->CSSetShaderResources(0, 1, &s);
        c->CSSetUnorderedAccessViews(0, 1, &u, &keep);
        c->CSSetConstantBuffers(0, 1, &b);
        for (UINT i = 0; i < count; ++i) if (instances[i]) instances[i]->Release();
    }
};

// CopyResource cannot write a destination still bound as a caller UAV.
struct CopyBindings {
    ID3D11DeviceContext* c;
    UINT slots, rtCount = 0;
    std::vector<ID3D11UnorderedAccessView*> cs, om;
    std::vector<UINT> keep;
    ID3D11RenderTargetView* rt[8] = {};
    explicit CopyBindings(ID3D11DeviceContext* context, ID3D11Texture2D* target, UINT count)
        : c(context), slots(count), cs(count), om(count), keep(count, UINT(-1)) {
        c->CSGetUnorderedAccessViews(0, slots, cs.data());
        c->OMGetRenderTargets(8, rt, nullptr);
        for (UINT i = 0; i < 8; ++i) if (rt[i]) rtCount = i + 1;
        c->OMGetRenderTargetsAndUnorderedAccessViews(0, nullptr, nullptr, rtCount, slots - rtCount, om.data() + rtCount);
        for (UINT i = 0; i < slots; ++i) {
            ID3D11UnorderedAccessView* empty = nullptr;
            ComPtr<ID3D11Resource> resource;
            if (cs[i]) cs[i]->GetResource(&resource);
            if (resource.Get() == target) c->CSSetUnorderedAccessViews(i, 1, &empty, &keep[i]);
        }
        auto unbound = om;
        bool changed = false;
        for (UINT i = rtCount; i < slots; ++i) {
            ComPtr<ID3D11Resource> resource;
            if (om[i]) om[i]->GetResource(&resource);
            if (resource.Get() == target) { unbound[i] = nullptr; changed = true; }
        }
        if (changed) c->OMSetRenderTargetsAndUnorderedAccessViews(D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL,
            nullptr, nullptr, rtCount, slots - rtCount, unbound.data() + rtCount, keep.data() + rtCount);
    }
    ~CopyBindings() {
        c->CSSetUnorderedAccessViews(0, slots, cs.data(), keep.data());
        c->OMSetRenderTargetsAndUnorderedAccessViews(D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL,
            nullptr, nullptr, rtCount, slots - rtCount, om.data() + rtCount, keep.data() + rtCount);
        for (auto p : cs) if (p) p->Release();
        for (auto p : om) if (p) p->Release();
        for (auto p : rt) if (p) p->Release();
    }
};

struct Session {
    DWORD thread = GetCurrentThreadId();
    UINT width = 0, height = 0, outWidth = 0, outHeight = 0, uavSlots = 8;
    bool poisoned = false, gpuPending = false;
    uint64_t value = 0, frames = 0;
    std::string provider = "unverified";
    HMODULE module = nullptr;
    PfnFfxCreateContext create = nullptr;
    PfnFfxDestroyContext destroy = nullptr;
    PfnFfxQuery query = nullptr;
    PfnFfxDispatch dispatch = nullptr;
    ffxContext sdk = nullptr;
    ffxOverrideVersion version = {};
    ffxCreateBackendDX12Desc backend = {};
    ffxCreateContextDescUpscale creation = {};
    ComPtr<ID3D11Device> d11;
    ComPtr<ID3D11DeviceContext4> c11;
    ComPtr<ID3D11Fence> f11;
    ComPtr<ID3D11Texture2D> low11, high11;
    ComPtr<ID3D11UnorderedAccessView> lowUav;
    ComPtr<ID3D11ComputeShader> conversion;
    ComPtr<ID3D12Device> d12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> commands;
    ComPtr<ID3D12Fence> f12;
    ComPtr<ID3D12Resource> low12, high12, motion, depth, result;
    FrameEstimator estimator;
    Handle event;
    std::chrono::steady_clock::time_point previous;

    ~Session() { if (sdk && destroy) destroy(&sdk, nullptr); if (module) FreeLibrary(module); }
    void wait(uint64_t target) {
        auto completed = f12->GetCompletedValue();
        if (completed == UINT64_MAX) fail("Shared fence device removed", DXGI_ERROR_DEVICE_REMOVED);
        if (completed < target) {
            check(f12->SetEventOnCompletion(target, event.value), "Fence event");
            DWORD rc = WaitForSingleObject(event.value, 30000);
            if (rc != WAIT_OBJECT_0) fail("GPU wait (session permanently disabled)", rc == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(WAIT_TIMEOUT) : HRESULT_FROM_WIN32(GetLastError()));
        }
        if (f12->GetCompletedValue() == UINT64_MAX) fail("Shared fence removed after wait", DXGI_ERROR_DEVICE_REMOVED);
        check(d12->GetDeviceRemovedReason(), "D3D12 device removed");
        check(d11->GetDeviceRemovedReason(), "D3D11 device removed");
    }
    void drain() {
        // Include the last D3D11 output copy before retiring shared resources.
        gpuPending = true;
        check(c11->Signal(f11.Get(), ++value), "D3D11 drain signal");
        c11->Flush();
        check(queue->Wait(f12.Get(), value), "D3D12 drain wait");
        check(queue->Signal(f12.Get(), ++value), "D3D12 drain signal");
        wait(value);
        gpuPending = false;
    }
    void bridge(UINT w, UINT h, ComPtr<ID3D11Texture2D>& a, ComPtr<ID3D12Resource>& b) {
        D3D11_TEXTURE2D_DESC t = {};
        t.Width = w; t.Height = h; t.ArraySize = t.MipLevels = 1;
        t.Format = DXGI_FORMAT_R8G8B8A8_UNORM; t.SampleDesc.Count = 1;
        t.Usage = D3D11_USAGE_DEFAULT;
        t.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        t.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        check(d11->CreateTexture2D(&t, nullptr, &a), "Create RGBA shared bridge");
        ComPtr<IDXGIResource1> shared;
        check(a.As(&shared), "Bridge IDXGIResource1");
        Handle handle;
        check(shared->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle.value), "Create bridge NT handle");
        check(d12->OpenSharedHandle(handle.value, IID_PPV_ARGS(&b)), "Open bridge in D3D12");
    }
    void texture(UINT w, UINT h, DXGI_FORMAT format, D3D12_RESOURCE_STATES state, ComPtr<ID3D12Resource>& resource) {
        D3D12_HEAP_PROPERTIES heap = {}; heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC t = {};
        t.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; t.Width = w; t.Height = h;
        t.DepthOrArraySize = t.MipLevels = 1; t.Format = format; t.SampleDesc.Count = 1;
        t.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        check(d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &t, state, nullptr, IID_PPV_ARGS(&resource)), "Create FSR texture");
    }
    void load() {
        HMODULE self = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&fsrsr::run), &self)) fail("Locate SR module", HRESULT_FROM_WIN32(GetLastError()));
        wchar_t path[32768];
        DWORD n = GetModuleFileNameW(self, path, _countof(path));
        if (!n || n >= _countof(path)) fail("SR module path");
        std::wstring file(path, n);
        file.erase(file.find_last_of(L"\\/") + 1);
        file += L"amd_fidelityfx_upscaler_dx12.dll";
        module = LoadLibraryExW(file.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) fail("Load adjacent upscaler DLL", HRESULT_FROM_WIN32(GetLastError()));
        create = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(module, "ffxCreateContext"));
        destroy = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(module, "ffxDestroyContext"));
        query = reinterpret_cast<PfnFfxQuery>(GetProcAddress(module, "ffxQuery"));
        dispatch = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(module, "ffxDispatch"));
        if (!create || !destroy || !query || !dispatch) fail("FFX exports missing", E_NOINTERFACE);
        CompatibilityEdit edit;
        edit.apply(module);
        uint64_t count = 0;
        ffxQueryDescGetVersions versions = {{FFX_API_QUERY_DESC_TYPE_GET_VERSIONS, nullptr}, FFX_API_EFFECT_ID_UPSCALE, d12.Get(), &count, nullptr, nullptr};
        ffx(query(nullptr, &versions.header), "GET_VERSIONS count");
        if (!count || count > 1024) fail("Invalid provider count");
        std::vector<uint64_t> ids(static_cast<size_t>(count));
        std::vector<const char*> names(static_cast<size_t>(count));
        const auto capacity = count;
        versions.versionIds = ids.data(); versions.versionNames = names.data();
        ffx(query(nullptr, &versions.header), "GET_VERSIONS data");
        if (count > capacity) fail("Provider count changed");
        bool found = false;
        for (size_t i = 0; i < count; ++i) {
            if (names[i] && names[i][0] == '4' && names[i][1] == '.') {
                version = {{FFX_API_DESC_TYPE_OVERRIDE_VERSION, nullptr}, ids[i]};
                provider = names[i]; found = true; break;
            }
        }
        if (!found) fail("No explicit 4.x provider; no FSR3 fallback");
        backend = {{FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12, &version.header}, d12.Get()};
        creation = {{FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE, &backend.header}, FFX_UPSCALE_ENABLE_AUTO_EXPOSURE,
            {width, height}, {outWidth, outHeight}, nullptr};
        ffx(create(&sdk, &creation.header, nullptr), "Create explicit FSR4 context");
        ffxQueryGetProviderVersion actual = {{FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION, nullptr}, 0, nullptr};
        ffx(query(&sdk, &actual.header), "GET_PROVIDER_VERSION");
        if (actual.versionId != version.versionId || !actual.versionName || actual.versionName[0] != '4' || actual.versionName[1] != '.')
            fail("FSR4 provider verification mismatch");
        provider = actual.versionName;
        edit.restore();
    }
    void initialize(ID3D11Device* device, ID3D11DeviceContext* context, UINT w, UINT h, UINT ow, UINT oh) {
        d11 = device; width = w; height = h; outWidth = ow; outHeight = oh;
        check(context->QueryInterface(IID_PPV_ARGS(&c11)), "Requires ID3D11DeviceContext4");
        ComPtr<ID3D11Device5> device5;
        check(device->QueryInterface(IID_PPV_ARGS(&device5)), "Requires ID3D11Device5");
        ComPtr<IDXGIDevice> dxgi;
        check(device->QueryInterface(IID_PPV_ARGS(&dxgi)), "Query caller DXGI device");
        ComPtr<IDXGIAdapter> adapter;
        check(dxgi->GetAdapter(&adapter), "Get caller adapter");
        check(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&d12)), "Create same-adapter D3D12");
        uavSlots = device->GetFeatureLevel() >= D3D_FEATURE_LEVEL_11_1 ? 64 : 8;
        D3D12_COMMAND_QUEUE_DESC q = {}; q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        check(d12->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)), "Create D3D12 queue");
        check(d12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&f12)), "Create shared fence");
        Handle handle;
        check(d12->CreateSharedHandle(f12.Get(), nullptr, GENERIC_ALL, nullptr, &handle.value), "Share fence");
        check(device5->OpenSharedFence(handle.value, IID_PPV_ARGS(&f11)), "Open D3D11 fence");
        event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!event.value) fail("Create fence event", HRESULT_FROM_WIN32(GetLastError()));
        bridge(w, h, low11, low12); bridge(ow, oh, high11, high12);
        check(device->CreateUnorderedAccessView(low11.Get(), nullptr, &lowUav), "Create conversion UAV");
        const char source[] = "Texture2D<float4> Input:register(t0); RWTexture2D<float4> Output:register(u0); [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID) { uint w,h; Output.GetDimensions(w,h); if(p.x<w && p.y<h) Output[p.xy]=Input.Load(int3(p.xy,0)); }";
        ComPtr<ID3DBlob> code, errors;
        check(D3DCompile(source, sizeof(source) - 1, "fsr_sr", nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &code, &errors), "Compile BGRA conversion");
        check(device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &conversion), "Create BGRA conversion");
        texture(w, h, DXGI_FORMAT_R16G16_FLOAT, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, motion);
        texture(w, h, DXGI_FORMAT_R32_FLOAT, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, depth);
        texture(ow, oh, DXGI_FORMAT_R8G8B8A8_UNORM, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, result);
        check(estimator.initialize(d12.Get(), w, h), "Initialize estimated motion/depth");
        check(d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "Create command allocator");
        check(d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&commands)), "Create command list");
        check(commands->Close(), "Close initial list");
        load();
    }
    void render(ID3D11ShaderResourceView* input, ID3D11Texture2D* output) {
        // Mark pending before the first GPU command, including failed Signal paths.
        gpuPending = true;
        {
            ComputeState saved(c11.Get());
            ID3D11UnorderedAccessView* empty = nullptr;
            UINT keep = UINT(-1);
            c11->CSSetUnorderedAccessViews(0, 1, &empty, &keep);
            auto u = lowUav.Get();
            c11->CSSetShader(conversion.Get(), nullptr, 0);
            c11->CSSetShaderResources(0, 1, &input);
            c11->CSSetUnorderedAccessViews(0, 1, &u, &keep);
            c11->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        }
        check(c11->Signal(f11.Get(), ++value), "D3D11 input signal");
        c11->Flush();
        check(queue->Wait(f12.Get(), value), "D3D12 wait input");
        check(allocator->Reset(), "Reset allocator");
        check(commands->Reset(allocator.Get(), nullptr), "Reset command list");
        check(estimator.record(commands.Get(), low12.Get(), motion.Get(), depth.Get(), true, frames == 0), "Record estimated inputs");
        ffxDispatchDescUpscale desc = {};
        desc.header = {FFX_API_DISPATCH_DESC_TYPE_UPSCALE, nullptr};
        desc.commandList = commands.Get();
        desc.color = ffxApiGetResourceDX12(low12.Get(), FFX_API_RESOURCE_STATE_COMMON);
        desc.depth = ffxApiGetResourceDX12(depth.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.motionVectors = ffxApiGetResourceDX12(motion.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.output = ffxApiGetResourceDX12(result.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
        desc.jitterOffset = {0, 0}; desc.motionVectorScale = {1, 1};
        desc.renderSize = {width, height}; desc.upscaleSize = {outWidth, outHeight};
        auto now = std::chrono::steady_clock::now();
        float elapsed = frames ? std::chrono::duration<float, std::milli>(now - previous).count() : 16.6667f;
        desc.frameTimeDelta = elapsed < 1 ? 1 : (elapsed > 1000 ? 1000 : elapsed);
        desc.preExposure = 1; desc.reset = frames == 0;
        desc.cameraNear = 0.1f; desc.cameraFar = 1000; desc.cameraFovAngleVertical = 1.04719755f;
        desc.viewSpaceToMetersFactor = 1;
        // Once record succeeds, submit even if FFX reports failure: estimator
        // history/state has advanced and must not be abandoned then reused.
        const auto rc = dispatch(&sdk, &desc.header);
        if (rc == FFX_API_RETURN_OK) {
            D3D12_RESOURCE_BARRIER b[] = {
                barrier(result.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE),
                barrier(high12.Get(), D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST)};
            commands->ResourceBarrier(2, b);
            commands->CopyResource(high12.Get(), result.Get());
            b[0] = barrier(result.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            b[1] = barrier(high12.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
            commands->ResourceBarrier(2, b);
        }
        check(commands->Close(), "Close SR list");
        ID3D12CommandList* lists[] = {commands.Get()};
        queue->ExecuteCommandLists(1, lists);
        check(queue->Signal(f12.Get(), ++value), "D3D12 completion signal");
        wait(value);
        ffx(rc, "FSR4 dispatch");
        check(c11->Wait(f11.Get(), value), "D3D11 wait output");
        {
            CopyBindings saved(c11.Get(), output, uavSlots);
            c11->CopyResource(output, high11.Get());
        }
        // Complete the copy too: a successful return means all resources can be
        // retired safely on the next resize/device change.
        drain();
        previous = now; ++frames;
    }
};
// Deliberately not a static owning pointer: no API shutdown exists. A poisoned
// session is quarantined until process exit, never destructed with in-flight GPU
// references (including command allocator, estimator, DLL and caller context).
// FSR4 contexts are thread-affine (COM apartment and command queue) and LS
// dispatches its scaling pass from more than one render thread, so each thread
// keeps its own session; sessions of exited threads intentionally leak.
thread_local Session* session = nullptr;
}

namespace fsrsr {
const char* status() { return diagnostic; }
bool run(ID3D11DeviceContext* context, ID3D11ShaderResourceView* input, ID3D11Texture2D* output) {
    if (session && session->poisoned) return false;
    try {
        if (!context || !input || !output) fail("Null SR argument", E_INVALIDARG);
        if (context->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE) fail("SR requires immediate context", E_INVALIDARG);
        ComPtr<ID3D11Device> device, inputDevice, outputDevice;
        context->GetDevice(&device); input->GetDevice(&inputDevice); output->GetDevice(&outputDevice);
        if (inputDevice.Get() != device.Get() || outputDevice.Get() != device.Get()) fail("SR device mismatch", E_INVALIDARG);
        ComPtr<ID3D11Resource> resource; input->GetResource(&resource);
        ComPtr<ID3D11Texture2D> color;
        check(resource.As(&color), "SR input must be Texture2D");
        D3D11_TEXTURE2D_DESC in = {}, out = {};
        D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
        color->GetDesc(&in); output->GetDesc(&out); input->GetDesc(&view);
        if (in.Format != DXGI_FORMAT_B8G8R8A8_UNORM || view.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
            view.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D || view.Texture2D.MostDetailedMip != 0 ||
            (in.Usage != D3D11_USAGE_DEFAULT && in.Usage != D3D11_USAGE_IMMUTABLE) || !(in.BindFlags & D3D11_BIND_SHADER_RESOURCE) ||
            in.ArraySize != 1 || in.SampleDesc.Count != 1 ||
            out.Format != DXGI_FORMAT_R8G8B8A8_UNORM || out.Usage != D3D11_USAGE_DEFAULT ||
            !(out.BindFlags & D3D11_BIND_UNORDERED_ACCESS) || out.ArraySize != 1 || out.MipLevels != 1 || out.SampleDesc.Count != 1 ||
            !in.Width || !in.Height || out.Width < in.Width || out.Height < in.Height)
            fail("Requires DEFAULT/IMMUTABLE BGRA87 SRV -> DEFAULT RGBA28 UAV, single sample/slice, output >= input", E_INVALIDARG);
        if (session && (session->d11.Get() != device.Get() || session->width != in.Width || session->height != in.Height ||
            session->outWidth != out.Width || session->outHeight != out.Height)) {
            session->drain();
            // Explicitly check SDK destruction before releasing its resources.
            if (session->sdk) ffx(session->destroy(&session->sdk, nullptr), "Destroy retired FSR4 context");
            delete session; session = nullptr;
        }
        if (!session) {
            session = new Session;
            session->initialize(device.Get(), context, in.Width, in.Height, out.Width, out.Height);
        }
        session->render(input, output);
        sprintf_s(diagnostic, "provider=%s dispatch_success_frames=%llu estimated_inputs=experimental jitter=0 INT8=unverified",
            session->provider.c_str(), static_cast<unsigned long long>(session->frames));
        return true;
    } catch (const Failure&) {
    } catch (const std::bad_alloc&) {
        strcpy_s(diagnostic, "FSR SR out of memory");
    } catch (...) {
        strcpy_s(diagnostic, "FSR SR unexpected native exception");
    }
    if (session) {
        session->poisoned = true;
        char reason[sizeof(diagnostic)]; strcpy_s(reason, diagnostic);
        sprintf_s(diagnostic, "provider=%s dispatch_success_frames=%llu DISABLED (resources quarantined, pending=%d): %.600s; estimated_inputs=experimental INT8=unverified",
            session->provider.c_str(), static_cast<unsigned long long>(session->frames), session->gpuPending ? 1 : 0, reason);
    }
    return false;
}
}
