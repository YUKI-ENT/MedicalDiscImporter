# MedicalDiscImporter icon proposals

候補比較: comparison.png。各案はSVG原稿、256px PNG、16/20/24/32/40/48/64/128/256pxを含むICOを用意する。A案を採用済みで、A-disc-slices.icoをEXEとフォームのアイコンに使用する。B・Cは比較用に保持する。

- A: 光学ディスク＋断層スライス。現行UIのティールに合わせた推奨案。
- B: CT/MRIのガントリ＋寝台。検査装置の形を優先した案。
- C: ディスク＋送信矢印。DICOM受け渡しを強調する案。

Windows上で `pwsh -NoProfile -File assets/icon-proposals/render.ps1` を実行してPNG・ICO・比較画像を再生成できる。独自のベクター図形のみで構成し、外部画像・フォントの埋め込みはない。
