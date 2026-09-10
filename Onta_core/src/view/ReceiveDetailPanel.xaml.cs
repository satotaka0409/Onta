using System.Windows.Controls;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信詳細パネルです（FH 受信後のファイルサイズ／ブロック数など）。
/// </summary>
public partial class ReceiveDetailPanel : UserControl
{
    public ReceiveDetailPanel()
    {
        InitializeComponent();
        Clear();
    }

    /// <summary>表示を未受信状態へ戻します。</summary>
    public void Clear()
    {
        FileNameBox.Text = "(未受信)";
        FileSizeBox.Text = "-";
        BlockCountBox.Text = "-";
    }

    /// <summary>コア実行状況から詳細表示を更新します。</summary>
    public void ApplyStatus(CoreExecutionStatus status)
    {
        FileNameBox.Text = string.IsNullOrWhiteSpace(status.FileName) ? "(未受信)" : status.FileName;
        FileSizeBox.Text = string.IsNullOrWhiteSpace(status.FileSizeText) ? "-" : status.FileSizeText;
        BlockCountBox.Text = string.IsNullOrWhiteSpace(status.BlockCountText) ? "-" : status.BlockCountText;
    }

    /// <summary>FH 由来のファイル情報が揃っているか。</summary>
    public static bool HasFileHeaderInfo(CoreExecutionStatus status)
    {
        return status.Progress.TotalBlockCount > 0
               && !string.IsNullOrWhiteSpace(status.FileSizeText)
               && status.FileSizeText != "-";
    }
}
