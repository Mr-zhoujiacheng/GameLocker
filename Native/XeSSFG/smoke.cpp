#include "xel_fg.h"
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <stdexcept>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;
namespace {
void hr(HRESULT value, const char* op) {
    if (FAILED(value)) {
        char text[768]; sprintf_s(text, "%s HRESULT=0x%08lX %s", op, (unsigned long)value, xelGetLastError());
        throw std::runtime_error(text);
    }
}
void expect(HRESULT value, HRESULT wanted, const char* op) {
    if (value != wanted) throw std::runtime_error(std::string("Unexpected validation result: ") + op);
}
LRESULT CALLBACK windowProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_CLOSE) { DestroyWindow(h); return 0; }
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}
unsigned number(const char* text) {
    char* end = nullptr; unsigned long value = strtoul(text, &end, 10);
    if (!*text || *end || *text == '-' || value > 1000000) throw std::runtime_error("Invalid numeric argument");
    return (unsigned)value;
}
}
int main(int argc, char** argv) {
    XelSession* session = nullptr;
    HWND window = nullptr;
    int exitCode = 0;
    try {
        unsigned frames = 120, adapterIndex = 0;
        bool explicitAdapter = false, disabled = false, exercise = false, estimateInputs = false;
#ifdef FSR_SMOKE
        unsigned hiddenFrames = 0;
#endif
        for (int i = 1; i < argc; ++i) {
            if (!strcmp(argv[i], "--frames") && i + 1 < argc) frames = number(argv[++i]);
            else if (!strcmp(argv[i], "--adapter") && i + 1 < argc) { adapterIndex = number(argv[++i]); explicitAdapter = true; }
            else if (!strcmp(argv[i], "--disabled")) disabled = true;
            else if (!strcmp(argv[i], "--exercise")) exercise = true;
            else if (!strcmp(argv[i], "--estimate-inputs")) estimateInputs = true;
#ifdef FSR_SMOKE
            else if (!strcmp(argv[i], "--hidden-frames") && i + 1 < argc) hiddenFrames = number(argv[++i]);
#endif
            else throw std::runtime_error("Usage: xel_smoke [--frames N] [--disabled] [--adapter index] [--exercise]");
        }
        if (!frames || (exercise && frames < 12)) throw std::runtime_error("Frames must be >0; exercise requires >=12");
        ComPtr<IDXGIFactory6> factory;
        hr(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");
        for (UINT i = 0;; ++i) {
            ComPtr<IDXGIAdapter1> a;
            HRESULT result = factory->EnumAdapters1(i, &a);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            hr(result, "EnumAdapters1");
            DXGI_ADAPTER_DESC1 desc = {}; hr(a->GetDesc1(&desc), "GetDesc1");
            printf("adapter[%u]=%ls vendor=0x%04X device=0x%04X software=%u\n", i, desc.Description, desc.VendorId, desc.DeviceId, !!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE));
        }
        ComPtr<IDXGIAdapter1> adapter;
        if (explicitAdapter) hr(factory->EnumAdapters1(adapterIndex, &adapter), "Select indexed adapter");
        else hr(factory->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)), "Select high performance adapter");
        DXGI_ADAPTER_DESC1 selected = {}; hr(adapter->GetDesc1(&selected), "Selected descriptor");
        printf("selected=%ls mode=%s frames=%u exercise=%u\n", selected.Description, disabled ? "disabled" : "2x SDR", frames, exercise);
        WNDCLASSW wc = {}; wc.hInstance = GetModuleHandleW(nullptr); wc.lpfnWndProc = windowProc;
        wc.lpszClassName = L"XelIndependentSmoke"; wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassW(&wc)) hr(HRESULT_FROM_WIN32(GetLastError()), "Register window class");
        constexpr UINT width = 1280, height = 720;
        const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        RECT rect = {0, 0, (LONG)width, (LONG)height}; AdjustWindowRect(&rect, style, FALSE);
        window = CreateWindowExW(0, wc.lpszClassName, L"Frame-generation standalone D3D11 smoke", style,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, wc.hInstance, nullptr);
        if (!window) hr(HRESULT_FROM_WIN32(GetLastError()), "Create window");
