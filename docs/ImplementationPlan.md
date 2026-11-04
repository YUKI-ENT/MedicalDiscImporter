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
