# L1Geometry MCP API 実装仕様（Job JSON生成）

## 1. 目的
本仕様は、`JobJsonModel` を MCP から安全かつ段階的に生成・検証・保存するための API を定義する。

- フィーチャ追加 API は `job.addFeature` に統合する
- MCP ツールは stateless（都度 `job` を受け取り、更新済み `job` を返す）
- C# 側の既存モデル（`JsonConverter.cs`）と整合する

---

## 2. 対象モデル（C#）
対象は以下の JSON 構造。

- `stock`
- `features[]`
- `output`

`features[].type` は discriminator として扱う。

- `DRILL`（`drill` 必須）
- `POCKET_RECT`（`pocketRect` 必須）
- `TURN_OD`（`turnOd` 必須）
- `TURN_ID`（`turnId` 必須）

---

## 3. API 設計方針

### 3.1 stateless 方針
更新系ツールは以下を共通で満たす。

- 入力: `job` + 更新対象
- 出力: `ok`, `job`, `errors[]`

セッションIDを持たないため、MCPサーバーの状態管理が不要。

### 3.2 エラー方針

- バリデーションは例外化せず `errors[]` で返す
- `errors[]` 要素:
  - `code`（機械判定用）
  - `path`（JSONPath風、例: `features[0].turnOd.profile`）
  - `message`（人間向け）

### 3.3 最小実装順
1. `job.create`
2. `job.setStock`
3. `job.addFeature`（統合）
4. `job.setOutput`
5. `job.validate`
6. `job.toJson`
7. `job.saveJson`
8. `job.fromJson`
9. `job.loadJson`

---

## 4. ツール一覧（提案）

### 4.1 `job.create`
空の `Job` を生成する（必要なら default 上書き可）。

### 4.2 `job.setStock`
`stock` を置換設定する。

### 4.3 `job.addFeature`
`features` に1件追加（任意で index 指定挿入）。

### 4.4 `job.setOutput`
`output` を置換設定する。

### 4.5 `job.validate`
業務ルール検証を実行し `errors[]` を返す。

### 4.6 `job.toJson`
`job` を JSON 文字列へ変換（pretty 指定対応）。

### 4.7 `job.saveJson`
JSON ファイルへ保存（ディレクトリ自動作成対応）。

### 4.8 `job.fromJson`
JSON 文字列から `job` を復元。

### 4.9 `job.loadJson`
JSON ファイルから `job` を復元。

---

## 5. 共通レスポンス仕様

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "ok": { "type": "boolean" },
    "job": { "$ref": "#/definitions/Job" },
    "json": { "type": "string" },
    "errors": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "code": { "type": "string" },
          "path": { "type": "string" },
          "message": { "type": "string" }
        },
        "required": ["code", "path", "message"]
      }
    }
  },
  "required": ["ok"]
}
```

---

## 6. JSON Schema 定義案（共通モデル）

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "L1Geometry Job Schema",
  "type": "object",
  "definitions": {
    "Axis": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "origin": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "dir": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "xdir": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 }
      },
      "required": ["origin", "dir", "xdir"]
    },
    "Stock": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "type": { "type": "string", "enum": ["BOX", "CYLINDER"] },
        "p1": { "type": "number" },
        "p2": { "type": "number" },
        "p3": { "type": "number" },
        "axis": { "$ref": "#/definitions/Axis" }
      },
      "required": ["type", "p1", "p2", "p3", "axis"]
    },
    "TurnProfilePoint": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "z": { "type": "number" },
        "radius": { "type": "number", "exclusiveMinimum": 0 }
      },
      "required": ["z", "radius"]
    },
    "DrillPayload": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "radius": { "type": "number", "exclusiveMinimum": 0 },
        "depth": { "type": "number", "exclusiveMinimum": 0 },
        "axis": { "$ref": "#/definitions/Axis" }
      },
      "required": ["radius", "depth", "axis"]
    },
    "PocketRectPayload": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "width": { "type": "number", "exclusiveMinimum": 0 },
        "height": { "type": "number", "exclusiveMinimum": 0 },
        "depth": { "type": "number", "exclusiveMinimum": 0 },
        "axis": { "$ref": "#/definitions/Axis" }
      },
      "required": ["width", "height", "depth", "axis"]
    },
    "TurnPayload": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "profile": {
          "type": "array",
          "items": { "$ref": "#/definitions/TurnProfilePoint" },
          "minItems": 2,
          "maxItems": 64
        },
        "axis": { "$ref": "#/definitions/Axis" }
      },
      "required": ["profile", "axis"]
    },
    "Feature": {
      "oneOf": [
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "type": { "const": "DRILL" },
            "drill": { "$ref": "#/definitions/DrillPayload" }
          },
          "required": ["type", "drill"]
        },
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "type": { "const": "POCKET_RECT" },
            "pocketRect": { "$ref": "#/definitions/PocketRectPayload" }
          },
          "required": ["type", "pocketRect"]
        },
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "type": { "const": "TURN_OD" },
            "turnOd": { "$ref": "#/definitions/TurnPayload" }
          },
          "required": ["type", "turnOd"]
        },
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "type": { "const": "TURN_ID" },
            "turnId": { "$ref": "#/definitions/TurnPayload" }
          },
          "required": ["type", "turnId"]
        }
      ]
    },
    "Output": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "linearDeflection": { "type": "number", "exclusiveMinimum": 0 },
        "angularDeflection": { "type": "number", "exclusiveMinimum": 0 },
        "parallel": { "type": "integer", "minimum": 1 },
        "dir": { "type": "string", "minLength": 1 },
        "stepFile": { "type": "string", "minLength": 1 },
        "stlFile": { "type": "string", "minLength": 1 },
        "deltaStepFile": { "type": "string", "minLength": 1 },
        "deltaStlFile": { "type": "string", "minLength": 1 }
      },
      "required": [
        "linearDeflection",
        "angularDeflection",
        "parallel",
        "dir",
        "stepFile",
        "stlFile",
        "deltaStepFile",
        "deltaStlFile"
      ]
    },
    "Job": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "stock": { "$ref": "#/definitions/Stock" },
        "features": { "type": "array", "items": { "$ref": "#/definitions/Feature" } },
        "output": { "$ref": "#/definitions/Output" }
      },
      "required": ["stock", "features", "output"]
    }
  }
}
```

