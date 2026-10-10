# 音多 ClickOnce 配布

`Onta_core` を **ClickOnce** で `Installer\Onta\` へ出力し、そのフォルダーを自己解凍形式のインストーラー `Installer\Onta-install-v<バージョン>.exe` にまとめます（バージョンは `Onta_core\Onta_core.csproj` の `<Version>`。例: `Onta-install-v1.0.0.exe`）。
**win-x64** と **win-arm64** の両方を同梱します。
マニュアル（日本語 `Onta_manual\ja\*.md`・英語 `Onta_manual\en\*.md`）とスプラッシュ画像（`onta_splash_512x256.png`）も各アーキテクチャに同梱されます。

## 前提

- .NET 8 SDK
- Windows（WPF / ClickOnce）
- **Visual Studio 2022 Build Tools**（または Visual Studio）に次を含むこと
  - MSBuild（`Microsoft.Component.MSBuild`）
  - ClickOnce Build Tools（`Microsoft.Component.ClickOnce.MSBuild`）
- `dotnet msbuild` では ClickOnce の `GenerateBootstrapper` / `Launcher` が動かないため、発行は **Framework 版 MSBuild** 必須（`publish.ps1` が自動選択）
- **7-Zip**（`7z.exe` と同じフォルダーに `7z.sfx` があること。`Program Files\7-Zip` または PATH から探す）。自己解凍形式のインストーラーの作成に使う。見つからなければ発行の前に止まる

## 発行手順

PowerShell:

```powershell
cd C:\proj\Onta\Installer
.\publish.ps1
```

成功すると `Installer\Onta\` に次が入り、同じ内容を自己解凍形式にした `Installer\Onta-install-v<バージョン>.exe` ができます（7-Zip の GUI 自己解凍。実行すると展開先を聞かれ、そこに `Onta` フォルダーができる）。

| パス                                                               | 説明                                                            |
| :----------------------------------------------------------------- | :-------------------------------------------------------------- |
| `Install-Onta.cmd`                                                 | `Install-Onta.ps1` を起動する入口                               |
| `Install-Onta.ps1`                                                 | 表示言語とデスクトップショートカットの有無を選ばせて保存し、CPU を判定して該当する `setup.exe` を起動 |
| `x64\setup.exe`                                                    | Intel / AMD 64bit 用 ClickOnce                                  |
| `arm64\setup.exe`                                                  | Windows on ARM 用 ClickOnce                                     |
| `x64\` / `arm64\` 配下の `Onta.application` / `Application Files\` | 各アーキテクチャの実体（`manual\` 含む）                        |
| `README-ja.txt` / `README-en.txt`                                  | 配布先向けの短い説明（ソースは `dist-README-ja.txt` / `dist-README-en.txt`） |
| `THIRD-PARTY-NOTICES.txt`                                          | 同梱サードパーティ（libopus）の著作権表示・ライセンス本文・免責 |

配布は `Onta-install-v<バージョン>.exe` 1 本で行います（USB / 共有フォルダへ `Onta` フォルダごとコピーしてもよい）。
インストーラーは発行のたびに作り直します（同じバージョンなら上書き。バージョンを上げると別ファイルになり、古いものは残る）。
中間出力は `Installer\obj\app\x64` / `arm64`（配布不要）。

ClickOnce はアーキテクチャごとに 1 デプロイメントのため、単一の fat バイナリではなく **x64 / arm64 を並べて同梱**する形です。

## インストール時の言語選択

ClickOnce の `setup.exe` の画面には言語選択を足せないため、`Install-Onta.cmd` から起動する `Install-Onta.ps1`（ソースは `Installer\Install-Onta.ps1`）で選ばせます。

- 日本語 / English のダイアログを出す。初期選択は OS の表示言語が日本語なら日本語、それ以外は English
- OK で `%LOCALAPPDATA%\Onta\Onta_language.txt` に `ja` / `en` を書き、`setup.exe` を起動する。キャンセルならインストールしない
- アプリは起動時に `--lang` → `Onta_language.txt` → OS の表示言語 の順で言語を決める（`AppLanguage`）
- `Install-Onta.ps1` は日本語を含むため **UTF-8（BOM 付き）** で保存する（Windows PowerShell 5.1 が BOM なしの UTF-8 を誤読するため）

## デスクトップショートカット

同じダイアログの「デスクトップにショートカットを作成する」（既定はオン。文言は選んだ言語に合わせる）で選ばせます。

- ClickOnce はデスクトップショートカットを作る設定にしている（pubxml の `CreateDesktopShortcut`）。ClickOnce が作ったものはアンインストールで消える
- `Install-Onta.ps1` は選択を `%LOCALAPPDATA%\Onta\Onta_desktop_shortcut.txt` に `1`（作る）/ `0`（作らない）で書く
- アプリは ClickOnce から起動したとき（環境変数 `ClickOnce_IsNetworkDeployed`）にこのファイルを読み、`0` ならデスクトップの `Onta x64.appref-ms`（ARM64 版は `Onta ARM64.appref-ms`）を消し、`1` で無ければスタートメニューのものをコピーして、ファイルを消す（`DesktopShortcutSetting`。インストール直後の自動起動で反映される）
- ショートカット名とスタートメニューのフォルダー名は、HKCU のアンインストール情報（`ShortcutFileName` / `ShortcutFolderName`）から取る（`ClickOnceRegistration`。名前は publish.ps1 が CPU ごとに付ける `ProductName`）
- `setup.exe` を直接実行したときはファイルを書かないので、ショートカットはそのまま残る

## アプリアイコン

`Onta_core\icon\Onta.ico` を `ApplicationIcon` に指定している（exe・ウィンドウ・スタートメニュー・デスクトップ）。ClickOnce のマニフェストは `iconFile` をファイルとして参照するため、ico も `Content` で同梱する。

- 「アプリと機能」のアイコンは、ClickOnce がアンインストール情報の `DisplayIcon` に自分の汎用アイコン（`dfshim.dll,2`）を書き、マニフェストのアイコンを使わない。そのためアプリが ClickOnce から起動するたびに、`DisplayIcon` を実行中の `Onta.exe,0` へ書き換える（`ClickOnceRegistration`。HKCU なので管理者権限は不要）。インストール直後の自動起動で反映される

- 作り直し: `Onta_core\icon\onta_icon_source.jpg`（1:1 の角丸タイル。角の外は白）を差し替えて `Onta_core\icon\Make-OntaIcon.ps1` を実行する。角の外を透明にした `Onta_icon_1024.png` と、16〜256 px の `Onta.ico`（256 px は PNG、それ未満は 32bit DIB）ができる
- 自己解凍インストーラー（`Onta-install-v*.exe`）と `setup.exe` のアイコンは 7-Zip / ClickOnce の既定のまま

## Visual Studio から発行する場合

1. `Onta_core\Onta_core.csproj` を開く
2. 右クリック → **発行**
3. プロファイル `ClickOnceFolder` を選択し、RID（`win-x64` / `win-arm64`）を切り替えてそれぞれ Publish
   （両方まとめる場合は `publish.ps1` を推奨）

## 同梱コンテンツ

| ソース                                | インストール先（アプリ直下）     |
| :------------------------------------ | :------------------------------- |
| `Onta_manual\ja\*.md`                 | `manual\ja\`                     |
| `Onta_manual\ja\picture\*.png`        | `manual\ja\picture\`             |
| `.cursor\rules\*.mdc`（仕様 4 本）    | `manual\ja\specification\`       |
| `Installer\THIRD-PARTY-NOTICES.txt`   | `manual\THIRD-PARTY-NOTICES.txt`（日英共通。ClickOnce は同じ元ファイルを 2 か所へ同梱できないため 1 つだけ） |
| `Onta_manual\en\*.md`                 | `manual\en\`                     |
| `Onta_manual\en\picture\*.png`        | `manual\en\picture\`             |
| `Onta_manual\99_licence.md`           | `manual\99_licence.md`           |
| `Onta_manual\onta_splash_512x256.png` | `manual\onta_splash_512x256.png` |

起動時にスプラッシュ画像を短時間表示します。

## 署名について

既定ではマニフェスト署名は **オフ**（`SignManifests=false`）です。
社内／本番配布で署名が必要な場合は、証明書を用意し pubxml の署名設定を有効にしてください。

## Opus（ストリーム）

ストリーム録音・再生には native `opus.dll` が必要です。

```
Onta_core/native/opus/win-x64/opus.dll
Onta_core/native/opus/win-arm64/opus.dll
```

ビルド時に実行ディレクトリへコピーされます（無い場合でもビルドは成功、実行時にエラー）。
RID が `win-arm64` のときは arm64 版、それ以外は x64 版だけを同梱します（ARM64 のプロセスは x64 の DLL を読み込めない）。

`opus.dll` は `VCRUNTIME140.dll` に依存し、自己完結の .NET 発行には含まれないため、VC++ 再頒布可能パッケージの無い PC 向けに同じフォルダーへ同梱します。

```
Onta_core/native/vcruntime/win-x64/vcruntime140.dll
Onta_core/native/vcruntime/win-arm64/vcruntime140.dll
```

入手元は Build Tools の `VC\Redist\MSVC\<バージョン>\{x64|arm64}\Microsoft.VC143.CRT\`（コンポーネント `Microsoft.VisualStudio.Component.VC.Redist.14.Latest`）。

配布時は `THIRD-PARTY-NOTICES.txt` を同梱し、libopus の著作権表示・ライセンス本文・免責を提供します。
