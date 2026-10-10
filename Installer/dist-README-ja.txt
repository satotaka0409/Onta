音多 (Onta) インストール

インストール手順:
  1. Onta-install-vX.Y.Z.exe（自己解凍形式のインストーラー。例: Onta-install-v1.0.0.exe）を実行し、解凍先を選んで「Extract」を押します。選んだ場所に Onta フォルダができます
  2. Onta フォルダの Install-Onta.cmd を実行します
  3. 表示言語（日本語 / English）を選びます。初期選択は Windows が日本語なら日本語、それ以外は English です
     デスクトップにショートカットを作るかどうかも選べます（初期状態は作る）
  4. CPU に合わせて setup.exe が起動するので、画面に従ってインストールします

手動でインストールする場合:
  x64\setup.exe     ... Intel / AMD 64bit
  arm64\setup.exe   ... Windows on ARM (Snapdragon 等)
  （setup.exe を直接実行したときは、言語を選んでいなければ Windows の表示言語で起動します）

各フォルダに Onta.application と Application Files が含まれます。
USB / 共有フォルダで配るときは、Onta-install-vX.Y.Z.exe か、解凍した Onta フォルダごとコピーしてください。
