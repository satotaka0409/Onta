namespace Onta.Stream;

/// <summary>
/// ストリームパケットの固定長とパイロット定数です。
/// </summary>
public static class StreamConstants
{
    /// <summary>既定サンプルレート（Hz）。</summary>
    public const int DefaultSampleRate = Onta.Core.OfdmConfig.ModemSampleRate;

    /// <summary>FFT サイズ。</summary>
    public const int FftSize = 256;

    /// <summary>データ部／ストリーム OFDM の CP 長。</summary>
    public const int DataCyclicPrefix = 16;

    /// <summary>ヘッダー部パイロット（3 バイト）。</summary>
    public static readonly byte[] HeaderPilot = { 0x38, 0xA7, 0x29 };

    /// <summary>ヘッダーエリア長。</summary>
    public const int HeaderBytes = 6;

    /// <summary>ヘッダー 3 バイト目のストリーム EOF ビット（Bit 7）。下位 4 bit は速度 ID。</summary>
    public const byte HeaderEndOfStreamFlag = 0x80;

    /// <summary>曲情報エリア長（CRC 含む）。</summary>
    public const int MetaBytes = 21;

    /// <summary>ストリームデータエリア長。</summary>
    public const int PayloadBytes = 1024;

    /// <summary>パケット全体長。</summary>
    public const int PacketBytes = HeaderBytes + MetaBytes + PayloadBytes; // 1051

    /// <summary>曲情報ブロックあたりのデータ長。</summary>
    public const int MetaBlockDataBytes = 16;

    /// <summary>曲情報ブロック最大数。</summary>
    public const int MetaMaxBlocks = 256;

    /// <summary>ヘッダー／曲情報前の無変調プリアンブル（秒）。</summary>
    public const double PreambleSeconds = 0.010;

    /// <summary>プリアンブルのサンプル数。</summary>
    /// <param name="sampleRate">サンプリング周波数（Hz）。1 未満は 1 として扱います。</param>
    /// <returns>10ms に相当するサンプル数。</returns>
    public static int PreambleSamples(int sampleRate) =>
        (int)Math.Round(PreambleSeconds * Math.Max(1, sampleRate));

    /// <summary>変調後 1 パケットの L/R ピーク振幅（ファイル画面の WAV 出力 SamplePeak と同じ）。</summary>
    public const double OutputPeak = 0.8;

    /// <summary>ヘッダー変調のサブキャリア数（ID=01: 48SC / 8PSK）。</summary>
    public const int HeaderSubcarriers = 48;
}
