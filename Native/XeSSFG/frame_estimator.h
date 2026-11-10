#pragma once

#include <d3d12.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstring>

// GPU-only screen-space approximation, not monocular/physical depth recovery.
// Caller owns submission and must drain before the next record, reinitialization,
// or destruction. Every successful record must be executed, not discarded.
// Changes the D3D12 compute root signature, PSO and descriptor heap; never D3D11.
class FrameEstimator {
public:
    FrameEstimator() = default;
    FrameEstimator(const FrameEstimator&) = delete;
    FrameEstimator& operator=(const FrameEstimator&) = delete;

    HRESULT initialize(ID3D12Device* device, UINT width, UINT height) {
        if (!device || !width || !height ||
            width > D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
            height > D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION) return E_INVALIDARG;
        if (device_) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);

        HRESULT hr = checkFormat(device, DXGI_FORMAT_R16G16_FLOAT, true);
        if (FAILED(hr)) return hr;
        hr = checkFormat(device, DXGI_FORMAT_R32_FLOAT, true);
        if (FAILED(hr)) return hr;

        ComPtr<ID3D12DescriptorHeap> descriptors;
        D3D12_DESCRIPTOR_HEAP_DESC hd = {};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = 6;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        hr = device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&descriptors));
        if (FAILED(hr)) return hr;
        ComPtr<ID3D12DescriptorHeap> clearDescriptors;
        hd.NumDescriptors = 2;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        hr = device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&clearDescriptors));
        if (FAILED(hr)) return hr;

        D3D12_DESCRIPTOR_RANGE ranges[2] = {};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        ranges[0].NumDescriptors = 3;
        ranges[0].OffsetInDescriptorsFromTableStart = 0;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        ranges[1].NumDescriptors = 3;
        ranges[1].OffsetInDescriptorsFromTableStart = 3;
        D3D12_ROOT_PARAMETER parameters[2] = {};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable.NumDescriptorRanges = 2;
        parameters[0].DescriptorTable.pDescriptorRanges = ranges;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants.Num32BitValues = 4;
        D3D12_ROOT_SIGNATURE_DESC rd = {};
        rd.NumParameters = 2;
        rd.pParameters = parameters;
        ComPtr<ID3DBlob> signature, errors;
        hr = D3D12SerializeRootSignature(&rd, D3D_ROOT_SIGNATURE_VERSION_1,
            &signature, &errors);
        if (FAILED(hr)) return hr;
        ComPtr<ID3D12RootSignature> root;
        hr = device->CreateRootSignature(0, signature->GetBufferPointer(),
            signature->GetBufferSize(), IID_PPV_ARGS(&root));
        if (FAILED(hr)) return hr;
        ComPtr<ID3D12PipelineState> match, resolve;
        hr = createPipeline(device, root.Get(), "Match", match);
        if (FAILED(hr)) return hr;
        hr = createPipeline(device, root.Get(), "Resolve", resolve);
        if (FAILED(hr)) return hr;
        const UINT flowWidth = (width + 7) / 8, flowHeight = (height + 7) / 8;
        ComPtr<ID3D12Resource> flow;
        hr = createTexture(device, flowWidth, flowHeight, DXGI_FORMAT_R16G16_FLOAT,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, flow);
        if (FAILED(hr)) return hr;

        device_ = device;
        descriptors_ = descriptors;
        clearDescriptors_ = clearDescriptors;
        root_ = root;
        match_ = match;
        resolve_ = resolve;
        flow_ = flow;
        width_ = width;
        height_ = height;
        flowWidth_ = flowWidth;
        flowHeight_ = flowHeight;
        stride_ = device->GetDescriptorHandleIncrementSize(hd.Type);
        return S_OK;
    }

    // Entry/exit states: color COMMON; motion/depth NON_PIXEL_SHADER_RESOURCE.
    // Inputs are fixed for this session. motion/depth must allow UAV writes and
    // be R16G16_FLOAT/R32_FLOAT, single mip/slice/sample at initialized dimensions.
    // Motion is current -> previous in full-resolution pixels (not normalized).
    HRESULT record(ID3D12GraphicsCommandList* commands, ID3D12Resource* color,
        ID3D12Resource* motion, ID3D12Resource* depth, bool enabled, bool reset) {
        if (!device_) return CO_E_NOTINITIALIZED;
        if (!commands || !color || !motion || !depth ||
            color == motion || color == depth || motion == depth) return E_INVALIDARG;
        if (commands->GetType() != D3D12_COMMAND_LIST_TYPE_DIRECT &&
            commands->GetType() != D3D12_COMMAND_LIST_TYPE_COMPUTE) return E_INVALIDARG;
        ComPtr<ID3D12Device> owner;
        HRESULT hr = commands->GetDevice(IID_PPV_ARGS(&owner));
        if (FAILED(hr)) return hr;
        if (owner.Get() != device_.Get()) return E_INVALIDARG;
        if (color_) {
            if (color != color_.Get() || motion != motion_.Get() || depth != depth_.Get())
                return E_INVALIDARG;
        } else {
            hr = bindInputs(color, motion, depth);
            if (FAILED(hr)) return hr;
        }

        const bool changed = !modeKnown_ || enabled != enabled_;
        if (!enabled && !changed) {
            history_ = false;
            return S_OK;
        }
        const bool seed = !history_ || reset || changed;
        ID3D12DescriptorHeap* heaps[] = {descriptors_.Get()};
        commands->SetDescriptorHeaps(1, heaps);
        D3D12_RESOURCE_BARRIER outputs[] = {
            transition(motion, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS),
            transition(depth, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
        commands->ResourceBarrier(2, outputs);
        if (!enabled || seed) {
            const float zero[4] = {};
            const float one[4] = {1, 1, 1, 1};
            commands->ClearUnorderedAccessViewFloat(gpu(4), clearCpu(0), motion, zero, 0, nullptr);
            commands->ClearUnorderedAccessViewFloat(gpu(5), clearCpu(1), depth, one, 0, nullptr);
        } else {
            D3D12_RESOURCE_BARRIER inputs[] = {
                transition(color, D3D12_RESOURCE_STATE_COMMON,
                    D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),
                transition(flow_.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS)};
            commands->ResourceBarrier(2, inputs);
            commands->SetComputeRootSignature(root_.Get());
            commands->SetComputeRootDescriptorTable(0, gpu(0));
            const UINT constants[] = {width_, height_, flowWidth_, flowHeight_};
            commands->SetComputeRoot32BitConstants(1, 4, constants, 0);
            commands->SetPipelineState(match_.Get());
            commands->Dispatch((flowWidth_ + 7) / 8, (flowHeight_ + 7) / 8, 1);
            auto ordered = uav(flow_.Get());
            commands->ResourceBarrier(1, &ordered);
            auto readable = transition(flow_.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            commands->ResourceBarrier(1, &readable);
            commands->SetPipelineState(resolve_.Get());
            commands->Dispatch((width_ + 7) / 8, (height_ + 7) / 8, 1);
        }
        D3D12_RESOURCE_BARRIER orderedOutputs[] = {uav(motion), uav(depth)};
        commands->ResourceBarrier(2, orderedOutputs);
        for (auto& b : outputs) {
            b.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            b.Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        }
        commands->ResourceBarrier(2, outputs);
        if (enabled) {
            const auto colorState = seed ? D3D12_RESOURCE_STATE_COMMON :
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
            D3D12_RESOURCE_BARRIER copying[] = {
                transition(color, colorState, D3D12_RESOURCE_STATE_COPY_SOURCE),
                transition(previous_.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                    D3D12_RESOURCE_STATE_COPY_DEST)};
            commands->ResourceBarrier(2, copying);
            commands->CopyResource(previous_.Get(), color);
            copying[0] = transition(color, D3D12_RESOURCE_STATE_COPY_SOURCE,
                D3D12_RESOURCE_STATE_COMMON);
            copying[1] = transition(previous_.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            commands->ResourceBarrier(2, copying);
        }
        history_ = enabled;
        enabled_ = enabled;
        modeKnown_ = true;
        return S_OK;
    }

private:
    template<class T> using ComPtr = Microsoft::WRL::ComPtr<T>;
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12DescriptorHeap> descriptors_, clearDescriptors_;
    ComPtr<ID3D12RootSignature> root_;
    ComPtr<ID3D12PipelineState> match_, resolve_;
    ComPtr<ID3D12Resource> previous_, flow_, color_, motion_, depth_;
    UINT width_ = 0, height_ = 0, flowWidth_ = 0, flowHeight_ = 0, stride_ = 0;
    bool modeKnown_ = false, enabled_ = false, history_ = false;

    D3D12_CPU_DESCRIPTOR_HANDLE cpu(UINT index) const {
        auto h = descriptors_->GetCPUDescriptorHandleForHeapStart();
        h.ptr += SIZE_T(index) * stride_;
        return h;
    }
    D3D12_CPU_DESCRIPTOR_HANDLE clearCpu(UINT index) const {
        auto h = clearDescriptors_->GetCPUDescriptorHandleForHeapStart();
        h.ptr += SIZE_T(index) * stride_;
        return h;
    }
    D3D12_GPU_DESCRIPTOR_HANDLE gpu(UINT index) const {
        auto h = descriptors_->GetGPUDescriptorHandleForHeapStart();
        h.ptr += UINT64(index) * stride_;
        return h;
    }
    static D3D12_RESOURCE_BARRIER transition(ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
        return b;
    }
    static D3D12_RESOURCE_BARRIER uav(ID3D12Resource* resource) {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        b.UAV.pResource = resource;
        return b;
    }
    static HRESULT checkFormat(ID3D12Device* device, DXGI_FORMAT format, bool writable) {
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support = {};
        support.Format = format;
        HRESULT hr = device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT,
            &support, sizeof(support));
        if (FAILED(hr)) return hr;
        if (!(support.Support1 & D3D12_FORMAT_SUPPORT1_TEXTURE2D) ||
            !(support.Support1 & D3D12_FORMAT_SUPPORT1_SHADER_LOAD)) return DXGI_ERROR_UNSUPPORTED;
        // No UAV typed loads are used, including for R16G16_FLOAT flow.
        if (writable && (!(support.Support1 & D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW) ||
            !(support.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE))) return DXGI_ERROR_UNSUPPORTED;
        return S_OK;
    }
    static HRESULT createTexture(ID3D12Device* device, UINT width, UINT height,
        DXGI_FORMAT format, D3D12_RESOURCE_FLAGS flags, ComPtr<ID3D12Resource>& out) {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width;
        desc.Height = height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Flags = flags;
        return device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, nullptr, IID_PPV_ARGS(&out));
    }
    HRESULT bindInputs(ID3D12Resource* color, ID3D12Resource* motion, ID3D12Resource* depth) {
        ID3D12Resource* resources[] = {color, motion, depth};
        for (UINT i = 0; i < 3; ++i) {
            ComPtr<ID3D12Device> owner;
            HRESULT hr = resources[i]->GetDevice(IID_PPV_ARGS(&owner));
            if (FAILED(hr)) return hr;
            if (owner.Get() != device_.Get()) return E_INVALIDARG;
            const auto d = resources[i]->GetDesc();
            if (d.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
                d.Width != width_ || d.Height != height_ || d.MipLevels != 1 ||
                d.DepthOrArraySize != 1 || d.SampleDesc.Count != 1 || d.SampleDesc.Quality != 0 ||
                (d.Flags & D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE)) return E_INVALIDARG;
            if (i != 0 && !(d.Flags & D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)) return E_INVALIDARG;
        }
        if (motion->GetDesc().Format != DXGI_FORMAT_R16G16_FLOAT ||
            depth->GetDesc().Format != DXGI_FORMAT_R32_FLOAT) return E_INVALIDARG;
        const auto format = color->GetDesc().Format;
        // These typed formats cover the existing bridge and common HDR bridges.
        // Typeless, depth, integer and planar formats cannot be bound as float4 here.
        if (format != DXGI_FORMAT_R8G8B8A8_UNORM && format != DXGI_FORMAT_B8G8R8A8_UNORM &&
            format != DXGI_FORMAT_R8G8B8A8_UNORM_SRGB && format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB &&
            format != DXGI_FORMAT_R10G10B10A2_UNORM && format != DXGI_FORMAT_R16G16B16A16_FLOAT)
            return DXGI_ERROR_UNSUPPORTED;
        HRESULT hr = checkFormat(device_.Get(), format, false);
        if (FAILED(hr)) return hr;
        ComPtr<ID3D12Resource> previous;
        hr = createTexture(device_.Get(), width_, height_, format, D3D12_RESOURCE_FLAG_NONE, previous);
        if (FAILED(hr)) return hr;
        ID3D12Resource* srvs[] = {color, previous.Get(), flow_.Get()};
        for (UINT i = 0; i < 3; ++i) {
            D3D12_SHADER_RESOURCE_VIEW_DESC view = {};
            view.Format = srvs[i]->GetDesc().Format;
            view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            view.Texture2D.MipLevels = 1;
            device_->CreateShaderResourceView(srvs[i], &view, cpu(i));
        }
        ID3D12Resource* uavs[] = {flow_.Get(), motion, depth};
        for (UINT i = 0; i < 3; ++i) {
            D3D12_UNORDERED_ACCESS_VIEW_DESC view = {};
            view.Format = uavs[i]->GetDesc().Format;
            view.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            device_->CreateUnorderedAccessView(uavs[i], nullptr, &view, cpu(i + 3));
            if (i != 0)
                device_->CreateUnorderedAccessView(uavs[i], nullptr, &view, clearCpu(i - 1));
        }
        previous_ = previous;
        color_ = color;
        motion_ = motion;
        depth_ = depth;
        return S_OK;
    }
    static HRESULT createPipeline(ID3D12Device* device, ID3D12RootSignature* root,
        const char* entry, ComPtr<ID3D12PipelineState>& out) {
        const char* source = shader();
        ComPtr<ID3DBlob> code, errors;
        HRESULT hr = D3DCompile(source, std::strlen(source), "frame_estimator.h", nullptr,
            nullptr, entry, "cs_5_0", D3DCOMPILE_ENABLE_STRICTNESS |
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (FAILED(hr)) return hr;
        D3D12_COMPUTE_PIPELINE_STATE_DESC desc = {};
        desc.pRootSignature = root;
        desc.CS = {code->GetBufferPointer(), code->GetBufferSize()};
        return device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&out));
    }
    static const char* shader() {
        return R"HLSL(
cbuffer Size : register(b0) { uint Width, Height, FlowWidth, FlowHeight; };
Texture2D<float4> Current : register(t0);
Texture2D<float4> Previous : register(t1);
Texture2D<float2> Flow : register(t2);
RWTexture2D<float2> FlowOut : register(u0);
RWTexture2D<float2> MotionOut : register(u1);
RWTexture2D<float> DepthOut : register(u2);

float Luma(float3 rgb) { return dot(rgb, float3(0.299, 0.587, 0.114)); }
int2 ClampPixel(int2 p) { return clamp(p, int2(0, 0), int2(Width - 1, Height - 1)); }

float PatchError(int2 center, int2 delta) {
    int2 target = center + delta;
    if (any(target < 0) || any(target >= int2(Width, Height))) return 1e6;
    float error = 0;
    [unroll] for (int y = -3; y <= 3; y += 3) {
        [unroll] for (int x = -3; x <= 3; x += 3) {
            float3 a = Current.Load(int3(ClampPixel(center + int2(x, y)), 0)).rgb;
            float3 b = Previous.Load(int3(ClampPixel(target + int2(x, y)), 0)).rgb;
            float3 difference = abs(a - b);
            error += dot(difference, float3(0.2, 0.2, 0.2)) + 0.4 * abs(Luma(a) - Luma(b));
        }
    }
    return error / 9.0;
}
float Cost(int2 center, int2 delta) {
    return PatchError(center, delta) + 0.00002 * length(float2(delta));
}
void Consider(int2 center, int2 candidate, inout float bestCost, inout int2 best) {
    float cost = Cost(center, candidate);
    if (cost < bestCost) { bestCost = cost; best = candidate; }
}

[numthreads(8, 8, 1)]
void Match(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= FlowWidth || tid.y >= FlowHeight) return;
    int2 center = ClampPixel(int2(tid.xy * 8) + int2(3, 3));
    int2 best = int2(0, 0);
    float zeroError = PatchError(center, best);
    float bestCost = zeroError;
    // Exhaustive local search avoids coarse-grid aliasing on fine textures.
    [loop] for (int y = -8; y <= 8; ++y)
        [loop] for (int x = -8; x <= 8; ++x)
            Consider(center, int2(x, y), bestCost, best);
    [loop] for (int y = -24; y <= 24; y += 8)
        [loop] for (int x = -24; x <= 24; x += 8)
            Consider(center, int2(x, y), bestCost, best);
    int2 coarse = best;
    [loop] for (int y = -6; y <= 6; y += 2)
        [loop] for (int x = -6; x <= 6; x += 2)
            Consider(center, coarse + int2(x, y), bestCost, best);
    int2 refined = best;
    [unroll] for (int y = -1; y <= 1; ++y)
        [unroll] for (int x = -1; x <= 1; ++x)
            Consider(center, refined + int2(x, y), bestCost, best);

    float mean = 0, square = 0;
    [unroll] for (int y = -3; y <= 3; y += 3) {
        [unroll] for (int x = -3; x <= 3; x += 3) {
            float value = Luma(Current.Load(int3(ClampPixel(center + int2(x, y)), 0)).rgb);
            mean += value; square += value * value;
        }
    }
    float variance = max(0.0, square / 9.0 - (mean / 9.0) * (mean / 9.0));
    float residual = PatchError(center, best);
    // Reject flat, inconsistent or non-improving matches instead of inventing flow.
    bool confident = variance > 0.00005 && residual < 0.055 &&
        zeroError - residual > max(0.0003, zeroError * 0.08);
    FlowOut[tid.xy] = confident ? float2(best) : float2(0, 0);
}

float2 ReadFlow(int2 p) {
    return Flow.Load(int3(clamp(p, int2(0, 0), int2(FlowWidth - 1, FlowHeight - 1)), 0));
}
[numthreads(8, 8, 1)]
void Resolve(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= Width || tid.y >= Height) return;
    // Block centers lie at 3 + 8*n in integer pixel coordinates.
    float2 grid = (float2(tid.xy) - 3.0) / 8.0;
    int2 base = int2(floor(grid));
    float2 weight = frac(grid);
    float2 motion = lerp(lerp(ReadFlow(base), ReadFlow(base + int2(1, 0)), weight.x),
        lerp(ReadFlow(base + int2(0, 1)), ReadFlow(base + int2(1, 1)), weight.x), weight.y);
    MotionOut[tid.xy] = motion;
    float speed = length(motion);
    // Local relative pseudo-depth only. No camera projection or true scene depth.
    // Standard (not reversed) perspective Z, near=0.1, far=1000.
    float distance = lerp(1000.0, 5.0, saturate(speed / 40.0));
    float deviceZ = 1000.0 / 999.9 - (1000.0 * 0.1 / 999.9) / distance;
    DepthOut[tid.xy] = speed < 0.001 ? 1.0 : clamp(deviceZ, 0.95, 0.9999);
}
)HLSL";
    }
};
