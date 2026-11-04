# MedicalDiscImporter

Windows 11 ARM64タブレット向け、.NET 10 / C# / WinFormsの光学ディスク取り込みアプリ。
現段階は **M1：再構築ISOの技術試作**。本番利用不可。患者データではなくダミーディスクで評価する。

## 実装済み

- Core / App / Testsを分離したVisual Studioソリューション。ソース・プロジェクトの改行はCRLF。
- 大きい患者ID入力、数字キーパッド、ドライブ選択・状態、開始確認、中止、進捗・結果表示。
- 同一Windowsセッションでの二重起動防止。処理中の多重取り込み防止。
- Windows標準IMAPI2FSで、Windowsから見えるファイルツリーをUDF形式の再構築ISOにする。
- 先にローカルのアクセス制限領域へ全ファイルをコピー。EXE等も単なるバイト列として保持し、実行しない。
- ISOをWindows標準の読み取り専用マウント機能で開き、元のファイル一覧と相対パス・サイズ・SHA-256・空ディレクトリを照合。欠落・名前変更・内容変更は失敗扱い。
- マウント試行後は成功・失敗・中止のいずれも解除し、解除状態を確認。読み取り中はスリープを抑止して終了時に解除。
- 作業フォルダ単位の処理ID、時刻、入力患者ID、ディスクのファイルシステム、Reconstructedフラグ、ISO SHA-256、件数、処理・解除状態をJSON記録。
- リンク／再解析ポイント拒否、パストラバーサル防御、異常なパス拒否、100,000エントリ・20 GiB制限、空き容量検査。
- x64 / ARM64の自己完結型単一EXE発行プロファイル。

## Visual Studioでデバッグ

1. .NET 10対応のVisual Studioと「.NETデスクトップ開発」、.NET 10 SDKを用意する。
2. `MedicalDiscImporter.sln` を開く。
3. `MedicalDiscImporter.App` をスタートアッププロジェクトに設定する。
4. x64 PCでは `Debug / x64`（またはAny CPU）でF5。ARM64用EXEをx64 PCで起動しない。

CLIではリポジトリルートで以下を実行する。

```powershell
dotnet build MedicalDiscImporter.sln -c Debug -p:Platform=x64
dotnet run --project src/MedicalDiscImporter.App
```

