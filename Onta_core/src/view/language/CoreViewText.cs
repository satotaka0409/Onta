using System.Globalization;

namespace Onta.View.Language;

/// <summary>
/// core 画面で使う表示文言を、現在の UI カルチャに応じて返します。
/// </summary>
public static class CoreViewText
{
    private static bool IsJapanese =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UI カルチャに応じて日本語または英語の表示文言を選びます。
    /// </summary>
    /// <param name="ja">日本語の文言。</param>
    /// <param name="en">英語の文言。</param>
    /// <returns>UI カルチャが日本語なら ja、それ以外は en。</returns>
    private static string T(string ja, string en) => IsJapanese ? ja : en;

    public static string AppName => "Onta";

    public static string MainTabMain => T("メイン", "Main");
    public static string MainTabSendDetail => T("送信詳細", "Send Details");
    public static string MainTabReceiveDetail => T("受信詳細", "Receive Details");
    public static string MainTabHistory => T("履歴", "History");
    public static string MainTabStream => T("ストリーム", "Stream");
    public static string MainTabPerformance => T("性能測定", "Performance");
    public static string MainTabManual => T("マニュアル", "Manual");

    public static string Send => T("送信", "Send");
    public static string Receive => T("受信", "Receive");
    public static string Start => T("スタート", "Start");
    public static string Stop => T("ストップ", "Stop");

    public static string Channel => T("チャンネル", "Channel");
    public static string Mono => T("モノラル", "Mono");
    public static string Stereo => T("ステレオ", "Stereo");
    public static string Subcarrier => T("サブキャリア", "Subcarrier");
    public static string Modulation => T("変調", "Modulation");
    public static string Output => T("出力", "Output");
    public static string SendFile => T("送信ファイル", "Input File");
    public static string WavOutput => T("WAV出力", "WAV Output");
    public static string AudioOutput => T("音声出力", "Audio Output");
    public static string OutputFile => T("出力ファイル", "Output File");
    public static string OutputDevice => T("出力デバイス", "Output Device");
    public static string Volume => T("音量", "Volume");

    public static string FileName => T("ファイル名", "File Name");
    public static string FileSize => T("ファイルサイズ", "File Size");
    public static string BlockCount => T("ブロック数", "Block Count");
    public static string Progress => T("進捗", "Progress");
    public static string WowFlutter => T("ワウフラッター", "Wow/Flutter");
    public static string Input => T("入力", "Input");
    public static string WavInput => T("WAV入力", "WAV Input");
    public static string AudioInput => T("音声入力", "Audio Input");
    public static string InputFile => T("入力ファイル", "Input File");
    public static string InputDevice => T("入力デバイス", "Input Device");
    public static string OutputFolder => T("出力フォルダー", "Output Folder");
    public static string ErrorRate => T("エラーレート", "Error Rate");
    public static string Viterbi => T("ビタビ", "Viterbi");
    public static string RsTurbo => T("RS／ターボ", "RS/Turbo");

    public static string GridItem => T("項目", "Item");
    public static string GridSeconds => T("秒数", "Seconds");
    public static string GridSize => T("サイズ", "Size");
    public static string GridMeter => T("メーター", "Meter");
    public static string GridResult => T("結果", "Result");
    public static string GridSubcarrierCount => T("サブキャリア数", "Subcarriers");

    public static string DefaultDevice => T("既定デバイス", "Default device");
    public static string NotReceived => T("(未受信)", "(Not received)");
    public static string Unknown => T("(不明)", "(Unknown)");
    public static string FhWaiting => T("(FH待ち)", "(Waiting for FH)");
    public static string UnregisteredData => T("(未登録データ)", "(Unregistered data)");
    public static string ParentUnknown => T("親不明", "Orphan");
    public static string Resolved => T("解決済み", "Resolved");

    public static string FhWaitingProgress => T("FH 待機中...", "Waiting for FH...");
    public static string AudioInputFhWaitingProgress => T("音声入力中 / FH 待機...", "Capturing audio / Waiting for FH...");
    public static string HistoryLoadedProgress => T("履歴を読み込みました", "History loaded");
    public static string WaitingProgress => T("待機中", "Waiting");
    public static string StartingProgress => T("開始中…", "Starting...");

