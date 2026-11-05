# 実装方針・調査メモ（2026-11-04）

## 選択肢と採用判断

| 項目 | 選択肢・実証可能性 | 今回の判断 |
|---|---|---|
| セクタ単位のISO | 読み取り専用の外部エンジン／SDKを採用し、ARM64バイナリ・権限・読取エラー・ライセンスを確認する必要あり。Windows IMAPIのファイルシステム生成を原本読み取りとは扱えない | エンジン未定。低レベルI/Oは自作しない。未実装 |
| 再構築ISO | Windows組み込みIMAPI2FS。単一セッション・開始アドレス0の結果ストリームはISO保存可能。AddTreeでファイルツリーを追加できる | ユーザー了承により採用。UDFのみ・strict compliance・Reconstructed記録。ファイル一覧／ハッシュをマウント後に照合 |
| ISO内容読み出し | Windows標準Storageコマンドと、外部ISO/UDFパーサーの比較。外部パーサーは形式互換性・依存管理が追加で必要 | Windows標準を採用。ARM64実機での権限・動作確認は未完了 |
| マルウェア検査 | MpCmdRunの通常戻り値0は「脅威なし」と「検出後修復」を区別しない。DisableRemediationでは除外無視・アーカイブ検査が可能だが、検出履歴／イベントには検出結果が記録されないという制約あり | 終了コードだけで合格にしない。通常検査＋履歴・イベント・状態の相関、またはDisableRemediationの出力検証を次段階で実証。未実装で正式出力を遮断 |
| DICOM | fo-dicom等のマネージドパーサーを候補にする。画像デコードを使わないタグ読み取りを優先 | 今回は依存追加なし。採用前にバージョン・ライセンス・推移依存・ARM64・悪意のある入力・拡張子なしファイルを確認 |
| RSDICOM公開 | サブフォルダ・完了マーカー非対応。フラットフォルダに多数ファイルの一括原子的公開はできない | 監視停止の手段と運用を確認するまで実装しない。独断で一式公開済みと扱わない |

IMAPIとStorageはWindowsに含まれるOS機能を利用する。ARM64で利用可能と文書だけで断定していない。COM登録・UDF生成・PowerShell Storage動作を実機で確認すること。
プロトタイプに外部NuGet依存や外部リッピング実行ファイルは含めていない。

## 公式資料

- [IFileSystemImage.CreateResultImage](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-createresultimage)：ISO保存できる条件。
- [IFsiDirectoryItem.AddTree](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifsidirectoryitem-addtree)：ツリー追加、includeBaseDirectory指定。
- [StrictFileSystemCompliance](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-put_strictfilesystemcompliance)：規格制約違反を例外にする。
- [FreeMediaBlocks](https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifilesystemimage-put_freemediablocks)：0は制限なし。アプリ側で読取容量を別途制限。
- [Mount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/mount-diskimage)：ISOマウントと読み取り専用指定。
- [Dismount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/dismount-diskimage)：ISO解除、OS／権限条件。
- [MpCmdRun](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus)：戻り値とDisableRemediationの制約。
- [.NET単一ファイル発行](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)：RID別発行、ネイティブライブラリの埋め込みと実行時展開。

## 検証記録

開発PCはWindows x64、.NET SDK 10.0.403。
ダミーファイルだけで、IMAPIのUDF生成とWindows標準マウント→ファイル照合→解除の統合試験を含む16試験が成功。
Debug/x64ビルドは警告0・エラー0。win-arm64とwin-x64の自己完結型発行を実行し、各出力フォルダがEXE 1個だけであることを確認。
発行済みx64 EXEの起動と画面描画を確認。画面は `artifacts/ui-published.png`、開発版のプレビューは `artifacts/ui-preview.png`。
ARM64実機・USB光学ドライブ・読取エラー・電源断・Defender・DICOM・RSDICOMの試験は未実施。

追加検証：照合の種類別診断とverification.jsonを追加し、ダミーISOの実マウント・解除を含む18試験に成功。
ユーザー提供の実ディスク処理ログでは58ファイルの内容照合とマウント解除が成功し、ErrorCodeはnull。開発側では当該ディスクの再試験はしていない。
成功画面に照合合格と記録用ISOハッシュを明示し、再構築方式の注意と区別するよう変更。

## 2026-11-05更新：Defender・DICOM・指定フォルダ出力

以下は上のM1時点の未実装判断を更新する。

