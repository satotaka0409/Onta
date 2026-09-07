using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信設定の読み取り専用スナップショットです。
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
