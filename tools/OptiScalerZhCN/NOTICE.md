# GameLocker OptiScaler Simplified Chinese build

This is a modified build of [OptiScaler-F5-DLSSNR-Multipass v0.1.39](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/tree/956bb7198e819da857710f7f9ace223790a7ba55), distributed under the GNU General Public License version 3. Copyright belongs to the respective upstream authors and contributors. The modifications are for display translation and an embedded CJK fallback font only. This build is not an upstream release or signed by upstream.

For corresponding source, use the exact revision above (including the Git submodule revisions named by that checkout), the GameLocker source revision that contains this binary, the `tools/OptiScalerZhCN` sources and catalog, and `tools/Build-OptiScalerZhCN.ps1`. See `docs/optiscaler-menu-zh-cn.md` for the build instructions. Preserve the upstream license and notices when redistributing; the upstream checkout records the licenses of its dependencies.

The embedded `GameLocker SC` fallback is a statically instantiated, renamed subset of [Noto Sans SC](https://github.com/google/fonts/tree/2894aab31764f10f29c421bdfd2340d3b382d384/ofl/notosanssc), Copyright 2014-2021 Adobe, licensed under the SIL Open Font License 1.1. The license text is shipped separately as `OFL-NotoSansSC.txt`. Only glyphs used by the Simplified Chinese catalog are bundled. The original source font and the generation procedure are available at the pinned revision and in the GameLocker source respectively.

GameLocker ships only the separately built localized OptiScaler module, not the OptiCaptureHost runtime, or the externally supplied Intel, NVIDIA, or AMD runtime DLLs with it. Install a compatible OptiCaptureHost runtime separately.
