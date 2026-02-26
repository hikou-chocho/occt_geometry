using L1GeometryAdapter;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var outputRoot = Path.Combine(app.Environment.ContentRootPath, "output");
Directory.CreateDirectory(outputRoot);

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseStaticFiles(new StaticFileOptions
{
	FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(outputRoot),
	RequestPath = "/output",
	ContentTypeProvider = new FileExtensionContentTypeProvider(),
});

app.MapGet("/pipeline/final-stl/{runId}/{fileName}", (string runId, string fileName) =>
{
	var safeRunId = Path.GetFileName(runId);
	if (string.IsNullOrWhiteSpace(safeRunId))
		return Results.BadRequest(new { error = "Invalid runId." });

	var safeFileName = SanitizeFileName(fileName, "result.stl");
	var runDir = Path.Combine(outputRoot, safeRunId);
	var filePath = Path.Combine(runDir, safeFileName);

	if (!System.IO.File.Exists(filePath))
		return Results.NotFound(new { error = "STL file not found." });

	try
	{
		var bytes = System.IO.File.ReadAllBytes(filePath);
		TryDeleteDirectory(runDir);
		return Results.File(bytes, "model/stl", safeFileName);
	}
	catch (Exception ex)
	{
		return Results.Problem(title: "STL fetch failed", detail: ex.Message, statusCode: 500);
	}
});

app.MapPost("/pipeline/run", (JobJsonModel request) =>
{
	try
	{
		var job = request.ToKernel();

		var runId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
		var runDir = Path.Combine(outputRoot, runId);
		Directory.CreateDirectory(runDir);

		var stepFile = SanitizeFileName(job.StepFile, "result.step");
		var stlFile = SanitizeFileName(job.StlFile, "result.stl");
		var deltaStepFile = SanitizeFileName(job.DeltaStepFile, "delta.step");
		var deltaStlFile = SanitizeFileName(job.DeltaStlFile, "delta.stl");

		var stepPath = Path.Combine(runDir, stepFile);
		var stlPath = Path.Combine(runDir, stlFile);
		var deltaStepPath = Path.Combine(runDir, deltaStepFile);
		var deltaStlPath = Path.Combine(runDir, deltaStlFile);

		using var kernel = new L1Kernel();

		var stock = job.Stock;
		var stockId = kernel.CreateStock(ref stock);
		var results = kernel.ApplyFeatures(stockId, job.Features);
		var finalResult = results[^1];

		var stepOpt = job.OutputOptions;
		stepOpt.Format = OutputFormat.Step;

		var stlOpt = job.OutputOptions;
		stlOpt.Format = OutputFormat.Stl;

		kernel.ExportShape(finalResult.ResultShapeId, stepOpt, stepPath);
		kernel.ExportShape(finalResult.ResultShapeId, stlOpt, stlPath);
		kernel.ExportShape(finalResult.DeltaShapeId, stepOpt, deltaStepPath);
		kernel.ExportShape(finalResult.DeltaShapeId, stlOpt, deltaStlPath);

		return Results.Ok(new PipelineRunResponse
		{
			RunId = runId,
			FinalStepUrl = $"/output/{runId}/{stepFile}",
			FinalStlUrl = $"/pipeline/final-stl/{runId}/{stlFile}",
			DeltaStepUrl = $"/output/{runId}/{deltaStepFile}",
			DeltaStlUrl = $"/output/{runId}/{deltaStlFile}",
		});
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem(title: "Pipeline execution failed", detail: ex.Message, statusCode: 500);
	}
});

app.Run();

static string SanitizeFileName(string? value, string fallback)
{
	var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value;
	var nameOnly = Path.GetFileName(candidate);
	if (string.IsNullOrWhiteSpace(nameOnly))
		return fallback;

	foreach (var invalid in Path.GetInvalidFileNameChars())
		nameOnly = nameOnly.Replace(invalid, '_');

	return nameOnly;
}

static void TryDeleteDirectory(string path)
{
	try
	{
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
	}
	catch
	{
	}
}

sealed class PipelineRunResponse
{
	public string RunId { get; set; } = string.Empty;
	public string FinalStepUrl { get; set; } = string.Empty;
	public string FinalStlUrl { get; set; } = string.Empty;
	public string DeltaStepUrl { get; set; } = string.Empty;
	public string DeltaStlUrl { get; set; } = string.Empty;
}
