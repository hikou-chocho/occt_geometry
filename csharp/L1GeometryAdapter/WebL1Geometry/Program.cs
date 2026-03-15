using L1GeometryAdapter;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var outputRoot = Path.Combine(app.Environment.ContentRootPath, "output");
Directory.CreateDirectory(outputRoot);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapJobApi();

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

		int currentId = stockId;
		OperationResult finalResult = default;
		foreach (var feature in job.Features)
		{
			finalResult = ApplyFeature(kernel, currentId, feature);
			currentId = finalResult.ResultShapeId;
		}

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

app.MapPost("/pipeline/reference-step", async (HttpRequest request) =>
{
	if (!request.HasFormContentType)
		return Results.BadRequest(new { error = "multipart/form-data is required." });

	try
	{
		var form = await request.ReadFormAsync();
		var file = form.Files["file"] ?? form.Files.FirstOrDefault();
		if (file is null || file.Length <= 0)
			return Results.BadRequest(new { error = "STEP file is required." });

		const long maxUploadBytes = 50L * 1024L * 1024L;
		if (file.Length > maxUploadBytes)
			return Results.BadRequest(new { error = "STEP file is too large. Max 50MB." });

		var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant();
		if (ext is not ".step" and not ".stp")
			return Results.BadRequest(new { error = "Only .step/.stp files are supported." });

		var refId = $"ref-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
		var refDir = Path.Combine(outputRoot, "reference", refId);
		Directory.CreateDirectory(refDir);

		var stepFile = SanitizeFileName(file.FileName, "reference.step");
		var stlFile = "reference.stl";
		var stepPath = Path.Combine(refDir, stepFile);
		var stlPath = Path.Combine(refDir, stlFile);

		await using (var fs = System.IO.File.Create(stepPath))
		{
			await file.CopyToAsync(fs);
		}

		using var kernel = new L1Kernel();
		var shapeId = kernel.ImportStep(stepPath);

		var stlOpt = new OutputOptions
		{
			Format = OutputFormat.Stl,
			LinearDeflection = 0.1,
			AngularDeflection = 0.5,
			Parallel = 1,
		};

		kernel.ExportShape(shapeId, stlOpt, stlPath);

		try
		{
			System.IO.File.Delete(stepPath);
		}
		catch
		{
		}

		CleanupOldReferenceDirectories(Path.Combine(outputRoot, "reference"), TimeSpan.FromMinutes(10));

		return Results.Ok(new ReferenceStepResponse
		{
			ReferenceId = refId,
			ReferenceStlUrl = $"/output/reference/{refId}/{stlFile}",
		});
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem(title: "Reference STEP import failed", detail: ex.Message, statusCode: 500);
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

static OperationResult ApplyFeature(L1Kernel kernel, int stockId, FeatureJsonModel feature)
{
	var type = (feature.Type ?? string.Empty).ToUpperInvariant();
	return type switch
	{
		"MILL_HOLE"    => kernel.ApplyMillHole(stockId, feature.MillHole!.ToKernel()),
		"POCKET_RECT"  => kernel.ApplyPocketRect(stockId, feature.PocketRect!.ToKernel()),
		"TURN_OD"      => ApplyTurn(kernel, stockId, feature.TurnOd!,
		                            (id, ax, segs, c) => kernel.ApplyTurnOd(id, ax, segs, c)),
		"TURN_ID"      => ApplyTurn(kernel, stockId, feature.TurnId!,
		                            (id, ax, segs, c) => kernel.ApplyTurnId(id, ax, segs, c)),
		"MILL_CONTOUR" => ApplyMillContour(kernel, stockId, feature.MillContour!),
		_ => throw new InvalidOperationException($"Unsupported feature.type: {feature.Type}"),
	};
}

static OperationResult ApplyTurn(L1Kernel kernel, int stockId, TurnJsonModel turn,
	Func<int, AxisDto, Path2DSegmentDto[], bool, OperationResult> applyFn)
{
	if (turn.Profile is null)
		throw new InvalidOperationException("turn profile is required.");
	return applyFn(stockId, turn.Axis.ToKernel(), turn.Profile.ToKernelSegments(), turn.Profile.Closed);
}

static OperationResult ApplyMillContour(L1Kernel kernel, int stockId, MillContourJsonModel mc)
{
	if (mc.Profile is null)
		throw new InvalidOperationException("millContour profile is required.");
	return kernel.ApplyMillContour(stockId, mc.Axis.ToKernel(),
	                               mc.Profile.ToKernelSegments(), mc.Profile.Closed, mc.Depth);
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

static void CleanupOldReferenceDirectories(string referenceRoot, TimeSpan ttl)
{
	try
	{
		if (!Directory.Exists(referenceRoot))
			return;

		var now = DateTime.UtcNow;
		foreach (var dir in Directory.EnumerateDirectories(referenceRoot))
		{
			var lastWriteUtc = Directory.GetLastWriteTimeUtc(dir);
			if ((now - lastWriteUtc) > ttl)
				TryDeleteDirectory(dir);
		}
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

sealed class ReferenceStepResponse
{
	public string ReferenceId { get; set; } = string.Empty;
	public string ReferenceStlUrl { get; set; } = string.Empty;
}
