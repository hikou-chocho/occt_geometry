using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using L1GeometryAdapter;

internal static class PreviewBridgeRoutes
{
	private static readonly ConcurrentDictionary<string, PreviewSessionState> Sessions = new();
	private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);

	public static WebApplication MapPreviewBridgeApi(this WebApplication app)
	{
		app.MapPost("/preview-api/session", (HttpRequest request, PreviewBridgeRequest body) =>
		{
			var sessionId = (body.Job.Meta?.SessionId ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(sessionId))
			{
				return Results.BadRequest(new PreviewBridgeResponse
				{
					Ok = false,
					Error = "meta.sessionId is required.",
				});
			}

			CleanupExpiredSessions();

			Sessions[sessionId] = new PreviewSessionState
			{
				Job = body.Job,
				UpdatedAtUtc = DateTime.UtcNow,
			};

			var baseUri = $"{request.Scheme}://{request.Host}";
			return Results.Ok(new PreviewBridgeResponse
			{
				Ok = true,
				SessionId = sessionId,
				StageCount = body.Job.Features.Count + 2,
				ViewUrl = $"{baseUri}/mcp-preview.html?sessionId={Uri.EscapeDataString(sessionId)}",
			});
		});

		app.MapGet("/preview-api/session/{sessionId}", (string sessionId) =>
		{
			CleanupExpiredSessions();

			if (!Sessions.TryGetValue(sessionId, out var session))
				return Results.NotFound(new PreviewSessionPayloadResponse { Ok = false, Error = "Preview session not found." });

			return Results.Ok(new PreviewSessionPayloadResponse
			{
				Ok = true,
				SessionId = sessionId,
				Job = session.Job,
				UpdatedAtUtc = session.UpdatedAtUtc,
			});
		});

		return app;
	}

	private static void CleanupExpiredSessions()
	{
		var threshold = DateTime.UtcNow - SessionTtl;
		foreach (var pair in Sessions)
		{
			if (pair.Value.UpdatedAtUtc < threshold)
				Sessions.TryRemove(pair.Key, out _);
		}
	}
}

internal sealed class PreviewSessionState
{
	public JobJsonModel Job { get; init; } = new();

	public DateTime UpdatedAtUtc { get; init; }
}

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
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Error { get; init; }
}

internal sealed class PreviewSessionPayloadResponse
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("sessionId")]
	public string SessionId { get; init; } = string.Empty;

	[JsonPropertyName("job")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public JobJsonModel? Job { get; init; }

	[JsonPropertyName("updatedAtUtc")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public DateTime? UpdatedAtUtc { get; init; }

	[JsonPropertyName("error")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Error { get; init; }
}
