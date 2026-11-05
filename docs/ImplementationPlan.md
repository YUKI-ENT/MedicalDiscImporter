# 実装方針・調査メモ（2026-11-04）

## 選択肢と採用判断

| 項目 | 選択肢・実証可能性 | 今回の判断 |
|---|---|---|
| セクタ単位のISO | 読み取り専用の外部エンジン／SDKを採用し、ARM64バイナリ・権限・読取エラー・ライセンスを確認する必要あり。Windows IMAPIのファイルシステム生成を原本読み取りとは扱えない | エンジン未定。低レベルI/Oは自作しない。未実装 |
| 再構築ISO | Windows組み込みIMAPI2FS。単一セッション・開始アドレス0の結果ストリームはISO保存可能。AddTreeでファイルツリーを追加できる | ユーザー了承により採用。UDFのみ・strict compliance・Reconstructed記録。ファイル一覧／ハッシュをマウント後に照合 |
| ISO内容読み出し | Windows標準Storageコマンドと、外部ISO/UDFパーサーの比較。外部パーサーは形式互換性・依存管理が追加で必要 | Windows標準を採用。ARM64実機での権限・動作確認は未完了 |
| マルウェア検査 | MpCmdRunの通常戻り値0は「脅威なし」と「検出後修復」を区別しない。DisableRemediationでは除外無視・アーカイブ検査が可能だが、検出履歴／イベントには検出結果が記録されないという制約あり | 終了コードだけで合格にしない。通常検査＋履歴・イベント・状態の相関、またはDisableRemediationの出力検証を次段階で実証。未実装で正式出力を遮断 |
| DICOM | fo-dicom等のマネージドパーサーを候補にする。画像デコードを使わないタグ読み取りを優先 | 今回は依存追加なし。採用前にバージョン・ライセンス・推移依存・ARM64・悪意のある入力・拡張子なしファイルを確認 |
| RSDICOM公開 | サブフォルダ・完了マーカー非対応。多数ファイルの一括原子的公開はできない | 最新依頼に従い、照合済みファイルを1件ずつフラットに受け渡す。部分成功を記録し、RSBase内部での取り込み完了は別途受入確認する |

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

## 2026-11-05更新：標準ユーザー用の検査サービス

- 設定画面にサービス登録・更新、接続確認、登録解除を追加。登録・更新・解除は別プロセスをrunasで起動してUAC承認を要求する。管理者資格情報はアプリへ渡さず、保存しない。
- 発行済み単一EXEをProgram Files配下へコピーし、自動起動のLocalSystemサービスとして登録する。同じEXEの専用引数でSCMへ接続する。配置先・作業領域はSYSTEMとAdministratorsのみ許可し、リンクや安全でない既存ACL・所有者は拒否する。解除では配置ファイルを残す。
- アプリはサービスへローカルの名前付きパイプでファイル内容を送る。サービスへコマンドや任意の元パスは渡さない。ネットワークログオンを拒否し、クライアントにパイプの追加インスタンス作成権限を与えない。クライアントはSYSTEM所有者を確認してからデータを送る。
- バージョン・操作・フレームサイズ・名前・親フォルダ・重複・件数・容量・ハッシュを検証する。保護領域への受信後と実検査後に同じ内容を照合する。アプリ側の再照合と既存の最終出力ゲートも維持する。
- 端末内は直列検査。切断・余分な入力で検査を中止し、不明なら出力しない。要求ヘッダー15秒、転送10分、全処理40分、クライアント45分の上限。後片付けが失敗したときも合格結果を返さない。異常終了で残ったGUID名の領域は次回起動時に削除する。
- 実パイプ経由の模擬検査で正常受信、日本語名・空フォルダ、転送改変、検査中改変、検出、切断・未完転送と削除を確認。不正プロトコル・パス・予約デバイス名・ADS・重複・親欠落・容量・件数・ハッシュ・フレーム上限、キャンセル、標準ユーザーによる偽サービス拒否も確認した。コンソール53件とDefender模擬17件、計70件成功。設定画面を画像化して表示を確認した。
- 実サービス登録、UAC操作、再起動後の自動起動、標準ユーザーからSYSTEMサービスを経由した正常Defender検査、ARM64実機での権限境界・停止時の動作は未検証。施設ポリシーでの設定非公開を回避する実装は行わない。

## 2026-11-05更新：接続確認の不安定さ・開始停止の修正

