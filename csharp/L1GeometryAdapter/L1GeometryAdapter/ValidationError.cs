using System.Text.Json.Serialization;

namespace L1GeometryAdapter;

public sealed class ValidationError
{
	[JsonPropertyName("code")]
	public string Code { get; init; } = string.Empty;

	[JsonPropertyName("path")]
	public string Path { get; init; } = string.Empty;

	[JsonPropertyName("message")]
	public string Message { get; init; } = string.Empty;
}
