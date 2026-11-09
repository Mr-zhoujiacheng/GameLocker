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
    install(TARGETS ls_probe ls_probe_loader RUNTIME DESTINATION .)
    install(FILES "${MINHOOK_ROOT}/LICENSE.txt" DESTINATION . RENAME MinHook-LICENSE.txt)
endif()
add_library(xel_fg SHARED xel_fg.cpp xel_fg.h)
target_compile_features(xel_fg PRIVATE cxx_std_17)
target_compile_definitions(xel_fg PRIVATE XEL_FG_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_include_directories(xel_fg PRIVATE "${XeSS_SDK_ROOT}/inc")
target_link_libraries(xel_fg PRIVATE d3d11 d3d12 dxgi
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
