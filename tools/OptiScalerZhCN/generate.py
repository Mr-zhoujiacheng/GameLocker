#!/usr/bin/env python3
"""Generate the native localization additions in a dedicated, pinned checkout."""
import argparse
import hashlib
import importlib.metadata
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
FONT_SHA256 = "a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da"
FORMAT = re.compile(r"%%|%(?:[-+ #0']*\d*|[-+ #0']*\*)(?:\.(?:\d*|\*))?(?:hh|ll|I64|[hljztL])?[diouxXfFeEgGaAcspn]")


def quoted(value):
    return '"' + ''.join(chr(i) if 32 <= i < 127 and i not in (34, 92) else f'\\{i:03o}'
                         for i in value.encode('utf-8')) + '"'


def conversions(value):
    # Unmatched percent characters are allowed in ordinary rendered labels, not new printf slots.
    return tuple(m.group() for m in FORMAT.finditer(value))


def catalog():
    entries = {}
    for number, line in enumerate((HERE / 'zh-CN.tsv').read_text(encoding='utf-8').splitlines(), 1):
        if not line or line.startswith('#'):
            continue
        original, translation = (s.replace(r'\n', '\n') for s in line.split('\t'))
        if not original or not translation or original in entries:
            raise ValueError(f'invalid or duplicate entry at catalog line {number}: {original!r}')
        if '##' in original or '##' in translation or conversions(original) != conversions(translation):
            raise ValueError(f'ID suffix or printf mismatch at catalog line {number}: {original!r}')
        entries[original] = translation
    return entries


def font_subset(font_file, output, entries):
    if hashlib.sha256(font_file.read_bytes()).hexdigest() != FONT_SHA256:
        raise ValueError('Noto Sans SC original does not match the pinned SHA-256')
    if importlib.metadata.version('fonttools') != '4.59.2':
        raise ValueError('fonttools==4.59.2 is required')
    from fontTools import subset
    from fontTools.ttLib import TTFont
    from fontTools.varLib.instancer import instantiateVariableFont

    needed = {ord(c) for text in entries.values() for c in text if ord(c) >= 128}
    options = subset.Options()
    options.name_IDs = ['*']
    options.name_languages = ['*']
    font = TTFont(font_file, recalcTimestamp=False)
    missing = needed - font.getBestCmap().keys()
    if missing:
        raise ValueError(f'font missing glyphs: {"".join(chr(c) for c in sorted(missing))}')
    instantiateVariableFont(font, {'wght': 400}, inplace=True)
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(unicodes=needed)
    subsetter.subset(font)
    if needed - font.getBestCmap().keys():
        raise ValueError('subsetting lost required characters')
    # Keep credit/license records, but do not use the original family name for the modified font.
    names = {1: 'GameLocker SC', 2: 'Regular', 3: 'GameLockerSC-Regular-1', 4: 'GameLocker SC Regular',
             6: 'GameLockerSC-Regular', 16: 'GameLocker SC', 17: 'Regular', 21: 'GameLocker SC', 22: 'Regular'}
    for record in font['name'].names:
        if record.nameID in names:
            record.string = names[record.nameID]
    font.save(output)
    return len(needed)


def read_source(path):
    content = path.read_bytes()
    enc = 'utf-16-le' if content.startswith(b'\xff\xfe') else 'utf-8'
    text = content.decode(enc)
    newline = '\r\n' if '\r\n' in text else '\n'
    return text.replace('\r\n', '\n'), enc, newline


def rewrite(path, pairs):
    text, enc, newline = read_source(path)
    for before, after in pairs:
        if text.count(before) != 1:
            raise ValueError(f'{path}: expected one exact anchor {before!r}')
        text = text.replace(before, after)
    path.write_bytes(text.replace('\n', newline).encode(enc))


def install(root, font_file):
    opti = root / 'OptiScaler'
    imgui = opti / 'include/imgui'
    menu = opti / 'menu'
    entries = catalog()
    glyphs = font_subset(font_file, menu / 'GameLockerZhSC.ttf', entries)
    (imgui / 'GameLockerZhCatalog.inc').write_text(''.join(
        f'    {{ {quoted(source)}, {quoted(target)} }},\n' for source, target in sorted(entries.items())),
        encoding='ascii', newline='\n')
    (imgui / 'GameLockerZhText.h').write_bytes((HERE / 'GameLockerZhText.h').read_bytes())
    (menu / 'GameLockerZhFont.h').write_bytes((HERE / 'GameLockerZhFont.h').read_bytes())
    rewrite(imgui / 'imgui.cpp', [
        ('#include "imgui.h"\n', '#include "imgui.h"\n#include "GameLockerZhText.h"\n'),
        ('    if (text != text_display_end)\n',
         '    GameLockerZh::TranslateSpan(text, text_display_end);\n    if (text != text_display_end)\n'),
        ('    // Perform CPU side clipping for single clipped element',
         '    GameLockerZh::TranslateSpan(text, text_display_end);\n    // Perform CPU side clipping for single clipped element'),
        ('    const ImVec2 text_size = text_size_if_known ? *text_size_if_known : CalcTextSize(text, text_end_full, false, 0.0f);',
         '    GameLockerZh::TranslateSpan(text, text_end_full);\n'
         '    const ImVec2 text_size = text_size_if_known ? *text_size_if_known : CalcTextSize(text, text_end_full, false, 0.0f);'),
        ('        text_display_end = text_end;\n\n    ImFont* font = g.Font;',
         '        text_display_end = text_end;\n\n    GameLockerZh::TranslateSpan(text, text_display_end);\n    ImFont* font = g.Font;'),
    ])
    rewrite(imgui / 'imgui_widgets.cpp', [
        ('#include "imgui.h"\n', '#include "imgui.h"\n#include "GameLockerZhText.h"\n'),
        ('    // Calculate length\n    const char* text_begin = text;',
         '    GameLockerZh::TranslateSpan(text, text_end);\n    // Calculate length\n    const char* text_begin = text;'),
        ('    ImFormatStringToTempBufferV(&text, &text_end, fmt, args);\n    TextEx(text, text_end, ImGuiTextFlags_NoWidthForLargeClippedText);',
         '    ImFormatStringToTempBufferV(&text, &text_end, GameLockerZh::Format(fmt), args);\n'
         '    TextEx(text, text_end, ImGuiTextFlags_NoWidthForLargeClippedText);'),
    ])
    rewrite(menu / 'menu_common.cpp', [
        ('#include "menu_common.h"\n', '#include "menu_common.h"\n#include "GameLockerZhFont.h"\n'),
        ('            io.FontDefault = gClassicFont;\n        }\n',
         '            if (!gClassicFont)\n                gClassicFont = atlas->AddFontDefault();\n'
         '            AddGameLockerZhFont(atlas, gClassicFont, fontSize);\n'
         '            AddGameLockerZhFont(atlas, gInterFont, fontSize);\n'
         '            io.FontDefault = gClassicFont;\n        }\n        else\n'
         '        {\n            io.FontDefault = io.Fonts->AddFontDefault();\n'
         '            AddGameLockerZhFont(io.Fonts, io.FontDefault, io.FontDefault->DefaultSize);\n        }\n'),
    ])
    resource = opti / 'OptiScaler.rc'
    text, enc, newline = read_source(resource)
    text += '\n4381 RCDATA "menu\\\\GameLockerZhSC.ttf"\n'
    resource.write_bytes(text.replace('\n', newline).encode(enc))
    print(f'Generated {len(entries)} translations, {glyphs} CJK/punctuation glyphs')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('font', type=Path)
    args = parser.parse_args()
    install(args.source.resolve(), args.font.resolve())
