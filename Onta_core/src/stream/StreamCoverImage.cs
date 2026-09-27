using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Onta.Stream;

/// <summary>
/// ジャケ写サイズ指定です。
/// </summary>
public enum StreamCoverFormat
{
    /// <summary>32×32 カラー。</summary>
    Color32 = 0,

    /// <summary>48×48 カラー。</summary>
    Color48 = 1,

    /// <summary>48×48 白黒。</summary>
    Gray48 = 2,

    /// <summary>64×64 白黒。</summary>
    Gray64 = 3,
}

/// <summary>
/// ジャケ写をストリーム用バイト列へ変換します。
/// </summary>
public static class StreamCoverImage
{
    /// <summary>曲情報メタに収まるジャケ写の最大バイト数（圧縮 PNG）。</summary>
    public const int MaxBytes = 4096;

    /// <summary>
    /// 画像ファイルを指定サイズへ縮小し、圧縮 PNG バイト列へ変換します。
    /// </summary>
    /// <param name="path">元画像パス。</param>
    /// <param name="format">出力解像度・カラー／白黒。</param>
    /// <param name="byteCount">圧縮 PNG のバイト数。</param>
    /// <returns>圧縮 PNG バイト列。</returns>
    public static byte[] EncodeFile(string path, StreamCoverFormat format, out int byteCount)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var (size, gray) = format switch
        {
            StreamCoverFormat.Color32 => (32, false),
            StreamCoverFormat.Color48 => (48, false),
            StreamCoverFormat.Gray48 => (48, true),
            StreamCoverFormat.Gray64 => (64, true),
            _ => (32, false),
        };

        var scaled = new TransformedBitmap(
            bitmap,
            new ScaleTransform(size / (double)bitmap.PixelWidth, size / (double)bitmap.PixelHeight));
        scaled.Freeze();

        var converted = new FormatConvertedBitmap(
            scaled,
            gray ? PixelFormats.Gray8 : PixelFormats.Bgr24,
            null,
            0);
        converted.Freeze();

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(converted));
        encoder.Save(stream);
        var png = stream.ToArray();
        byteCount = png.Length;
        return png;
    }
}
