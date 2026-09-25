using Onta.Core;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定画面の定数です。OFDM 本体の FFT（256）とは分離します。
/// </summary>
internal static class PerformanceConstants
{
    /// <summary>
    /// 性能測定の表示用解析 FFT 長の既定値です（メイン処理の <c>OfdmConfig.FixedFftSize=256</c> とは別）。
    /// UI で 1024/2048/4096/8192 を選択可能。
    /// </summary>
    public const int VizFftSize = PerformanceFftAnalyzer.DefaultSize;

    /// <summary>性能測定の PCM サンプリング周波数（Hz）。</summary>
    public const int SampleRate = 44100;

    /// <summary>
    /// オシロスコープ用に保持する直近 PCM 長です（約 370 ms @ 44.1 kHz）。FFT 窓以上。
    /// </summary>
    public const int ScopeCaptureSamples = 16384;
}

/// <summary>
/// 性能測定の信号モードです。
/// </summary>
internal enum PerformanceSignalMode : byte
{
    /// <summary>単一正弦波。</summary>
    Tone = 0,

    /// <summary>対数スイープ（20Hz〜20kHz、高域の進行は純対数の半分）。</summary>
    Sweep = 1,

    /// <summary>OFDM 連続変調（ペイロードは乱数。ファイルは使わない）。</summary>
    Modulated = 2,

    /// <summary>帯域制限ホワイトノイズ（約 20Hz〜20kHz）。</summary>
    WhiteNoise = 3
}

/// <summary>
/// 性能測定送信の設定スナップショットです。
/// </summary>
internal readonly record struct PerformanceTxSettings(
    ChannelMode ChannelMode,
    PerformanceSignalMode SignalMode,
    double ToneHz,
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    double DurationSeconds,
    bool WriteWav,
    string WavPath,
    bool PlayAudio,
    int AudioDeviceNumber,
    /// <summary>PCM 正弦波／変調の振幅（0.1〜1.0）。再生デバイス音量とは別。</summary>
    double SignalAmplitude,
    /// <summary>音声出力デバイスの再生音量（0〜1）。</summary>
    double OutputVolume);

/// <summary>
/// 性能測定受信の設定スナップショットです。
/// </summary>
internal readonly record struct PerformanceRxSettings(
    ChannelMode ChannelMode,
    bool UseWavInput,
    string WavPath,
    int InputDeviceNumber,
    double InputGain,
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    /// <summary>OFDM 受信時に I-Q コンスタレーションを出すか。</summary>
    bool CaptureConstellation,
    /// <summary>ワウ基準の選び方（トーン一覧 / スイープは無し / OFDM キャリア）。</summary>
    PerformanceSignalMode SignalMode);

/// <summary>
/// 性能測定画面の永続化用 UI 設定です（Onta_setting.bin）。
/// </summary>
internal readonly record struct PerformanceUiSettingsSnapshot(
    PerformanceSignalMode SignalMode,
    double ToneHz,
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    double DurationSeconds,
    bool WriteWav,
    string WavPath,
    int OutputDeviceNumber,
    /// <summary>信号振幅（0.1〜1.0）。</summary>
    double SignalAmplitude,
    /// <summary>出力デバイス音量（0〜1）。</summary>
    double OutputVolume,
    bool RxUseWavInput,
    string RxWavPath,
    int InputDeviceNumber,
    /// <summary>入力ゲイン（0〜1）。</summary>
    double InputGain,
    /// <summary>表示 FFT 長（1024/2048/4096/8192）。</summary>
    int FftSize,
    /// <summary>表示 FFT 窓関数。</summary>
    PerformanceFftWindowKind FftWindowKind)
{
    /// <summary>
    /// ファイル未作成時の既定値（音量 80%、その他は画面左上の既定選択）を返します。
    /// </summary>
    /// <returns>既定の UI 設定スナップショット。</returns>
    public static PerformanceUiSettingsSnapshot CreateDefault() =>
        new(
            SignalMode: PerformanceSignalMode.Tone,
            ToneHz: 315.0,
            ActiveSubcarriers: 16,
            ModulationScheme: ModulationScheme.Bpsk,
            DurationSeconds: 30.0,
            WriteWav: true,
            WavPath: string.Empty,
            OutputDeviceNumber: -1,
            SignalAmplitude: 0.80,
            OutputVolume: 0.80,
            RxUseWavInput: true,
            RxWavPath: string.Empty,
            InputDeviceNumber: -1,
            InputGain: 0.80,
            FftSize: PerformanceFftAnalyzer.DefaultSize,
            FftWindowKind: PerformanceFftWindowKind.Hanning);
}
