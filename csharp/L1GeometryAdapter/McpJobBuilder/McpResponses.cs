using System.Text.Json.Serialization;
using L1GeometryAdapter;

namespace McpJobBuilder;

// edit 系レスポンス: { "job": ... }
internal sealed record JobOnlyResponse(
	[property: JsonPropertyName("job")] JobJsonModel Job);

// validate レスポンス: { "ok": bool, "errors": [...] | null }
internal sealed record ValidateResponse(
	[property: JsonPropertyName("ok")] bool Ok,
	[property: JsonPropertyName("errors")] List<ValidationError>? Errors);

internal sealed class PreviewBridgeRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();
}

internal sealed class PreviewBridgeResponse
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("sessionId")]
	public string SessionId { get; init; } = string.Empty;

	[JsonPropertyName("viewUrl")]
	public string ViewUrl { get; init; } = string.Empty;

	[JsonPropertyName("stageCount")]
	public int StageCount { get; init; }

	[JsonPropertyName("error")]
	public string? Error { get; init; }
}
