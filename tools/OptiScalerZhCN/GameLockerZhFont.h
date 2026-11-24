#pragma once

// Merged separately into every base font, including the non-HQ font.
static void AddGameLockerZhFont(ImFontAtlas* atlas, ImFont* base, float size)
{
    if (!base)
        return;
    HMODULE module = nullptr;
    const auto address = reinterpret_cast<LPCWSTR>(reinterpret_cast<ULONG_PTR>(&AddGameLockerZhFont));
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            address, &module))
        return;

    const HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(4381), MAKEINTRESOURCEW(10)); // RT_RCDATA
    const HGLOBAL loaded = resource ? LoadResource(module, resource) : nullptr;
    const DWORD length = resource ? SizeofResource(module, resource) : 0;
    void* data = loaded ? LockResource(loaded) : nullptr;
    if (!data || !length)
        return;
    ImFontConfig config;
    config.FontDataOwnedByAtlas = false; // Windows keeps the resource mapped as long as the module is loaded.
    config.MergeMode = true;
    config.DstFont = base;
    // Keep the original Latin, metric numbers, and symbols preferred; the atlas will load CJK on demand.
    atlas->AddFontFromMemoryTTF(data, static_cast<int>(length), size, &config);
}