    public static string Iq => "I-Q";

    /// <summary>
    /// I-Q 送信グラフの見出しを返します。
    /// </summary>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="subcarrierCount">サブキャリア数。</param>
    /// <returns>表示文字列。</returns>
    public static string IqSendFormat(string modulationScheme, int subcarrierCount) =>
        T($"I-Q 送信 ({modulationScheme} / SC={subcarrierCount})", $"I-Q TX ({modulationScheme} / SC={subcarrierCount})");

    /// <summary>
    /// 音声入力デバイス名を含む入力元表示を返します。
    /// </summary>
    /// <param name="deviceName">入力デバイス名。</param>
    /// <returns>表示文字列。</returns>
    public static string AudioInputSource(string deviceName) =>
        T($"(音声入力: {deviceName})", $"(Audio input: {deviceName})");

    public static string AudioInputSourceSimple => T("(音声入力)", "(Audio input)");

    public static string ReceiveWavSource => T("(WAV受信)", "(WAV receive)");

    public static string DialogSelectInputFile => T("送信入力ファイルを選択", "Select input file");
    public static string DialogSelectReceiveWavFile => T("受信WAVファイルを選択", "Select receive WAV file");
    public static string DialogSelectOutputFolder => T("受信ファイルの出力フォルダーを選択", "Select output folder for received file");
    public static string DialogWavOutputDestination => T("WAV 出力先", "WAV output destination");
    public static string DialogWavOutputFile => T("WAV 出力ファイル", "WAV output file");

    public static string FilterAllFiles => T("すべてのファイル (*.*)|*.*", "All files (*.*)|*.*");
    public static string FilterPng => "PNG (*.png)|*.png";
    public static string FilterWav => "WAV (*.wav)|*.wav";
    public static string FilterReceiveWav => T("WAV (*.wav)|*.wav|すべてのファイル (*.*)|*.*", "WAV (*.wav)|*.wav|All files (*.*)|*.*");

    /// <summary>
    /// 送信中エラーのメッセージを返します。
    /// </summary>
    /// <param name="message">エラー内容。</param>
    /// <returns>表示文字列。</returns>
    public static string ErrorWhileSending(string message) => T($"送信中にエラーが発生しました。\n{message}", $"An error occurred during transmission.\n{message}");

    /// <summary>
    /// 受信開始失敗のメッセージを返します。
    /// </summary>
    /// <param name="message">エラー内容。</param>
    /// <returns>表示文字列。</returns>
    public static string ErrorReceiveStartFailed(string message) => T($"受信開始に失敗しました。\n{message}", $"Failed to start receiving.\n{message}");

    /// <summary>
    /// 出力フォルダー作成失敗のメッセージを返します。
    /// </summary>
    /// <param name="message">エラー内容。</param>
    /// <returns>表示文字列。</returns>
    public static string ErrorOutputFolderCreateFailed(string message) => T($"出力フォルダーを作成できません。\n{message}", $"Could not create output folder.\n{message}");

    public static string MessageSelectInputFile => T("入力ファイルを選択してください。", "Please select an input file.");
    public static string MessageInputFileNotFound => T("入力ファイルが見つかりません。", "Input file was not found.");
    public static string MessageEnableWavOrAudio => T("WAV出力または音声出力を有効にしてください。", "Enable WAV output or realtime audio output.");
    public static string MessageCoreAlreadyRunning => T("Core は既に動作中です。先に現在の送信を停止してください。", "Core is already running. Stop current transmission first.");
    public static string MessageSelectOutputFolder => T("出力フォルダーを選択してください。", "Please select an output folder.");
    public static string MessageSelectWavInputFile => T("WAV入力ファイルを選択してください。", "Please select a WAV input file.");
    public static string MessageWavInputNotFound => T("WAV入力ファイルが見つかりません。", "WAV input file was not found.");
    public static string MessageReceiveCoreAlreadyRunning => T("受信コアは既に動作中です。", "Receive core is already running.");
    public static string MessageReceiveCoreOrAudioStartFailed => T("受信コアは既に動作中、または音声デバイスの開始に失敗しました。", "Receive core is already running, or audio device failed to start.");

