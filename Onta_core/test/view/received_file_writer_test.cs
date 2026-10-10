using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// 全ブロックがそろった受信ファイルの出力フォルダーへの書き出しです。
/// </summary>
public sealed class ReceivedFileWriterTest
{
    /// <summary>
    /// FH のファイル名で書き出し、同じ内容を書き直しても別名を作らないこと。
    /// </summary>
    [Fact]
    public void Write_SameContent_ReusesExistingFile()
    {
        using var dir = new TempDir();
        var payload = new byte[] { 1, 2, 3, 4 };

        var first = ReceivedFileWriter.Write(dir.Path, "test.png", payload);
        var second = ReceivedFileWriter.Write(dir.Path, "test.png", payload);

        Assert.Equal(Path.Combine(dir.Path, "test.png"), first);
        Assert.Equal(first, second);
        Assert.Equal(payload, File.ReadAllBytes(first));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    /// <summary>
    /// 同名で内容の違うファイルがあれば上書きせず、番号付きの名前で書き出すこと。
    /// </summary>
    [Fact]
    public void Write_DifferentContent_AddsNumberInsteadOfOverwriting()
    {
        using var dir = new TempDir();
        var existing = Path.Combine(dir.Path, "test.png");
        File.WriteAllBytes(existing, [9, 9]);
        File.WriteAllBytes(Path.Combine(dir.Path, "test (2).png"), [8, 8]);

        var written = ReceivedFileWriter.Write(dir.Path, "test.png", [1, 2, 3]);

        Assert.Equal(Path.Combine(dir.Path, "test (3).png"), written);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(existing));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(written));
    }

    /// <summary>
    /// FH のファイル名にフォルダーや使えない文字があっても、出力フォルダーの直下に書き出すこと。
    /// </summary>
    [Theory]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("dir/sub/a:b?.txt", "a_b_.txt")]
    [InlineData("name. ", "name")]
    [InlineData("", "received.bin")]
    [InlineData("..", "received.bin")]
    public void SanitizeFileName_KeepsFileInsideOutputFolder(string fileName, string expected)
    {
        Assert.Equal(expected, ReceivedFileWriter.SanitizeFileName(fileName));
    }

    /// <summary>
    /// テストごとの一時フォルダーです。
    /// </summary>
    private sealed class TempDir : IDisposable
    {
        /// <summary>一時フォルダーのパスです。</summary>
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "onta_test_received_file",
            Guid.NewGuid().ToString("N"));

        /// <summary>
        /// 一時フォルダーを作ります。
        /// </summary>
        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>
        /// 一時フォルダーを削除します。
        /// </summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 削除失敗はテスト結果に影響させない。
            }
        }
    }
}
