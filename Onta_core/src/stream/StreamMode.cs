using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// ストリーム速度（変調モード）ID です。
/// </summary>
public enum StreamModeId : byte
{
    /// <summary>48SC / 8PSK / ステレオ（Opus 18.4 kbps）。</summary>
    Rate18k = 0x01,

    /// <summary>56SC / 8PSK / ステレオ（Opus 20.4 kbps）。</summary>
    Rate20k = 0x02,

    /// <summary>64SC / 8PSK / ステレオ（Opus 23.2 kbps）。</summary>
    Rate23k = 0x03,

    /// <summary>48SC / 16QAM / ステレオ（Opus 24.8 kbps）。</summary>
    Rate24k = 0x04,

    /// <summary>56SC / 16QAM / ステレオ（Opus 28.4 kbps）。</summary>
    Rate28k = 0x05,

    /// <summary>64SC / 16QAM / ステレオ（Opus 30.4 kbps）。</summary>
    Rate30k = 0x06,

    /// <summary>72SC / 16QAM / ステレオ（Opus 33.2 kbps）。</summary>
    Rate33k = 0x07,

    /// <summary>64SC / 64QAM / ステレオ（Opus 44.4 kbps）。</summary>
    Rate44k = 0x08,

    /// <summary>72SC / 64QAM / ステレオ（Opus 50.4 kbps）。</summary>
    Rate50k = 0x09,
}

/// <summary>
/// ストリーム変調モードの解決結果です。
/// </summary>
/// <param name="Id">モード ID。</param>
/// <param name="Subcarriers">サブキャリア数。</param>
/// <param name="Modulation">変調方式。</param>
/// <param name="OpusBitrateBps">Opus（CBR）ビットレート。</param>
/// <param name="DisplayKbps">画面表示用 kbps。</param>
public readonly record struct StreamModeInfo(
    StreamModeId Id,
    int Subcarriers,
    ModulationScheme Modulation,
    int OpusBitrateBps,
    int DisplayKbps)
{
    /// <summary>
    /// 曲情報／データ部の畳み込みパンクチャ率です（8PSK/16QAM は 2/3、64QAM は 3/4。ファイルのデータ部と同じ対応）。
    /// </summary>
    public ConvolutionalCode.PunctureRate BodyPuncture => Modulation switch
    {
        ModulationScheme.Psk8 or ModulationScheme.Qam16 => ConvolutionalCode.PunctureRate.Rate2_3,
        ModulationScheme.Qam64 => ConvolutionalCode.PunctureRate.Rate3_4,
        _ => throw new ArgumentOutOfRangeException(nameof(Modulation), Modulation, "Unsupported stream modulation."),
    };
}

/// <summary>
/// ストリームモード ID と OFDM／Opus パラメータの対応です。
/// </summary>
public static class StreamMode
{
    /// <summary>
    /// Opus（CBR・20ms）ビットレート。1 パケットの Opus フレーム数 N = ceil(パケット長 / (0.97 × 20ms)) とし、
    /// 1 フレーム = floor(1024 / N) − 2 バイト（長さフィールド 2 バイト分を除く）× 400 で求める。
    /// パケットが運ぶ音声時間がエアタイムより 3% 以上長くなり、実時間から遅れない。
    /// </summary>
    private static readonly StreamModeInfo[] Table =
    {
        new(StreamModeId.Rate18k, 48, ModulationScheme.Psk8, 18400, 18),
        new(StreamModeId.Rate20k, 56, ModulationScheme.Psk8, 20400, 20),
        new(StreamModeId.Rate23k, 64, ModulationScheme.Psk8, 23200, 23),
        new(StreamModeId.Rate24k, 48, ModulationScheme.Qam16, 24800, 24),
        new(StreamModeId.Rate28k, 56, ModulationScheme.Qam16, 28400, 28),
        new(StreamModeId.Rate30k, 64, ModulationScheme.Qam16, 30400, 30),
        new(StreamModeId.Rate33k, 72, ModulationScheme.Qam16, 33200, 33),
        new(StreamModeId.Rate44k, 64, ModulationScheme.Qam64, 44400, 44),
        new(StreamModeId.Rate50k, 72, ModulationScheme.Qam64, 50400, 50),
    };

    /// <summary>
    /// 全モード一覧を返します。
    /// </summary>
    public static IReadOnlyList<StreamModeInfo> All => Table;

    /// <summary>
    /// モード ID を解決します。
    /// </summary>
    /// <param name="id">ストリーム速度 ID。</param>
    /// <returns>モード情報。</returns>
    public static StreamModeInfo Resolve(StreamModeId id)
    {
        foreach (var row in Table)
        {
            if (row.Id == id)
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown stream mode id.");
    }

    /// <summary>
    /// バイト値からモードを解決します。不明なら null。
    /// </summary>
    /// <param name="raw">ヘッダーの速度バイト（下位 4 bit）。</param>
    /// <returns>モード情報。不明時は null。</returns>
    public static StreamModeInfo? TryResolve(byte raw)
    {
        var id = (StreamModeId)(raw & 0x0F);
        foreach (var row in Table)
        {
            if (row.Id == id)
            {
                return row;
            }
        }

        return null;
    }
}
