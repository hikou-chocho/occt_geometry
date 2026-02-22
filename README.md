# occt_geometry

Windows + MSVC 向けの OCCT 7.9.3 依存 DLL プロジェクトです。

## OCCT導入（最短手順）

1. OCCT配布物（Windows 64bit / vc14 の combined パッケージ）をダウンロードして展開
2. 展開フォルダを `C:\Develop` 配下に置く（例: `C:\Develop\opencascade-7.9.3-vc14-64-combined`）
3. `OpenCASCADE_DIR` を `OpenCASCADEConfig.cmake` があるディレクトリに指定

例:

```powershell
$env:OpenCASCADE_DIR = "C:\Develop\opencascade-7.9.3-vc14-64-combined\opencascade-7.9.3-vc14-64\cmake"
```

`CMakeLists.txt` はこの `OpenCASCADE_DIR` から以下を自動解決します。

- include: `.../inc`
- lib: `.../win64/vc14/lib`
- dll: `.../win64/vc14/bin`

## Build

```powershell
cmake -S . -B build -DOpenCASCADE_DIR="C:\Develop\opencascade-7.9.3-vc14-64-combined\opencascade-7.9.3-vc14-64\cmake"
cmake --build build --config Release
```

または、環境変数を使う場合:

```powershell
cmake -S . -B build
cmake --build build --config Release
```

## Run sample

```powershell
.\build\Release\occt_geometry_sample.exe .\samples\box_drill_case.txt
```

入力は `samples/box_drill_case.txt`（key=value）です。引数省略時も同ファイルを既定で読み込みます。

追加ケース:

- `samples/pocket_rect_case.txt`
- `samples/turn_od_case.txt`

実行すると、`output.dir` で指定したフォルダ（既定 `out`）に以下を出力します。

- `box_drill.step`
- `box_drill.stl`