#ifdef FSR_SMOKE
        if (!hiddenFrames)
#endif
        { ShowWindow(window, SW_SHOW); UpdateWindow(window); }
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL obtained;
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        hr(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, &device, &obtained, &context), "Create D3D11 device");
        ComPtr<ID3D11DeviceContext1> context1; hr(context.As(&context1), "ClearView context");
        D3D11_TEXTURE2D_DESC td = {}; td.Width = width; td.Height = height; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> texture; hr(device->CreateTexture2D(&td, nullptr, &texture), "Create source texture");
        ComPtr<ID3D11RenderTargetView> rtv; hr(device->CreateRenderTargetView(texture.Get(), nullptr, &rtv), "Create source RTV");
        hr(xelCreate(device.Get(), context.Get(), window, width, height, &session), "xelCreate");
        hr(xelSetEnabled(session, !disabled), "Initial enable state");
        hr(xelSetEstimation(session, estimateInputs), "Initial estimation state");
        printf("screen_estimation=%u (motion-derived pseudo-depth, not engine data)\n", estimateInputs);
        if (exercise) {
            expect(xelSubmit(session, nullptr, 16.6667f, 0), E_INVALIDARG, "null source");
            expect(xelSubmit(session, texture.Get(), 0, 0), E_INVALIDARG, "zero milliseconds");
            expect(xelResize(session, width, height), E_NOTIMPL, "resize requires recreation");
            ComPtr<ID3D11Texture2D> bad;
            td.Width = width / 2; hr(device->CreateTexture2D(&td, nullptr, &bad), "Create invalid dimensions");
            expect(xelSubmit(session, bad.Get(), 16.6667f, 0), E_INVALIDARG, "dimensions");
            bad.Reset(); td.Width = width; td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            hr(device->CreateTexture2D(&td, nullptr, &bad), "Create invalid format");
            expect(xelSubmit(session, bad.Get(), 16.6667f, 0), E_INVALIDARG, "format");
            ComPtr<ID3D11Device> otherDevice; ComPtr<ID3D11DeviceContext> otherContext;
            hr(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, levels, 2, D3D11_SDK_VERSION,
                &otherDevice, nullptr, &otherContext), "Create other device");
            bad.Reset(); td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            hr(otherDevice->CreateTexture2D(&td, nullptr, &bad), "Create wrong-device texture");
            expect(xelSubmit(session, bad.Get(), 16.6667f, 0), E_INVALIDARG, "device");
            HRESULT wrongThread = S_OK;
            std::thread worker([&] { XelStats s = {}; wrongThread = xelGetStats(session, &s); }); worker.join();
            expect(wrongThread, RPC_E_WRONG_THREAD, "thread affinity");
            printf("validation=null/time/dimensions/format/device/resize/thread PASS\n");
        }
        XelStats stats = {};
        bool currentEnabled = !disabled;
        unsigned disabledObserved = 0, enabledObserved = 0;
        auto started = std::chrono::steady_clock::now();
        for (unsigned frame = 0; frame < frames; ++frame) {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) throw std::runtime_error("Window closed before requested frames completed");
                TranslateMessage(&msg); DispatchMessageW(&msg);
            }
#ifdef FSR_SMOKE
            if (hiddenFrames && frame == hiddenFrames) {
                printf("bootstrap: showing window independently of presentation stats\n");
                ShowWindow(window, SW_SHOWNOACTIVATE); UpdateWindow(window);
                hr(xelResetHistory(session), "Reset after hidden bootstrap");
            }
