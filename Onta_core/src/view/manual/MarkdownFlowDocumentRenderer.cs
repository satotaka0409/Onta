using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Onta.View.Manual;

/// <summary>
/// Markdown テキストを WPF の <see cref="FlowDocument"/> へ整形します（マニュアル表示用のサブセット）。
/// </summary>
/// <remarks>
/// ブロック: 見出し・段落・箇条書き／番号付きリスト（入れ子可）・表・引用・コードブロック・水平線。
/// インライン: 太字・斜体・インラインコード・リンク・画像・&lt;br&gt;。
/// 段落内の改行は日本語の 1 行 1 文スタイルに合わせ、そのまま改行として表示します。
/// </remarks>
internal sealed class MarkdownFlowDocumentRenderer
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRegex = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceRegex = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex BrRegex = new(@"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingBrRegex = new(@"<br\s*/?>\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaceholderRegex = new(@"^\[スクリーンショット挿入[:：].*\]$", RegexOptions.Compiled);

    private static readonly double[] HeadingSizes = [22, 18, 15, 13.5, 13, 13];

    private readonly string _baseDirectory;
    private readonly Action<string> _linkClicked;

    private readonly Brush _textBrush = ResolveBrush("BrushText", Color.FromRgb(0xE8, 0xEA, 0xED));
    private readonly Brush _mutedBrush = ResolveBrush("BrushTextMuted", Color.FromRgb(0xB0, 0xB5, 0xBF));
    private readonly Brush _borderBrush = ResolveBrush("BrushBorder", Color.FromRgb(0x6E, 0x73, 0x80));
    private readonly Brush _borderSoftBrush = ResolveBrush("BrushBorderSoft", Color.FromRgb(0x5C, 0x61, 0x6C));
    private readonly Brush _inputBrush = ResolveBrush("BrushInput", Color.FromRgb(0x2F, 0x32, 0x38));
    private readonly Brush _panelBrush = ResolveBrush("BrushPanel", Color.FromRgb(0x4A, 0x4E, 0x57));
    private readonly Brush _panelAltBrush = ResolveBrush("BrushPanelAlt", Color.FromRgb(0x55, 0x59, 0x65));
    private readonly Brush _accentBrush = ResolveBrush("BrushAccent", Color.FromRgb(0x8A, 0x90, 0x9C));
    private readonly Brush _headingBrush = Frozen(Colors.White);
    private readonly Brush _linkBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0xF8));
    private readonly Brush _codeForeground = Frozen(Color.FromRgb(0xF0, 0xC9, 0x87));
    private readonly Brush _codeBackground = Frozen(Color.FromRgb(0x26, 0x29, 0x2E));
    private readonly FontFamily _bodyFont = new("Segoe UI, Yu Gothic UI");
    private readonly FontFamily _codeFont = new("Consolas, Yu Gothic UI");

    /// <summary>
    /// レンダラーを初期化します。
    /// </summary>
    /// <param name="baseDirectory">相対パスの画像を解決する基準フォルダー（表示中 Markdown のフォルダー）。</param>
    /// <param name="linkClicked">リンククリック時に Markdown 上の URL（そのままの文字列）を受け取るコールバック。</param>
    public MarkdownFlowDocumentRenderer(string baseDirectory, Action<string> linkClicked)
    {
        _baseDirectory = baseDirectory;
        _linkClicked = linkClicked;
    }

    /// <summary>
    /// Cursor ルール先頭の YAML front matter（--- ... ---）を除きます。
    /// </summary>
    /// <param name="markdown">Markdown テキスト。</param>
    /// <returns>front matter を除いた本文。無ければ元の文字列。</returns>
    private static string StripCursorFrontMatter(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return markdown;
        }

        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return markdown;
        }

        return text[(end + "\n---\n".Length)..].TrimStart('\n');
    }

    /// <summary>
    /// Markdown テキスト全体を FlowDocument へ変換します。
    /// </summary>
    /// <param name="markdown">Markdown テキスト。</param>
    /// <returns>表示用の FlowDocument。</returns>
    public FlowDocument Render(string markdown)
    {
        var document = CreateDocument();
        markdown = StripCursorFrontMatter(markdown);
        var lines = markdown
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Replace("\t", "    "))
            .ToList();
        AddBlocks(document.Blocks, lines, listDepth: 0);
        return document;
    }

    /// <summary>
    /// 1 段落だけのメッセージ文書を作成します（フォルダー未検出・読込失敗の表示用）。
    /// </summary>
    /// <param name="message">表示する文言。</param>
    /// <returns>メッセージのみを含む FlowDocument。</returns>
    public FlowDocument RenderMessage(string message)
    {
        var document = CreateDocument();
        document.Blocks.Add(new Paragraph(new Run(message)) { Foreground = _mutedBrush });
        return document;
    }

    /// <summary>
    /// 共通の書式（フォント・色・余白）を設定した空の FlowDocument を作成します。
    /// </summary>
    /// <returns>書式だけを設定した空の FlowDocument。</returns>
    private FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            FontFamily = _bodyFont,
            FontSize = 13,
            Foreground = _textBrush,
            Background = _inputBrush,
            PagePadding = new Thickness(28, 16, 28, 28),
            TextAlignment = TextAlignment.Left,
        };
    }

    /// <summary>
    /// 行リストをブロック要素へ変換して追加します。リスト項目・引用の中身にも再帰的に使います。
    /// </summary>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行（インデント除去済み）。</param>
    /// <param name="listDepth">箇条書きの入れ子の深さ（マーカー形状の切替用）。</param>
    private void AddBlocks(BlockCollection target, List<string> lines, int listDepth)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                i++;
                continue;
            }

            if (FenceRegex.IsMatch(line))
            {
                i = AddCodeBlock(target, lines, i);
                continue;
            }

            var heading = MatchHeading(line);
            if (heading.Success)
            {
                target.Add(CreateHeading(heading.Groups[1].Length, heading.Groups[2].Value));
                i++;
                continue;
            }

            if (RuleRegex.IsMatch(line))
            {
                target.Add(CreateRule());
                i++;
                continue;
            }

            if (IsTableStart(lines, i))
            {
                i = AddTable(target, lines, i);
                continue;
            }

            if (IsQuote(line))
            {
                i = AddQuote(target, lines, i, listDepth);
                continue;
            }

            if (ListItemRegex.IsMatch(line))
            {
                i = AddList(target, lines, i, listDepth);
                continue;
            }

            i = AddParagraph(target, lines, i);
        }
    }

    /// <summary>
    /// 行が段落を打ち切る別ブロックの開始行かどうかを判定します。
    /// </summary>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="index">判定する行の位置。</param>
    /// <returns>空行・フェンス・見出し・水平線・表・引用・リスト項目なら true。</returns>
    private static bool IsBlockStart(List<string> lines, int index)
    {
        var line = lines[index];
        return IsBlank(line)
            || FenceRegex.IsMatch(line)
            || MatchHeading(line).Success
            || RuleRegex.IsMatch(line)
            || line.TrimStart().StartsWith('|')
            || IsQuote(line)
            || ListItemRegex.IsMatch(line);
    }

    /// <summary>
    /// ATX 見出し（先頭 3 スペースまで）に一致するか調べます。
    /// </summary>
    /// <param name="line">判定する行。</param>
    /// <returns>一致した Match。インデントが 4 以上なら Match.Empty。</returns>
    private static Match MatchHeading(string line)
    {
        return IndentOf(line) <= 3 ? HeadingRegex.Match(line.TrimStart()) : Match.Empty;
    }

    /// <summary>
    /// 見出し段落を作成します。H1/H2 は下線を付けます。
    /// </summary>
    /// <param name="level">見出しレベル（1〜6）。</param>
    /// <param name="text">見出しテキスト（インライン記法可）。</param>
    /// <returns>見出しの Paragraph。</returns>
    private Paragraph CreateHeading(int level, string text)
    {
        var paragraph = new Paragraph
        {
            FontSize = HeadingSizes[Math.Clamp(level, 1, 6) - 1],
            FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = _headingBrush,
            Margin = level == 1 ? new Thickness(0, 4, 0, 14) : new Thickness(0, 18, 0, 8),
        };
        if (level <= 2)
        {
            paragraph.BorderBrush = _borderBrush;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
        }

        paragraph.Inlines.AddRange(ParseInlines(text));
        return paragraph;
    }

    /// <summary>
    /// 水平線（下罫線だけの空段落）を作成します。
    /// </summary>
    /// <returns>下罫線だけの空 Paragraph。</returns>
    private Paragraph CreateRule()
    {
        return new Paragraph
        {
            FontSize = 1,
            Margin = new Thickness(0, 8, 0, 12),
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>
    /// 空行または別ブロック開始までを 1 段落として追加します。
    /// </summary>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">段落開始行の位置。</param>
    /// <returns>次に処理する行番号。</returns>
    private int AddParagraph(BlockCollection target, List<string> lines, int start)
    {
        var parts = new List<string>();
        var i = start;
        while (i < lines.Count && (i == start || !IsBlockStart(lines, i)))
        {
            parts.Add(lines[i].Trim());
            i++;
        }

        if (parts.All(part => PlaceholderRegex.IsMatch(part)))
        {
            target.Add(CreatePlaceholder(parts));
            return i;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        for (var k = 0; k < parts.Count; k++)
        {
            if (k > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            // 行末 <br> は行区切りの改行と重複させない
            paragraph.Inlines.AddRange(ParseInlines(TrailingBrRegex.Replace(parts[k], string.Empty)));
        }

        target.Add(paragraph);
        return i;
    }

    /// <summary>
    /// 「[スクリーンショット挿入: …]」行を控えめな枠付きの注記として作成します。
    /// </summary>
    /// <param name="parts">プレースホルダー行（トリム済み）。</param>
    /// <returns>枠付きの注記 Paragraph。</returns>
    private Paragraph CreatePlaceholder(List<string> parts)
    {
        var paragraph = new Paragraph
        {
            Foreground = _mutedBrush,
            FontStyle = FontStyles.Italic,
            BorderBrush = _borderSoftBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 12),
        };
        for (var k = 0; k < parts.Count; k++)
        {
            if (k > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(parts[k]));
        }

        return paragraph;
    }

    /// <summary>
    /// フェンス（``` / ~~~）で囲まれたコードブロックを等幅の枠付き段落として追加します。
    /// </summary>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">開始フェンス行の位置。</param>
    /// <returns>閉じフェンスの次の行番号。</returns>
    private int AddCodeBlock(BlockCollection target, List<string> lines, int start)
    {
        var fence = FenceRegex.Match(lines[start]).Groups[1].Value;
        var body = new List<string>();
        var i = start + 1;
        while (i < lines.Count && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
        {
            body.Add(lines[i]);
            i++;
        }

        if (i < lines.Count)
        {
            i++;
        }

        var paragraph = new Paragraph
        {
            FontFamily = _codeFont,
            FontSize = 12,
            Foreground = _textBrush,
            Background = _codeBackground,
            BorderBrush = _borderSoftBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10),
        };
        for (var k = 0; k < body.Count; k++)
        {
            if (k > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(body[k]));
        }

        target.Add(paragraph);
        return i;
    }

    /// <summary>
    /// 行が引用（&gt;）かどうかを判定します。
    /// </summary>
    /// <param name="line">判定する行。</param>
    /// <returns>インデント 3 以下で &gt; で始まれば true。</returns>
    private static bool IsQuote(string line)
    {
        return IndentOf(line) <= 3 && line.TrimStart().StartsWith('>');
    }

    /// <summary>
    /// 連続する引用行を、左罫線付きのセクションとして追加します（中身は再帰的にブロック解析）。
    /// </summary>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">引用開始行の位置。</param>
    /// <param name="listDepth">箇条書きの入れ子の深さ（中身のマーカー切替用）。</param>
    /// <returns>引用の次の行番号。</returns>
    private int AddQuote(BlockCollection target, List<string> lines, int start, int listDepth)
    {
        var inner = new List<string>();
        var i = start;
        while (i < lines.Count && IsQuote(lines[i]))
        {
            var text = lines[i].TrimStart()[1..];
            inner.Add(text.StartsWith(' ') ? text[1..] : text);
            i++;
        }

        var section = new Section
        {
            BorderBrush = _accentBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = _panelBrush,
            Padding = new Thickness(12, 8, 10, 0),
            Margin = new Thickness(0, 2, 0, 12),
        };
        AddBlocks(section.Blocks, inner, listDepth);
        target.Add(section);
        return i;
    }

    /// <summary>
    /// 表の開始行（パイプ行＋区切り行）かどうかを判定します。
    /// </summary>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="index">判定する行の位置。</param>
    /// <returns>次行が区切り行なら true。</returns>
    private static bool IsTableStart(List<string> lines, int index)
    {
        return lines[index].TrimStart().StartsWith('|')
            && index + 1 < lines.Count
            && lines[index + 1].Contains('-')
            && TableSeparatorRegex.IsMatch(lines[index + 1]);
    }

    /// <summary>
    /// パイプ区切りの表を、列幅が内容に合わせて決まる Grid として追加します。
    /// </summary>
    /// <remarks>FlowDocument の Table は列幅が等分になり短い表が間延びするため、自動幅の Grid を使います。</remarks>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">ヘッダー行の位置。</param>
    /// <returns>表の次の行番号。</returns>
    private int AddTable(BlockCollection target, List<string> lines, int start)
    {
        var header = SplitTableRow(lines[start]);
        var alignments = SplitTableRow(lines[start + 1]).Select(ParseAlignment).ToList();
        var rows = new List<List<string>>();
        var i = start + 2;
        while (i < lines.Count && lines[i].TrimStart().StartsWith('|'))
        {
            rows.Add(SplitTableRow(lines[i]));
            i++;
        }

        var columnCount = header.Count;
        var cellMaxWidth = columnCount <= 2 ? 520.0 : columnCount == 3 ? 380.0 : 300.0;
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true,
        };
        for (var c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        AddTableRow(grid, header, 0, alignments, isHeader: true, cellMaxWidth);
        for (var r = 0; r < rows.Count; r++)
        {
            AddTableRow(grid, rows[r], r + 1, alignments, isHeader: false, cellMaxWidth);
        }

        target.Add(new BlockUIContainer(grid) { Margin = new Thickness(0, 2, 0, 12) });
        return i;
    }

    /// <summary>
    /// 表の 1 行分のセルを Grid に追加します。セル数が列数に満たない場合は空セルで埋めます。
    /// </summary>
    /// <param name="grid">追加先の Grid。</param>
    /// <param name="cells">セル文字列。</param>
    /// <param name="rowIndex">Grid 上の行番号（0 がヘッダー）。</param>
    /// <param name="alignments">列ごとの文字寄せ。</param>
    /// <param name="isHeader">ヘッダー行なら true。</param>
    /// <param name="cellMaxWidth">セルの最大幅（超えると折り返す）。</param>
    private void AddTableRow(Grid grid, List<string> cells, int rowIndex, List<TextAlignment> alignments, bool isHeader, double cellMaxWidth)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var columnCount = grid.ColumnDefinitions.Count;
        for (var c = 0; c < columnCount; c++)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = cellMaxWidth,
                FontFamily = _bodyFont,
                FontSize = 13,
                Foreground = _textBrush,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = c < alignments.Count ? alignments[c] : TextAlignment.Left,
            };
            textBlock.Inlines.AddRange(ParseInlines(c < cells.Count ? cells[c] : string.Empty));

            var border = new Border
            {
                BorderBrush = _borderSoftBrush,
                BorderThickness = new Thickness(c == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1),
                Background = isHeader ? _panelAltBrush : rowIndex % 2 == 1 ? _inputBrush : _panelBrush,
                Padding = new Thickness(8, 4, 8, 4),
                Child = textBlock,
            };
            Grid.SetRow(border, rowIndex);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
        }
    }

    /// <summary>
    /// 表の行をセル文字列に分割します。エスケープされた \| とインラインコード内の | では分割しません。
    /// </summary>
    /// <param name="line">パイプ区切りの 1 行。</param>
    /// <returns>トリム済みのセル文字列。</returns>
    private static List<string> SplitTableRow(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        var inCode = false;
        for (var p = 0; p < text.Length; p++)
        {
            var ch = text[p];
            if (ch == '\\' && p + 1 < text.Length && text[p + 1] == '|')
            {
                current.Append("\\|");
                p++;
                continue;
            }

            if (ch == '`')
            {
                inCode = !inCode;
            }

            if (ch == '|' && !inCode)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    /// <summary>
    /// 区切り行のセル（:--- / :---: / ---:）から文字寄せを求めます。
    /// </summary>
    /// <param name="cell">区切り行の 1 セル。</param>
    /// <returns>両端コロンなら中央、右端のみなら右寄せ、それ以外は左寄せ。</returns>
    private static TextAlignment ParseAlignment(string cell)
    {
        var left = cell.StartsWith(':');
        var right = cell.EndsWith(':');
        return left && right ? TextAlignment.Center : right ? TextAlignment.Right : TextAlignment.Left;
    }

    /// <summary>
    /// 同じインデント・同じ種類（箇条書き／番号付き）の項目が続く範囲を 1 つのリストとして追加します。
    /// </summary>
    /// <remarks>項目より深くインデントされた行は、その項目の子（入れ子リストや続きの段落）として再帰解析します。</remarks>
    /// <param name="target">追加先のブロックコレクション。</param>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">最初の項目行の位置。</param>
    /// <param name="listDepth">箇条書きの入れ子の深さ（マーカー形状の切替用）。</param>
    /// <returns>リストの次の行番号。</returns>
    private int AddList(BlockCollection target, List<string> lines, int start, int listDepth)
    {
        var first = ListItemRegex.Match(lines[start]);
        var indent = IndentOf(lines[start]);
        var ordered = IsOrderedMarker(first.Groups[2].Value);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : UnorderedMarker(listDepth),
            Padding = new Thickness(24, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
        };
        if (ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var startIndex) && startIndex > 0)
        {
            list.StartIndex = startIndex;
        }

        var i = start;
        while (i < lines.Count)
        {
            if (IsBlank(lines[i]))
            {
                var next = NextNonBlank(lines, i);
                if (next >= 0 && IsSiblingItem(lines[next], indent, ordered))
                {
                    i = next;
                    continue;
                }

                break;
            }

            var match = ListItemRegex.Match(lines[i]);
            if (!match.Success || !IsSiblingItem(lines[i], indent, ordered))
            {
                break;
            }

            var contentIndent = indent + match.Groups[2].Length + 1;
            var itemLines = new List<string> { match.Groups[3].Value };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    var next = NextNonBlank(lines, i);
                    if (next >= 0 && IndentOf(lines[next]) > indent)
                    {
                        itemLines.Add(string.Empty);
                        i++;
                        continue;
                    }

                    break;
                }

                var lineIndent = IndentOf(line);
                if (lineIndent > indent)
                {
                    itemLines.Add(line[Math.Min(lineIndent, contentIndent)..]);
                    i++;
                    continue;
                }

                if (!IsBlockStart(lines, i))
                {
                    // インデントなしの続き行は項目本文の続きとして扱う
                    itemLines.Add(line.Trim());
                    i++;
                    continue;
                }

                break;
            }

            var item = new ListItem();
            AddBlocks(item.Blocks, itemLines, listDepth + 1);
            TightenListItem(item);
            list.ListItems.Add(item);
        }

        target.Add(list);
        return i;
    }

    /// <summary>
    /// 行が指定インデント・種類の兄弟リスト項目かどうかを判定します。
    /// </summary>
    /// <param name="line">判定する行。</param>
    /// <param name="indent">兄弟項目の行頭空白数。</param>
    /// <param name="ordered">番号付きリストなら true、箇条書きなら false。</param>
    /// <returns>インデントと種類が一致する項目なら true。</returns>
    private static bool IsSiblingItem(string line, int indent, bool ordered)
    {
        var match = ListItemRegex.Match(line);
        return match.Success
            && IndentOf(line) == indent
            && IsOrderedMarker(match.Groups[2].Value) == ordered;
    }

    /// <summary>
    /// マーカーが番号付き（1. / 1)）かどうかを判定します。
    /// </summary>
    /// <param name="marker">リストマーカー文字列。</param>
    /// <returns>先頭が数字なら true。</returns>
    private static bool IsOrderedMarker(string marker)
    {
        return char.IsDigit(marker[0]);
    }

    /// <summary>
    /// 入れ子の深さに応じた箇条書きマーカー（● → ○ → ■）を返します。
    /// </summary>
    /// <param name="depth">入れ子の深さ（0 が最外）。</param>
    /// <returns>Disc、Circle、Square を深さで巡回したマーカー。</returns>
    private static TextMarkerStyle UnorderedMarker(int depth)
    {
        return (depth % 3) switch
        {
            0 => TextMarkerStyle.Disc,
            1 => TextMarkerStyle.Circle,
            _ => TextMarkerStyle.Square,
        };
    }

    /// <summary>
    /// リスト項目内の段落・入れ子リストの余白を詰めます（行間の詰まったリスト表示）。
    /// </summary>
    /// <param name="item">余白を詰めるリスト項目。</param>
    private static void TightenListItem(ListItem item)
    {
        foreach (var block in item.Blocks)
        {
            if (block is Paragraph paragraph && paragraph.BorderThickness == default)
            {
                paragraph.Margin = new Thickness(0, 0, 0, 2);
            }
            else if (block is List nested)
            {
                nested.Margin = new Thickness(0, 0, 0, 2);
            }
        }
    }

    /// <summary>
    /// インライン記法を解析して Inline 要素列に変換します。
    /// </summary>
    /// <param name="text">1 行分（または表セル・見出し）のテキスト。</param>
    /// <returns>Run / Bold / Italic / Hyperlink / LineBreak などの要素列。</returns>
    private List<Inline> ParseInlines(string text)
    {
        var result = new List<Inline>();
        var buffer = new StringBuilder();

        /// <summary>
        /// 蓄積したテキストを Run として結果へ追加し、バッファを空にします。
        /// </summary>
        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new Run(buffer.ToString()));
                buffer.Clear();
            }
        }

        var p = 0;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch == '\\' && p + 1 < text.Length && IsEscapable(text[p + 1]))
            {
                buffer.Append(text[p + 1]);
                p += 2;
                continue;
            }

            if (ch == '`')
            {
                var end = text.IndexOf('`', p + 1);
                if (end > p + 1)
                {
                    Flush();
                    result.Add(CreateInlineCode(text[(p + 1)..end]));
                    p = end + 1;
                    continue;
                }
            }

            if (ch == '<')
            {
                var br = BrRegex.Match(text, p);
                if (br.Success)
                {
                    Flush();
                    result.Add(new LineBreak());
                    p += br.Length;
                    continue;
                }
            }

            if (ch == '*' && p + 1 < text.Length && text[p + 1] == '*')
            {
                var end = text.IndexOf("**", p + 2, StringComparison.Ordinal);
                if (end > p + 2)
                {
                    Flush();
                    var bold = new Bold();
                    bold.Inlines.AddRange(ParseInlines(text[(p + 2)..end]));
                    result.Add(bold);
                    p = end + 2;
                    continue;
                }
            }

            if (ch == '*' && p + 1 < text.Length && !char.IsWhiteSpace(text[p + 1]))
            {
                var end = text.IndexOf('*', p + 1);
                if (end > p + 1)
                {
                    Flush();
                    var italic = new Italic();
                    italic.Inlines.AddRange(ParseInlines(text[(p + 1)..end]));
                    result.Add(italic);
                    p = end + 1;
                    continue;
                }
            }

            if (ch == '!' && p + 1 < text.Length && text[p + 1] == '['
                && TryParseLink(text, p + 1, out var alt, out var src, out var afterImage))
            {
                Flush();
                result.Add(CreateImage(alt, src));
                p = afterImage;
                continue;
            }

            if (ch == '[' && TryParseLink(text, p, out var label, out var url, out var afterLink))
            {
                Flush();
                result.Add(CreateHyperlink(label, url));
                p = afterLink;
                continue;
            }

            buffer.Append(ch);
            p++;
        }

        Flush();
        return result;
    }

    /// <summary>
    /// バックスラッシュでエスケープできる記号かどうかを判定します。
    /// </summary>
    /// <param name="ch">判定する文字。</param>
    /// <returns>Markdown のエスケープ対象なら true。</returns>
    private static bool IsEscapable(char ch)
    {
        return "\\`*_{}[]()#+-.!|<>~".Contains(ch);
    }

    /// <summary>
    /// Markdown 本文のリンクと画像を、描画と同じ解析で取り出します（コードブロック・インラインコード内は除く）。
    /// </summary>
    /// <param name="markdown">Markdown 本文。</param>
    /// <returns>画像なら IsImage=true とリンク先の組の列（出現順）。</returns>
    internal static List<(bool IsImage, string Target)> ExtractLinks(string markdown)
    {
        var result = new List<(bool IsImage, string Target)>();
        string? openFence = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var fence = FenceRegex.Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                if (openFence is null)
                {
                    openFence = marker;
                }
                else if (openFence == marker)
                {
                    openFence = null;
                }

                continue;
            }

            if (openFence is null)
            {
                CollectInlineLinks(line, result);
            }
        }

        return result;
    }

    /// <summary>
    /// 1 行分のインライン記法からリンクと画像を取り出します（ラベル内の画像も含めます）。
    /// </summary>
    /// <param name="text">対象テキスト。</param>
    /// <param name="result">見つけたリンクの追加先。</param>
    private static void CollectInlineLinks(string text, List<(bool IsImage, string Target)> result)
    {
        var p = 0;
        while (p < text.Length)
        {
            var ch = text[p];
            if (ch == '\\' && p + 1 < text.Length && IsEscapable(text[p + 1]))
            {
                p += 2;
                continue;
            }

            if (ch == '`')
            {
                var end = text.IndexOf('`', p + 1);
                if (end > p + 1)
                {
                    p = end + 1;
                    continue;
                }
            }

            if (ch == '!' && p + 1 < text.Length && text[p + 1] == '['
                && TryParseLink(text, p + 1, out _, out var src, out var afterImage))
            {
                result.Add((true, src));
                p = afterImage;
                continue;
            }

            if (ch == '[' && TryParseLink(text, p, out var label, out var url, out var afterLink))
            {
                CollectInlineLinks(label, result);
                result.Add((false, url));
                p = afterLink;
                continue;
            }

            p++;
        }
    }

    /// <summary>
    /// [ラベル](URL) 形式を解析します。ラベル内の入れ子の角括弧にも対応します。
    /// </summary>
    /// <param name="text">対象テキスト。</param>
    /// <param name="start">'[' の位置。</param>
    /// <param name="label">ラベル文字列。</param>
    /// <param name="url">URL（タイトル部分は除去）。</param>
    /// <param name="next">解析後の次の位置。</param>
    /// <returns>リンク形式として解析できた場合は true。</returns>
    private static bool TryParseLink(string text, int start, out string label, out string url, out int next)
    {
        label = string.Empty;
        url = string.Empty;
        next = start;

        var depth = 0;
        var close = -1;
        for (var p = start; p < text.Length; p++)
        {
            if (text[p] == '\\')
            {
                p++;
                continue;
            }

            if (text[p] == '[')
            {
                depth++;
            }
            else if (text[p] == ']' && --depth == 0)
            {
                close = p;
                break;
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return false;
        }

        label = text[(start + 1)..close];
        url = text[(close + 2)..end].Trim();
        var titleStart = url.IndexOf(" \"", StringComparison.Ordinal);
        if (titleStart >= 0)
        {
            url = url[..titleStart];
        }

        url = url.Trim('<', '>');
        next = end + 1;
        return true;
    }

    /// <summary>
    /// クリックでコールバックを呼ぶハイパーリンクを作成します。
    /// </summary>
    /// <param name="label">表示ラベル（インライン記法可）。</param>
    /// <param name="url">リンク先 URL。</param>
    /// <returns>クリックでコールバックを呼ぶ Hyperlink。</returns>
    private Hyperlink CreateHyperlink(string label, string url)
    {
        var link = new Hyperlink
        {
            Foreground = _linkBrush,
            ToolTip = url,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        link.Inlines.AddRange(ParseInlines(label));
        link.Click += (_, _) => _linkClicked(url);
        return link;
    }

    /// <summary>
    /// インラインコード（等幅・背景付き）の Run を作成します。
    /// </summary>
    /// <param name="code">コード文字列。</param>
    /// <returns>等幅・背景付きの Run。</returns>
    private Run CreateInlineCode(string code)
    {
        return new Run(code)
        {
            FontFamily = _codeFont,
            Foreground = _codeForeground,
            Background = _codeBackground,
        };
    }

    /// <summary>
    /// 画像を埋め込みます。読み込めない場合は代替テキストを表示します。
    /// </summary>
    /// <param name="alt">代替テキスト。</param>
    /// <param name="source">画像パス（基準フォルダーからの相対、または絶対パス）。</param>
    /// <returns>読み込めた画像の InlineUIContainer。失敗時は代替テキストの Run。</returns>
    private Inline CreateImage(string alt, string source)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(_baseDirectory, Uri.UnescapeDataString(source)));
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    MaxWidth = 900,
                    ToolTip = alt,
                };
                return new InlineUIContainer(image);
            }
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or ArgumentException)
        {
        }

        return new Run(Onta.View.Language.CoreViewText.ManualImagePlaceholder(alt)) { Foreground = _mutedBrush };
    }

    /// <summary>
    /// 空行（空白のみを含む行）かどうかを判定します。
    /// </summary>
    /// <param name="line">判定する行。</param>
    /// <returns>空白のみ、または空なら true。</returns>
    private static bool IsBlank(string line)
    {
        return string.IsNullOrWhiteSpace(line);
    }

    /// <summary>
    /// 行頭の空白数を返します。
    /// </summary>
    /// <param name="line">対象の行。</param>
    /// <returns>先頭の連続した半角スペースの数。</returns>
    private static int IndentOf(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 指定位置以降で最初の空行でない行番号を返します。
    /// </summary>
    /// <param name="lines">対象の行リスト。</param>
    /// <param name="start">探索を始める行番号（この行を含む）。</param>
    /// <returns>見つからなければ -1。</returns>
    private static int NextNonBlank(List<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (!IsBlank(lines[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// アプリのリソースからブラシを取得します。見つからない場合は既定色を使います。
    /// </summary>
    /// <param name="key">リソースキー。</param>
    /// <param name="fallback">リソースが無いときの色。</param>
    /// <returns>リソースの Brush、または凍結した単色ブラシ。</returns>
    private static Brush ResolveBrush(string key, Color fallback)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? Frozen(fallback);
    }

    /// <summary>
    /// 凍結済みの単色ブラシを作成します。
    /// </summary>
    /// <param name="color">ブラシの色。</param>
    /// <returns>Freeze 済みの SolidColorBrush。</returns>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
