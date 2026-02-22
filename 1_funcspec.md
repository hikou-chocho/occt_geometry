# L1 Geometry Kernel（OCCT Adapter）機能仕様書（Phase1 / Cut + Δ + STEP/STL出力）

## 1. 目的
本モジュールは、Open CASCADE Technology（OCCT）を用いて以下を提供する。
1. 素体（Stock）形状の生成
2. 加工フィーチャの適用（ブーリアン演算は Cut のみ）
3. Cut 結果形状（Result）の生成
4. Cut により除去された体積差分 Δ（Delta）の取得
5. 形状の出力（STEP / STL の選択）
6. 形状の破棄（リソース解放）

本モジュールは L0（C#）から P/Invoke を介して利用される。L0 は OCCT 型に依存しない。

---

## 2. 適用範囲（Phase1）
| 項目 | 対応 |
|---|---|
| ブーリアン演算 | Cut のみ |
| 差分 Δ | 体積差分（Shape）として取得 |
| Feature再計算（履歴/DAG） | 対象外（Phase2） |
| トポロジ命名 | 対象外（Phase2） |
| 出力形式 | STEP / STL |
| メッシュ生成 | STL出力時に利用（OCCTメッシャ） |

---

## 3. 用語定義
| 用語 | 定義 |
|---|---|
| Stock | 加工前ソリッド形状 |
| Tool | フィーチャ形状（Cut用ツール形状） |
| Result | `Cut(Stock, Tool)` の結果形状 |
| Δ（Delta） | `Common(Stock, Tool)` で定義される除去体積（削れた領域） |
| ShapeId | L1内部で管理する形状識別子（整数） |
| DTO | L0↔L1境界のデータ構造（OCCT非依存） |
| Output | STEP または STL のファイル出力 |

---

## 4. アーキテクチャ境界
### 4.1 レイヤ
```
DTO（OCCT非依存）
↓
L1 C ABI API
↓
OcctKernelImpl / ShapeRegistry
↓
L1_ApplyFeature 内の feature.type 分岐
↓
OCCT
```

### 4.2 依存制約
- DTO は OCCT の型を含まない。
- L0（C#）は ShapeId のみを保持し、OCCT型やポインタを保持しない。
- 例外は C ABI 境界を越えて伝播させない。

---

## 5. データ仕様（DTO）

### 5.1 AxisDto
座標系定義（右手系）を表す。
- `origin`：原点
- `dir`：Z方向（主方向）
- `xdir`：X方向（dirと直交）

```c
typedef struct AxisDto {
  double origin[3];
  double dir[3];
  double xdir[3];
} AxisDto;
```

### 5.2 StockDto

素体形状のパラメータ定義。

```c
typedef enum StockType {
  STOCK_BOX = 1,
  STOCK_CYLINDER = 2
} StockType;

typedef struct StockDto {
  StockType type;
  double p1;
  double p2;
  double p3;
  AxisDto axis;
} StockDto;
```

パラメータの意味：

* `STOCK_BOX`：`p1=dx, p2=dy, p3=dz`
* `STOCK_CYLINDER`：`p1=radius, p2=height, p3` は未使用（0推奨）

### 5.3 FeatureDto（Cut専用）

Phase1 では Cut の Tool 形状を生成できる最小集合を定義する。

```c
typedef enum FeatureType {
  FEAT_DRILL = 1,
  FEAT_POCKET_RECT = 2,
  FEAT_TURN_OD = 3
} FeatureType;

typedef struct DrillFeatureDto {
  double radius;
  double depth;
  AxisDto axis;
} DrillFeatureDto;

typedef struct PocketRectFeatureDto {
  double width;
  double height;
  double depth;
  AxisDto axis;
} PocketRectFeatureDto;

#define L1_TURN_OD_PROFILE_MAX 64

typedef struct TurnOdFeatureDto {
  double targetDiameter;
  double length;
  int profileCount;
  double profileZ[L1_TURN_OD_PROFILE_MAX];
  double profileRadius[L1_TURN_OD_PROFILE_MAX];
  AxisDto axis;
} TurnOdFeatureDto;

typedef struct FeatureDto {
  FeatureType type;
  union {
    DrillFeatureDto drill;
    PocketRectFeatureDto pocketRect;
    TurnOdFeatureDto turnOd;
  } u;
} FeatureDto;
```

---

## 6. インターフェース仕様（論理I/F）

### 6.1 OperationResult

Cut適用結果。

```c
typedef struct OperationResult {
  int resultShapeId;   // Result shape id
  int deltaShapeId;    // Δ shape id
  int errorCode;       // 0:OK, 非0:エラー
} OperationResult;
```

### 6.2 Output仕様

出力形式とパラメータ。

