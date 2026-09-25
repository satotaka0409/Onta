using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Onta.Core;
using Onta.History;

namespace Onta.View.Core;

/// <summary>
/// 受信の詳細進捗（FH/各ブロック/合計）を表示・履歴化するパネルです。
/// </summary>
public partial class ReceiveDetailPanel : UserControl
{
    private const int FileHeaderBytes = 880;

    private readonly ObservableCollection<DetailRow> _rows = [];
    private DetailRow? _fhRow;
    private DetailRow? _fileSizeRow;
    private DetailRow? _blockCountRow;
    private DetailRow? _totalRow;
    private readonly List<DetailRow> _blockRows = [];
    private readonly Dictionary<string, DetailRow> _orphanRowsByHash = new(StringComparer.Ordinal);
    private ReceiveBlockState[] _blockStates = Array.Empty<ReceiveBlockState>();
    private string[] _blockErrors = Array.Empty<string>();
    private string _sourcePath = string.Empty;
    private string _fileName = string.Empty;
    private bool _lastCompletedSuccess;
    private string _lastCompletionMessage = string.Empty;
    private string _lastOutputPath = string.Empty;
    private DateTime _sourceCreatedAtUtc;
    private DateTime _sourceUpdatedAtUtc;
    private int _builtBlockCount = -1;
    private bool _awaitingProgressReset;

    /// <summary>
    /// 受信詳細パネルを初期化します。
    /// </summary>
    public ReceiveDetailPanel()
    {
        InitializeComponent();
        DetailGrid.ItemsSource = _rows;
        Clear();
    }

    /// <summary>
    /// 表示状態を初期化します。
    /// </summary>
    public void Clear()
    {
        _rows.Clear();
        _blockRows.Clear();
        _orphanRowsByHash.Clear();
        _blockStates = Array.Empty<ReceiveBlockState>();
        _blockErrors = Array.Empty<string>();
        _sourcePath = string.Empty;
        _fileName = string.Empty;
        _lastCompletedSuccess = false;
        _lastCompletionMessage = string.Empty;
        _lastOutputPath = string.Empty;
        _sourceCreatedAtUtc = DateTime.MinValue;
        _sourceUpdatedAtUtc = DateTime.MinValue;
        _fhRow = null;
        _fileSizeRow = null;
        _blockCountRow = null;
        _totalRow = null;
        _builtBlockCount = -1;
        _awaitingProgressReset = true;
    }

    /// <summary>
    /// 同一ファイル再受信時にメーター／ブロック状態を必ずやり直します。
    /// </summary>
    private void ResetBlockProgressUi()
    {
        for (var i = 0; i < _blockStates.Length; i++)
        {
            _blockStates[i] = ReceiveBlockState.Unknown;
            _blockErrors[i] = string.Empty;
            if (i < _blockRows.Count)
            {
                _blockRows[i].SetResult("-");
                _blockRows[i].SetProgressPercent(0, force: true);
            }
        }

        if (_fhRow is not null)
        {
            _fhRow.SetProgressPercent(0, force: true);
        }

        if (_totalRow is not null)
        {
            _totalRow.SetResult("-");
            _totalRow.SetProgressPercent(0, force: true);
        }
    }

    /// <summary>
    /// 再受信直後の進捗リセットが未実施なら実行します。
    /// </summary>
    private void ResetBlockProgressUiIfNeeded()
    {
        if (!_awaitingProgressReset)
        {
            return;
        }

        ResetBlockProgressUi();
        _awaitingProgressReset = false;
    }

    /// <summary>
    /// 履歴保存時に使う入力ソースパスを設定します。
    /// </summary>
    /// <param name="sourcePath">入力ソースパス。</param>
    public void SetSourcePath(string sourcePath)
    {
        _sourcePath = sourcePath ?? string.Empty;
    }

    /// <summary>
    /// 直近完了結果を保持します。
    /// </summary>
    /// <param name="success">成功可否。</param>
    /// <param name="message">完了メッセージ。</param>
    /// <param name="outputPath">出力パス。</param>
    public void MarkCompletion(bool success, string message, string? outputPath)
    {
        _lastCompletedSuccess = success;
        _lastCompletionMessage = message ?? string.Empty;
        _lastOutputPath = outputPath ?? string.Empty;
    }

    /// <summary>
    /// FH確定情報を受信詳細へ反映します。
    /// </summary>
    /// <param name="fileName">受信ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ。</param>
    /// <param name="blockCount">ブロック数。</param>
    /// <param name="createdAtUtc">ファイル作成日時（UTC）。不明時は null。</param>
    /// <param name="updatedAtUtc">ファイル更新日時（UTC）。不明時は null。</param>
    public void ApplyFileHeader(
        string fileName,
        string fileSizeText,
        int blockCount,
        DateTime? createdAtUtc,
        DateTime? updatedAtUtc)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "(不明)" : fileName;
        var size = NormalizeSizeText(fileSizeText);
        var blocks = Math.Max(0, blockCount);
        EnsureRows(name, size, blocks > 0 ? blocks.ToString() : "-", blocks);
        _sourceCreatedAtUtc = createdAtUtc?.ToUniversalTime() ?? DateTime.MinValue;
        _sourceUpdatedAtUtc = updatedAtUtc?.ToUniversalTime() ?? DateTime.MinValue;
        ResetBlockProgressUiIfNeeded();
        if (_fhRow is not null)
        {
            ApplyFileHeaderDefaults(_fhRow, ready: true);
            _fhRow.SetProgressPercent(100);
        }