    /// <summary>
    /// 受信結果と出力先をまとめたメッセージを返します。
    /// </summary>
    /// <param name="message">受信結果の文言。</param>
    /// <param name="outputPath">出力先パス。未指定なら空として扱います。</param>
    /// <returns>表示文字列。</returns>
    public static string MessageReceiveOutput(string message, string? outputPath)
    {
        var path = outputPath ?? string.Empty;
        return T($"{message}\n出力: {path}", $"{message}\nOutput: {path}");
    }
    public static string MessageReceiveInterrupted => T("受信を中断しました。", "Receive was cancelled.");

    public static string MessageTransmissionCompletedTitle => T("送信完了", "Transmission completed");

    /// <summary>
    /// 送信完了ダイアログの本文を返します。
    /// </summary>
    /// <param name="inputPath">入力ファイルパス。</param>
    /// <param name="wavLine">WAV 出力の表示行。</param>
    /// <param name="channel">チャンネル表示。</param>
    /// <param name="subcarrier">サブキャリア表示。</param>
    /// <param name="modulation">変調方式表示。</param>
    /// <param name="interleave">インターリーブ回数。</param>
    /// <param name="device">出力デバイス表示。</param>
    /// <param name="playedRealtime">音声をリアルタイム再生したか。</param>
    /// <returns>表示文字列。</returns>
    public static string MessageTransmissionCompleted(
        string inputPath,
        string wavLine,
        string channel,
        string subcarrier,
        string modulation,
        int interleave,
        string device,
        bool playedRealtime)
    {
        var audioLine = playedRealtime
            ? T("音声: リアルタイム再生\n", "Audio: realtime playback\n")
            : string.Empty;

        return T(
            "送信が完了しました。\n\n"
            + $"入力: {inputPath}\n"
            + wavLine
            + $"チャンネル: {channel}\n"
            + $"サブキャリア: {subcarrier}\n"
            + $"変調: {modulation}\n"
            + $"Interleave: {interleave}\n"
            + $"デバイス: {device}\n"
            + audioLine,
            "Transmission completed.\n\n"
            + $"Input: {inputPath}\n"
            + wavLine
            + $"Channel: {channel}\n"
            + $"Subcarrier: {subcarrier}\n"
            + $"Modulation: {modulation}\n"
            + $"Interleave: {interleave}\n"
            + $"Device: {device}\n"
            + audioLine);
    }

    public static string MessageWavDisabledLine => T("WAV: (無効)\n", "WAV: (disabled)\n");

    /// <summary>
    /// WAV 出力パスの表示行を返します。
    /// </summary>
    /// <param name="path">WAV ファイルパス。</param>
    /// <returns>表示文字列。</returns>
    public static string MessageWavPathLine(string path) => $"WAV: {path}\n";

    public static string StageCancelRequested => T("中断要求中", "Cancelling");
    public static string StageInputLoading => T("入力読込中", "Loading input");
    public static string StagePlayingAudio => T("音声再生中", "Playing audio");
    public static string StageCancelled => T("中断", "Cancelled");

    public static string InputUnselected => T("(未選択)", "(Not selected)");
    public static string ReceiveCompletedSuccessfully => T("受信が完了しました。", "Receive completed successfully.");
    public static string ReceiveFailed => T("受信に失敗しました。", "Receive failed.");
    public static string SendHistoryCompleted => T("送信完了", "Send completed");

