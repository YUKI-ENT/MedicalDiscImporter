# MedicalDiscImporter

Windows 11 ARM64 / x64向け .NET 10 / C# / WinForms光学ディスク取り込みアプリ。
M1に続く **Defender検査・DICOM一覧・フォルダ出力** を実装。施設での受入試験は未完了。まずダミーデータで評価する。

## 今回の機能

- ディスクの全ファイルをアクセス制限されたローカルNTFSへコピーし、IMAPI2FSでUDFの再構築ISOを作る。
- ISOを読み取り専用マウントし、元ファイルのパス・サイズ・SHA-256とフォルダ構成を照合。必ず解除を確認する。
- コピーした**全ファイル**をDefenderのカスタム検査にかける。ISO本体だけの検査で代替しない。
- 検査前後の通常稼働・リアルタイム保護・定義日時を確認する。終了コード0だけでは合格にしない。
- 対象フォルダの開始イベント1000と同じScan IDの完了イベント1001、検出履歴、期間中の検出・中止・エラー・設定変更を確認する。修復済み検出も出力禁止。
- 検査後に全ファイルとISOを再照合し、削除・修復・変更を検知する。
- fo-dicom 5.2.6で全ファイルを拡張子に依存せず解析し、患者名・撮影日・シリーズ番号・シーケンス・ファイル数をシリーズ単位でメインフォームに一覧表示する。
- シーケンスはSeriesDescription、SequenceName、ProtocolNameの重複しない値を併記。画像デコード・画像表示はしない。
- 撮影日はStudyDate → SeriesDate → AcquisitionDate → ContentDateの順。不明なら取り込み日で代用し、画面とjob.jsonに明示する。
- 合格後、ISOとDICOMを別々の指定先へ保存する。DICOMの内容・階層・名前を保持し、**拡張子のないファイルのみ `.dcm` を付加**する。DICOMDIRや付属ViewerはDICOM出力から除外する。
- コピー先のサイズ・SHA-256を照合後に最終名へ変更。上書き禁止。ISOとDICOMの成否を別々に記録し、部分成功を表示する。
- M1の二重起動・多重取り込み防止、数字キーパッド、進捗・中止、読取中スリープ抑止を維持。

## 使い方と出力

1. AutoPlayとAutoRunを施設の設定・管理ポリシーで無効化する。ディスク内Viewerを起動しない。
2. 「出力先・設定」で別々のISO保存先とDICOM出力先を指定する。絶対パスのローカルフォルダ／UNC共有に対応。書込権限・リンク拒否・隔離領域との分離を確認する。
3. Defender定義の有効日数を設定する（既定7日、1～30日）。設定は `%LOCALAPPDATA%/MedicalDiscImporter/settings.json` に保存。
4. 患者ID（半角数字1～32桁）を入力し、ドライブを選んで開始する。確認画面には両出力先を表示する。
5. 検査結果・一覧・保存先を確認する。失敗・中止・DICOMなし・部分成功を区別する。

ISO名は `患者ID-yyyyMMdd.iso`（取り込み日）。同名なら `-02`、`-03` … を付加する。
DICOMは `<DICOM出力先>/<患者ID-yyyyMMdd-処理ID>/` に一式出力する。
元の `IMAGES/IM0001` は `IMAGES/IM0001.dcm`、`IM0002.IMA` はそのまま。
`.dcm`付加後の重複・ファイルとフォルダの競合は、ISO出力前に失敗とする。

今回の依頼に合わせた汎用フォルダ抽出。RSBase用の改名・フラットな監視フォルダへの直接転送は別工程。
RSDICOMはサブフォルダ／完了マーカー非対応のため、このDICOM出力先にRSDICOM監視先を指定しない。

## Defenderの前提

Windows標準Windows PowerShell、Defenderモジュール、MpCmdRun、Defender Operationalイベントログを使用する。
Microsoftの手順ではMpCmdRunは管理者実行が前提。除外設定・履歴を参照できる権限も必要。
**開発PCの通常権限では除外設定が非公開で、出力禁止になることを確認済み。施設管理者の管理下で管理者として起動して受入試験する。**
一般スタッフ権限での運用には、権限を分離した検査サービス等が別途必要。設定画面の権限分離も未実装。

通常検査は除外を尊重するため、除外パス・拡張子・プロセス・脅威別アクション設定が存在する場合、非公開の場合、アーカイブ検査無効時も合格にしない。アプリはDefender設定を変更しない。
無効／受動モード、古い・未来日時の定義、権限不足、検査未完了、開始対象や完了イベントの相関不能、検出、履歴取得不能、検査後の変更、DICOM候補の破損では両出力を禁止する。
検査中の端末の別の場所の検出・設定変更も保守的に止める。
通常ファイルでDICOM解析不能なら対象外にするが、DICOMヘッダー／`.dcm`を持つ候補の破損は失敗扱い。
検査プロセスは30分でタイムアウト。中止・タイムアウトでも出力しない。中止後もOS側の検査が継続する可能性がある。
定義更新経路、クラウド保護、自動サンプル送信は施設ポリシーで管理する。検査合格は無害の保証ではない。