---

## 7. MCP Tool 定義案（inputSchema）

```json
[
  {
    "name": "job.create",
    "description": "空のjobを生成する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "defaults": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "stock": { "$ref": "#/definitions/Stock" },
            "output": { "$ref": "#/definitions/Output" }
          }
        }
      }
    }
  },
  {
    "name": "job.setStock",
    "description": "stockを設定する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" },
        "stock": { "$ref": "#/definitions/Stock" }
      },
      "required": ["job", "stock"]
    }
  },
  {
    "name": "job.addFeature",
    "description": "featuresへ1件追加する（typeで判別）",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" },
        "feature": { "$ref": "#/definitions/Feature" },
        "index": { "type": "integer", "minimum": 0 }
      },
      "required": ["job", "feature"]
    }
  },
  {
    "name": "job.setOutput",
    "description": "outputを設定する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" },
        "output": { "$ref": "#/definitions/Output" }
      },
      "required": ["job", "output"]
    }
  },
  {
    "name": "job.validate",
    "description": "jobを検証しerrors[]を返す",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" }
      },
      "required": ["job"]
    }
  },
  {
    "name": "job.toJson",
    "description": "jobをJSON文字列へ変換する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" },
        "pretty": { "type": "boolean", "default": true }
      },
      "required": ["job"]
    }
  },
  {
    "name": "job.saveJson",
    "description": "jobをJSONファイルへ保存する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "job": { "$ref": "#/definitions/Job" },
        "path": { "type": "string", "minLength": 1 },
        "pretty": { "type": "boolean", "default": true },
        "ensureDirectory": { "type": "boolean", "default": true }
      },
      "required": ["job", "path"]
    }
  },
  {
    "name": "job.fromJson",
    "description": "JSON文字列からjobを生成する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "json": { "type": "string", "minLength": 1 }
      },
      "required": ["json"]
    }
  },
  {
    "name": "job.loadJson",
    "description": "JSONファイルからjobを生成する",
    "inputSchema": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "path": { "type": "string", "minLength": 1 }
      },
      "required": ["path"]
    }
  }
]
```

---

## 8. 業務ルール（`job.validate`）

`inputSchema` だけでは不足するため、以下は実装で検証する。

1. `features.length >= 1`
2. `axis.origin/dir/xdir` は長さ3（Schemaでも担保、二重チェック）
3. `TURN_*` の `profile.length` は 2以上
4. `TURN_*` の `profile.length` は `L1GeometryKernelNative.TurnOdProfileMax` 以下
5. `stock.type` と payload の対応不整合がない
6. `output` のファイル名/ディレクトリ文字列が空でない

---

## 9. 実装メモ（C#）

- 既存 `JsonJobConverter` を流用し、MCP層は薄く保つ
- `job.addFeature` は `feature.type` で `FeatureJsonModel` へマッピング
- `type` 文字列は受領時に `ToUpperInvariant()` で正規化
- `job.toJson` は `System.Text.Json` の `WriteIndented` を切り替え
- `job.saveJson` は `ensureDirectory=true` のとき `Directory.CreateDirectory` を実行

---

## 10. 今後の拡張候補

- `job.removeFeature`（index削除）
- `job.updateFeature`（index置換）
- `job.normalize`（大文字化・既定値補完）
- `job.getSchema`（クライアント向けスキーマ提供）