    public static string Reload => T("再読込", "Reload");
    public static string DeleteHistory => T("履歴を削除", "Delete history");
    public static string ReceiveHistory => T("受信履歴", "Receive History");
    public static string SendHistory => T("送信履歴", "Send History");
    public static string UnknownBlock => T("不明ブロック", "Unknown Blocks");
    public static string ReceivedAt => T("受信日時", "Received At");
    public static string SentAt => T("送信日時", "Sent At");
    public static string FileNameHeader => T("ファイル名", "File Name");
    public static string FileSizeHeader => T("ファイルサイズ", "File Size");
    public static string Result => T("結果", "Result");
    public static string AudioWav => T("音声/WAV", "Audio/WAV");
    public static string AudioWavInput => T("音声/WAV入力", "Audio/WAV Input");
    public static string WavFileName => T("WAVファイル名", "WAV File Name");
    public static string Download => T("ダウンロード", "Download");
    public static string CreatedTimestamp => T("作成タイムスタンプ", "Created");
    public static string UpdatedTimestamp => T("更新タイムスタンプ", "Updated");
    public static string BlockSize => T("ブロックサイズ", "Block Size");
    public static string BlockPosition => T("ブロック位置", "Block Position");
    public static string FileHash => T("ファイルハッシュ値", "File Hash");
    public static string BlockHash => T("ブロックハッシュ値", "Block Hash");
    public static string TooltipToggleBlockDetails => T("クリックでブロック明細を開閉", "Click to toggle block details");

    /// <summary>
    /// 履歴件数のステータス文言を返します。
    /// </summary>
    /// <param name="receiveCount">受信件数。</param>
    /// <param name="sendCount">送信件数。</param>
    /// <param name="unknownCount">不明ブロック件数。</param>
    /// <returns>表示文字列。</returns>
    public static string StatusHistorySummary(int receiveCount, int sendCount, int unknownCount) =>
        T($"受信 {receiveCount} 件 / 送信 {sendCount} 件 / 不明ブロック {unknownCount} 件",
          $"Receive {receiveCount} / Send {sendCount} / Unknown {unknownCount}");
    public static string HistoryNoExportableData => T("履歴データから復元可能なデータがありません。", "No exportable payload in history.");
    public static string SaveFileTitle => T("ファイルを保存", "Save file");
    public static string FilterAllAndBinary => T("すべてのファイル (*.*)|*.*|バイナリ (*.bin)|*.bin", "All files (*.*)|*.*|Binary (*.bin)|*.bin");

    /// <summary>
    /// 保存完了のステータス文言を返します。
    /// </summary>
    /// <param name="path">保存先パス。</param>
    /// <returns>表示文字列。</returns>
    public static string StatusSaved(string path) => T($"保存: {path}", $"Saved: {path}");
    public static string StatusNoDataToSave => T("保存対象のデータがありません。", "No data to save.");

    /// <summary>
    /// 保存失敗のステータス文言を返します。
    /// </summary>
    /// <param name="message">失敗内容。</param>
    /// <returns>表示文字列。</returns>
    public static string StatusSaveFailed(string message) => T($"保存失敗: {message}", $"Save failed: {message}");

    /// <summary>
    /// 履歴削除の確認文を返します。
    /// </summary>
    /// <param name="fileName">ファイル名。</param>
    /// <param name="entryId">履歴エントリ ID。</param>
    /// <returns>表示文字列。</returns>
    public static string ConfirmDeleteHistory(string fileName, string entryId) =>
        T($"履歴を削除しますか？\n{fileName}\nEntryId={entryId}", $"Delete this history entry?\n{fileName}\nEntryId={entryId}");
    public static string DeleteHistoryTitle => T("履歴削除", "Delete History");
    public static string StatusDeleted => T("削除しました。", "Deleted.");
    public static string StatusDeleteTargetNotFound => T("削除対象が見つかりません。", "Target entry was not found.");
    public static string Complete => T("COMPLETE", "COMPLETE");
    public static string Incomplete => T("IN-COMPLETE", "IN-COMPLETE");

    public static string StreamRate => T("速度", "Rate");
    public static string StreamIo => T("入出力", "I/O");
    public static string StreamFileInput => T("ファイル入力", "File Input");
    public static string TrackTitle => T("曲タイトル", "Title");
    public static string Artist => T("アーティスト", "Artist");
    public static string CoverArt => T("ジャケ写", "Cover Art");
    public static string CoverFmt32Color => T("32x32カラー", "32x32 color");
    public static string CoverFmt48Color => T("48x48カラー", "48x48 color");
    public static string CoverFmt48Gray => T("48x48白黒", "48x48 grayscale");
    public static string CoverFmt64Gray => T("64x64白黒", "64x64 grayscale");
    public static string SelectFile => T("ファイル指定", "Select File");

