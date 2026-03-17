namespace L1GeometryAdapter;

public static class JobJsonDefaults
{
	public static JobJsonModel CreateEmptyJob(JobDefaultsOptions? defaults = null)
	{
		defaults ??= new JobDefaultsOptions();
		var sessionId = string.IsNullOrWhiteSpace(defaults.SessionId)
			? CreateSessionId()
			: defaults.SessionId!.Trim();

		return new JobJsonModel
		{
			Stock = defaults.Stock ?? new StockJsonModel(),
			Features = new List<FeatureJsonModel>(),
			Output = defaults.Output ?? CreateOutput(sessionId),
			Meta = new MetaJsonModel { SessionId = sessionId },
		};
	}

	public static OutputJsonModel CreateOutput(string sessionId) => new()
	{
		LinearDeflection = 0.1,
		AngularDeflection = 0.5,
		Parallel = 1,
		Dir = "out",
		StepFile = $"{sessionId}_result.step",
		StlFile = $"{sessionId}_result.stl",
		DeltaStepFile = $"{sessionId}_delta.step",
		DeltaStlFile = $"{sessionId}_delta.stl",
		RemovalStepFile = $"{sessionId}_removal.step",
		RemovalStlFile = $"{sessionId}_removal.stl",
	};

	public static string CreateSessionId()
	{
		return $"sess-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0, 0x10000):x4}";
	}
}

public sealed class JobDefaultsOptions
{
	public StockJsonModel? Stock { get; init; }

	public OutputJsonModel? Output { get; init; }

	public string? SessionId { get; init; }
}