- M1の実機動作確認をユーザーから受領。既存の再構築・内容照合・解除フローを保持し、後段にImportPipelineを追加した。
- DICOMはfo-dicom 5.2.6（MS-PL、.NET Standard 2.0）を採用。NuGetメタデータ・公式APIを確認し、管理コードのタグ読み取りのみ使用する。ネイティブ画像codecは導入しない。依存のMicrosoft.Extensions等とCommunityToolkitのライセンス・通知も同梱する。復元時点のNuGet脆弱性情報に既知の該当はなかった。
- 全ファイルを解析し、拡張子なし／Part 10 preambleなしにも対応。DICOMDIRはファイルメタのMediaStorageSOPClassUIDで識別して出力対象外にする。患者名はSpecificCharacterSetを尊重する。
- シリーズ単位の患者名・撮影日・シーケンス・ファイル数を一覧表示。StudyDate、SeriesDate、AcquisitionDate、ContentDateの順で妥当な日付を取り、不明なら取込日を明示する。画像はデコードしない。
- 最新依頼に合わせ、指定フォルダへの抽出は元階層・元名を保持し、拡張子がない場合だけ.dcmを付加する。RSBase改名・フラットな監視フォルダへの公開は今回に含めない。RSDICOM側の監視制御問題を汎用フォルダ抽出と混同しない。
- Defenderは通常カスタム検査を採用。DisableRemediationによるコンソールの翻訳依存の解析は採用しない。通常稼働・定義日時・リアルタイム保護を前後に検査し、正常終了コード、対象パスの開始イベント1000とScan IDが一致する完了イベント1001、脅威履歴、検出・中止・エラー・設定変更イベントを照合する。不明なら出力禁止。
- 通常検査が除外を尊重するため、除外や脅威別アクションが存在する場合、除外を参照不能な場合、アーカイブ検査無効時は出力しない。アプリは施設のDefender設定を変更しない。
- 開発PCの通常権限ではGet-MpPreferenceの除外がN/Aになることを実測。実検査器による出力禁止経路を確認した。MicrosoftのMpCmdRun手順は管理者起動が前提。一般スタッフ用の権限分離サービス・管理者限定設定は別工程であり未実装。正常合格と脅威検出の実スキャン、ARM64上のパーサー／Defenderは未検証として残す。
- スキャン後に全ファイルとISOのSHA-256を再確認。出力もサイズ・SHA-256を照合する。ISOは同日衝突で続番、DICOMは処理IDのフォルダに一式準備してリネームする。既存ファイルは上書きしない。部分成功・中止・不明をjob.jsonに保存する。
- 出力先の.partialは障害時に保持。共有の容量・障害回復・強制終了時の出力とログの整合は自動修復せず、管理者が確認する。ISOとDICOMの一括原子的公開は保証しない。

追加の一次資料：

- [fo-dicom 5.2.6パッケージと依存](https://www.nuget.org/packages/fo-dicom/5.2.6)
- [DicomFile公式API](https://fo-dicom.github.io/stable/v5/api/FellowOakDicom.DicomFile.html)
- [fo-dicomライセンス](https://github.com/fo-dicom/fo-dicom/blob/5.2.6/License.txt)
- [Defenderイベント1000・1001・検出／エラー](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus)
- [Get-MpComputerStatus](https://learn.microsoft.com/en-us/powershell/module/defender/get-mpcomputerstatus)
- [Get-MpThreatDetection](https://learn.microsoft.com/en-us/powershell/module/defender/get-mpthreatdetection)

検証：ダミーDICOMによる日本語名・シーケンス・拡張子なし・preambleなし・DICOMDIR除外・不正日付の代替、非DICOM除外、破損候補拒否、検査不合格・修復変更・ISO変更・改名衝突・既存ISO保持・部分成功・中止・DICOMなしを検査した。実IMAPI・マウント解除・実Defenderの権限不足時出力禁止も含め39試験成功。Defender合格経路は差し替え検査器で検証し、実Defenderの正常スキャンを成功したとは扱わない。

追加検証：製品のDefender判定スクリプトを模擬OS応答で直接実行する17試験を追加。実スキャン呼び出しは試験ハーネス内でのみ置換し、製品に検査省略スイッチは設けていない。正常合格、無効、古い／未来定義、除外／非公開除外、アーカイブ無効、完了なし、Scan ID違い、対象違い、非0終了、履歴取得不能、修復済み検出、検出イベント、中止、エラー、設定変更を確認。合計56試験成功。x64/ARM64の自己完結型発行も成功。ライセンス・通知はEXEと一緒に配布する。実機での正常Defender検査確認が残ることは変更しない。
