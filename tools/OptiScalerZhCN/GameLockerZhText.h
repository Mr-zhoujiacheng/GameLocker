#pragma once

#include <algorithm>
#include <cstring>
#include <iterator>
#include <string_view>

namespace GameLockerZh
{
struct Entry { std::string_view english; const char* chinese; };

inline constexpr Entry labels[] = {
#include "GameLockerZhCatalog.inc"
};

inline const char* Lookup(const char* start, const char* end = nullptr)
{
    if (!start || start == end || !*start || static_cast<unsigned char>(*start) >= 0x80)
        return nullptr;
    const auto key = end ? std::string_view(start, static_cast<size_t>(end - start)) : std::string_view(start);
    const auto it = std::lower_bound(std::begin(labels), std::end(labels), key,
        [](const Entry& entry, std::string_view value) { return entry.english < value; });
    return it != std::end(labels) && it->english == key ? it->chinese : nullptr;
}

// Used only after ImGui has consumed any ID/suffix from the canonical English label.
inline void TranslateSpan(const char*& start, const char*& end)
{
    if (const char* translated = Lookup(start, end))
    {
        start = translated;
        end = translated + std::strlen(translated);
    }
}

inline const char* Format(const char* english)
{
    if (const char* translated = Lookup(english))
        return translated;
    return english;
}
}
