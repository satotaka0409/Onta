# 音多 ClickOnce 配布

`Onta_core` を **ClickOnce** で `Installer\publish\` へ出力します。  
**win-x64** と **win-arm64** の両方を同梱します。  
マニュアル（`Onta_manual\*.md`）とスプラッシュ画像（`onta_splash_512x256.png`）も各アーキテクチャに同梱されます。

## 前提

- .NET 8 SDK
- Windows（WPF / ClickOnce）
- **Visual Studio 2022 Build Tools**（または Visual Studio）に次を含むこと
  - MSBuild（`Microsoft.Component.MSBuild`）
  - ClickOnce Build Tools（`Microsoft.Component.ClickOnce.MSBuild`）
- `dotnet msbuild` では ClickOnce の `GenerateBootstrapper` / `Launcher` が動かないため、発行は **Framework 版 MSBuild** 必須（`publish.ps1` が自動選択）

## 発行手順

PowerShell:

```powershell
cd C:\proj\Onta\Installer
.\publish.ps1
```

成功すると `Installer\publish\` に次が入ります。

| パス | 説明 |
| :--- | :--- |
| `Install-Onta.cmd` | CPU を判定して該当する `setup.exe` を起動 |
| `x64\setup.exe` | Intel / AMD 64bit 用 ClickOnce |
| `arm64\setup.exe` | Windows on ARM 用 ClickOnce |
| `x64\` / `arm64\` 配下の `Onta.application` / `Application Files\` | 各アーキテクチャの実体（`manual\` 含む） |
| `README.txt` | 配布先向けの短い説明 |

USB / 共有フォルダへは `publish` フォルダごとコピーして配布してください。  
中間出力は `Installer\obj\app\x64` / `arm64`（配布不要）。

ClickOnce はアーキテクチャごとに 1 デプロイメントのため、単一の fat バイナリではなく **x64 / arm64 を並べて同梱**する形です。

## Visual Studio から発行する場合

1. `Onta_core\Onta_core.csproj` を開く
2. 右クリック → **発行**
3. プロファイル `ClickOnceFolder` を選択し、RID（`win-x64` / `win-arm64`）を切り替えてそれぞれ Publish  
   （両方まとめる場合は `publish.ps1` を推奨）

## 同梱コンテンツ

| ソース | インストール先（アプリ直下） |
| :--- | :--- |
| `Onta_manual\*.md` | `manual\` |
| `Onta_manual\onta_splash_512x256.png` | `manual\onta_splash_512x256.png` |

起動時にスプラッシュ画像を短時間表示します。

## 署名について

既定ではマニフェスト署名は **オフ**（`SignManifests=false`）です。  
社内／本番配布で署名が必要な場合は、証明書を用意し pubxml の署名設定を有効にしてください。
