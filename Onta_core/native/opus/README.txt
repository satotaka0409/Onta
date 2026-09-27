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
