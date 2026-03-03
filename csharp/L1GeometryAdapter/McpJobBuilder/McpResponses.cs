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
