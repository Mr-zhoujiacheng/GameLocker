#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <MinHook.h>
#include <atomic>
#include <mutex>
#include <unordered_map>
#include <cstdio>
#include <cstdarg>
#include <cstdint>
#include "ls_bridge.h"

using Microsoft::WRL::ComPtr;
namespace {
std::mutex gate;
FILE* logFile = nullptr;
std::atomic<unsigned long long> sequence{0};
std::atomic<bool> ready{false};
thread_local bool inside = false;
std::unordered_map<void*, unsigned> dispatchCounts;
unsigned presentCount = 0, copyCount = 0;
using Dispatch = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT);
using Indirect = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Buffer*, UINT);
using Present = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using CreateCS = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T, ID3D11ClassLinkage*, ID3D11ComputeShader**);
using Copy = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, ID3D11Resource*);
using CopyRegion = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, UINT, UINT, UINT, ID3D11Resource*, UINT, const D3D11_BOX*);
Dispatch originalDispatch = nullptr;
Indirect originalIndirect = nullptr;
Present originalPresent = nullptr;
Present1 originalPresent1 = nullptr;
CreateCS originalCreateCS = nullptr;
Copy originalCopy = nullptr;
CopyRegion originalRegion = nullptr;
void record(const char* format, ...) {
    if (!logFile) return;
    LARGE_INTEGER now; QueryPerformanceCounter(&now);
    fprintf(logFile, "%llu qpc=%lld tid=%lu ", ++sequence, now.QuadPart, GetCurrentThreadId());
    va_list args; va_start(args, format); vfprintf(logFile, format, args); va_end(args);
    fputc('\n', logFile); fflush(logFile);
}
void texture(const char* kind, UINT slot, ID3D11Resource* resource) {
    if (!resource) return;
    ComPtr<ID3D11Texture2D> t;
    if (FAILED(resource->QueryInterface(IID_PPV_ARGS(&t)))) return;
    D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
    record("texture kind=%s slot=%u resource=%p size=%ux%u format=%u bind=0x%x misc=0x%x mips=%u array=%u samples=%u usage=%u",
        kind, slot, t.Get(), d.Width, d.Height, d.Format, d.BindFlags, d.MiscFlags, d.MipLevels, d.ArraySize, d.SampleDesc.Count, d.Usage);
}
void inspect(ID3D11DeviceContext* context, UINT x, UINT y, UINT z, bool indirect) {
    if (!ready || inside) return;
    inside = true;
    try {
        std::lock_guard<std::mutex> lock(gate);
        ComPtr<ID3D11ComputeShader> shader;
        context->CSGetShader(&shader, nullptr, nullptr);
        // Bounded per shader: this is a discovery trace, never an FG classifier.
        if (dispatchCounts.size() < 4096 && ++dispatchCounts[shader.Get()] <= 4) {
            record("dispatch context=%p shader=%p groups=%u,%u,%u indirect=%u", context, shader.Get(), x, y, z, indirect);
            ID3D11ShaderResourceView* srvs[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};
            context->CSGetShaderResources(0, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, srvs);
            for (UINT i = 0; i < D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT; ++i) if (srvs[i]) {
                ComPtr<ID3D11Resource> r; srvs[i]->GetResource(&r); texture("srv", i, r.Get()); srvs[i]->Release();
            }
            ID3D11UnorderedAccessView* uavs[D3D11_PS_CS_UAV_REGISTER_COUNT] = {};
            context->CSGetUnorderedAccessViews(0, D3D11_PS_CS_UAV_REGISTER_COUNT, uavs);
            for (UINT i = 0; i < D3D11_PS_CS_UAV_REGISTER_COUNT; ++i) if (uavs[i]) {
                ComPtr<ID3D11Resource> r; uavs[i]->GetResource(&r); texture("uav", i, r.Get()); uavs[i]->Release();
            }
        }
    } catch (...) { /* A diagnostic must not interrupt the renderer. */ }
    inside = false;
}
void STDMETHODCALLTYPE hookDispatch(ID3D11DeviceContext* c, UINT x, UINT y, UINT z) {
    inspect(c,x,y,z,false);
    bool skip = false;
    if (ready && !inside) {
        inside = true;
        try { skip = lsbridge::skipDispatch(c); } catch (...) {}
        inside = false;
    }
    if (skip) return;
    originalDispatch(c,x,y,z);
    if (ready && !inside) {
        inside = true;
        try { lsbridge::afterDispatch(c,x,y,z); } catch (...) {}
        inside = false;
    }
}
void STDMETHODCALLTYPE hookIndirect(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT offset) { inspect(c,offset,0,0,true); originalIndirect(c,b,offset); }
HRESULT STDMETHODCALLTYPE hookCreateCS(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11ComputeShader** result) {
    HRESULT hr = originalCreateCS(d,code,size,linkage,result);
    if (ready && !inside && SUCCEEDED(hr) && result && *result) {
        inside = true;
        try {
            uint64_t hash = 14695981039346656037ull;
            auto bytes = static_cast<const unsigned char*>(code);
            for (SIZE_T i = 0; i < size; ++i) { hash ^= bytes[i]; hash *= 1099511628211ull; }
            std::lock_guard<std::mutex> lock(gate);
            record("shader device=%p shader=%p bytes=%zu fnv1a64=%016llx", d, *result, size, hash);
            lsbridge::registerShader(*result, hash, size);
        } catch (...) {}
        inside = false;
    }
    return hr;
}
void inspectCopy(ID3D11DeviceContext* c, ID3D11Resource* dst, ID3D11Resource* src, bool region) {
    if (!ready || inside) return;
    inside = true;
    try {
        std::lock_guard<std::mutex> lock(gate);
        if (++copyCount <= 256) {
            record("copy context=%p region=%u", c, region);
            texture("copy-source",0,src); texture("copy-destination",0,dst);
        }
    } catch (...) {}
    inside = false;
}
void STDMETHODCALLTYPE hookCopy(ID3D11DeviceContext* c, ID3D11Resource* dst, ID3D11Resource* src) { inspectCopy(c,dst,src,false); originalCopy(c,dst,src); }
void STDMETHODCALLTYPE hookRegion(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT sub, UINT x, UINT y, UINT z, ID3D11Resource* src, UINT ssub, const D3D11_BOX* box) {
    inspectCopy(c,dst,src,true); originalRegion(c,dst,sub,x,y,z,src,ssub,box);
}
void inspectPresent(IDXGISwapChain* swap, UINT sync, UINT flags) {
    if (!ready || inside) return;
    inside = true;
    try {
        std::lock_guard<std::mutex> lock(gate);
        if (++presentCount <= 256) {
            DXGI_SWAP_CHAIN_DESC d = {}; swap->GetDesc(&d);
            ComPtr<ID3D11Device> device; swap->GetDevice(IID_PPV_ARGS(&device));
            record("present swap=%p device=%p hwnd=%p size=%ux%u format=%u sync=%u flags=0x%x", swap, device.Get(), d.OutputWindow, d.BufferDesc.Width, d.BufferDesc.Height, d.BufferDesc.Format, sync, flags);
            ComPtr<ID3D11Texture2D> back;
            if (SUCCEEDED(swap->GetBuffer(0,IID_PPV_ARGS(&back)))) texture("backbuffer",0,back.Get());
        }
    } catch (...) {}
    inside = false;
}
bool bridgePresent(IDXGISwapChain* s, UINT flags) {
    if (!ready || inside) return false;
    inside = true;
    bool taken = false;
    try {
        taken = lsbridge::present(s,flags);
        if (!taken) taken = lsbridge::fallbackPresent(s,flags);
    } catch (...) {}
    inside = false;
    return taken;
}
HRESULT STDMETHODCALLTYPE hookPresent(IDXGISwapChain* s, UINT sync, UINT flags) {
    inspectPresent(s,sync,flags);
    return bridgePresent(s,flags) ? S_OK : originalPresent(s,sync,flags);
}
HRESULT STDMETHODCALLTYPE hookPresent1(IDXGISwapChain1* s, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    inspectPresent(s,sync,flags);
    return bridgePresent(s,flags) ? S_OK : originalPresent1(s,sync,flags,p);
}
void* targets[7] = {};
template<class F> bool hook(unsigned index, void* target, void* replacement, F* original) {
    targets[index] = target;
    return MH_CreateHook(target,replacement,reinterpret_cast<void**>(original)) == MH_OK;
}
}
extern "C" __declspec(dllexport) DWORD WINAPI probeStart(void* mode) {
    // Explicit call outside DllMain, once per process. Keep DLL loaded until exit.
    static std::atomic<bool> started{false};
    if (started.exchange(true)) return ERROR_ALREADY_INITIALIZED;
    wchar_t directory[MAX_PATH], path[MAX_PATH];
    if (!GetTempPathW(MAX_PATH,directory)) return GetLastError();
    swprintf_s(path,L"%sGameLocker-LS-probe-%lu.log",directory,GetCurrentProcessId());
    if (_wfopen_s(&logFile,path,L"w") != 0) return ERROR_OPEN_FAILED;
    LARGE_INTEGER frequency; QueryPerformanceFrequency(&frequency);
    record("probe version=2 qpcFrequency=%lld observeOnly=%u classifier=version-specific-BGRA-RGBA", frequency.QuadPart, mode ? 0u : 1u);
    WNDCLASSW wc = {}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"GameLockerProbeDiscovery";
    ATOM atom = RegisterClassW(&wc);
    if (!atom) return GetLastError();
    HWND window = CreateWindowW(wc.lpszClassName,L"",WS_OVERLAPPED,0,0,64,64,nullptr,nullptr,wc.hInstance,nullptr);
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain> swap;
    DXGI_SWAP_CHAIN_DESC desc = {}; desc.BufferDesc.Width = 64; desc.BufferDesc.Height = 64;
    desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
    desc.OutputWindow = window; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    HRESULT hr = window ? D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&desc,&swap,&device,nullptr,&context) : E_FAIL;
    bool ok = false;
    if (SUCCEEDED(hr) && MH_Initialize() == MH_OK) {
        auto cv = *reinterpret_cast<void***>(context.Get());
        auto dv = *reinterpret_cast<void***>(device.Get());
        auto sv = *reinterpret_cast<void***>(swap.Get());
        ComPtr<IDXGISwapChain1> swap1;
        ok = SUCCEEDED(swap.As(&swap1));
        if (ok) {
            auto s1v = *reinterpret_cast<void***>(swap1.Get());
            ok = hook(0,cv[41],reinterpret_cast<void*>(hookDispatch),&originalDispatch)
                && hook(1,cv[42],reinterpret_cast<void*>(hookIndirect),&originalIndirect)
                && hook(2,dv[18],reinterpret_cast<void*>(hookCreateCS),&originalCreateCS)
                && hook(3,cv[47],reinterpret_cast<void*>(hookCopy),&originalCopy)
                && hook(4,cv[46],reinterpret_cast<void*>(hookRegion),&originalRegion)
                && hook(5,sv[8],reinterpret_cast<void*>(hookPresent),&originalPresent)
                && hook(6,s1v[22],reinterpret_cast<void*>(hookPresent1),&originalPresent1);
        }
        if (ok) {
            for (auto target : targets) if (MH_QueueEnableHook(target) != MH_OK) ok = false;
            if (ok) ok = MH_ApplyQueued() == MH_OK;
        }
        if (!ok) {
            for (auto target : targets) if (target) { MH_DisableHook(target); MH_RemoveHook(target); }
        }
    }
    swap.Reset(); context.Reset(); device.Reset();
    if (window) DestroyWindow(window);
    UnregisterClassW(wc.lpszClassName,wc.hInstance);
    record("installation success=%u hr=0x%08lx requiresCaptureRestart=1",ok,static_cast<unsigned long>(hr));
    if (ok && (mode == reinterpret_cast<void*>(1) || mode == reinterpret_cast<void*>(2))) {
        HMODULE module = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&probeStart), &module);
        if (!lsbridge::start(module, mode == reinterpret_cast<void*>(2))) record("bridge startup failed; original presentation retained");
    }
    ready = ok;
    return ok ? ERROR_SUCCESS : ERROR_NOT_SUPPORTED;
}
#ifdef XEL_PROBE_SELFTEST
#include <d3dcompiler.h>
int main(int argc, char** argv) {
    const bool bridgeFallbackTest = argc > 1;
    const bool bypassTest = argc > 1 && strcmp(argv[1], "--bypass") == 0;
    DWORD status = probeStart(bypassTest ? reinterpret_cast<void*>(2) : bridgeFallbackTest ? reinterpret_cast<void*>(1) : nullptr);
    if (status) { printf("probeStart failed=%lu\n",status); return 1; }
    HWND w = CreateWindowW(L"STATIC",L"Probe self-test",WS_OVERLAPPEDWINDOW,0,0,128,128,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    DXGI_SWAP_CHAIN_DESC d = {}; d.BufferDesc.Width = 64; d.BufferDesc.Height = 64;
    d.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount = 2; d.OutputWindow = w; d.Windowed = TRUE;
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<IDXGISwapChain> swap;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&d,&swap,&device,nullptr,&context);
    if (FAILED(hr)) return 2;
    const char shader[] = "RWTexture2D<float4> dst : register(u0); Texture2D<float4> src : register(t0); [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID) { dst[p.xy] = src.Load(int3(p.xy,0)); }";
    ComPtr<ID3DBlob> code, errors;
    hr = D3DCompile(shader,sizeof(shader)-1,nullptr,nullptr,nullptr,"main","cs_5_0",0,0,&code,&errors);
    if (FAILED(hr)) return 3;
    ComPtr<ID3D11ComputeShader> cs;
    if (FAILED(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&cs))) return 4;
    D3D11_TEXTURE2D_DESC td = {}; td.Width = 64; td.Height = 64; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    ComPtr<ID3D11Texture2D> a,b; ComPtr<ID3D11ShaderResourceView> srv; ComPtr<ID3D11UnorderedAccessView> uav;
    if (FAILED(device->CreateTexture2D(&td,nullptr,&a)) || FAILED(device->CreateTexture2D(&td,nullptr,&b)) ||
        FAILED(device->CreateShaderResourceView(a.Get(),nullptr,&srv)) || FAILED(device->CreateUnorderedAccessView(b.Get(),nullptr,&uav))) return 5;
    context->CSSetShader(cs.Get(),nullptr,0);
    auto s = srv.Get(); auto u = uav.Get();
    context->CSSetShaderResources(0,1,&s); context->CSSetUnorderedAccessViews(0,1,&u,nullptr);
    context->Dispatch(8,8,1);
    s = nullptr; u = nullptr;
    context->CSSetShaderResources(0,1,&s); context->CSSetUnorderedAccessViews(0,1,&u,nullptr);
    ComPtr<ID3D11Texture2D> back; if (FAILED(swap->GetBuffer(0,IID_PPV_ARGS(&back)))) return 6;
    context->CopyResource(back.Get(),b.Get());
    if (FAILED(swap->Present(0,0))) return 7;
    const bool fallback = !bridgePresent(swap.Get(), 0) && !bridgePresent(swap.Get(), DXGI_PRESENT_TEST);
    bool pass = dispatchCounts[cs.Get()] > 0 && presentCount > 0 && copyCount > 0 && fallback;
    if (bypassTest) {
        const float red[] = {1,0,0,1};
        context->ClearUnorderedAccessViewFloat(uav.Get(), red);
        context->CSSetShader(cs.Get(),nullptr,0);
        bool bypassPass = lsbridge::testBypass(device.Get(),context.Get(),cs.Get(),b.Get(),swap.Get());
        D3D11_TEXTURE2D_DESC readDesc = td;
        readDesc.BindFlags = 0; readDesc.Usage = D3D11_USAGE_STAGING;
        readDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> readback;
        if (SUCCEEDED(device->CreateTexture2D(&readDesc,nullptr,&readback))) {
            context->CopyResource(readback.Get(),back.Get());
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped))) {
                auto pixel = static_cast<const unsigned char*>(mapped.pData);
                bypassPass = bypassPass && pixel[0] == 255 && pixel[1] == 0 && pixel[2] == 0 && pixel[3] == 255;
                context->Unmap(readback.Get(),0);
            } else bypassPass = false;
        } else bypassPass = false;
        printf("bypass gating + raw fallback GPU readback=%s\n", bypassPass ? "PASS" : "FAIL");
        pass = pass && bypassPass;
    }
    printf("unrecognized shader / Present(TEST) fallback=%s bridgeMode=%u\n", fallback ? "PASS" : "FAIL", bridgeFallbackTest ? 1u : 0u);
    printf("probe selftest dispatch=%u present=%u copy=%u result=%s (not a Lossless Scaling integration test)\n", dispatchCounts[cs.Get()],presentCount,copyCount,pass ? "PASS" : "FAIL");
    back.Reset(); swap.Reset(); context.Reset(); device.Reset(); DestroyWindow(w);
    return pass ? 0 : 8;
}
#else
BOOL WINAPI DllMain(HINSTANCE, DWORD, void*) { return TRUE; }
#endif
