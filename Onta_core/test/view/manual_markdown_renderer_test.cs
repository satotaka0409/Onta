using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Onta.View.Manual;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// マニュアルタブの Markdown → FlowDocument 変換です。
/// </summary>
public sealed class ManualMarkdownRendererTest
{
    private const string ManualDir = @"C:\proj\Onta\Onta_manual";

    [Fact]
    public void Render_BlockElements_MapToFlowDocument()
    {
        const string markdown = """
            # 見出し

            本文1行目<br>
            本文2行目

            - 親
              - 子
            - 親2

            6. 六
            7. 七

            | 項目 | 値 |
            | :--- | ---: |
            | A | 1 |

            > 注意 **太字**
            """;

        StaThread.Run(() =>
        {
            var document = new MarkdownFlowDocumentRenderer(ManualDir, _ => { }).Render(markdown);
            var blocks = document.Blocks.ToList();

            Assert.IsType<Paragraph>(blocks[0]);
            var body = Assert.IsType<Paragraph>(blocks[1]);
            Assert.Single(body.Inlines.OfType<LineBreak>());

            var bullets = Assert.IsType<List>(blocks[2]);
            Assert.Equal(2, bullets.ListItems.Count);
            Assert.Contains(bullets.ListItems.First().Blocks, block => block is List);

            var ordered = Assert.IsType<List>(blocks[3]);
            Assert.Equal(TextMarkerStyle.Decimal, ordered.MarkerStyle);
            Assert.Equal(6, ordered.StartIndex);

            var table = Assert.IsType<BlockUIContainer>(blocks[4]);
            var grid = Assert.IsType<Grid>(table.Child);
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(2, grid.RowDefinitions.Count);

            Assert.IsType<Section>(blocks[5]);
        });
    }

    [Fact]
    public void Render_StripsCursorFrontMatter()
    {
        const string markdown = """
            ---
            alwaysApply: true
            ---

            # データ構造

            本文
            """;

        StaThread.Run(() =>
        {
            var document = new MarkdownFlowDocumentRenderer(ManualDir, _ => { }).Render(markdown);
            var heading = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            var text = string.Concat(heading.Inlines.OfType<Run>().Select(run => run.Text));
            Assert.Equal("データ構造", text);
            Assert.DoesNotContain(
                document.Blocks.OfType<Paragraph>(),
                paragraph => string.Concat(paragraph.Inlines.OfType<Run>().Select(run => run.Text)).Contains("alwaysApply", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Render_Links_InvokeCallbackWithRawUrl()
    {
        StaThread.Run(() =>
        {
            string? clicked = null;
            var document = new MarkdownFlowDocumentRenderer(ManualDir, url => clicked = url)
                .Render("[メイン](./02_メイン画面の説明.md)");
            var link = ((Paragraph)document.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();

            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));

            Assert.Equal("./02_メイン画面の説明.md", clicked);
        });
    }

    [Fact]
    public void Render_AllManuals_ProducesDocumentsAndIndexImage()
    {
        var files = Directory.GetFiles(ManualDir, "*.md");
        Assert.NotEmpty(files);

        StaThread.Run(() =>
        {
            foreach (var path in files)
            {
                var document = new MarkdownFlowDocumentRenderer(ManualDir, _ => { }).Render(File.ReadAllText(path));
                Assert.True(document.Blocks.Count > 0, path);
            }

            foreach (var name in new[] { "00_index.md", "04_ストリーム画面の説明.md" })
            {
                var document = new MarkdownFlowDocumentRenderer(ManualDir, _ => { })
                    .Render(File.ReadAllText(Path.Combine(ManualDir, name)));
                SavePng(document, TestPaths.ResolveOutputPath($"manual_{Path.GetFileNameWithoutExtension(name)}.png"));
            }
        });
    }

    /// <summary>
    /// FlowDocument の 1 ページ目を PNG として保存します（目視確認用）。
    /// </summary>
    private static void SavePng(FlowDocument document, string path)
    {
        const double width = 1000;
        const double height = 1400;
        document.PageWidth = width;
        document.PageHeight = height;
        document.ColumnWidth = double.PositiveInfinity;

        var page = ((IDocumentPaginatorSource)document).DocumentPaginator.GetPage(0);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(document.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(page.Visual), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
