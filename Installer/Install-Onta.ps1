# Onta installer launcher: choose the display language, then start the setup.exe for this CPU.
# 選んだ言語は %LOCALAPPDATA%\Onta\Onta_language.txt（ja / en）に保存し、アプリが起動時に読みます。
# デスクトップショートカットの有無は Onta_desktop_shortcut.txt（1 / 0）に保存し、アプリが初回起動で反映します。

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$osIsJapanese = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq "ja"

$form = New-Object System.Windows.Forms.Form
$form.Text = "Onta Setup"
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Font
$form.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$form.ClientSize = New-Object System.Drawing.Size(360, 226)

$label = New-Object System.Windows.Forms.Label
$label.Text = "表示言語を選んでください`r`nSelect the display language"
$label.Location = New-Object System.Drawing.Point(20, 16)
$label.Size = New-Object System.Drawing.Size(320, 44)
$form.Controls.Add($label)

$japanese = New-Object System.Windows.Forms.RadioButton
$japanese.Text = "日本語"
$japanese.Location = New-Object System.Drawing.Point(36, 66)
$japanese.Size = New-Object System.Drawing.Size(280, 26)
$japanese.Checked = $osIsJapanese
$form.Controls.Add($japanese)

$english = New-Object System.Windows.Forms.RadioButton
$english.Text = "English"
$english.Location = New-Object System.Drawing.Point(36, 96)
$english.Size = New-Object System.Drawing.Size(280, 26)
$english.Checked = -not $osIsJapanese
$form.Controls.Add($english)

$desktopShortcut = New-Object System.Windows.Forms.CheckBox
$desktopShortcut.Location = New-Object System.Drawing.Point(20, 136)
$desktopShortcut.Size = New-Object System.Drawing.Size(320, 26)
$desktopShortcut.Checked = $true
$form.Controls.Add($desktopShortcut)

# チェックボックスとキャンセルの文言を、選んでいる表示言語に合わせる
$updateTexts = {
    if ($japanese.Checked) {
        $desktopShortcut.Text = "デスクトップにショートカットを作成する"
        $cancel.Text = "キャンセル"
    }
    else {
        $desktopShortcut.Text = "Create a desktop shortcut"
        $cancel.Text = "Cancel"
    }
}

$ok = New-Object System.Windows.Forms.Button
$ok.Text = "OK"
$ok.Location = New-Object System.Drawing.Point(160, 178)
$ok.Size = New-Object System.Drawing.Size(84, 30)
$ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
$form.Controls.Add($ok)
$form.AcceptButton = $ok

$cancel = New-Object System.Windows.Forms.Button
$cancel.Location = New-Object System.Drawing.Point(256, 178)
$cancel.Size = New-Object System.Drawing.Size(84, 30)
$cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$form.Controls.Add($cancel)
$form.CancelButton = $cancel

$japanese.Add_CheckedChanged($updateTexts)
& $updateTexts

if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    exit 1
}

$language = if ($japanese.Checked) { "ja" } else { "en" }
$dataDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Onta"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-Content -LiteralPath (Join-Path $dataDir "Onta_language.txt") -Value $language -Encoding ASCII
# ClickOnce はデスクトップショートカットを必ず作るので、作らないを選んだらアプリが初回起動で消す（1 = 作る / 0 = 作らない）
$shortcutChoice = if ($desktopShortcut.Checked) { "1" } else { "0" }
Set-Content -LiteralPath (Join-Path $dataDir "Onta_desktop_shortcut.txt") -Value $shortcutChoice -Encoding ASCII

$isArm64 = $env:PROCESSOR_ARCHITECTURE -eq "ARM64" -or $env:PROCESSOR_ARCHITEW6432 -eq "ARM64"
$archFolder = if ($isArm64) { "arm64" } else { "x64" }
Start-Process -FilePath (Join-Path $PSScriptRoot "$archFolder\setup.exe")
exit 0
