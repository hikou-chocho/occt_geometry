# MCP Job Builder 仕様書 v1.1

## 1. 目的とスコープ

### 1.1 目的

MCP（Model Context Protocol）経由で **Job JSON（Stock / Features / Output）を組み立て、妥当性検証する**ための tool 群（4本）を定義する。

### 1.2 スコープ

**含む**

* Job JSON の生成（create）
* Job JSON の編集（set_stock / add_feature）
* Job JSON の妥当性検証（validate）
* 検証エラーの機械可読な返却（errors）

**含まない**

* OCCT 実行（形状生成・ブール演算・エクスポート等）
* ファイル I/O（save/load）
* サーバ側のジョブ永続化

---

## 2. 用語

* **Job**: OCCT 実行に投入可能な形状生成/加工定義 JSON。`stock`, `features[]`, `output` を持つ。
* **編集系 tool**: `job_create`, `job_set_stock`, `job_add_feature`
* **検証 tool**: `job_validate`

---

## 3. 基本原則（決め）

### [D1] 編集系は失敗しない（業務エラーを返さない）

* `job_create` / `job_set_stock` / `job_add_feature` は **業務的な失敗（ok=false, errors[]）を返さない**。
* これらは **Job JSON を返すだけ**であり、妥当性判定は行わない。

> 例外：入力 JSON がデシリアライズ不能な場合は tool 呼び出しエラー（[D2]）。

---

### [D2] 妥当性判定は validate に一本化（型チェック含む）

* `feature.type` の対応可否など、**あらゆる妥当性判定は `job_validate` のみ**が行う。
* ただし **各 tool の入力 JSON がデシリアライズ不能**な場合は **tool 呼び出しエラー**とする（業務エラーではない）。

---

### [D3] index < 0 の扱いを固定（append）

* `job_add_feature` の `index < 0` は **末尾追加（append）**として扱う。

---

### [D4] errors[].path は配列 index を表現できる形式に固定

* `errors[].path` は **ブラケット形式**に固定する。

  * 例: `features[1].type`, `features[0].placement.attach`, `stock.type`

---

### [D5] 編集系の正規化範囲を固定（typeのみ）

編集系 tool は入力 Job に対して以下のみを正規化して返す。

* 対象: `stock.type`, `feature.type`
* 規則: `Trim()` + `ToUpperInvariant()`
* それ以外の全フィールドは **変更しない**（補完・推測・並べ替え・暗黙変換などを行わない）

---

### [D6] 未知フィールド（additionalProperties）を禁止

各 tool の入力 JSON において、モデルに定義されていないフィールド（未知プロパティ）は許可しない。

* 未知フィールドを含む入力は **デシリアライズエラー**（tool 呼び出しエラー）とする。
* このエラーは `job_validate.errors` ではなく、MCP/JSON-RPC 等の **呼び出しエラー**として返す。

---

### [D7] validate の errors 順序を固定（決定性保証）

`job_validate` が返す `errors[]` は、同一入力 Job に対して順序が変わらないことを保証する。

* ソートキー（固定）: `path` 昇順 → `code` 昇順（必要なら `message` 昇順を追加可）
* 同一入力に対して **errors の並び順は常に同一**

---

## 4. 推奨事項（決めではない）

### [R1] errors.code 互換性（改名禁止・追加のみ）

* 既存 `errors[].code` は改名しない（追加のみ）
* 非推奨化する場合も段階移行（旧code維持、または併記）を推奨

### [R2] errors.path の厳密表記

* ルート表記は混在させない（推奨: `job.` を付けない）
* “存在しない要素” を指す必要がある場合は実在する親パスに寄せる（推奨）

---

## 5. データモデル（概略）

Job の最低構造（概略）は以下。

```json
{
  "stock": { /* StockJsonModel */ },
  "features": [ /* FeatureJsonModel */ ],
  "output": { /* OutputJsonModel */ }
}
```

