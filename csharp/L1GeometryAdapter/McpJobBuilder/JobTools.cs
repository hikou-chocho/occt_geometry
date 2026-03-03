using System.ComponentModel;
using System.Text.Json;
using L1GeometryAdapter;
using ModelContextProtocol.Server;

namespace McpJobBuilder;

[McpServerToolType]
internal static class JobTools
{
	private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

	// ---------------------------------------------------------------
	// job_create
	// ---------------------------------------------------------------

	[McpServerTool(Name = "job_create"), Description(
		"Creates a new empty Job. features is always initialized as an empty array. " +
		"Optional defaults can supply initial stock and output values.")]
	internal static string JobCreate(
		[Description("Optional initial values for stock and output.")] JobDefaults? defaults = null)
	{
		var job = new JobJsonModel
		{
			Stock = defaults?.Stock ?? new StockJsonModel(),
			Features = new List<FeatureJsonModel>(),
			Output = defaults?.Output ?? new OutputJsonModel(),
		};

		return JsonSerializer.Serialize(new JobOnlyResponse(job), CompactOptions);
	}

	// ---------------------------------------------------------------
	// job_set_stock
	// ---------------------------------------------------------------

	[McpServerTool(Name = "job_set_stock"), Description(
		"Replaces the stock of an existing job. " +
		"stock.type is normalized (Trim + UpperCase). No validation is performed [D2].")]
	internal static string JobSetStock(
		[Description("The current job.")] JobJsonModel job,
		[Description("The new stock to set.")] StockJsonModel stock)
	{
		// [D5] stock.type: Trim + ToUpperInvariant
		stock.Type = (stock.Type ?? string.Empty).Trim().ToUpperInvariant();
		job.Stock = stock;

		return JsonSerializer.Serialize(new JobOnlyResponse(job), CompactOptions);
	}

	// ---------------------------------------------------------------
	// job_add_feature
	// ---------------------------------------------------------------

	[McpServerTool(Name = "job_add_feature"), Description(
		"Adds or inserts a feature into an existing job. " +
		"feature.type is normalized (Trim + UpperCase). No validation is performed [D2]. " +
		"index rules: null or < 0 or >= features.count → append; otherwise insert at index [D3].")]
	internal static string JobAddFeature(
		[Description("The current job.")] JobJsonModel job,
		[Description("The feature to add.")] FeatureJsonModel feature,
		[Description("Insertion index (optional). Negative or omitted means append.")] int? index = null)
	{
		// [D5] feature.type: Trim + ToUpperInvariant
		feature.Type = (feature.Type ?? string.Empty).Trim().ToUpperInvariant();

		var features = new List<FeatureJsonModel>(job.Features);

		// [D3] index < 0, null, or >= count → append
		if (index is null || index.Value < 0 || index.Value >= features.Count)
			features.Add(feature);
		else
			features.Insert(index.Value, feature);

		job.Features = features;

		return JsonSerializer.Serialize(new JobOnlyResponse(job), CompactOptions);
	}

	// ---------------------------------------------------------------
	// job_validate
	// ---------------------------------------------------------------

	[McpServerTool(Name = "job_validate"), Description(
		"Validates a job and returns a list of errors. " +
		"This is the sole validation mechanism [D2]. " +
		"Errors are sorted by path then code [D7].")]
	internal static string JobValidate(
		[Description("The job to validate.")] JobJsonModel job)
	{
		var errors = JobValidator.Validate(job);

		var response = errors.Count == 0
			? new ValidateResponse(Ok: true, Errors: null)
			: new ValidateResponse(Ok: false, Errors: errors);

		return JsonSerializer.Serialize(response, CompactOptions);
	}
}
