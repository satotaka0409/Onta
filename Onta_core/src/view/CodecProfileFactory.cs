using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送受信プロファイル組み立てです。
/// </summary>
internal static class CodecProfileFactory
{
    /// <summary>
    /// 送信設定スナップショットから符号化プロファイルを生成します。
    /// </summary>
    public static FileWavCodecProfile FromSnapshot(SendSettingsSnapshot snap)
    {
        return Create(
            snap.ActiveSubcarriers,
            snap.ModulationScheme,
            snap.ChannelMode,
            snap.BlockInterleaveFactor);
    }

    /// <summary>
    /// WAV 受信用プロファイルを生成します。
    /// モノラル／ステレオは WAV チャンネル数のみ参照します。
    /// FH/BH の SC・変調は仕様どおり固定（GROUP-B・9SC・BPSK）で、
    /// データ部の SC／変調は BH から読み取るため、ここではプレースホルダです。
    /// </summary>
    public static FileWavCodecProfile ForWavReceive(string wavPath)
    {
        var channels = WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        // ActiveSubcarriers/Modulation は BH 確定前のプレースホルダ。
        // インターリーブは第1パスだけで全ブロックを受け取れるため 1 を既定とする。
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
