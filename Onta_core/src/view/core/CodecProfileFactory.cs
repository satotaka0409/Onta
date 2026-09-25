using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// UI設定から送受信用の `FileWavCodecProfile` を生成するファクトリです。
/// </summary>
internal static class CodecProfileFactory
{
    /// <summary>
    /// 送信設定スナップショットからコーデックプロファイルを生成します。
    /// </summary>
    /// <param name="snap">送信設定スナップショット。</param>
    /// <returns>送信に利用するコーデックプロファイル。</returns>
    public static FileWavCodecProfile FromSnapshot(SendSettingsSnapshot snap)
    {
        return Create(
            snap.ActiveSubcarriers,
            snap.ModulationScheme,
            snap.ChannelMode,
            snap.BlockInterleaveFactor);
    }

    /// <summary>
    /// 受信WAVのチャネル数に合わせた既定プロファイルを生成します。
    /// </summary>
    /// <param name="wavPath">受信対象WAVファイル。</param>
    /// <returns>WAV受信用のコーデックプロファイル。</returns>
    public static FileWavCodecProfile ForWavReceive(string wavPath)
    {
        var channels = WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        return ForReceive(channelMode);
    }

    /// <summary>
    /// 音声入力受信用の既定プロファイルを生成します。
    /// </summary>
    /// <param name="channelMode">入力チャネル構成。</param>
    /// <returns>音声受信用プロファイル。</returns>
    public static FileWavCodecProfile ForAudioReceive(ChannelMode channelMode = ChannelMode.Stereo)
    {
        return ForReceive(channelMode);
    }

    /// <summary>
    /// 受信開始用の安全側既定プロファイル（BPSK / SC=16 / Interleave=1）を生成します。
    /// </summary>
    /// <param name="channelMode">チャネル構成（モノラル／ステレオ）。</param>
    /// <returns>受信用コーデックプロファイル。</returns>
    private static FileWavCodecProfile ForReceive(ChannelMode channelMode)
    {
        // 受信は安全側の既定値（BPSK / SC=16 / Interleave=1）で開始する。
        return Create(
            activeSubcarriers: 16,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: channelMode,
            blockInterleaveFactor: 1);
    }

    /// <summary>
    /// 指定パラメータから FileWavCodecProfile を組み立てます（FFT サイズは SC/チャネルから決定）。
    /// </summary>
    /// <param name="activeSubcarriers">データ部アクティブサブキャリア数。</param>
    /// <param name="modulationScheme">データ部変調方式。</param>
    /// <param name="channelMode">チャネル構成。</param>
    /// <param name="blockInterleaveFactor">ブロック時系列インターリーブ倍率（1〜2）。</param>
    /// <returns>送受信用コーデックプロファイル。</returns>
    private static FileWavCodecProfile Create(
        int activeSubcarriers,
        ModulationScheme modulationScheme,
        ChannelMode channelMode,
        int blockInterleaveFactor)
    {
        var fft = OfdmConfig.ResolveFftSize(activeSubcarriers, channelMode);
        return new FileWavCodecProfile(
            ActiveSubcarriers: activeSubcarriers,
            ModulationScheme: modulationScheme,
            ChannelMode: channelMode,
            HeaderFftSize: fft,
            DataFftSize: fft,
            BlockInterleaveFactor: Math.Clamp(blockInterleaveFactor, 1, 2));
    }
}