- 実サービスと同じ64 KiBバッファのパイプで、クライアントが10 ms遅れて読むだけで応答が失われることを再現した。従来の単発試験はパイプをDisposeし、バッファサイズも異なっていたため、実際のDisconnectと再接続の競合を検出できなかった。
- 通信をv2へ更新。応答送信後、クライアントから受信確認（ACK）を5秒以内に受け取ってから切断する。接続確認と検査結果の両方に適用する。応答を読む前の切断によるデータ破棄を防ぎ、旧クライアントとの混在を避ける。
- 接続直後にクライアントが切断したときのWaitForConnectionエラーとDisconnectエラーを依頼単位で処理する。リセットできないパイプはアクセス権を保持したファクトリで作り直す。個別の通信失敗でサービス全体を終了しない。
- SCMの停止ハンドラは停止中を報告してCancelAsyncで中断を依頼し、速やかに戻る。状態照会への応答で開始中を実行中に上書きしない。起動時・致命的エラーと要求エラーの診断コードを保護されたservice.logへ記録する。
- 開始・停止は状態遷移の完了まで待つ。自動起動と手動開始の競合、開始中に停止要求、すでに停止済みの競合を扱う。更新・解除はSCMの停止状態だけでなく、取得したサービスプロセスの終了も待つ。待機権限だけのプロセスハンドルを使い、強制終了は行わない。
- 設定画面に管理者操作の開始・停止を追加。接続失敗時はサービス状態を照会し、単に未接続とだけ表示しない。管理者操作のエラーにはWindowsエラー番号を表示する。
- 実パイプで30回の連続接続と遅延受信、受け付け前の切断からの復帰、接続待機中・受信確認待機中の停止、および模擬SCMで開始停止の競合・失敗・待機上限を検証した。コンソール65件とDefender模擬17件、計82件成功。実際のUAC操作・SCMでの更新・開始停止とDefender実検査の確認は別途必要。

### x64実サービスでの追加確認

- ユーザーによるUAC承認のもと、既存登録の更新を試みたところ1072（削除保留）を実測。サービス管理画面を閉じると登録が消え、再登録・起動に成功した。1072には管理画面を閉じる案内を追加し、解除成功時もWindowsへ削除要求を行ったことを正確に表示する。
- 標準ユーザー（昇格していないことを確認）から初回30回連続接続に成功し、サービスはRUNNINGを維持した。
- 発行済みEXEの管理処理で停止・再開がそれぞれ終了コード0となり、SCM上のSTOPPED・RUNNINGを確認。再開後も標準ユーザーで10回連続接続に成功した。
- 無害な1 KiBのダミーファイルを標準ユーザーからサービスへ送信して実検査を依頼。サービスはDefender設定の安全条件でUnknownを返し、正常合格とは扱わなかった。Defender設定は変更していない。
- 続けて管理処理による停止・登録解除に成功し、SCMで1060（未登録）を確認。同じ配布用EXEで再登録・開始し、AUTO_START・RUNNINGおよび標準ユーザーからの10回連続接続を確認した。計50回の実サービス接続に成功。最終状態は自動起動・実行中。
- 正常なDefender合格、PC再起動後の動作、ARM64実機上のサービス制御は未検証として残す。

## 2026-11-05更新：対象別の除外確認と実Defender合格

- 従来は無関係な除外が1件でも存在すると検査前に停止していた。Microsoftの仕様ではプロセス除外はオンデマンド検査に適用されないため、この存在だけで停止する判定を削除した。
- フォルダ・拡張子除外は独自のワイルドカード照合を行わず、実際の保護作業領域のルートと全ファイルに対して検査前後にMpCmdRun -CheckExclusionを実行する。Microsoftが記載する終了コード1（除外対象外）のみ許可する。0と未定義・エラーの終了コードは拒否する。除外非公開、条件付き除外、脅威別アクション、アーカイブ検査無効・取得不能は理由を分けて拒否する。設定は変更しない。
- 修正後の実検査は終了コード0となったが、イベント1000の対象がfolder:_接頭辞と二重バックスラッシュで記録され、従来の照合が失敗していた。実イベントで形式を確認し、通常パス・folder:_付き・folder:_付きのエスケープされたパスについて、対象ディレクトリ全体の厳密な一致を許可した。対象の部分一致や複数対象は許可しない。Scan IDの一致、完了順序、検出・エラー・保護状態の確認は維持した。
- 製品スクリプトの模擬試験は32件成功。対象除外・対象外の除外・プロセス除外・確認コマンドの異常終了・検査後に除外変更・非公開・条件付き除外・実形式のイベントパス・類似／複数対象の拒否を追加した。ハーネスは実コマンドを明示的に差し替え、製品に検査省略スイッチは追加していない。Windows PowerShell 5.1で日本語の試験名・理由も扱うため試験スクリプトはUTF-8 BOM付き、CRLFとした。
- ユーザーのUAC承認でx64実サービスを更新。標準ユーザーからダミーDICOMの検査に合格し、終了コード0と同一Scan IDの1000・1001イベントを確認した。さらに実サービスの合格結果でダミー取り込みデータのISO・DICOM出力が許可されることを確認した。光学読み取り・ISO構築部分は既存の検証済み状態を模したダミーフィクスチャを使い、実光学ドライブの試験とは区別する。
- 194件の1 KiBダミーファイルを別途作成し、標準ユーザーからの実サービス検査が約7.5秒で合格した。Defender定義1.463.135.0、開始イベント28895・完了28896のScan ID一致を確認した。患者データ・脅威テストファイルは使用せず、Defender設定も変更していない。
- コンソール66件（--serviceを含む）と模擬Defender32件、計98件成功。実機の正常Defender合格の未検証は解消。再起動後の動作、ARM64実機、実ディスクでの一連の取り込みは引き続き受入対象とする。

