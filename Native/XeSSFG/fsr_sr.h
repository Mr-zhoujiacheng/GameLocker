#pragma once
#include <d3d11.h>

// Single immediate-context render thread. Experimental estimated motion/depth,
// unjittered input; this is not equivalent to engine-integrated temporal SR.
namespace fsrsr {
    bool run(ID3D11DeviceContext* context, ID3D11ShaderResourceView* input,
        ID3D11Texture2D* output);
    const char* status();
}