## 記録と障害時

作業領域は `%LOCALAPPDATA%/MedicalDiscImporter/Quarantine/<処理ID>/`。
現在ユーザー・SYSTEM・Administratorsに限定したACLを作成し、コピー、ISO、manifest.json、verification.json、scan.json、job.jsonを保持する。
scan.jsonは判定理由、定義版、Scan ID、開始・完了イベントのRecord ID、終了コードを記録する。
患者名・シーケンスやコマンドの生出力は通常ログに保存しない。manifestの元パス、患者ID、出力名は患者情報を含み得るためアクセス権・保持期限を管理する。

失敗・中止でも作業領域を自動削除しない。出力先の `.partial` ファイル／フォルダも保持する。
最終名への変更は同じ出力フォルダ内で行う。DICOMは一式準備後にフォルダ名を変更する。
ISOとDICOMを一括で原子的に保存することはできない。ISO保存後のDICOM失敗は `PartialOutput` とし、保存済みISOを削除しない。
強制終了・電源断・ネットワーク断では、終了時刻のないjob.json、`.partial`、最終出力を管理者が照合する。
自動再送・再開は未実装。再取り込みは別処理になるため重複に注意する。
共有の空き容量は事前保証できず、コピー失敗時に止める。作業領域はM1の空き容量検査を継続する。

## ビルド・発行

.NET 10 SDKと.NETデスクトップ開発対応Visual Studioを用意し、ソリューションのAppをスタートアップにする。

```powershell
dotnet build MedicalDiscImporter.sln -c Debug -p:Platform=x64
dotnet run --project src/MedicalDiscImporter.App
dotnet publish src/MedicalDiscImporter.App -p:PublishProfile=WinArm64
dotnet publish src/MedicalDiscImporter.App -p:PublishProfile=WinX64
```

出力は `artifacts/publish/win-arm64/MedicalDiscImporter.App.exe` と `artifacts/publish/win-x64/MedicalDiscImporter.App.exe`。
自己完結型単一EXE。ランタイムは実行時に一時展開する。COMのためトリミング・NativeAOTを使わない。
fo-dicomは.NET Standard 2.0のマネージドパーサー（MS-PL）。ネイティブ画像codecは追加しない。
ARM64の発行と実機動作は別に確認する。配布時はライセンス／通知を同梱する。

## 試験

追加テストフレームワーク不要のコンソールランナー。`dotnet test`での検出には非対応。

```powershell
dotnet run --project tests/MedicalDiscImporter.Tests
dotnet run --project tests/MedicalDiscImporter.Tests -- --imapi --mount
# 管理者権限・除外なし・最新定義の端末で正常完了の相関を検証
dotnet run --project tests/MedicalDiscImporter.Tests -- --defender
# 通常権限で状態を取得できない端末で出力禁止を検証（上記と別の環境条件）
dotnet run --project tests/MedicalDiscImporter.Tests -- --defender-blocked
# Defender判定の模擬OS応答試験（実スキャン・設定変更なし）
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tests/MedicalDiscImporter.Tests/DefenderScan.Tests.ps1
```

試験成果物は `artifacts/tests/<ID>/` に保持。開発環境ではコンソール39件とDefender模擬17件、計56件が成功。
解析・日付代替・ハッシュ一致・出力禁止・衝突・部分成功をダミーDICOMと差し替え検査器で検証する。
`--defender`は無害なダミーDICOMへの実スキャン。正常完了イベント相関を必須とし、未確認を成功扱いしない。脅威を作成したりDefender設定を変更したりしない。

実機受入ではARM64 Surface＋USBドライブで正常、複数日付、日本語名、拡張子なし、破損、中止、抜去、容量不足、共有切断を試す。
管理者が検査ポリシーの検証環境で、検出時の両出力禁止、古い定義・無効状態・イベント取得不能も確認する。
本番利用の前に出力権限・バックアップ・保持期限と一般スタッフ用の権限分離を決める。

## 残る制限

再構築ISOは原本のセクタ・トラック・旧セッション・起動情報・音声を保持しない。Windowsから見えるツリーと内容を検証する。忠実なISO/BIN/CUEは未実装。
重複排除、撮影日選択、RSBase命名、電子カルテ転送、設定の管理者権限分離、自動保持期限・障害回復は未実装。
IMAPI COMとマウント／解除は即時中断できない場合がある。DICOM解析はタグ境界で中止を確認する。
画面は125～200% DPIと縦横表示で実機確認する。小画面では横向きを推奨する。
調査根拠と更新記録は [docs/ImplementationPlan.md](docs/ImplementationPlan.md) を参照。