```c
typedef enum OutputFormat {
  OUT_STEP = 1,
  OUT_STL  = 2
} OutputFormat;

typedef struct OutputOptions {
  OutputFormat format;
  // STL出力時のメッシュ分割パラメータ（OCCTメッシャに渡す）
  // STEP出力時は未使用（0で可）
  double linearDeflection;   // 例: 0.05 (mm)
  double angularDeflection;  // 例: 0.5 (deg) またはラジアン運用は実装で統一
  int    parallel;           // 0/1
} OutputOptions;
```

---

## 7. 機能仕様

### 7.1 CreateStock

#### 入力

* `StockDto dto`

#### 処理

1. `dto.type` に応じて素体形状を生成する。

   * Box：`BRepPrimAPI_MakeBox(dx, dy, dz)`
   * Cylinder：`BRepPrimAPI_MakeCylinder(radius, height)`
2. （現行実装）`dto.axis` による配置変換は未適用。ローカル原点基準で生成する。
3. 生成形状を内部レジストリへ登録する。

#### 出力

* `ShapeId`（登録した Stock の識別子）

---

### 7.2 ApplyFeature（Cut）

#### 入力

* `ShapeId stockShapeId`
* `FeatureDto feature`

#### 処理

1. `stockShapeId` から Stock 形状を取得する。
2. `feature.type` に応じて Tool 形状を生成する（現行実装は `L1_ApplyFeature` 内分岐）。
3. Result を以下で生成する。

   * `Result = Cut(Stock, Tool)`
4. Δ（Delta）を以下で生成する。

   * `Δ = Common(Stock, Tool)`
5. Result と Δ を内部レジストリへ登録する。

#### 出力

* `OperationResult`

  * `resultShapeId`：Result の ShapeId
  * `deltaShapeId`：Δ の ShapeId
  * `errorCode`：0（成功）またはエラーコード

#### Δ（Delta）正式定義

Cut時の除去体積は以下で定義する。

* `Δ = Common(Stock, Tool)`

---

### 7.3 DeleteShape

#### 入力

* `ShapeId shapeId`

#### 処理

* 内部レジストリから該当 ShapeId の形状を削除する。

#### 出力

* なし（エラーは戻り値で通知）

---

### 7.4 ExportShape（STEP / STL）

#### 入力

* `ShapeId shapeId`
* `OutputOptions options`
* `const char* filePath`（UTF-8、または実装でUTF-16/W指定）

#### 処理

* `options.format` に応じて出力する。

  * STEP：

    1. Shape を STEP Writer に渡し、ファイル出力する。
  * STL：

    1. `BRepMesh_IncrementalMesh` 等で Shape をメッシュ化する。
    2. STL Writer に渡し、ファイル出力する。
* 出力前に shapeId の存在チェックを行う。

#### 出力

* `errorCode`（0:成功、非0:失敗）

---

## 8. Feature実装仕様（Phase1）

各 Feature は `L1_ApplyFeature` 内で Tool 形状を生成する。

### 8.1 Drill（円柱）

* Tool：円柱（半径=radius、高さ=depth）を `axis` に配置

### 8.2 PocketRect（矩形ポケット）

* Tool：直方体（width, height, depth）を `axis` に配置
* 直方体のローカル原点・向きの取り決め（本仕様）

  * ローカルZ方向（axis.dir）に depth を押し出す
  * ローカルX/Y方向（axis.xdir と axis.dir×axis.xdir）に width/height を割当
  * 現行実装では `axis.origin` をポケット中心として扱い、`xdir/ydir` 方向に半幅オフセットした corner から Box を生成する

### 8.3 TurnOd（外径旋削の近似）

* legacyモード（`profileCount<=1`）

  * Tool：環状体 `outerCylinder - innerCylinder(targetDiameter/2)` を生成
  * `outerCylinder` 半径は stock の境界ボックスから推定
* profileモード（`profileCount>=2`）

  * 各区間 `[z_i, z_{i+1}]` に対して `profileRadius[i]` の目標外径を適用
  * 各区間で環状体セグメントを生成し、Fuse で1つの Tool に結合
* どちらのモードでも最終計算は `Result = Cut(Stock, Tool)`, `Δ = Common(Stock, Tool)`

---

## 9. エラー仕様

### 9.1 エラーコード

| code | 内容                       |
| ---: | ------------------------ |
|    0 | 成功                       |
|    1 | 無効な引数（NULL、範囲外、未知のenum等） |
|    2 | shapeId が未登録             |
|    3 | FeatureType 未対応          |
|    4 | OCCT例外捕捉                 |
|    5 | ブーリアン演算失敗（Result生成不可）    |
|    6 | Δ生成失敗（Common生成不可）        |
|    7 | 出力失敗（Writer/ファイルI/O）     |

### 9.2 例外取り扱い

* OCCT例外を含むすべての例外は L1 内で捕捉し、エラーコードで返却する。
* 例外メッセージは Phase1 では外部へ返さない（必要な場合は Phase2 で拡張）。

---

## 10. ライフサイクル仕様

