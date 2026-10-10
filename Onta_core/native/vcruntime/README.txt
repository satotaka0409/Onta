# VC++ runtime for opus.dll

opus.dll (libopus, MSVC shared build) imports VCRUNTIME140.dll.
The self-contained .NET publish does not include it (only WPF's vcruntime140_cor3.dll),
so PCs without the Visual C++ Redistributable could not load opus.dll (stream screen).
The app-local copy here is bundled next to opus.dll (Onta_core.csproj, per RID).

- `win-x64/vcruntime140.dll`
- `win-arm64/vcruntime140.dll`

Source: Visual Studio 2022 Build Tools redistributable folder
`VC\Redist\MSVC\<version>\{x64|arm64}\Microsoft.VC143.CRT\vcruntime140.dll`
(component Microsoft.VisualStudio.Component.VC.Redist.14.Latest).
App-local deployment of these files is permitted by the Visual Studio redistribution terms.
