using System.Text.Json.Serialization;
using L1GeometryAdapter;

namespace McpJobBuilder;

// job_create の defaults 引数
// [D6] 未知フィールドは tool 呼び出しエラー
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JobDefaults
{
	[JsonPropertyName("stock")]
	public StockJsonModel? Stock { get; init; }

	[JsonPropertyName("output")]
	public OutputJsonModel? Output { get; init; }

	[JsonPropertyName("sessionId")]
	public string? SessionId { get; init; }
}