#endif
            if (exercise && estimateInputs && frame == frames / 4)
                hr(xelSetEstimation(session, 0), "Estimation off");
            if (exercise && estimateInputs && frame == 3 * frames / 4)
                hr(xelSetEstimation(session, 1), "Estimation restore");
            if (exercise && frame == frames / 3) {
                currentEnabled = false; hr(xelSetEnabled(session, 0), "Toggle off");
            }
            if (exercise && frame == 2 * frames / 3) {
                currentEnabled = !disabled; hr(xelSetEnabled(session, currentEnabled), "Toggle restore");
                hr(xelResetHistory(session), "Explicit history reset");
            }
            const float background[4] = {0.025f, 0.05f, 0.12f, 1};
            const float foreground[4] = {0.95f, 0.55f, 0.08f, 1};
            context->ClearRenderTargetView(rtv.Get(), background);
            LONG x = (LONG)((frame * 7) % (width - 160));
            D3D11_RECT area = {x, 280, x + 160, 440};
            context1->ClearView(rtv.Get(), foreground, &area, 1);
            hr(xelSubmit(session, texture.Get(), 16.6667f, frame == 0), "xelSubmit");
            hr(xelGetStats(session, &stats), "xelGetStats");
#ifdef FSR_SMOKE
            if (frame < hiddenFrames) {
                printf("hidden=%u sent=%u generatedResult=%d flags=0x%X warnings=%llu\n", frame,
                    stats.lastFramesSent, stats.lastFrameGenerationResult, stats.flags,
                    (unsigned long long)stats.statusWarnings);
                if (!stats.lastFramesSent && (stats.flags & XEL_STATUS_LAST_INTERPOLATED))
                    throw std::runtime_error("No successful presentation but interpolation flag set");
                continue;
            }
#endif
            if (stats.flags & XEL_STATUS_VALID) {
                if (!currentEnabled) {
                    if (stats.lastFramesSent != 1 || (stats.flags & (XEL_STATUS_SDK_ENABLED | XEL_STATUS_LAST_INTERPOLATED)))
                        throw std::runtime_error("Disabled frame unexpectedly interpolated/enabled");
                    ++disabledObserved;
                } else if (stats.flags & XEL_STATUS_LAST_INTERPOLATED) ++enabledObserved;
            }
            if (frame < 4 || frame % 30 == 0 || frame + 1 == frames)
                printf("present=%u sent=%u fgResult=%d query=%d flags=0x%X\n", stats.lastPresentId, stats.lastFramesSent,
                    stats.lastFrameGenerationResult, stats.lastStatusQueryResult, stats.flags);
        }
        double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        printf("summary submitted=%llu SDK_sent=%llu interpolated_sent=%llu interpolating_presents=%llu warnings=%llu flags=0x%X elapsed_seconds=%.3f\n",
            (unsigned long long)stats.submittedFrames, (unsigned long long)stats.sdkFramesSent,
            (unsigned long long)stats.interpolatedFramesSent, (unsigned long long)stats.interpolatingPresents,
            (unsigned long long)stats.statusWarnings, stats.flags, seconds);
        printf("SDK_sent counts frames sent for presentation, NOT measured scanout or FPS.\n");
        if (!disabled && !enabledObserved) { fprintf(stderr, "FAIL: enabled mode produced no SDK-confirmed interpolation\n"); exitCode = 3; }
        if ((disabled || exercise) && !disabledObserved) { fprintf(stderr, "FAIL: disabled path not observed\n"); exitCode = 4; }
        hr(xelDestroy(&session), "xelDestroy");
        hr(xelDestroy(&session), "Repeated null destroy");
        printf("destroy=PASS exit=%d\n", exitCode);
    } catch (const std::exception& e) {
        fprintf(stderr, "FAIL: %s\n", e.what()); exitCode = 2;
    }
    if (session) {
        HRESULT cleanup = xelDestroy(&session);
        if (FAILED(cleanup)) { fprintf(stderr, "Cleanup failed: %s\n", xelGetLastError()); exitCode = 5; }
    }
    if (window && IsWindow(window)) DestroyWindow(window);
    return exitCode;
}
