using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 送信開始時点の設定値を保持するスナップショットです。
/// </summary>
/// <param name="ChannelMode">モノラル／ステレオ。</param>
/// <param name="ActiveSubcarriers">データ部アクティブサブキャリア数。</param>
/// <param name="ModulationScheme">データ部変調方式。</param>
/// <param name="BlockInterleaveFactor">ブロック時系列インターリーブ倍率（1 または 2）。</param>
/// <param name="InputFilePath">送信元ファイルパス。</param>
/// <param name="WriteWav">WAV ファイルへ出力するか。</param>
/// <param name="WavOutputPath">WAV 出力パス（WriteWav 時）。</param>
/// <param name="PlayAudio">音声デバイスへ再生するか。</param>
/// <param name="AudioDeviceNumber">再生デバイス番号。</param>
/// <param name="AudioDeviceName">再生デバイス表示名。</param>
/// <param name="AudioVolume">再生音量（0〜1）。</param>
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
    string AudioDeviceName,
    double AudioVolume);




