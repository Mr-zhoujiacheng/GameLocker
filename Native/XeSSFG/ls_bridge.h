#pragma once
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>

// Opt in explicitly with probe_loader --bridge. Call start outside DllMain.
// Hooks must guard recursion, and call afterDispatch AFTER the original Dispatch.
// Compute bypass is a separate opt-in; restart LS to restore its FG history.
namespace lsbridge {
bool start(HMODULE module, bool bypass = false);
bool skipDispatch(ID3D11DeviceContext* context);
// When takeover fails after bypass, copy raw to LS or suppress unsafe presentation.
bool fallbackPresent(IDXGISwapChain* swapchain, UINT flags);
void registerShader(ID3D11ComputeShader* shader, uint64_t hash, SIZE_T size);
void afterDispatch(ID3D11DeviceContext* context, UINT x, UINT y, UINT z);
// True alone authorizes skipping the original Present. Never skip Present(TEST).
bool present(IDXGISwapChain* swapchain, UINT flags);
#ifdef XEL_PROBE_SELFTEST
bool testBypass(ID3D11Device*, ID3D11DeviceContext*, ID3D11ComputeShader*, ID3D11Texture2D*, IDXGISwapChain*);
#endif
}
