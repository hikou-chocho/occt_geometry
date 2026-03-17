# MCP Web Preview Usage

## Overview

This repository now supports a minimal flow:

1. Build or edit `job.json` through MCP tools
2. Send the full job to the local Web preview bridge
3. Open a preview-only Web page and inspect stock, each feature stage, and the final model

In this flow, the authoritative `job.json` remains on the VSCode side.
The Web app is display-only.

## Components

- MCP server: `C:\Develop\occt_geometry\csharp\L1GeometryAdapter\McpJobBuilder`
- Web app: `C:\Develop\occt_geometry\csharp\L1GeometryAdapter\WebL1Geometry`
- Preview page: `/mcp-preview.html?sessionId=...`

## Job rules

- `job_create()` now creates:
  - `output`
  - `meta.sessionId`
- `meta.sessionId` is the canonical session identifier
- Web preview receives the full job, not diffs

Example job shape:

```json
{
  "stock": {},
  "features": [],
  "output": {
    "linearDeflection": 0.1,
    "angularDeflection": 0.5,
    "parallel": 1,
    "dir": "out"
  },
  "meta": {
    "sessionId": "sess-20260318-031500-1a2b"
  }
}
```

## Start the Web app

Run the Web app locally:

```powershell
cd C:\Develop\occt_geometry\csharp\L1GeometryAdapter\WebL1Geometry
dotnet run
```

Default local URL:

```text
http://localhost:5159
```

## MCP tool flow

Recommended MCP flow:

1. `job_create`
2. `job_set_stock`
3. `job_add_feature`
4. `job_validate`
5. `job_preview_web`

## Tool reference

### `job_create`

Creates a new job with:

- empty `features`
- generated `meta.sessionId`
- default `output` if not explicitly provided

You may optionally pass:

- `defaults.stock`
- `defaults.output`
- `defaults.sessionId`

### `job_preview_web`

Sends the full job to the local Web preview bridge.

Inputs:

- `job`
- optional `webBaseUrl`

If `webBaseUrl` is omitted, MCP uses:

1. `L1GEOMETRY_WEB_BASE_URL`
2. `http://localhost:5159`

Response shape:

```json
{
  "ok": true,
  "sessionId": "sess-20260318-031500-1a2b",
  "viewUrl": "http://localhost:5159/mcp-preview.html?sessionId=sess-20260318-031500-1a2b",
  "stageCount": 5
}
```

## How to preview

1. Start the Web app
2. Use MCP tools to build a job
3. Call `job_preview_web`
4. Open `viewUrl` in a browser
5. Select a stage from the left panel

The preview page shows:

- stock-only stage
- each feature stage with `model + delta`
- final output stage with final model

`removal` is intentionally not shown in this v1 flow.

## Notes

- The preview page is read-only
- The Web app stores preview sessions in memory
- If the Web app restarts, previously issued preview URLs stop working
- Hard-gate validation for `/pipeline/*` is not implemented yet
- Path2D kernel-safe validation is not implemented yet

## Troubleshooting

### `job_preview_web` cannot connect

Check that the Web app is running and reachable at:

```text
http://localhost:5159
```

Or set:

```powershell
$env:L1GEOMETRY_WEB_BASE_URL = "http://localhost:5159"
```

### `meta.sessionId is required`

Use `job_create()` as the starting point, or ensure the job includes:

```json
"meta": {
  "sessionId": "..."
}
```

### Preview page opens but no model appears

Possible causes:

- invalid feature payload
- kernel execution failure
- unsupported Path2D content

At this stage, preview uses the existing `/pipeline/preview` behavior.
