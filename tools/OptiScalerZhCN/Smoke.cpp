#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include "imgui.h"
#include "imgui_internal.h"
#include "GameLockerZhText.h"
#include "MenuPages.h"

static void Require(bool condition, const char* message)
{
    if (!condition)
    {
        std::fprintf(stderr, "%s\n", message);
        std::exit(1);
    }
}

int wmain(int argc, wchar_t** argv)
{
    Require(argc == 2, "Usage: Smoke.exe <localized-dxgi.dll>");
    // Map resources without executing DLL initialization or loading its imports.
    const HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    Require(module != nullptr, "Unable to map localized DLL resources");
    const HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(4381), MAKEINTRESOURCEW(10));
    Require(resource != nullptr, "Embedded CJK font is absent");
    const HGLOBAL handle = LoadResource(module, resource);
    void* bytes = handle ? LockResource(handle) : nullptr;
    Require(bytes != nullptr, "Unable to map the embedded CJK font");

    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.DisplaySize = ImVec2(800, 600);
    io.BackendFlags |= ImGuiBackendFlags_RendererHasTextures;
    ImFont* base = io.Fonts->AddFontDefault();
    ImFontConfig config;
    config.MergeMode = true;
    config.DstFont = base;
    config.FontDataOwnedByAtlas = false;
    Require(io.Fonts->AddFontFromMemoryTTF(bytes, static_cast<int>(SizeofResource(module, resource)),
                                          base->DefaultSize, &config) == base,
            "CJK font did not merge into the base font");
    for (float size : {base->DefaultSize, base->DefaultSize * 1.5f})
    {
        ImFontBaked* baked = base->GetFontBaked(size);
        Require(baked->FindGlyphNoFallback('R') != nullptr, "Merged font lost original Latin glyphs");
        for (const GameLockerZh::Entry& entry : GameLockerZh::labels)
        {
            for (const char* cursor = entry.chinese; *cursor;)
            {
                unsigned int cp = 0;
                cursor += ImTextCharFromUtf8(&cp, cursor, nullptr);
                Require(cp < 128 || baked->FindGlyphNoFallback(static_cast<ImWchar>(cp)) != nullptr,
                        "Missing baked CJK glyph");
            }
        }
    }

    ImGui::NewFrame();
    ImGui::SetNextWindowSize({640, 480});
    Require(ImGui::Begin("Native menu localization probe"), "Unable to open ImGui window");
    ImGui::LogToBuffer();

    const char* text = GameLockerZh::Lookup("Save Settings");
    Require(text && ImGui::CalcTextSize("Save Settings##original", nullptr, true).x == ImGui::CalcTextSize(text).x,
            "Localized button is not measured in Chinese");
    const ImGuiID originalId = ImGui::GetID("Save Settings##original");
    Require(originalId != ImGui::GetID("Save Settings##different"), "Suffix no longer separates IDs");
    auto* draw = ImGui::GetWindowDrawList();
    const auto sameUvs = [draw](int first, int middle, int last)
    {
        if (middle - first <= 0 || middle - first != last - middle)
            return false;
        for (int i = 0; i < middle - first; ++i)
            if (draw->VtxBuffer[first + i].uv.x != draw->VtxBuffer[middle + i].uv.x ||
                draw->VtxBuffer[first + i].uv.y != draw->VtxBuffer[middle + i].uv.y)
                return false;
        return true;
    };
    const int buttonStart = draw->VtxBuffer.Size;
    ImGui::Button("Save Settings##original");
    Require(ImGui::GetItemID() == originalId, "Drawn button changed its original ID");
    const int buttonMiddle = draw->VtxBuffer.Size;
    ImGui::Button(text);
    Require(sameUvs(buttonStart, buttonMiddle, draw->VtxBuffer.Size), "Button glyphs differ from literal Chinese");

    const char* navigation = MenuPages::Label(MenuPages::Page::FrameGeneration);
    Require(std::strcmp(MenuPages::Name(MenuPages::Page::FrameGeneration), "framegen") == 0,
            "Native menu changed its saved page name");
    const auto navId = ImGui::GetID(navigation);
    constexpr ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags_Leaf | ImGuiTreeNodeFlags_NoTreePushOnOpen;
    const int navStart = draw->VtxBuffer.Size;
    ImGui::TreeNodeEx(navigation, flags);
    Require(ImGui::GetItemID() == navId, "Drawn navigation changed its original ID");
    const int navMiddle = draw->VtxBuffer.Size;
    ImGui::TreeNodeEx(GameLockerZh::Lookup(navigation), flags);
    Require(sameUvs(navStart, navMiddle, draw->VtxBuffer.Size), "Navigation glyphs differ from literal Chinese");
    ImGui::Text("Input latency: %s%.0f ms", "~", 24.8);
    ImGui::TextUnformatted("A foreign label not in the catalog");

    char translatedFormat[100] {};
    std::snprintf(translatedFormat, sizeof(translatedFormat), GameLockerZh::Format("Input latency: %s%.0f ms"), "~", 24.8);
    const char* log = ImGui::GetCurrentContext()->LogBuffer.c_str();
    Require(std::strstr(log, translatedFormat) != nullptr && std::strstr(translatedFormat, "~25 ms") != nullptr,
            "Formatted display text did not retain its original values");
    Require(std::strstr(log, "A foreign label not in the catalog") != nullptr, "Unknown text did not fall back unchanged");
    ImGui::LogFinish();
    ImGui::End();
    ImGui::Render();
    Require(ImGui::GetDrawData()->TotalVtxCount > 0, "Native ImGui draw data is empty");
    ImGui::DestroyContext();
    FreeLibrary(module);
    std::puts("PASS: native embedded CJK glyphs at two sizes, Chinese draws, stable labels/IDs, formatted values, unknown-label fallback");
}