    /// <summary>
    /// サイズ数値の表示を返します。
    /// </summary>
    /// <param name="value">サイズ。</param>
    /// <returns>表示文字列。</returns>
    public static string SizeLabel(int value) => T($"サイズ: {value}", $"Size: {value}");

    /// <summary>
    /// 幅と高さのピクセルサイズ表示を返します。
    /// </summary>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <returns>表示文字列。</returns>
    public static string SizeLabelPixels(int width, int height) => T($"サイズ: {width}x{height}", $"Size: {width}x{height}");
    public static string SizeLabelUnknown => T("サイズ: -", "Size: -");
    public static string SizeOver => T("サイズオーバー", "Size over limit");

    /// <summary>
    /// バイト数の表示を返します。
    /// </summary>
    /// <param name="value">バイト数。</param>
    /// <returns>表示文字列。</returns>
    public static string ByteCountLabel(int value) => T($"バイト数: {value}", $"Bytes: {value}");
    public static string ByteCount0 => ByteCountLabel(0);
    public static string ErrorRateWithEllipse => T("エラーレート", "Error Rate");

    /// <summary>
    /// デバイス番号と名前の表示を返します。番号が負なら既定デバイスです。
    /// </summary>
    /// <param name="index">デバイス番号。負なら既定。</param>
    /// <param name="name">デバイス名。</param>
    /// <returns>表示文字列。</returns>
    public static string DefaultDeviceWithIndexName(int index, string name) =>
        index < 0 ? DefaultDevice : $"{index}: {name}";
    public static string FilterAudioFiles => T(
        "音声ファイル (*.wav;*.flac;*.mp3)|*.wav;*.flac;*.mp3|WAV (*.wav)|*.wav|FLAC (*.flac)|*.flac|MP3 (*.mp3)|*.mp3|All (*.*)|*.*",
        "Audio files (*.wav;*.flac;*.mp3)|*.wav;*.flac;*.mp3|WAV (*.wav)|*.wav|FLAC (*.flac)|*.flac|MP3 (*.mp3)|*.mp3|All (*.*)|*.*");
    public static string FilterImageFiles => "Image|*.png;*.jpg;*.jpeg;*.bmp|All (*.*)|*.*";

    /// <summary>
    /// ジャケ写処理エラーの表示を返します。
    /// </summary>
    /// <param name="message">エラー内容。</param>
    /// <returns>表示文字列。</returns>
    public static string CoverByteError(string message) => T($"エラー: {message}", $"Error: {message}");
    public static string CoverSizeOverMessage => T("ジャケ写がサイズオーバーです。", "Cover art is over the size limit.");
    public static string SendingNow => T("送信中…", "Transmitting...");
    public static string StopRequested => T("停止要求", "Stop requested");
    public static string ReceivingNow => T("受信中…", "Receiving...");

    /// <summary>
    /// 失敗理由付きの表示を返します。
    /// </summary>
    /// <param name="message">失敗内容。</param>
    /// <returns>表示文字列。</returns>
    public static string FailedWith(string message) => T($"失敗: {message}", $"Failed: {message}");

    /// <summary>
    /// 受信中のパケット数とエラー数の表示を返します。
    /// </summary>
    /// <param name="packetsReceived">受理したパケット数。</param>
    /// <param name="packetErrors">エラーパケット数。</param>
    /// <returns>表示文字列。</returns>
    public static string ReceivingPacketStatus(int packetsReceived, int packetErrors) =>
        T($"受信中… パケット {packetsReceived:N0}（エラー {packetErrors:N0}）",
          $"Receiving... Packets {packetsReceived:N0} (Errors {packetErrors:N0})");
    public static string StreamRxRunningTitle => T("ストリーム受信", "Stream RX");
    public static string StreamRxStopped => T("ストリーム受信停止", "Stream RX stopped");
    public static string StreamTxRunningTitle => T("ストリーム送信", "Stream TX");
    public static string StreamTxCompleted => T("ストリーム送信完了", "Stream TX completed");
    public static string Cancelled => T("キャンセル", "Cancelled");
    public static string InputFileNotFound => T("入力ファイルがありません。", "Input file was not found.");

