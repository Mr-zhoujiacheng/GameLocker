#pragma once
#include <windows.h>
#include <d3d11.h>
#include <stdint.h>

#ifdef XEL_FG_EXPORTS
#define XEL_API __declspec(dllexport)
#else
#define XEL_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C" {
#endif

typedef struct XelSession XelSession;

/* Windows x64 only. All calls for a session (including destroy) must be made on
   its owning thread (creating thread unless explicitly transferred). Caller must serialize ALL use of the immediate context
   with these calls. No reentrancy. Create retains device/context COM references;
   the HWND must remain valid until destroy. No other swapchain may use this HWND.
   Width/height are fixed, nonzero client pixels. SDR RGBA8 UNORM, 2x only.
   Requires D3D11Device5/Context4, NT shared resources/fences and same-adapter
   D3D12 XeSS-FG support. No injection, hooks, SDK patches or game integration.
   Output is initialized to NULL. Even on create failure, if *output is non-NULL
   caller MUST destroy it: cleanup was deferred (e.g. GPU timeout/SDK teardown
   failure). Never submit that failed-create handle. Requires MSVC x64 runtime. */
XEL_API HRESULT __cdecl xelCreate(ID3D11Device* device, ID3D11DeviceContext* immediate,
    HWND window, uint32_t width, uint32_t height, XelSession** output);

/* Source belongs to the exact caller device: RGBA8 UNORM, matching dimensions,
   one mip/array slice, sample count 1, quality 0, DEFAULT usage, no CPU access.
   Caller finishes recording source rendering before submit. Submit copies without
   changing caller pipeline state and blocks for conservative GPU completion after
   SDK Present; source may be reused on return. Frame time must be finite > 0 ms.
   Zero motion and flat depth are placeholders, NOT engine-quality motion data.
   IDs start at 1, never wrap; recreate when exhausted. Failures after submission
   poison the session: destroy/recreate rather than reuse potentially busy objects.
   XeLL markers measure this bridge, not caller simulation/rendering. */
XEL_API HRESULT __cdecl xelSubmit(XelSession* session, ID3D11Texture2D* source,
    float frameMilliseconds, uint32_t resetHistory);
XEL_API HRESULT __cdecl xelSetEnabled(XelSession* session, uint32_t enabled);
XEL_API HRESULT __cdecl xelResetHistory(XelSession* session);
/* Under exclusive caller serialization, drain GPU work and transfer thread ownership. */
XEL_API HRESULT __cdecl xelAdoptThread(XelSession* session);
/* Deliberately unsupported: returns E_NOTIMPL, destroy/recreate instead. */
XEL_API HRESULT __cdecl xelResize(XelSession* session, uint32_t width, uint32_t height);

#define XEL_STATUS_VALID 1u
#define XEL_STATUS_REQUESTED_ENABLED 2u
#define XEL_STATUS_SDK_ENABLED 4u
#define XEL_STATUS_LAST_INTERPOLATED 8u
#define XEL_STATUS_POISONED 16u

typedef struct XelStats {
    uint64_t submittedFrames;
    uint64_t sdkFramesSent; /* SDK framesPresented: SENT for presentation, not scanout/FPS. */
    uint64_t interpolatedFramesSent; /* Extra frames in successful, SDK-enabled presents only. */
    uint64_t interpolatingPresents;
    uint64_t statusWarnings;
    uint32_t lastPresentId;
    uint32_t lastFramesSent;
    int32_t lastFrameGenerationResult; /* official SDK enum numeric value */
    int32_t lastStatusQueryResult;
    uint32_t flags; /* Requested enabled alone never means interpolation occurred. */
} XelStats;
XEL_API HRESULT __cdecl xelGetStats(XelSession* session, XelStats* output);
/* Releases proxy references BEFORE SDK destroy, then XeLL and owned resources.
   NULL *session is allowed. On failure handle is retained for a destroy retry.
   Successful destroy clears *session. Device-removed cleanup is still attempted. */
XEL_API HRESULT __cdecl xelDestroy(XelSession** session);
/* Thread-local UTF-8 diagnostic; copy before next API call on this thread.
   Available even when create failed. No exceptions cross the C ABI. */
XEL_API const char* __cdecl xelGetLastError(void);

#ifdef __cplusplus
}
#endif