        if (_totalRow is not null)
        {
            _totalRow.SetSize(size);
        }
    }

    /// <summary>
    /// Core の受信状態を詳細表示へ反映します。
    /// </summary>
    /// <param name="status">Core 実行状態。</param>
    public void ApplyStatus(CoreExecutionStatus status)
    {
        // FH未確定中は既存行がある場合のみ進捗を緩やかに更新する。
        if (!HasFileHeaderInfo(status))
        {
            if (_builtBlockCount < 0 && _rows.Count == 0)
            {
                return;
            }

            // 既に FH OK なら全体進捗でメーターを上書きしない。
            if (_fhRow is not null
                && string.Equals(_fhRow.ResultText, "OK", StringComparison.Ordinal))
            {
                EnsureFileHeaderMeterComplete();
                return;
            }

            if (_fhRow is not null && status.IsRunning)
            {
                _fhRow.SetProgressPercent(Math.Clamp(status.Progress.ProgressPercent, 0.0, 99.0));
            }

            return;
        }

        var fileName = string.IsNullOrWhiteSpace(status.FileName) || status.FileName == "(未受信)"
            ? "(不明)"
            : status.FileName;
        var fileSize = NormalizeSizeText(status.FileSizeText);
        var blockCountText = string.IsNullOrWhiteSpace(status.BlockCountText) ? "-" : status.BlockCountText;
        var totalBlocks = Math.Max(0, status.Progress.TotalBlockCount);

        EnsureRows(fileName, fileSize, blockCountText, totalBlocks);
        ResetBlockProgressUiIfNeeded();

        // FH行はブロック総数の確定有無で表示を切り替える。
        if (_fhRow is not null)
        {
            if (totalBlocks > 0)
            {
                ApplyFileHeaderDefaults(_fhRow, ready: true);
            }
            else if (status.IsRunning)
            {
                ApplyFileHeaderDefaults(_fhRow, ready: false);
                _fhRow.SetProgressPercent(Math.Clamp(status.Progress.ProgressPercent, 0.0, 99.0));
            }
            else
            {
                ApplyFileHeaderDefaults(_fhRow, ready: false);
                _fhRow.SetProgressPercent(0, force: true);
            }
        }

        EnsureFileHeaderMeterComplete();

        // 現在処理中のブロックとフレーム種別を取得する。
        var currentBlock = status.Progress.CurrentBlockIndex;
        var frame = status.Progress.CurrentFrame;
        var passIndex = Math.Max(0, status.Progress.PassIndex);
        var emissionOrder = _blockRows.Count > 0
            ? FileWavCodec.GetBlockEmissionOrder(_blockRows.Count, passIndex)
            : [];
        var currentOrdinal = -1;
        if (currentBlock >= 0
            && frame is CoreFrameKind.Bh or CoreFrameKind.Bd
            && emissionOrder.Length > 0)
        {
            currentOrdinal = Array.IndexOf(emissionOrder, currentBlock);
        }

        // OK 判定は実受信ペイロード同期（SyncCapturedBlocks）に任せ、
        // ここでのエラー率ヒューリスティックでは Accepted にしない。
        if (frame == CoreFrameKind.Bd
            && currentBlock >= 0
            && currentBlock < _blockRows.Count
            && status.ErrorRate.FrameKind == CoreFrameKind.Bd
            && status.ErrorRate.LatestPercent >= 99.9
            && !string.IsNullOrWhiteSpace(status.LastError))
        {
            SetBlockError(currentBlock, status.LastError);
        }

        for (var i = 0; i < _blockRows.Count; i++)
        {
            if (_blockStates[i] == ReceiveBlockState.Accepted
                || _blockStates[i] == ReceiveBlockState.Error)
            {
                // BD 処理済みはメーターを完了表示のまま維持する。
                _blockRows[i].SetProgressPercent(100);
                continue;
            }

            var isCurrent = status.IsRunning
                            && i == currentBlock
                            && frame is CoreFrameKind.Bh or CoreFrameKind.Bd;

            if (isCurrent)
            {
                var local = status.Progress.CurrentBlockProgressPercent;
                if (local <= 0.0)
                {
                    local = frame == CoreFrameKind.Bh ? 20.0 : 50.0;
                }

                _blockRows[i].SetProgressPercent(Math.Clamp(local, 1.0, 99.0), force: true);

                if (status.ErrorRate.FrameKind == CoreFrameKind.Bd
                    && status.ErrorRate.LatestPercent >= 99.9
                    && !string.IsNullOrWhiteSpace(status.LastError))
                {
                    SetBlockError(i, status.LastError);
                }

                continue;
            }

            // 送信順で現在より前のブロックは通過済み。ポーリング間隔で取りこぼしてもメーターを消さない。
            var ordinal = emissionOrder.Length > 0 ? Array.IndexOf(emissionOrder, i) : i;
            if (status.IsRunning
                && currentOrdinal >= 0
                && ordinal >= 0
                && ordinal < currentOrdinal)
            {
                if (_blockRows[i].ProgressPercent < 99.0)
                {
                    _blockRows[i].SetProgressPercent(99.0);
                }

                continue;
            }

            // BH 受信済み（サイズが埋まっている）ブロックは進捗を 0 に戻さない。
            if (!string.IsNullOrWhiteSpace(_blockRows[i].SizeText)
                && _blockRows[i].SizeText != "-")
            {
                if (_blockRows[i].ProgressPercent < 20.0)
                {
                    _blockRows[i].SetProgressPercent(20.0);
                }

                continue;
            }

            // 未着手（現在より後ろ）だけ 0 に戻す。FH 中は触らない。
            if (frame is CoreFrameKind.Bh or CoreFrameKind.Bd
                && _blockRows[i].ProgressPercent > 0.0)
            {
                _blockRows[i].SetProgressPercent(0, force: true);
            }
        }

        if (_totalRow is not null)
        {
            _totalRow.SetSize(fileSize);

            if (!status.IsRunning && !status.IsCompleted)
            {
                _totalRow.SetProgressPercent(0, force: true);
            }
            else if (status.IsCompleted && !status.IsFaulted)
            {
                _totalRow.SetResult("OK");
                _totalRow.SetProgressPercent(100);
            }
            else if (status.IsFaulted)
            {
                _totalRow.SetResult("NG");
                _totalRow.SetProgressPercent(Math.Clamp(status.Progress.ProgressPercent, 0.0, 100.0));
            }
            else
            {
                _totalRow.SetProgressPercent(Math.Clamp(status.Progress.ProgressPercent, 0.0, 100.0));
            }
        }

        if (!status.IsRunning && !status.IsCompleted && !string.IsNullOrWhiteSpace(status.LastError))
        {
            var index = status.Progress.CurrentBlockIndex;
            if (index >= 0 && index < _blockRows.Count)
            {
                SetBlockError(index, status.LastError);
            }
        }

        if (!string.IsNullOrWhiteSpace(status.LastError)
            && (status.LastError.StartsWith("ORPHAN ", StringComparison.Ordinal)
                || status.LastError.StartsWith("ORPHAN-RESOLVED", StringComparison.Ordinal)))
        {
            UpdateOrphanRowsFromStatus(status.LastError);
        }
    }

    /// <summary>
    /// 現在の受信詳細から履歴エントリを生成します。実ペイロードがあるブロックのみ完了扱いとします。
    /// </summary>
    /// <param name="orphans">親未確定の孤立ブロック一覧。null 可。</param>
    /// <param name="capturedBlocks">受信済みブロック（ペイロード付き）。null 可。</param>
    /// <param name="fileHashHex">ファイル全体ハッシュ（SHA-512 16進）。null 可。</param>
    /// <param name="capturedHeaders">BH 由来のブロックメタ。null 可。</param>
    /// <returns>履歴保存用の受信エントリ。</returns>
    internal ReceiveHistoryEntry CaptureHistoryEntry(
        IReadOnlyList<ReceiveOrphanHistory>? orphans = null,
        IReadOnlyDictionary<int, ReceiveCapturedBlockInfo>? capturedBlocks = null,
        string? fileHashHex = null,
        IReadOnlyDictionary<int, ReceiveCapturedBlockHeaderInfo>? capturedHeaders = null)
    {
        var fileName = _fileName;
        if (string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, "(Not received)", StringComparison.Ordinal)
            || string.Equals(fileName, "(未受信)", StringComparison.Ordinal)
            || string.Equals(fileName, "(FH待ち)", StringComparison.Ordinal))
        {
            fileName = "(未登録データ)";
        }

        var fileSizeText = _fileSizeRow?.SizeText ?? "-";
        var blockCountText = _blockCountRow?.SizeText ?? "-";
        var fileSize = ParseFileSize(fileSizeText);
        var blockCount = ParseBlockCount(blockCountText, _blockStates.Length);

        var blocks = new List<ReceiveBlockHistory>(Math.Max(_blockStates.Length, blockCount));
        var maxIndex = Math.Max(_blockStates.Length, blockCount);
        for (var i = 0; i < maxIndex; i++)
        {
            var hasCapture = capturedBlocks is not null
                && capturedBlocks.TryGetValue(i, out var captured)
                && captured.BlockData.Length > 0;
            var hasHeader = capturedHeaders is not null
                && capturedHeaders.TryGetValue(i, out var header);

            // 実ペイロードがあるときだけ完了。UI の Accepted 推定だけでは OK にしない。
            if (hasCapture)
            {
                var capturedInfo = capturedBlocks![i];
                var dataModulation = new byte[4];
                var contentHash = new byte[32];
                Buffer.BlockCopy(
                    capturedInfo.DataModulation,
                    0,
                    dataModulation,
                    0,
                    Math.Min(4, capturedInfo.DataModulation.Length));
                Buffer.BlockCopy(
                    capturedInfo.ContentHash,
                    0,
                    contentHash,
                    0,
                    Math.Min(32, capturedInfo.ContentHash.Length));
                var blockData = capturedInfo.BlockData.ToArray();
                blocks.Add(new ReceiveBlockHistory(
                    DataModulation: dataModulation,
                    BlockIndex: i,
                    BlockSize: blockData.Length,
                    ContentHash: contentHash,
                    BlockComplete: true,
                    BlockData: blockData,
                    State: ReceiveBlockState.Accepted,
                    ErrorText: string.Empty));
                continue;
            }

            var uiState = i < _blockStates.Length ? _blockStates[i] : ReceiveBlockState.Unknown;
            if (uiState == ReceiveBlockState.Unknown && !hasHeader)
            {
                continue;
            }

            // Accepted だがペイロード無し、または BH のみ → 未完了として保存する。
            var state = uiState == ReceiveBlockState.Accepted
                ? ReceiveBlockState.Error
                : (uiState == ReceiveBlockState.Unknown ? ReceiveBlockState.Unknown : uiState);
            var dataModulationMeta = new byte[4];
            var contentHashMeta = new byte[32];
            var declaredSize = 0;
            if (hasHeader)
            {
                var headerInfo = capturedHeaders![i];
                Buffer.BlockCopy(
                    headerInfo.DataModulation,
                    0,
                    dataModulationMeta,
                    0,
                    Math.Min(4, headerInfo.DataModulation.Length));
                Buffer.BlockCopy(
                    headerInfo.ContentHash,
                    0,
                    contentHashMeta,
                    0,
                    Math.Min(32, headerInfo.ContentHash.Length));
                declaredSize = Math.Max(0, headerInfo.BlockSize);
            }

            blocks.Add(new ReceiveBlockHistory(
                DataModulation: dataModulationMeta,
                BlockIndex: i,
                BlockSize: declaredSize,
                ContentHash: contentHashMeta,
                BlockComplete: false,
                BlockData: Array.Empty<byte>(),
                State: state == ReceiveBlockState.Unknown ? ReceiveBlockState.Error : state,
                ErrorText: i < _blockErrors.Length ? _blockErrors[i] : string.Empty));
        }

        return new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Receive,
            InputDevice: string.IsNullOrWhiteSpace(_sourcePath) ? ReceiveInputDevice.Audio : ReceiveInputDevice.Wav,
            DataModulation: new byte[4],
            ReceivedAtUtc: DateTime.Now,
            CreatedAtUtc: _sourceCreatedAtUtc == DateTime.MinValue ? DateTime.Now : _sourceCreatedAtUtc.ToLocalTime(),
            UpdatedAtUtc: _sourceUpdatedAtUtc == DateTime.MinValue ? DateTime.Now : _sourceUpdatedAtUtc.ToLocalTime(),
            ContentHashHex: ResolveReceiveHash(fileHashHex, orphans),
            SourcePath: _sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: blockCount,
            IsSuccess: _lastCompletedSuccess,
            OutputPath: _lastOutputPath,
            CompletionMessage: _lastCompletionMessage,
            Blocks: blocks,
            Orphans: orphans ?? Array.Empty<ReceiveOrphanHistory>());
    }

    /// <summary>
    /// 履歴用のファイルハッシュ（SHA-512 16進）を解決します。コア値が無ければ孤立キーから拾います。
    /// </summary>
    /// <param name="fileHashHex">コアが保持するファイルハッシュ。null／空可。</param>
    /// <param name="orphans">孤立ブロック一覧。ファイルハッシュ抽出のフォールバック。</param>
    /// <returns>大文字16進ハッシュ。解決できなければ空文字。</returns>
    private static string ResolveReceiveHash(
        string? fileHashHex,
        IReadOnlyList<ReceiveOrphanHistory>? orphans)
    {
        if (!string.IsNullOrWhiteSpace(fileHashHex)
            && LooksLikeHexHash(fileHashHex))
        {
            return fileHashHex.Trim().ToUpperInvariant();
        }

        if (orphans is not null)
        {
            foreach (var orphan in orphans)
            {
                if (TrySplitOrphanIdentity(orphan.HashHex, out _, out var orphanFileHash, out _)
                    && LooksLikeHexHash(orphanFileHash))
                {
                    return orphanFileHash.ToUpperInvariant();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 孤立キー `index:fileHash:blockHash` を分解します。
    /// </summary>
    /// <param name="identity">コロン区切りの孤立識別子。</param>
    /// <param name="blockIndexText">分解後のブロック番号文字列。</param>
    /// <param name="fileHashHex">分解後のファイルハッシュ。</param>
    /// <param name="blockHashHex">分解後のブロックハッシュ。</param>
    /// <returns>形式とハッシュ長が妥当なら true。</returns>
    internal static bool TrySplitOrphanIdentity(
        string? identity,
        out string blockIndexText,
        out string fileHashHex,
        out string blockHashHex)
    {
        blockIndexText = string.Empty;
        fileHashHex = string.Empty;
        blockHashHex = string.Empty;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        var parts = identity.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        blockIndexText = parts[0];
        fileHashHex = parts[1];
        blockHashHex = parts[2];
        return LooksLikeHexHash(fileHashHex) && LooksLikeHexHash(blockHashHex);
    }

    /// <summary>
    /// 16進ハッシュ文字列かどうかを判定します（長さ 64 または 128）。
    /// </summary>
    /// <param name="value">判定対象文字列。</param>
    /// <returns>SHA-256/SHA-512 相当の16進なら true。</returns>
    private static bool LooksLikeHexHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();
        if (s.Length is not (64 or 128))
        {
            return false;
        }

        foreach (var c in s)
        {
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 受信履歴エントリを詳細グリッドへ反映します。
    /// </summary>
    /// <param name="history">反映する受信履歴エントリ。</param>
    internal void ApplyHistory(ReceiveHistoryEntry history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _sourcePath = history.SourcePath;
        _sourceCreatedAtUtc = history.CreatedAtUtc;
        _sourceUpdatedAtUtc = history.UpdatedAtUtc;
        _lastCompletedSuccess = history.IsSuccess;
        _lastCompletionMessage = history.CompletionMessage;
        _lastOutputPath = history.OutputPath;

        var blockCount = Math.Max(0, history.BlockCount);
        var fileSizeText = history.FileSize > 0 ? history.FileSize.ToString("N0") : "-";
        EnsureRows(history.FileName, fileSizeText, blockCount.ToString(), blockCount);

        for (var i = 0; i < _blockRows.Count; i++)
        {
            _blockRows[i].ProgressPercent = 0;
            _blockRows[i].SetResult("-");
            _blockRows[i].SetSize("-");
            _blockRows[i].SetModulationFields("-", "-", "-");
            _blockStates[i] = ReceiveBlockState.Unknown;
            _blockErrors[i] = string.Empty;
        }

        _orphanRowsByHash.Clear();

        foreach (var block in history.Blocks)
        {
            if (block.BlockIndex < 0 || block.BlockIndex >= _blockRows.Count)
            {
                continue;
            }

            if (block.BlockComplete && (block.BlockSize > 0 || block.BlockData.Length > 0))
            {
                SetBlockAccepted(block.BlockIndex);
                ApplyBlockDisplayMeta(
                    block.BlockIndex,
                    block.DataModulation,
                    block.BlockSize > 0 ? block.BlockSize : block.BlockData.Length);
                _blockRows[block.BlockIndex].ProgressPercent = 100;
            }
            else if (block.State == ReceiveBlockState.Error || !block.BlockComplete)
            {
                SetBlockError(block.BlockIndex, block.ErrorText);
                ApplyBlockDisplayMeta(block.BlockIndex, block.DataModulation, block.BlockSize);
                _blockRows[block.BlockIndex].ProgressPercent = 0;
            }
        }

        foreach (var orphan in history.Orphans)
        {
            var detail = string.IsNullOrWhiteSpace(orphan.Detail) ? "親不明" : orphan.Detail;
            AddOrUpdateOrphanRow(orphan.HashHex, $"親不明 / {detail} / {orphan.Payload.Length} bytes");
        }

        if (_fhRow is not null)
        {
            ApplyFileHeaderDefaults(_fhRow, ready: true);
            _fhRow.ProgressPercent = 100;
        }

        if (_totalRow is not null)
        {
            _totalRow.SetSize(fileSizeText);
            _totalRow.ProgressPercent = history.IsSuccess ? 100 : 0;
            _totalRow.SetResult(history.IsSuccess ? "OK" : "NG");
        }
    }

    /// <summary>
    /// FH情報が揃っている状態かを判定します。
    /// </summary>
    /// <param name="status">Core 実行状態。</param>
    /// <returns>FH情報が揃っていれば true。</returns>
    public static bool HasFileHeaderInfo(CoreExecutionStatus status)
    {
        return status.Progress.TotalBlockCount > 0
               && !string.IsNullOrWhiteSpace(status.FileSizeText)
               && status.FileSizeText != "-"
               && !string.IsNullOrWhiteSpace(status.FileName)
               && status.FileName != "(未受信)"
               && status.FileName != "(FH待ち)";
    }

    /// <summary>
    /// FH / ファイルサイズ / ブロック数 / BLK / Total の行構成を整えます。
    /// </summary>
    /// <param name="fileName">表示用ファイル名。</param>
    /// <param name="fileSize">表示用ファイルサイズ。</param>
    /// <param name="blockCountText">ブロック数の表示テキスト（totalBlocks が 0 のとき使用）。</param>
    /// <param name="totalBlocks">構築する BLK 行数。</param>
    private void EnsureRows(string fileName, string fileSize, string blockCountText, int totalBlocks)
    {
        _fileName = fileName;

        if (_builtBlockCount == totalBlocks && _rows.Count > 0 && _fhRow is not null)
        {
            // ブロック構成が同じなら情報行のみ更新する。
            _fileSizeRow?.SetSize(NormalizeSizeText(fileSize));
            _blockCountRow?.SetSize(totalBlocks > 0 ? totalBlocks.ToString() : blockCountText);
            _totalRow?.SetSize(NormalizeSizeText(fileSize));
            return;
        }

        _rows.Clear();
        _blockRows.Clear();
        _orphanRowsByHash.Clear();
        _blockStates = new ReceiveBlockState[totalBlocks];
        _blockErrors = new string[totalBlocks];
        _fhRow = null;
        _fileSizeRow = null;
        _blockCountRow = null;
        _totalRow = null;
        _builtBlockCount = totalBlocks;

        _fhRow = DetailRow.Segment("ファイルヘッダ");
        ApplyFileHeaderDefaults(_fhRow, ready: totalBlocks > 0);
        _rows.Add(_fhRow);

        _fileSizeRow = DetailRow.Info("ファイルサイズ", NormalizeSizeText(fileSize));
        _rows.Add(_fileSizeRow);

        _blockCountRow = DetailRow.Info(
            "ブロック数",
            totalBlocks > 0 ? totalBlocks.ToString() : blockCountText);
        _rows.Add(_blockCountRow);

        for (var i = 0; i < totalBlocks; i++)
        {
            var row = DetailRow.Segment($"BLK-{i}");
            _blockRows.Add(row);
            _rows.Add(row);
        }

        _totalRow = DetailRow.Segment("Total");
        _totalRow.SetSize(NormalizeSizeText(fileSize));
        _rows.Add(_totalRow);
    }

    /// <summary>
    /// FH行の既定メタ（mono / 8 / BPSK / 880）と結果を設定します。
    /// </summary>
    /// <param name="fhRow">ファイルヘッダ行。</param>
    /// <param name="ready">true なら結果 OK・メーター 100%、false なら結果「-」。</param>
    private static void ApplyFileHeaderDefaults(DetailRow fhRow, bool ready)
    {
        fhRow.SetSize(FileHeaderBytes.ToString("N0"));
        fhRow.SetModulationFields("mono", "8", "BPSK");
        fhRow.SetResult(ready ? "OK" : "-");
        if (ready)
        {
            fhRow.SetProgressPercent(100);
        }
    }

    /// <summary>
    /// FH が確定済みならメーターを 100% に固定します。
    /// </summary>
    private void EnsureFileHeaderMeterComplete()
    {
        if (_fhRow is null)
        {
            return;
        }

        if (string.Equals(_fhRow.ResultText, "OK", StringComparison.Ordinal))
        {
            _fhRow.SetProgressPercent(100);
        }
    }

    /// <summary>
    /// ステータス文字列から孤立ブロック行を更新します。
    /// </summary>
    /// <param name="statusError">ORPHAN / ORPHAN-RESOLVED を含む LastError 文字列。</param>
    private void UpdateOrphanRowsFromStatus(string statusError)
    {
        var hash = ExtractHash(statusError);
        if (string.IsNullOrWhiteSpace(hash))
        {
            hash = "UNKNOWN";
        }

        var detail = statusError.StartsWith("ORPHAN-RESOLVED", StringComparison.Ordinal)
            ? "解決済み"
            : "親不明";
        AddOrUpdateOrphanRow(hash, detail + " / " + statusError);
    }

    /// <summary>
    /// 孤立ブロック情報行を追加または更新します。
    /// </summary>
    /// <param name="hash">孤立キー（または UNKNOWN）。</param>
    /// <param name="detail">結果列に表示する詳細文言。</param>
    private void AddOrUpdateOrphanRow(string hash, string detail)
    {
        var key = string.IsNullOrWhiteSpace(hash) ? "UNKNOWN" : hash;
        if (_orphanRowsByHash.TryGetValue(key, out var existing))
        {
            existing.SetResult(Truncate(detail, 64));
            return;
        }

        var row = DetailRow.Info(
            $"ORPHAN-{Truncate(key, 12)}",
            Truncate(detail, 64),
            "-",
            "-",
            "-",
            "-");
        _orphanRowsByHash[key] = row;
        _rows.Add(row);
    }

    /// <summary>
    /// ステータス文字列から hash= 以降の16進値を取り出します。
    /// </summary>
    /// <param name="text">hash= を含む文字列。</param>
    /// <returns>抽出した16進文字列。見つからなければ空。</returns>
    private static string ExtractHash(string text)
    {
        var marker = "hash=";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return string.Empty;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < text.Length)
        {
            var c = text[end];
            if (!Uri.IsHexDigit(c))
            {
                break;
            }

            end++;
        }

        return text.Substring(start, end - start);
    }

    /// <summary>
    /// 表示用サイズ文字列を N0（bytes なし）へ正規化します。
    /// </summary>
    /// <param name="fileSizeText">元のサイズ表示（数字や "bytes" 付き可）。</param>
    /// <returns>桁区切り数値、または "-"。</returns>
    private static string NormalizeSizeText(string fileSizeText)
    {
        if (string.IsNullOrWhiteSpace(fileSizeText) || fileSizeText == "-")
        {
            return "-";
        }

        var size = ParseFileSize(fileSizeText);
        return size > 0 ? size.ToString("N0") : "-";
    }

    /// <summary>
    /// ファイルサイズ表示から数値を取り出します。
    /// </summary>
    /// <param name="text">数字を含むサイズ文字列。</param>
    /// <returns>抽出したバイト数。解析失敗時は 0。</returns>
    private static long ParseFileSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    /// <summary>
    /// ブロック数テキストを整数へ変換します。
    /// </summary>
    /// <param name="text">ブロック数の表示文字列。</param>
    /// <param name="fallback">解析失敗時の代替値。</param>
    /// <returns>非負のブロック数。失敗時は fallback。</returns>
    private static int ParseBlockCount(string text, int fallback)
    {
        if (int.TryParse(text, out var value) && value >= 0)
        {
            return value;
        }

        return fallback;
    }

    /// <summary>
    /// ブロック行を受理済み（OK）としてマークします。
    /// </summary>
    /// <param name="index">ブロック番号（0 始まり）。</param>
    private void SetBlockAccepted(int index)
    {
        if (index < 0 || index >= _blockRows.Count)
        {
            return;
        }

        _blockStates[index] = ReceiveBlockState.Accepted;
        _blockErrors[index] = string.Empty;
        _blockRows[index].SetResult("OK");
        _blockRows[index].SetProgressPercent(100);
    }

    /// <summary>
    /// コアが受理したブロックを詳細行へ反映します。
    /// </summary>
    /// <param name="capturedBlocks">受信済みブロック（ペイロード付き）。</param>
    internal void SyncCapturedBlocks(IReadOnlyDictionary<int, ReceiveCapturedBlockInfo>? capturedBlocks)
    {
        if (capturedBlocks is null || capturedBlocks.Count == 0 || _blockRows.Count == 0)
        {
            return;
        }

        foreach (var pair in capturedBlocks)
        {
            if (pair.Value.BlockData.Length <= 0)
            {
                continue;
            }

            SetBlockAccepted(pair.Key);
            ApplyBlockDisplayMeta(pair.Key, pair.Value.DataModulation, pair.Value.BlockData.Length);
        }
    }

    /// <summary>
    /// BH 受信済みメタを詳細行へ反映します（BD 未受信でも表示）。
    /// </summary>
    /// <param name="headers">BH 由来の変調・サイズ。</param>
    internal void SyncBlockHeaders(IReadOnlyDictionary<int, ReceiveCapturedBlockHeaderInfo>? headers)
    {
        if (headers is null || headers.Count == 0 || _blockRows.Count == 0)
        {
            return;
        }

        foreach (var pair in headers)
        {
            if (pair.Key < 0 || pair.Key >= _blockRows.Count)
            {
                continue;
            }

            // 受理済みは SyncCapturedBlocks 側のサイズを優先するため、未受理のみ BH 宣言サイズを出す。
            if (_blockStates[pair.Key] == ReceiveBlockState.Accepted)
            {
                var (channel, sc, mod) = FormatDataModulation(pair.Value.DataModulation);
                _blockRows[pair.Key].SetModulationFields(channel, sc, mod);
                continue;
            }

            ApplyBlockDisplayMeta(pair.Key, pair.Value.DataModulation, pair.Value.BlockSize);
        }
    }

    /// <summary>
    /// BD 処理結果を詳細行の結果列へ反映します。
    /// </summary>
    /// <param name="outcomes">ブロック番号 → true=OK / false=NG。</param>
    internal void SyncBlockBdOutcomes(IReadOnlyDictionary<int, bool>? outcomes)
    {
        if (outcomes is null || outcomes.Count == 0 || _blockRows.Count == 0)
        {
            return;
        }

        foreach (var pair in outcomes)
        {
            var index = pair.Key;
            if (index < 0 || index >= _blockRows.Count)
            {
                continue;
            }

            if (pair.Value)
            {
                SetBlockAccepted(index);
                _blockRows[index].SetProgressPercent(100);
            }
            else
            {
                SetBlockError(index, "NG");
                _blockRows[index].SetProgressPercent(100);
            }
        }
    }

    /// <summary>
    /// ブロック行へサイズと変調メタを反映します。
    /// </summary>
    /// <param name="index">ブロック番号。</param>
    /// <param name="dataModulation">データ部変調方式 4 バイト。null 可。</param>
    /// <param name="payloadBytes">表示するブロックサイズ（バイト）。</param>
    private void ApplyBlockDisplayMeta(int index, byte[]? dataModulation, int payloadBytes)
    {
        if (index < 0 || index >= _blockRows.Count)
        {
            return;
        }

        _blockRows[index].SetSize(payloadBytes > 0 ? payloadBytes.ToString("N0") : "-");
        var (channel, sc, mod) = FormatDataModulation(dataModulation);
        _blockRows[index].SetModulationFields(channel, sc, mod);
    }

    /// <summary>
    /// DataModulation バイト列を表示文言へ変換します。
    /// </summary>
    /// <param name="dataModulation">[0]=SC数 [1]=変調 [2]=mono/stereo。null 可。</param>
    /// <returns>チャネル・サブキャリア数・変調の表示文字列タプル。</returns>
    private static (string ChannelText, string SubcarrierText, string ModulationText) FormatDataModulation(
        byte[]? dataModulation)
    {
        if (dataModulation is null || dataModulation.Length < 3)
        {
            return ("-", "-", "-");
        }

        var sc = OfdmConfig.IsSupportedActiveSubcarriers(dataModulation[0])
            ? dataModulation[0].ToString()
            : "-";
        var modulation = dataModulation[1] switch
        {
            1 => "BPSK",
            2 => "QPSK",
            6 => "8PSK",
            3 => "16QAM",
            4 => "64QAM",
            5 => "256QAM",
            _ => "-"
        };
        var channel = dataModulation[2] switch
        {
            0 => "mono",
            1 => "stereo",
            _ => "-"
        };

        return (channel, sc, modulation);
    }

    /// <summary>
    /// ブロック行をエラー（NG）としてマークします。受理済みは上書きしません。
    /// </summary>
    /// <param name="index">ブロック番号。</param>
    /// <param name="error">エラー文言。空や "NG" は結果列を "NG" にします。</param>
    private void SetBlockError(int index, string error)
    {
        if (index < 0 || index >= _blockRows.Count)
        {
            return;
        }

        // 実ペイロード受理済みは上書きしない。
        if (_blockStates[index] == ReceiveBlockState.Accepted)
        {
            return;
        }

        _blockStates[index] = ReceiveBlockState.Error;
        _blockErrors[index] = error ?? string.Empty;
        var trimmed = (error ?? string.Empty).Trim();
        string detail;
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "NG", StringComparison.Ordinal))
        {
            detail = "NG";
        }
        else if (trimmed.StartsWith("NG:", StringComparison.OrdinalIgnoreCase)
                 || trimmed.StartsWith("NG：", StringComparison.Ordinal))
        {
            detail = Truncate(trimmed, 64);
        }
        else
        {
            detail = $"NG: {Truncate(trimmed, 64)}";
        }

        _blockRows[index].SetResult(detail);
    }

    /// <summary>
    /// 文字列を指定長へ切り詰めます。超過時は末尾に "..." を付けます。
    /// </summary>
    /// <param name="text">元文字列。</param>
    /// <param name="maxLength">最大文字数（"..." 込み）。</param>
    /// <returns>切り詰め後の文字列。</returns>
    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
    }

    /// <summary>
    /// 詳細グリッド1行分の表示モデルです。
    /// </summary>
    public sealed class DetailRow : INotifyPropertyChanged
    {
        private double _progressPercent;
        private string _resultText;
        private string _sizeText;
        private string _channelText;
        private string _subcarrierText;
        private string _modulationText;

        /// <summary>
        /// 詳細グリッド行を初期化します。
        /// </summary>
        /// <param name="name">項目名（FH / BLK-n / Total など）。</param>
        /// <param name="resultText">結果列の初期値。</param>
        /// <param name="sizeText">サイズ列の初期値。</param>
        /// <param name="channelText">stereo/mono 列の初期値。</param>
        /// <param name="subcarrierText">サブキャリア数列の初期値。</param>
        /// <param name="modulationText">変調列の初期値。</param>
        /// <param name="showMeter">true なら進捗メーターを表示する。</param>
        private DetailRow(
            string name,
            string resultText,
            string sizeText,
            string channelText,
            string subcarrierText,
            string modulationText,
            bool showMeter)
        {
            Name = name;
            _resultText = resultText;
            _sizeText = sizeText;
            _channelText = channelText;
            _subcarrierText = subcarrierText;
            _modulationText = modulationText;
            MeterVisibility = showMeter ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Name { get; }
        public string ResultText => _resultText;
        public string SizeText => _sizeText;
        public string ChannelText => _channelText;
        public string SubcarrierText => _subcarrierText;
        public string ModulationText => _modulationText;
        public Visibility MeterVisibility { get; }

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProgressPercent(value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// メーター付きセグメント行を生成します。
        /// </summary>
        /// <param name="name">項目名。</param>
        /// <returns>進捗メーター付きの DetailRow。</returns>
        public static DetailRow Segment(string name) =>
            new(name, "-", "-", "-", "-", "-", showMeter: true);

        /// <summary>
        /// メーター無しの情報行を生成します（結果以外は "-"）。
        /// </summary>
        /// <param name="name">項目名。</param>
        /// <param name="sizeText">サイズ列の値。</param>
        /// <returns>メーター無しの DetailRow。</returns>
        public static DetailRow Info(string name, string sizeText) =>
            new(name, "-", sizeText, "-", "-", "-", showMeter: false);

        /// <summary>
        /// メーター無しの情報行を全カラム指定で生成します。
        /// </summary>
        /// <param name="name">項目名。</param>
        /// <param name="result">結果列。</param>
        /// <param name="size">サイズ列。</param>
        /// <param name="channel">stereo/mono 列。</param>
        /// <param name="sc">サブキャリア数列。</param>
        /// <param name="mod">変調列。</param>
        /// <returns>メーター無しの DetailRow。</returns>
        public static DetailRow Info(
            string name,
            string result,
            string size,
            string channel,
            string sc,
            string mod) =>
            new(name, result, size, channel, sc, mod, showMeter: false);

        /// <summary>
        /// 進捗メーター値を更新します。
        /// </summary>
        /// <param name="value">0..100 の進捗。</param>
        /// <param name="force">true のとき同一値でも PropertyChanged を飛ばす。</param>
        public void SetProgressPercent(double value, bool force = false)
        {
            var clamped = Math.Clamp(value, 0.0, 100.0);
            if (!force && Math.Abs(_progressPercent - clamped) < 0.05)
            {
                return;
            }

            _progressPercent = clamped;
            OnPropertyChanged(nameof(ProgressPercent));
        }

        /// <summary>
        /// 結果列を更新します。
        /// </summary>
        /// <param name="value">新しい結果文言（OK / NG / - など）。</param>
        public void SetResult(string value)
        {
            if (_resultText == value)
            {
                return;
            }

            _resultText = value;
            OnPropertyChanged(nameof(ResultText));
        }

        /// <summary>
        /// サイズ列を更新します。
        /// </summary>
        /// <param name="value">新しいサイズ表示。</param>
        public void SetSize(string value)
        {
            if (_sizeText == value)
            {
                return;
            }

            _sizeText = value;
            OnPropertyChanged(nameof(SizeText));
        }

        /// <summary>
        /// stereo/mono・サブキャリア数・変調列を更新します。
        /// </summary>
        /// <param name="channel">stereo または mono。</param>
        /// <param name="sc">サブキャリア数の表示。</param>
        /// <param name="mod">変調方式の表示。</param>
        public void SetModulationFields(string channel, string sc, string mod)
        {
            if (_channelText != channel)
            {
                _channelText = channel;
                OnPropertyChanged(nameof(ChannelText));
            }

            if (_subcarrierText != sc)
            {
                _subcarrierText = sc;
                OnPropertyChanged(nameof(SubcarrierText));
            }

            if (_modulationText != mod)
            {
                _modulationText = mod;
                OnPropertyChanged(nameof(ModulationText));
            }
        }

        /// <summary>
        /// PropertyChanged を発火します。
        /// </summary>
        /// <param name="propertyName">変更したプロパティ名。省略時は呼び出し元。</param>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