    public static string ReferenceSignal => T("基準信号", "Reference Signal");
    public static string Modulated => T("変調", "Modulated");
    public static string Sweep20to20k => T("スイープ(20Hz～20KHz)", "Sweep (20Hz-20KHz)");
    public static string WhiteNoise20to20k => T("ホワイトノイズ(20Hz～20KHz)", "White Noise (20Hz-20KHz)");
    public static string Duration => T("秒数", "Duration");

    /// <summary>
    /// 秒数の表示を返します。
    /// </summary>
    /// <param name="sec">秒数。</param>
    /// <returns>表示文字列。</returns>
    public static string DurationSec(int sec) => T($"{sec}秒", $"{sec}s");
    public static string Duration30Sec => DurationSec(30);
    public static string Duration60Sec => DurationSec(60);
    public static string Duration120Sec => DurationSec(120);
    public static string Scope => T("オシロスコープ", "Oscilloscope");
    public static string LissajousWowCounter => T("リサージュ・ワウ・Fカウンタ", "Lissajous / Wow / F Counter");
    public static string Window => T("窓", "Window");
    public static string LrSync => T("L/R 同期", "L/R Sync");
    public static string FrequencyCounter => T("周波数カウンタ", "Frequency Counter");
    public static string DistortionRate => T("歪み率", "Distortion Rate");
    public static string LissajousTitleBase => T("リサージュ（アジマス）  L→X / R→Y  ・同位相で対角線", "Lissajous (Azimuth)  L->X / R->Y  - diagonal when in phase");

    /// <summary>
    /// オシロスコープの AUTO トリガー見出しを返します。
    /// </summary>
    /// <param name="channel">チャネル名。</param>
    /// <param name="triggered">トリガーがかかっているか。</param>
    /// <returns>表示文字列。</returns>
    public static string ScopeAutoTitle(string channel, bool triggered)
    {
        var suffix = triggered ? T("AUTO ↑", "AUTO ^") : "AUTO";
        return T($"{channel} オシロスコープ  {suffix}", $"{channel} Scope  {suffix}");
    }

    public static string DialogWavInputFile => T("WAV 入力ファイル", "WAV input file");
    public static string MessageTxStartFailed => T("送信を開始できませんでした。", "Could not start transmission.");
    public static string MessageSelectRxWavFile => T("WAV 入力ファイルを選択してください。", "Please select a WAV input file.");
    public static string MessageRxStartFailedWav => T("受信を開始できませんでした（WAV ファイルを確認してください）。", "Could not start receiving (check WAV file).");
    public static string MessageRxStartFailedAudio => T("受信を開始できませんでした（入力デバイスを確認してください）。", "Could not start receiving (check input device).");

    /// <summary>
    /// 送信中の経過秒表示を返します。
    /// </summary>
    /// <param name="seconds">経過秒。</param>
    /// <returns>表示文字列。</returns>
    public static string TxRunningDuration(double seconds) => T($"送信中… {seconds:0}s", $"Transmitting... {seconds:0}s");
    public static string RxAnalyzingWav => T("WAV 解析中", "Analyzing WAV");
    public static string Done => T("完了", "Done");
    public static string Stopped => T("停止", "Stopped");
    public static string TxCompleted => T("送信完了", "Transmission completed");
    public static string LissajousHintAlmostInPhase => T("ほぼ同位相（対角線）", "Nearly in phase (diagonal)");
    public static string LissajousHintPhaseDiffEllipse => T("位相差あり（楕円）", "Phase difference (ellipse)");
    public static string LissajousHintLargePhaseDiff => T("位相差大", "Large phase difference");
    public static string LissajousHintNearReversePhase => T("逆相寄り", "Near reverse phase");

    /// <summary>
    /// 相関の目安を付けたリサージュ見出しを返します。
    /// </summary>
    /// <param name="hint">位相の目安。</param>
    /// <returns>表示文字列。</returns>
    public static string LissajousTitleWithHint(string hint) => T($"リサージュ（アジマス）  L→X / R→Y  ・{hint}", $"Lissajous (Azimuth)  L->X / R->Y  - {hint}");
}
