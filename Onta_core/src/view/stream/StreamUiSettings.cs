using System.Text;
using Onta.Stream;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム画面の永続化設定です（送信: 速度・入出力・デバイス・曲情報、受信: 入力デバイス）。
/// </summary>
/// <param name="ModeId">ストリーム速度。</param>
/// <param name="UseWavInput">ファイル入力なら true、音声入力なら false。</param>
/// <param name="WavPath">入力ファイル（WAV / FLAC / MP3）のパス。</param>
/// <param name="InputDeviceNumber">送信の音声入力デバイス番号（-1 は既定）。</param>
/// <param name="InputVolume">送信の入力音量 0〜1。</param>
/// <param name="OutputDeviceNumber">送信の音声出力デバイス番号（-1 は既定）。</param>
/// <param name="OutputVolume">送信の出力音量 0〜1。</param>
/// <param name="Title">曲タイトル。</param>
/// <param name="Artist">アーティスト。</param>
/// <param name="CoverPath">ジャケ写の画像ファイルのパス。</param>
/// <param name="CoverFormat">ジャケ写の形式。</param>
/// <param name="RxInputDeviceNumber">受信の音声入力デバイス番号（-1 は既定。保存されていなければ null）。</param>
/// <param name="RxInputVolume">受信の入力音量 0〜1（保存されていなければ null）。</param>
internal sealed record StreamUiSettings(
    StreamModeId ModeId,
    bool UseWavInput,
    string WavPath,
    int InputDeviceNumber,
    double InputVolume,
    int OutputDeviceNumber,
    double OutputVolume,
    string Title,
    string Artist,
    string CoverPath,
    StreamCoverFormat CoverFormat,
    int? RxInputDeviceNumber,
    double? RxInputVolume)
{
    /// <summary>ブロック内の形式バージョン。項目を足すときは末尾へ追加して上げ、読み側は古い版なら既定値にする。</summary>
    /// <remarks>1: 送信設定のみ。2: 受信の入力デバイス・音量を追加。</remarks>
    private const byte FormatVersion = 2;

    /// <summary>
    /// 長さ付きブロック（int32 バイト数 + 本体）として書き込みます。
    /// </summary>
    /// <param name="writer">設定バイナリの書き込み先。</param>
    public void WriteBlock(BinaryWriter writer)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(FormatVersion);
            w.Write((byte)ModeId);
            w.Write(UseWavInput);
            w.Write(WavPath ?? string.Empty);
            w.Write(InputDeviceNumber);
            w.Write(InputVolume);
            w.Write(OutputDeviceNumber);
            w.Write(OutputVolume);
            w.Write(Title ?? string.Empty);
            w.Write(Artist ?? string.Empty);
            w.Write(CoverPath ?? string.Empty);
            w.Write((byte)CoverFormat);
            w.Write(RxInputDeviceNumber ?? -1);
            w.Write(RxInputVolume ?? 0.8);
        }

        writer.Write((int)body.Length);
        writer.Write(body.GetBuffer(), 0, (int)body.Length);
    }

    /// <summary>
    /// 読み取り位置に長さ付きブロックがあれば読み込みます。無い・壊れているときは null です。
    /// </summary>
    /// <param name="reader">設定バイナリの読み取り位置。</param>
    /// <returns>妥当性チェック済みの設定。読めなければ null。</returns>
    public static StreamUiSettings? TryReadBlock(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        if (stream.Position + sizeof(int) > stream.Length)
        {
            return null;
        }

        var length = reader.ReadInt32();
        if (length <= 0 || stream.Position + length > stream.Length)
        {
            return null;
        }

        try
        {
            using var body = new BinaryReader(new MemoryStream(reader.ReadBytes(length)), Encoding.UTF8);
            var version = body.ReadByte();
            if (version < 1)
            {
                return null;
            }

            var modeId = (StreamModeId)body.ReadByte();
            var useWav = body.ReadBoolean();
            var wavPath = body.ReadString();
            var inputDevice = body.ReadInt32();
            var inputVolume = body.ReadDouble();
            var outputDevice = body.ReadInt32();
            var outputVolume = body.ReadDouble();
            var title = body.ReadString();
            var artist = body.ReadString();
            var coverPath = body.ReadString();
            var coverFormat = (StreamCoverFormat)body.ReadByte();
            int? rxInputDevice = null;
            double? rxInputVolume = null;
            if (version >= 2)
            {
                rxInputDevice = body.ReadInt32();
                rxInputVolume = Math.Clamp(body.ReadDouble(), 0.0, 1.0);
            }

            return new StreamUiSettings(
                ModeId: Enum.IsDefined(modeId) ? modeId : StreamModeId.Rate18k,
                UseWavInput: useWav,
                WavPath: wavPath,
                InputDeviceNumber: inputDevice,
                InputVolume: Math.Clamp(inputVolume, 0.0, 1.0),
                OutputDeviceNumber: outputDevice,
                OutputVolume: Math.Clamp(outputVolume, 0.0, 1.0),
                Title: title,
                Artist: artist,
                CoverPath: coverPath,
                CoverFormat: Enum.IsDefined(coverFormat) ? coverFormat : StreamCoverFormat.Color32,
                RxInputDeviceNumber: rxInputDevice,
                RxInputVolume: rxInputVolume);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
