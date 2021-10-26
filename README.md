# FASTBuild
Forked from https://github.com/fastbuild/fastbuild

Based on https://github.com/Quanwei1992/FASTBuild_UnrealEngine

Added support for PS5 (PS4 should be supported as well)

VS2017/VS2019 Monitor Extension - https://github.com/peterwoytiuk/FASTBuildMonitor

## Instructions:
    1. Run build.bat to compile FBuild.exe and FBuildWorker.exe
    2. Set required paths:
        2.1. FASTBUILD_CACHE_PATH for Cache (or set it in FASTBuild.cs)
        2.2. FASTBUILD_BROKERAGE_PATH for distributable
        2.3. Add FBuild.exe to PATH (or set it in FASTBuild.cs)

    3. Copy binaries to Agents and run FBuildWorker.exe on them (-console to be more verbose)

## Notes
    Some flags are stripped in FBuild.exe
    LightCache is supported for MSVC only