根拠：[Microsoftのカスタム除外とCheckExclusion](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-exclusions-configure)、[MpCmdRunのコマンドと検査結果](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus)。

## 2026-11-05更新：Defender詳細表示・RSBase自動取込用出力

- 完了画面へ検査対象ファイル数、重複しない脅威検出記録数、除外確認を含む検査処理時間、定義バージョンを追加。Defender内部の検査項目数は取得していないため「スキャン件数」と偽らず、実際の対象ファイル数と明記する。DetectionIDで重複を除き、イベントだけで検出件数を確定できない場合は取得不可とする。scan.jsonとjob.jsonにも値を保存する。
- ユーザーへ命名を確認し、区切りは各1個の~、連番は日付ごとに6桁として合意。設定画面へRSBase検査名（既定CTMRI）を追加し、従来のDICOM出力先をRSBase自動取込先として使う。新規出力は元階層を保持せず、患者ID~連番~yyyy_MM_dd~検査名~RSB.dcmを直下に配置する。DICOM内容は変更しない。
- 連番は患者ID・撮影日ごとに永続化し、検査名を変えても戻さない。既存RSBファイルの最大番号と採番記録の大きい方に続ける。6桁超は自然に拡張する。採番記録は取込先の.MedicalDiscImporter/sequence.json、排他は同じ共有内のsequence.lock。監視側が直ちにファイルを移動・削除しても、複数PCが同時出力しても同一番号を再利用しない。上書きせず、別の出力元と衝突した場合は新しい番号を予約する。
- 全DICOMを同じ共有内の準備領域に.pending名でコピーし、サイズ・SHA-256を照合する。全件の番号を永続予約し、元ファイルと正式名の対応を私有作業領域のrsbase-output.jsonへ記録してから、正式名へ1件ずつ変更する。これは一括原子的な公開ではない。正式名にした件数をjob.jsonへ都度記録し、途中失敗・中止ではISO保存済み状態と受け渡し済み件数を表示する。自動再送や既存RSBase/RSDICOMの変更は行わない。
- 試験で日本語検査名・不正文字拒否、日付別採番、既存番号の継続と100万番への拡張、監視側の即時消費、同時出力、外部衝突、中断、採番JSON破損、DICOM内容不変を確認した。Defenderでは対象件数、0件と取得不可の区別、重複検出記録を1件とすること、所要時間を確認した。
- ユーザーのUAC承認でx64実サービスを更新。標準ユーザーから実Defender検査合格とTargetFileCount=5、ThreatDetectionCount=0、ElapsedSeconds=2.39を実測。開始イベント28905・完了28906を相関した。ダミー取り込みデータでは、実サービスの合格後に3件のRSB名DICOMを取込先直下へ受け渡し、ジョブの詳細値・件数と記録も確認した。試験先は作業フォルダ内のダミー専用であり、実RSBase取込先や患者データには書き込んでいない。
- コンソール74件（--serviceを含む）とDefender模擬33件、計107件成功。設定画面を描画して検査名・取込先・サービス操作と保存ボタンの表示を確認した。実RSBase/RSDICOM内部での取込結果、UNC共有の監視、ARM64実機受入は未検証として残す。

参考：[RS_Base公式配布のRS_Receiver取扱説明書](https://rsbase.jp/inc/download/RS_Base/14_RS_Receiver.pdf)、[Microsoft Defenderの開始・完了・検出イベント](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus)。
