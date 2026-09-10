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
    /// モノラル／ステレオは WAV のチャンネル数から決め、送信側 UI には依存しません。
    /// SC／変調／インターリーブは復号ヒント（暫定: 送信詳細と同じ値を渡す場合あり）。
    /// </summary>
    public static FileWavCodecProfile ForWavReceive(
        string wavPath,
        int activeSubcarriers,
        ModulationScheme modulationScheme,
        int blockInterleaveFactor)
    {
        var channels = WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        return Create(activeSubcarriers, modulationScheme, channelMode, blockInterleaveFactor);
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