> StockJsonModel / FeatureJsonModel / OutputJsonModel の内部フィールド定義（スキーマ詳細）は別紙（ドメインモデル仕様）に従う。本仕様書は tool 振る舞いとエラーモデルを規定する。

---

## 6. 共通レスポンス

### 6.1 編集系 tool レスポンス（常に成功）

編集系 tool は `job` を返す。

```json
{
  "job": { /* JobJsonModel */ }
}
```

---

### 6.2 validate レスポンス

`job_validate` は妥当性判定結果を返す。

成功例：

```json
{
  "ok": true,
  "errors": null
}
```

失敗例：

```json
{
  "ok": false,
  "errors": [
    {
      "code": "INVALID_FEATURE_TYPE",
      "path": "features[1].type",
      "message": "Unsupported feature.type: TURN_XY"
    }
  ]
}
```

---

### 6.3 tool 呼び出しエラー（デシリアライズ不能・未知フィールド等）

* これは業務エラーではなく、tool 呼び出し自体が成立しないエラー。
* 返却形式は MCP 実装（例：JSON-RPC error）に従う。
* **入力 JSON 全文をエラーに含めない**ことを推奨（漏洩防止）。

---

## 7. tool 定義

## 7.1 job_create

### 目的

新規 Job を生成する。

### 入力

* `defaults`（省略可）

  * `defaults.stock`（省略可）
  * `defaults.output`（省略可）

### 振る舞い

* `features` は必ず空配列で初期化する。
* `defaults.stock` があれば stock 初期値として採用する。なければ空 stock を生成する。
* `defaults.output` があれば output 初期値として採用する。なければ空 output を生成する。

### 出力

* `job`

---

## 7.2 job_set_stock

### 目的

既存 job の stock を差し替える。

### 入力

* `job`（必須）
* `stock`（必須）

### 振る舞い

* `job.stock = stock`
* [D5] に従い `stock.type` を `Trim + Uppercase` 正規化する。
* 妥当性検証は行わない（[D2]）。

### 出力

* 更新後 `job`

---

## 7.3 job_add_feature

### 目的

既存 job に feature を追加または挿入する。

### 入力

* `job`（必須）
* `feature`（必須）
* `index`（省略可, int）

### 振る舞い

1. `feature.type` を [D5] に従い `Trim + Uppercase` 正規化する。
2. `index` の扱い（固定）

   * `index == null` → append
   * `index < 0` → append（[D3]）
   * `index >= features.Count` → append
   * `0 <= index < features.Count` → insert
3. 妥当性検証（feature.type の対応可否等）は行わない（[D2]）。

### 出力

* 更新後 `job`

---

## 7.4 job_validate

### 目的

job の妥当性判定を行う（唯一の判定機構）。

### 入力

* `job`（必須）

### 振る舞い

* job 全体（stock / features / output）の整合性を検証する。
* feature.type の対応可否を含む（[D2]）。
* `errors[]` は [D7] に従い決定的順序で返す。

### errors 仕様

`errors[]` 要素は以下を必須とする。

* `code` : 安定した識別子（例：`INVALID_FEATURE_TYPE`, `MISSING_FIELD`, `OUT_OF_RANGE`）
* `path` : [D4] ブラケット形式（配列 index を含む）
* `message` : 人間可読な説明

---

## 8. 運用フロー（期待される手順）

1. `job_create` で空 job を作成
2. `job_set_stock` で stock を設定
3. `job_add_feature` を必要回数呼び、features を積む
4. `job_validate` を呼び `ok=true` を確認
5. `ok=false` の場合は `errors[].path` を参照して job を修正し再 validate

---

## 9. 付録A：errors.path 表記ルール（確定）

* ルート表記は付けない（例：`job.` を付与しない）
* フィールドは `.` 区切り
* 配列要素は `[N]`
* 例

  * `features[0].type`
  * `features[3].placement.attach.face`
  * `stock.type`
  * `output.format`

---
