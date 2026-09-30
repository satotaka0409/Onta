namespace Onta.View.Stream;

/// <summary>
/// ストリーム送信設定です。
/// </summary>
public sealed class StreamTxSettings
{
    /// <summary>速度モード。</summary>
    public Onta.Stream.StreamModeId ModeId { get; init; } = Onta.Stream.StreamModeId.Rate18k;

    /// <summary>true=ファイル入力（WAV/FLAC/MP3）、false=音声入力。</summary>
    public bool UseWavInput { get; init; } = true;

    /// <summary>入力ファイルパス（WAV / FLAC / MP3）。</summary>
    public string WavPath { get; init; } = string.Empty;

    /// <summary>入力デバイス番号。</summary>
    public int InputDevice { get; init; }

    /// <summary>出力デバイス番号。</summary>
    public int OutputDevice { get; init; }

    /// <summary>入力音量 0〜1。</summary>
    public double InputVolume { get; init; } = 1.0;

    /// <summary>出力音量 0〜1。</summary>
    public double OutputVolume { get; init; } = 0.8;

    /// <summary>曲タイトル。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>アーティスト。</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>ジャケ写パス。</summary>
    public string CoverPath { get; init; } = string.Empty;

    /// <summary>ジャケ写形式。</summary>
    public Onta.Stream.StreamCoverFormat CoverFormat { get; init; } = Onta.Stream.StreamCoverFormat.Color32;
}

/// <summary>
/// ストリーム受信設定です（入力は音声入力のみ）。
/// </summary>
public sealed class StreamRxSettings
{
    /// <summary>入力デバイス。</summary>
    public int InputDevice { get; init; }

    /// <summary>入力音量。</summary>
    public double InputVolume { get; init; } = 0.8;

    /// <summary>復号音声の再生デバイス番号。</summary>
    public int OutputDevice { get; init; }

    /// <summary>復号音声の再生音量 0〜1。</summary>
    public double OutputVolume { get; init; } = 0.8;
}
