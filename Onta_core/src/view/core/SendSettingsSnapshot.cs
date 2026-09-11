using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 送信開始時点の設定値を保持するスナップショットです。
/// </summary>
public sealed record SendSettingsSnapshot(
    ChannelMode ChannelMode,
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    int BlockInterleaveFactor,
    string InputFilePath,
    bool WriteWav,
    string WavOutputPath,
    bool PlayAudio,
    int AudioDeviceNumber,
    string AudioDeviceName);




