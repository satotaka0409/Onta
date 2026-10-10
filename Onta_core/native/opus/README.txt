# native libopus

Place platform builds here:

- `win-x64/opus.dll`
- `win-arm64/opus.dll`

Build from https://github.com/xiph/opus or use a prebuilt Windows DLL named `opus.dll`.
The app loads `opus.dll` from the executable directory (copied at build when present).

## win-x64 (bundled)

`win-x64/opus.dll` is libopus v1.4 (MSVC17 shared) from:
https://github.com/ShiftMediaProject/opus/releases/tag/v1.4
(`libopus_v1.4_msvc17.zip` → `bin/x64/opus.dll`)

## win-arm64 (bundled)

`win-arm64/opus.dll` is libopus v1.4 built from the official source
(https://github.com/xiph/opus, tag v1.4, commit 82ac57d9f1aaf575800cf17373348e45b7ce6c0d)
with Visual Studio 2022 Build Tools (MSVC 14.44, ARM64 cross compiler):

    cmake -S opus -B build-arm64 -G "Visual Studio 17 2022" -A ARM64 -DBUILD_SHARED_LIBS=ON -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF
    cmake --build build-arm64 --config Release

Both builds import VCRUNTIME140.dll; the app-local copy is in `../vcruntime/`.
