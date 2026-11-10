cmake_minimum_required(VERSION 3.24)
project(GameLockerXeSSFG LANGUAGES CXX)
if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Windows x64 with MSVC is required")
endif()
set(XeSS_SDK_ROOT "" CACHE PATH "External official Intel XeSS SDK root (required)")
if(NOT IS_ABSOLUTE "${XeSS_SDK_ROOT}")
    message(FATAL_ERROR "Set XeSS_SDK_ROOT to an absolute external SDK path")
endif()
set(required_files
    inc/xess_fg/xefg_swapchain.h inc/xess_fg/xefg_swapchain_d3d12.h
    inc/xell/xell.h inc/xell/xell_d3d12.h
    lib/libxess_fg.lib lib/libxell.lib
    bin/libxess_fg.dll bin/libxell.dll LICENSE.txt third-party-programs.txt inc/MIT.txt)
foreach(file IN LISTS required_files)
    if(NOT EXISTS "${XeSS_SDK_ROOT}/${file}")
        message(FATAL_ERROR "Missing official SDK file: ${XeSS_SDK_ROOT}/${file}")
    endif()
endforeach()
set(FSR4_SDK_ROOT "" CACHE PATH "Optional external FidelityFX upscale API include root")
set(FSR4_RUNTIME_ROOT "" CACHE PATH "Optional external standalone upscaler DLL directory")
option(XEL_BUILD_LS_PROBE "Build opt-in Lossless Scaling diagnostic hooks" OFF)
if(XEL_BUILD_LS_PROBE)
    set(MINHOOK_ROOT "" CACHE PATH "External pinned MinHook source root")
    if(NOT EXISTS "${MINHOOK_ROOT}/include/MinHook.h" OR NOT EXISTS "${MINHOOK_ROOT}/LICENSE.txt")
        message(FATAL_ERROR "Set MINHOOK_ROOT to an external MinHook source tree")
    endif()
    enable_language(C)
    add_subdirectory("${MINHOOK_ROOT}" "${CMAKE_BINARY_DIR}/minhook" EXCLUDE_FROM_ALL)
    add_library(ls_probe SHARED ls_probe.cpp ls_bridge.cpp ls_bridge.h)
    target_compile_features(ls_probe PRIVATE cxx_std_17)
    target_compile_definitions(ls_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    target_compile_options(ls_probe PRIVATE /W4 /EHsc /utf-8)
    target_link_libraries(ls_probe PRIVATE minhook d3d11 dxgi user32)
    add_executable(ls_probe_selftest ls_probe.cpp ls_bridge.cpp ls_bridge.h)
    target_compile_features(ls_probe_selftest PRIVATE cxx_std_17)
    target_compile_definitions(ls_probe_selftest PRIVATE XEL_PROBE_SELFTEST WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    target_compile_options(ls_probe_selftest PRIVATE /W4 /EHsc /utf-8)
    target_link_libraries(ls_probe_selftest PRIVATE minhook d3d11 d3dcompiler dxgi user32)
    add_executable(ls_probe_loader probe_loader.cpp)
    target_compile_features(ls_probe_loader PRIVATE cxx_std_17)
    target_compile_definitions(ls_probe_loader PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    target_compile_options(ls_probe_loader PRIVATE /W4 /EHsc /utf-8)
    add_custom_command(TARGET ls_probe POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${MINHOOK_ROOT}/LICENSE.txt"
            "$<TARGET_FILE_DIR:ls_probe>/MinHook-LICENSE.txt" VERBATIM)
    if(FSR4_SDK_ROOT)
        foreach(file ffx_api.h ffx_api_types.h ffx_upscale.h dx12/ffx_api_dx12.h)
            if(NOT EXISTS "${FSR4_SDK_ROOT}/${file}")
                message(FATAL_ERROR "Missing FSR4 API header: ${file}")
            endif()
        endforeach()
        foreach(target ls_probe ls_probe_selftest)
            target_sources(${target} PRIVATE fsr_sr.cpp fsr_sr.h frame_estimator.h)
            target_include_directories(${target} PRIVATE "${FSR4_SDK_ROOT}")
            target_compile_definitions(${target} PRIVATE XEL_FSR4_SR)
            target_link_libraries(${target} PRIVATE d3d12 d3dcompiler)
        endforeach()
        if(NOT EXISTS "${FSR4_RUNTIME_ROOT}/amd_fidelityfx_upscaler_dx12.dll" OR
           NOT EXISTS "${FSR4_RUNTIME_ROOT}/FidelityFX_v2_LICENSE.md")
            message(FATAL_ERROR "FSR4 standalone runtime and its license are required")
        endif()
        add_custom_command(TARGET ls_probe POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${FSR4_RUNTIME_ROOT}/amd_fidelityfx_upscaler_dx12.dll"
                "${FSR4_RUNTIME_ROOT}/FidelityFX_v2_LICENSE.md" "$<TARGET_FILE_DIR:ls_probe>" VERBATIM)
        install(FILES "${FSR4_RUNTIME_ROOT}/amd_fidelityfx_upscaler_dx12.dll"
            "${FSR4_RUNTIME_ROOT}/FidelityFX_v2_LICENSE.md" DESTINATION .)
    endif()
    install(TARGETS ls_probe ls_probe_loader RUNTIME DESTINATION .)
    install(FILES "${MINHOOK_ROOT}/LICENSE.txt" DESTINATION . RENAME MinHook-LICENSE.txt)
endif()
add_library(xel_fg SHARED xel_fg.cpp xel_fg.h)
target_compile_features(xel_fg PRIVATE cxx_std_17)
target_compile_definitions(xel_fg PRIVATE XEL_FG_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(xel_fg PRIVATE "${XeSS_SDK_ROOT}/inc")
target_link_libraries(xel_fg PRIVATE d3d11 d3d12 d3dcompiler dxgi
    "${XeSS_SDK_ROOT}/lib/libxess_fg.lib" "${XeSS_SDK_ROOT}/lib/libxell.lib")
add_executable(xel_smoke smoke.cpp)
target_compile_features(xel_smoke PRIVATE cxx_std_17)
target_compile_definitions(xel_smoke PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_link_libraries(xel_smoke PRIVATE xel_fg d3d11 dxgi user32)
foreach(target xel_fg xel_smoke)
    target_compile_options(${target} PRIVATE /W4 /EHsc /utf-8)
    add_custom_command(TARGET ${target} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${XeSS_SDK_ROOT}/bin/libxess_fg.dll" "${XeSS_SDK_ROOT}/bin/libxell.dll"
            "$<TARGET_FILE_DIR:${target}>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${XeSS_SDK_ROOT}/LICENSE.txt"
            "$<TARGET_FILE_DIR:${target}>/Intel-XeSS-LICENSE.txt"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${XeSS_SDK_ROOT}/third-party-programs.txt"
            "$<TARGET_FILE_DIR:${target}>/Intel-third-party-programs.txt"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${XeSS_SDK_ROOT}/inc/MIT.txt"
            "$<TARGET_FILE_DIR:${target}>/Intel-headers-MIT.txt"
        VERBATIM)
endforeach()
install(TARGETS xel_fg RUNTIME DESTINATION .)
install(FILES "${XeSS_SDK_ROOT}/bin/libxess_fg.dll" "${XeSS_SDK_ROOT}/bin/libxell.dll" DESTINATION .)
install(FILES "${XeSS_SDK_ROOT}/LICENSE.txt" DESTINATION . RENAME Intel-XeSS-LICENSE.txt)
install(FILES "${XeSS_SDK_ROOT}/third-party-programs.txt" DESTINATION . RENAME Intel-third-party-programs.txt)
install(FILES "${XeSS_SDK_ROOT}/inc/MIT.txt" DESTINATION . RENAME Intel-headers-MIT.txt)

set(FSR_SDK_ROOT "" CACHE PATH "Optional external official FidelityFX SDK v1.1.4 root")
if(FSR_SDK_ROOT)
    if(NOT IS_ABSOLUTE "${FSR_SDK_ROOT}")
        message(FATAL_ERROR "FSR_SDK_ROOT must be an absolute external SDK path")
    endif()
    foreach(file ffx-api/include/ffx_api/ffx_framegeneration.h
            PrebuiltSignedDLL/amd_fidelityfx_dx12.dll LICENSE.txt)
        if(NOT EXISTS "${FSR_SDK_ROOT}/${file}")
            message(FATAL_ERROR "Missing official FSR SDK file: ${FSR_SDK_ROOT}/${file}")
        endif()
    endforeach()
    add_library(fsr_fg SHARED fsr_fg.cpp xel_fg.h)
    target_compile_features(fsr_fg PRIVATE cxx_std_17)
    target_compile_definitions(fsr_fg PRIVATE XEL_FG_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(fsr_fg PRIVATE /W4 /EHsc /utf-8)
    target_include_directories(fsr_fg PRIVATE "${FSR_SDK_ROOT}/ffx-api/include")
    target_link_libraries(fsr_fg PRIVATE d3d11 d3d12 d3dcompiler dxgi user32)
    add_custom_command(TARGET fsr_fg POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${FSR_SDK_ROOT}/PrebuiltSignedDLL/amd_fidelityfx_dx12.dll" "$<TARGET_FILE_DIR:fsr_fg>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${FSR_SDK_ROOT}/LICENSE.txt"
            "$<TARGET_FILE_DIR:fsr_fg>/AMD-FidelityFX-MIT.txt" VERBATIM)
    add_executable(fsr_smoke smoke.cpp)
    target_compile_features(fsr_smoke PRIVATE cxx_std_17)
    target_compile_definitions(fsr_smoke PRIVATE FSR_SMOKE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    target_compile_options(fsr_smoke PRIVATE /W4 /EHsc /utf-8)
    target_link_libraries(fsr_smoke PRIVATE fsr_fg d3d11 dxgi user32)
    install(TARGETS fsr_fg RUNTIME DESTINATION .)
    install(FILES "${FSR_SDK_ROOT}/PrebuiltSignedDLL/amd_fidelityfx_dx12.dll" DESTINATION .)
    install(FILES "${FSR_SDK_ROOT}/LICENSE.txt" DESTINATION . RENAME AMD-FidelityFX-MIT.txt)
endif()
