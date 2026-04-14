using L1GeometryAdapter;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService();
var app = builder.Build();

var outputRoot = Path.Combine(app.Environment.ContentRootPath, "output");
var previewRoot = Path.Combine(outputRoot, "preview");
var previewStepRoot = Path.Combine(outputRoot, "preview-step");
Directory.CreateDirectory(outputRoot);
Directory.CreateDirectory(previewRoot);
Directory.CreateDirectory(previewStepRoot);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapJobApi();
app.MapPreviewBridgeApi();

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
		var hasRemovalStep = !string.IsNullOrWhiteSpace(job.RemovalStepFile);
		var hasRemovalStl = !string.IsNullOrWhiteSpace(job.RemovalStlFile);
		var removalStepFile = hasRemovalStep ? SanitizeFileName(job.RemovalStepFile, "removal.step") : null;
		var removalStlFile = hasRemovalStl ? SanitizeFileName(job.RemovalStlFile, "removal.stl") : null;

		var stepPath = Path.Combine(runDir, stepFile);
		var stlPath = Path.Combine(runDir, stlFile);
		var deltaStepPath = Path.Combine(runDir, deltaStepFile);
		var deltaStlPath = Path.Combine(runDir, deltaStlFile);
		var removalStepPath = removalStepFile is null ? null : Path.Combine(runDir, removalStepFile);
		var removalStlPath = removalStlFile is null ? null : Path.Combine(runDir, removalStlFile);

		using var kernel = new L1Kernel();
		var replay = ReplayJob(kernel, request);
		if (!replay.HasFeatureResult)
			throw new InvalidOperationException("features must contain at least one item.");

		var stepOpt = CreateOutputOptions(request.Output, OutputFormat.Step);
		var stlOpt = CreateOutputOptions(request.Output, OutputFormat.Stl);

		kernel.ExportShape(replay.FinalShapeId, stepOpt, stepPath);
		kernel.ExportShape(replay.FinalShapeId, stlOpt, stlPath);
		kernel.ExportShape(replay.LastResult.DeltaShapeId, stepOpt, deltaStepPath);
		kernel.ExportShape(replay.LastResult.DeltaShapeId, stlOpt, deltaStlPath);
		if (removalStepPath is not null)
			kernel.ExportShape(replay.LastResult.RemovalShapeId, stepOpt, removalStepPath);
		if (removalStlPath is not null)
			kernel.ExportShape(replay.LastResult.RemovalShapeId, stlOpt, removalStlPath);

		return Results.Ok(new PipelineRunResponse
		{
			RunId = runId,
			FinalStepUrl = $"/output/{runId}/{stepFile}",
			FinalStlUrl = $"/pipeline/final-stl/{runId}/{stlFile}",
			DeltaStepUrl = $"/output/{runId}/{deltaStepFile}",
			DeltaStlUrl = $"/output/{runId}/{deltaStlFile}",
			RemovalStepUrl = removalStepFile is null ? null : $"/output/{runId}/{removalStepFile}",
			RemovalStlUrl = removalStlFile is null ? null : $"/output/{runId}/{removalStlFile}",
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

app.MapPost("/preview-api/session/{sessionId}/final-step", (string sessionId) =>
{
	if (string.IsNullOrWhiteSpace(sessionId))
		return Results.BadRequest(new { error = "sessionId is required." });

	if (!PreviewBridgeRoutes.TryGetSession(sessionId, out var session))
		return Results.NotFound(new { error = "Preview session not found." });

	try
	{
		CleanupOldDirectories(previewStepRoot, TimeSpan.FromMinutes(10));

		var stepId = $"preview-step-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
		var stepDir = Path.Combine(previewStepRoot, stepId);
		Directory.CreateDirectory(stepDir);

		try
		{
			var stepFile = SanitizeFileName(session.Job.Output.StepFile, "result.step");
			var stepPath = Path.Combine(stepDir, stepFile);

			using var kernel = new L1Kernel();
			var replay = ReplayJob(kernel, session.Job);
			var stepOpt = CreateOutputOptions(session.Job.Output, OutputFormat.Step);
			kernel.ExportShape(replay.FinalShapeId, stepOpt, stepPath);

			var bytes = System.IO.File.ReadAllBytes(stepPath);
			return Results.File(bytes, "application/step", stepFile);
		}
		finally
		{
			TryDeleteDirectory(stepDir);
		}
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem(title: "Final STEP generation failed", detail: ex.Message, statusCode: 500);
	}
});

app.MapPost("/pipeline/preview", (PreviewStageRequest request) =>
{
	try
	{
		var previewId = $"preview-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
		var previewDir = Path.Combine(previewRoot, previewId);
		Directory.CreateDirectory(previewDir);

		var modelFile = "model.stl";
		var deltaFile = "delta.stl";
		var removalFile = "removal.stl";
		var modelPath = Path.Combine(previewDir, modelFile);
		var deltaPath = Path.Combine(previewDir, deltaFile);
		var removalPath = Path.Combine(previewDir, removalFile);

		using var kernel = new L1Kernel();
		var stlOpt = CreateOutputOptions(request.Job.Output, OutputFormat.Stl);
		var stockId = CreateStockShapeId(kernel, request.Job);

		var stageIndex = request.StageIndex;
		var featureCount = request.Job.Features.Count;

		if (stageIndex < 0 || featureCount == 0)
		{
			kernel.ExportShape(stockId, stlOpt, modelPath);
			CleanupOldDirectories(previewRoot, TimeSpan.FromMinutes(10));
			return Results.Ok(new PreviewStageResponse
			{
				StageIndex = stageIndex,
				ModelStlUrl = $"/output/preview/{previewId}/{modelFile}",
			});
		}

		var cappedStageIndex = Math.Min(stageIndex, featureCount - 1);
		var currentId = stockId;
		OperationResult lastResult = default;

		for (var i = 0; i <= cappedStageIndex; i++)
		{
			lastResult = ApplyFeature(kernel, currentId, request.Job.Features[i]);
			currentId = lastResult.ResultShapeId;
		}

		var resultShapeId = stageIndex >= featureCount ? currentId : lastResult.ResultShapeId;
		kernel.ExportShape(resultShapeId, stlOpt, modelPath);

		string? deltaUrl = null;
		string? removalUrl = null;
		if (stageIndex < featureCount)
		{
			kernel.ExportShape(lastResult.DeltaShapeId, stlOpt, deltaPath);
			kernel.ExportShape(lastResult.RemovalShapeId, stlOpt, removalPath);
			deltaUrl = $"/output/preview/{previewId}/{deltaFile}";
			removalUrl = $"/output/preview/{previewId}/{removalFile}";
		}

		CleanupOldDirectories(previewRoot, TimeSpan.FromMinutes(10));

		return Results.Ok(new PreviewStageResponse
		{
			StageIndex = stageIndex,
			ModelStlUrl = $"/output/preview/{previewId}/{modelFile}",
			DeltaStlUrl = deltaUrl,
			RemovalStlUrl = removalUrl,
		});
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem(title: "Preview generation failed", detail: ex.Message, statusCode: 500);
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

		CleanupOldDirectories(Path.Combine(outputRoot, "reference"), TimeSpan.FromMinutes(10));

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

static OutputOptions CreateOutputOptions(OutputJsonModel output, OutputFormat format)
{
	return new OutputOptions
	{
		Format = format,
		LinearDeflection = output.LinearDeflection,
		AngularDeflection = output.AngularDeflection,
		Parallel = output.Parallel,
	};
}

static int CreateStockShapeId(L1Kernel kernel, JobJsonModel job)
{
	var stock = job.Stock.ToKernel();
	return kernel.CreateStock(ref stock);
}

static ReplayJobResult ReplayJob(L1Kernel kernel, JobJsonModel job)
{
	var stockId = CreateStockShapeId(kernel, job);
	var currentId = stockId;
	var hasFeatureResult = false;
	OperationResult lastResult = default;

	foreach (var feature in job.Features)
	{
		lastResult = ApplyFeature(kernel, currentId, feature);
		currentId = lastResult.ResultShapeId;
		hasFeatureResult = true;
	}

	return new ReplayJobResult
	{
		StockShapeId = stockId,
		FinalShapeId = hasFeatureResult ? currentId : stockId,
		HasFeatureResult = hasFeatureResult,
		LastResult = lastResult,
	};
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

static void CleanupOldDirectories(string rootPath, TimeSpan ttl)
{
	try
	{
		if (!Directory.Exists(rootPath))
			return;

		var now = DateTime.UtcNow;
		foreach (var dir in Directory.EnumerateDirectories(rootPath))
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

readonly record struct ReplayJobResult
{
	public int StockShapeId { get; init; }
	public int FinalShapeId { get; init; }
	public bool HasFeatureResult { get; init; }
	public OperationResult LastResult { get; init; }
}

sealed class PipelineRunResponse
{
	public string RunId { get; set; } = string.Empty;
	public string FinalStepUrl { get; set; } = string.Empty;
	public string FinalStlUrl { get; set; } = string.Empty;
	public string DeltaStepUrl { get; set; } = string.Empty;
	public string DeltaStlUrl { get; set; } = string.Empty;
	public string? RemovalStepUrl { get; set; }
	public string? RemovalStlUrl { get; set; }
}

sealed class PreviewStageRequest
{
	public JobJsonModel Job { get; set; } = new();
	public int StageIndex { get; set; }
}

sealed class PreviewStageResponse
{
	public int StageIndex { get; set; }
	public string ModelStlUrl { get; set; } = string.Empty;
	public string? DeltaStlUrl { get; set; }
	public string? RemovalStlUrl { get; set; }
}

sealed class ReferenceStepResponse
{
	public string ReferenceId { get; set; } = string.Empty;
	public string ReferenceStlUrl { get; set; } = string.Empty;
}
