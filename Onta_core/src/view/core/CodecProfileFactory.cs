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
        // 受信は安全側の既定値（BPSK / SC=9 / Interleave=1）で開始する。
        return Create(
            activeSubcarriers: 9,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: channelMode,
            blockInterleaveFactor: 1);
    }

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




