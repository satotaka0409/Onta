using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信設定から <see cref="FileWavCodecProfile"/> を組み立てます。
/// </summary>
internal static class CodecProfileFactory
{
    /// <summary>
    /// 画面スナップショットから符号化プロファイルを生成します（繰り返し回数を含む）。
    /// </summary>
    public static FileWavCodecProfile FromSnapshot(SendSettingsSnapshot snap)
    {
        var fft = OfdmConfig.ResolveFftSize(snap.ActiveSubcarriers, snap.ChannelMode);
        return new FileWavCodecProfile(
            ActiveSubcarriers: snap.ActiveSubcarriers,
            ModulationScheme: snap.ModulationScheme,
            ChannelMode: snap.ChannelMode,
            HeaderFftSize: fft,
            DataFftSize: fft,
            BlockInterleaveFactor: Math.Clamp(snap.BlockInterleaveFactor, 1, 2));
    }
}