SDKのMSBuildサーバー・ワークロード検出が制限環境で失敗する場合の回避例：

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build MedicalDiscImporter.sln -m:1 -nr:false -p:UseSharedCompilation=false
```

## 単一ファイルで発行

Visual StudioのAppプロジェクトの「発行」から `WinArm64` または `WinX64` を選ぶ。CLIは以下。

```powershell
dotnet publish src/MedicalDiscImporter.App -p:PublishProfile=WinArm64
dotnet publish src/MedicalDiscImporter.App -p:PublishProfile=WinX64
```

出力は `artifacts/publish/win-arm64/MedicalDiscImporter.App.exe` および `artifacts/publish/win-x64/MedicalDiscImporter.App.exe`。
.NETランタイム同梱。初回の復元・発行にはMicrosoftのランタイムパックを取得できる開発環境が必要。配布端末のインターネット接続は前提にしない。
単一EXEは実行時にネイティブランタイムをユーザーの一時領域へ展開する。書込権限と空き容量が必要。COMのためトリミング・NativeAOTは使用しない。
Windowsに組み込まれたIMAPI2FSとWindows PowerShell / Storageモジュールは別途OS側に必要。外部ツールやNuGetライブラリの追加はない。

## 試作の使い方と保管場所

1. AutoPlayをWindows設定から無効にし、AutoRunも管理ポリシーで無効にする。ディスク内Viewerを起動しない。
2. ダミーデータを収録したCD/DVDとUSB DVDドライブを接続する。
3. 「ディスクを再確認」、患者ID入力、「試作取り込み開始」。最終結果とSHA-256を確認する。
4. ISO・コピー済みファイル・manifest.json・job.jsonは `%LOCALAPPDATA%/MedicalDiscImporter/Quarantine/<処理ID>/` に保持される。

作業領域はローカルNTFS限定。現在ユーザー・SYSTEM・Administratorsのみアクセス可能なACLを作成する。
失敗・中止時も作業領域を保持する。自動削除は未実装。管理者がマウント解除済みであることを確認したうえで、施設の保管期限に沿って削除する。空き容量を監視する。
manifestには元の相対ファイル名が含まれるため、患者情報を含む可能性がある。保管権限・保持期間を患者情報と同様に管理する。
作業ルートは固定、ISO保存先・RSBase取込先を設定する機能は未実装。「設定・注意事項」は読み取り専用。

### 照合結果の読み方

「ファイル内容照合：合格」は、ディスク読取時の一覧とISO内の全ファイルの相対パス・サイズ・SHA-256、およびフォルダ構成が一致したことを意味する。
表示される「ISOファイルSHA-256（記録用）」はISOファイル自体のハッシュであり、不一致エラーの表示ではない。原本ディスクとのセクタ単位比較は実施しない。
`job.json` の `ContentsVerified: true` は照合合格、`Unmounted: true` は解除確認済みを示す。
再構築方式では原本のセクタ配置等は保持しないため、ファイル内容照合に合格しても原本と同一のディスクイメージとは扱わない。
照合結果はアクセス制限された処理フォルダの `verification.json` に記録する。不合格時は欠落・追加・サイズ相違・内容相違を件数付きで表示する。
詳細には元のファイル名が含まれるため、患者情報と同様に管理する。

## 現段階で利用できない機能

- 原本のセクタ単位読み取り、忠実なISO、トラック／セッションの識別、BIN/CUE。音楽CDは未対応。
- 再構築ISOはファイルデータとツリーの照合のみ。起動情報・セッション情報・音声トラック・ACL等は保持しない。Windowsから見えないファイルや旧セッションの存在も検出できない。
- Defender連携、DICOM解析・日付別命名・重複判定、管理者設定、保持期間による削除。
- ISOの最終保存、電子カルテへの正式転送、ネットワーク障害からの再送。

**Defender未検査のため正式出力は常に無効**。今回の処理完了は本番取り込み成功を意味しない。ISOファイルは隔離領域内の試験成果物。
再構築前のファイルコピー中にもOSのリアルタイム保護でファイルが除去される可能性がある。読み取り不能・照合不一致なら失敗とする。除外設定を追加しない。
COMのAddTree / CreateResultImageとWindowsのマウント・解除中は即時中断できない。操作完了後に中止を反映し、後始末する。
OSコマンドが応答しない場合は処理が待機し続ける可能性がある。強制終了・電源断後は管理者がマウント状態とjob.jsonを確認する。終了時刻なしの記録は未完了として扱う。
一般ユーザー権限でのARM64実機動作、USB DVDドライブとの相性、ディスク抜去時の動作は未検証。

## 試験

追加NuGet不要のコンソール試験ランナー。`dotnet test`によるテスト検出には対応しない。

```powershell
dotnet run --project tests/MedicalDiscImporter.Tests
dotnet run --project tests/MedicalDiscImporter.Tests -- --imapi --mount
```

後者はダミーISOを実際にマウント・解除するWindows統合試験。ドライブ文字割り当てとISOマウントが許可された端末で行う。
試験用ジャンクションを作成して拒否を確認し、リンク自体だけを削除する。試験成果物は `artifacts/tests/<ID>` に保持。
既知ハッシュ、入力制限、上書き防止、名前変更・欠落・内容変更・空フォルダ欠落、キャンセル、リンク拒否、実IMAPI／マウント照合を検査する。

## ARM64 SurfaceでのM1受入試験

- ARM64版EXEを一般ユーザーで起動。IMAPI COM登録、UDF生成、Storageモジュールの利用権限を確認。
- USBドライブでCD-R、DVD-R、DVD二層、大容量、拡張子なし・日本語名・空フォルダのダミーディスクを試す。
- 原本の見えるファイル一覧と作業manifest、ISOマウント後の一覧・ハッシュを比較。
- 読み取り中／ISO生成中／マウント中／照合中の中止・ディスク抜去・破損ディスク・空き容量不足で失敗を表示することを確認。
- 成否・中止にかかわらず仮想ドライブが残らないこと、スリープ抑止が解除されることを確認。
- 画面の125～200%表示、縦向き・横向き、タップによる入力、OSキーボードを確認。小画面では横向きを推奨。

## 次のマイルストーン

M2はDICOMパーサーの依存・ARM64互換性を確認して、ダミーデータで日付選択・安定ソート・命名を実装する。
M3はDefender状態・定義鮮度・完了・脅威検出・変更されたファイル一覧を突き合わせ、未完了・不明なら公開しないゲートを実装する。
M4は管理者設定・ネットワーク転送・ハッシュ照合・障害回復。

RSDICOMはサブフォルダ／完了マーカーに非対応であることを確認済み。監視フォルダに複数ファイルを一括原子的公開することはできない。
正式転送実装の前に、RSDICOMの監視停止・再開を運用またはAPIで保証できるか確認が必要。停止を保証できない場合、施設側と部分公開を許容する受け渡し方式を決める。
本番データを出す前にダミーDICOMで `患者ID~000001~yyyy_MM_dd~CTMRI~RSB.dcm` の受理、日付別連番、同名衝突、途中コピー・ネットワーク断の挙動を実機確認する。

専用VLAN・一般ユーザー・アプリ実行制限・BitLockerを推奨。Defender定義の更新経路、クラウド保護・自動サンプル送信の扱いは院内ポリシーで管理する。
将来の転送先は最小権限の受け渡しサービスを想定し、Karte VLAN全体へのSMB書き込みを前提にしない。

実装方針と調査根拠は [docs/ImplementationPlan.md](docs/ImplementationPlan.md) を参照。