* L0 は ShapeId の寿命管理責任を持つ。
* Cut 適用後、不要になった shapeId（過去のStockや中間Result等）は DeleteShape により破棄できる。
* L1 は DeleteShape されない限り、登録形状を保持する。

---

## 11. 非機能要件

### 11.1 精度

* OCCT 標準トレランスを前提とする。
* STL出力の精度は `OutputOptions.linearDeflection / angularDeflection` に依存する。

### 11.2 性能

* ApplyFeature は `Cut` と `Common` の2回の演算を行うため、Cut単体より計算コストが増加する。
* STL出力はメッシュ分割コストを含む。

### 11.3 スレッド

* Phase1ではスレッドセーフ性を保証しない。
* 同一 kernel インスタンスへの同時呼出は不可。

---

## 12. C ABI（P/Invoke想定）API仕様

本仕様の論理I/Fを C ABI として公開する。戻り値は errorCode とし、出力は out 引数で返す。

```c
#ifdef _WIN32
  #ifdef L1_GEOMETRY_KERNEL_BUILD
    #define L1_API __declspec(dllexport)
  #else
    #define L1_API __declspec(dllimport)
  #endif
#else
  #define L1_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

L1_API void* L1_CreateKernel();
L1_API int   L1_DestroyKernel(void* kernel);

L1_API int   L1_CreateStock(void* kernel, const StockDto* dto, int* outStockId);

L1_API int   L1_ApplyFeature(void* kernel,
                            int stockId,
                            const FeatureDto* feature,
                            OperationResult* outResult);

L1_API int   L1_DeleteShape(void* kernel, int shapeId);

L1_API int   L1_ExportShape(void* kernel,
                            int shapeId,
                            const OutputOptions* opt,
                            const char* filePathUtf8);

#ifdef __cplusplus
}
#endif
```

---

## 13. クラス構成（L1内部）

* `OcctKernelImpl`：API本体、例外捕捉、Registry管理
* `ShapeRegistry`：`ShapeId -> TopoDS_Shape` の管理
* `L1_ApplyFeature`：`FeatureType` ごとの Tool 生成とブーリアン演算

---

## 14. 受入条件（Phase1完了条件）

1. Box / Cylinder の Stock を生成できる。
2. Drill / PocketRect / TurnOd を Tool として生成できる。
3. ApplyFeature により Result と Δ の ShapeId を取得できる。
4. ExportShape で STEP と STL を選択して出力できる。
5. DeleteShape により形状を解放できる。
6. C#（P/Invoke）から利用できる（C ABI、例外非伝播）。

---

## 15. 動作確認用CPP（ケースファイル読込方式）

動作確認用実行ファイルは、Stock / Feature / Output を1つの外部ケースファイルにまとめ、
それを読み込んで処理を実行する形式とする。

### 15.1 ケースファイル形式

- 文字コード: UTF-8
- 1行1項目の `key=value`
- `#` 始まり行はコメント

例（Drill on Box）:

```text
stock.type=BOX
stock.p1=100.0
stock.p2=80.0
stock.p3=20.0
stock.axis.origin=0.0,0.0,0.0
stock.axis.dir=0.0,0.0,1.0
stock.axis.xdir=1.0,0.0,0.0

feature.type=DRILL
feature.drill.radius=8.0
feature.drill.depth=12.0
feature.drill.axis.origin=50.0,40.0,20.0
feature.drill.axis.dir=0.0,0.0,-1.0
feature.drill.axis.xdir=1.0,0.0,0.0

output.linearDeflection=0.1
output.angularDeflection=0.5
output.parallel=1
output.dir=out
output.stepFile=box_drill.step
output.stlFile=box_drill.stl
output.deltaStepFile=box_drill_delta.step
output.deltaStlFile=box_drill_delta.stl
```

例（TurnOd profile）:

```text
feature.type=TURN_OD
feature.turnOd.profile.count=6
feature.turnOd.profile.0.z=0.0
feature.turnOd.profile.0.radius=25.0
feature.turnOd.profile.1.z=20.0
feature.turnOd.profile.1.radius=25.0
feature.turnOd.profile.2.z=20.0
feature.turnOd.profile.2.radius=20.0
feature.turnOd.profile.3.z=40.0
feature.turnOd.profile.3.radius=20.0
feature.turnOd.profile.4.z=40.0
feature.turnOd.profile.4.radius=15.0
feature.turnOd.profile.5.z=80.0
feature.turnOd.profile.5.radius=15.0
```

### 15.2 動作フロー

1. ケースファイルを読み込み DTO に展開
2. `L1_CreateStock` を実行
3. `L1_ApplyFeature` を実行
4. `L1_ExportShape` を Result/Delta それぞれに対して STEP/STL 実行
5. 不要 shapeId を `L1_DeleteShape` で解放

### 15.3 受入条件（サンプル）

- ケースファイル変更のみで寸法・穴位置・出力先を切替可能
- サンプル実行で Result/Delta の STEP/STL が生成される

---
