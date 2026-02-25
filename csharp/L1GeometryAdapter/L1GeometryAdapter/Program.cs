using L1GeometryAdapter;

string jsonPath = args.Length >= 1
    ? args[0]
    : Path.Combine("samples", "machining_job.json");

KernelJobModel job;
try
{
    var json = JsonJobConverter.DeserializeFile(jsonPath);
    job = json.ToKernel();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load job json: {jsonPath}");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

try
{
    using var kernel = new L1Kernel();

    var stock = job.Stock;
    int stockId = kernel.CreateStock(ref stock);
    var results = kernel.ApplyFeatures(stockId, job.Features);
    var finalResult = results[^1];

    string outDir = Path.Combine(Directory.GetCurrentDirectory(), job.OutputDir);
    Directory.CreateDirectory(outDir);

    string stepPath = Path.Combine(outDir, job.StepFile);
    string stlPath = Path.Combine(outDir, job.StlFile);
    string deltaStepPath = Path.Combine(outDir, job.DeltaStepFile);
    string deltaStlPath = Path.Combine(outDir, job.DeltaStlFile);

    var stepOpt = job.OutputOptions;
    stepOpt.Format = OutputFormat.Step;

    var stlOpt = job.OutputOptions;
    stlOpt.Format = OutputFormat.Stl;

    kernel.ExportShape(finalResult.ResultShapeId, stepOpt, stepPath);
    kernel.ExportShape(finalResult.ResultShapeId, stlOpt, stlPath);
    kernel.ExportShape(finalResult.DeltaShapeId, stepOpt, deltaStepPath);
    kernel.ExportShape(finalResult.DeltaShapeId, stlOpt, deltaStlPath);

    Console.WriteLine($"Generated: {stepPath}");
    Console.WriteLine($"Generated: {stlPath}");
    Console.WriteLine($"Generated: {deltaStepPath}");
    Console.WriteLine($"Generated: {deltaStlPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

