using System.Text.Json;
using System.Text.Json.Serialization;
using L1GeometryAdapter;

// ---------------------------------------------------------------
// Response model
// ---------------------------------------------------------------

internal sealed class JobApiResponse
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("job")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public JobJsonModel? Job { get; init; }

	[JsonPropertyName("json")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Json { get; init; }

	[JsonPropertyName("errors")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<ValidationError>? Errors { get; init; }
}

// ---------------------------------------------------------------
// Request models
// ---------------------------------------------------------------

internal sealed class CreateRequest
{
	[JsonPropertyName("defaults")]
	public JobDefaults? Defaults { get; init; }
}

internal sealed class JobDefaults
{
	[JsonPropertyName("stock")]
	public StockJsonModel? Stock { get; init; }

	[JsonPropertyName("output")]
	public OutputJsonModel? Output { get; init; }

	[JsonPropertyName("sessionId")]
	public string? SessionId { get; init; }
}

internal sealed class SetStockRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();

	[JsonPropertyName("stock")]
	public StockJsonModel Stock { get; init; } = new();
}

internal sealed class AddFeatureRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();

	[JsonPropertyName("feature")]
	public FeatureJsonModel Feature { get; init; } = new();

	[JsonPropertyName("index")]
	public int? Index { get; init; }
}

internal sealed class SetOutputRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();

	[JsonPropertyName("output")]
	public OutputJsonModel Output { get; init; } = new();
}

internal sealed class JobOnlyRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();
}

internal sealed class ToJsonRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();

	[JsonPropertyName("pretty")]
	public bool Pretty { get; init; } = true;
}

internal sealed class SaveJsonRequest
{
	[JsonPropertyName("job")]
	public JobJsonModel Job { get; init; } = new();

	[JsonPropertyName("path")]
	public string Path { get; init; } = string.Empty;

	[JsonPropertyName("pretty")]
	public bool Pretty { get; init; } = true;

	[JsonPropertyName("ensureDirectory")]
	public bool EnsureDirectory { get; init; } = true;
}

internal sealed class FromJsonRequest
{
	[JsonPropertyName("json")]
	public string Json { get; init; } = string.Empty;
}

internal sealed class LoadJsonRequest
{
	[JsonPropertyName("path")]
	public string Path { get; init; } = string.Empty;
}

// ---------------------------------------------------------------
// Route registration
// ---------------------------------------------------------------

internal static class JobApiExtensions
{
	private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };
	private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

	public static WebApplication MapJobApi(this WebApplication app)
	{
		app.MapPost("/job/create", (CreateRequest? request) =>
		{
			var job = JobJsonDefaults.CreateEmptyJob(new JobDefaultsOptions
			{
				Stock = request?.Defaults?.Stock,
				Output = request?.Defaults?.Output,
				SessionId = request?.Defaults?.SessionId,
			});

			return Results.Ok(new JobApiResponse { Ok = true, Job = job });
		});

		app.MapPost("/job/setStock", (SetStockRequest request) =>
		{
			var job = request.Job;
			job.Stock = request.Stock;
			job.Stock.Type = (job.Stock.Type ?? string.Empty).Trim().ToUpperInvariant();

			return Results.Ok(new JobApiResponse { Ok = true, Job = job });
		});

		app.MapPost("/job/addFeature", (AddFeatureRequest request) =>
		{
			var job = request.Job;
			var feature = request.Feature;
			feature.Type = (feature.Type ?? string.Empty).Trim().ToUpperInvariant();

			var features = new List<FeatureJsonModel>(job.Features);

			// [D3] index < 0 or null or >= count → append
			if (request.Index is null || request.Index.Value < 0 || request.Index.Value >= features.Count)
				features.Add(feature);
			else
				features.Insert(request.Index.Value, feature);

			job.Features = features;
			return Results.Ok(new JobApiResponse { Ok = true, Job = job });
		});

		app.MapPost("/job/setOutput", (SetOutputRequest request) =>
		{
			var job = request.Job;
			job.Output = request.Output;

			return Results.Ok(new JobApiResponse { Ok = true, Job = job });
		});

		app.MapPost("/job/validate", (JobOnlyRequest request) =>
		{
			var errors = JobValidator.Validate(request.Job);
			return Results.Ok(new JobApiResponse
			{
				Ok = errors.Count == 0,
				Job = request.Job,
				Errors = errors.Count > 0 ? errors : null,
			});
		});

		app.MapPost("/job/toJson", (ToJsonRequest request) =>
		{
			var options = request.Pretty ? PrettyOptions : CompactOptions;
			var json = JsonSerializer.Serialize(request.Job, options);

			return Results.Ok(new JobApiResponse { Ok = true, Job = request.Job, Json = json });
		});

		app.MapPost("/job/saveJson", (SaveJsonRequest request) =>
		{
			if (string.IsNullOrWhiteSpace(request.Path))
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "EMPTY_PATH", Path = "path", Message = "path must not be empty." }
					}
				});
			}

			try
			{
				var options = request.Pretty ? PrettyOptions : CompactOptions;
				var json = JsonSerializer.Serialize(request.Job, options);

				if (request.EnsureDirectory)
				{
					var dir = Path.GetDirectoryName(request.Path);
					if (!string.IsNullOrEmpty(dir))
						Directory.CreateDirectory(dir);
				}

				File.WriteAllText(request.Path, json);
				return Results.Ok(new JobApiResponse { Ok = true, Job = request.Job });
			}
			catch (IOException ex)
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "IO_ERROR", Path = "path", Message = ex.Message }
					}
				});
			}
		});

		app.MapPost("/job/fromJson", (FromJsonRequest request) =>
		{
			if (string.IsNullOrWhiteSpace(request.Json))
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "EMPTY_JSON", Path = "json", Message = "json must not be empty." }
					}
				});
			}

			try
			{
				var job = JsonJobConverter.Deserialize(request.Json);
				return Results.Ok(new JobApiResponse { Ok = true, Job = job });
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException)
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "PARSE_ERROR", Path = "json", Message = ex.Message }
					}
				});
			}
		});

		app.MapPost("/job/loadJson", (LoadJsonRequest request) =>
		{
			if (string.IsNullOrWhiteSpace(request.Path))
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "EMPTY_PATH", Path = "path", Message = "path must not be empty." }
					}
				});
			}

			try
			{
				var job = JsonJobConverter.DeserializeFile(request.Path);
				return Results.Ok(new JobApiResponse { Ok = true, Job = job });
			}
			catch (FileNotFoundException ex)
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "FILE_NOT_FOUND", Path = "path", Message = ex.Message }
					}
				});
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException)
			{
				return Results.Ok(new JobApiResponse
				{
					Ok = false,
					Errors = new List<ValidationError>
					{
						new() { Code = "PARSE_ERROR", Path = "path", Message = ex.Message }
					}
				});
			}
		});

		return app;
	}
}
