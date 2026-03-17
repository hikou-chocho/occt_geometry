using L1GeometryAdapter;

namespace L1GeometryAdapter
{
    public class Program
    {
        int Main(string[] args)
        {

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

                int currentId = stockId;
                OperationResult finalResult = default;
                foreach (var feature in job.Features)
                {
                    finalResult = ApplyFeature(kernel, currentId, feature);
                    currentId = finalResult.ResultShapeId;
                }

                string outDir = Path.Combine(Directory.GetCurrentDirectory(), job.OutputDir);
                Directory.CreateDirectory(outDir);

                string stepPath      = Path.Combine(outDir, job.StepFile);
                string stlPath       = Path.Combine(outDir, job.StlFile);
                string deltaStepPath = Path.Combine(outDir, job.DeltaStepFile);
                string deltaStlPath  = Path.Combine(outDir, job.DeltaStlFile);
                string? removalStepPath = string.IsNullOrWhiteSpace(job.RemovalStepFile)
                    ? null
                    : Path.Combine(outDir, job.RemovalStepFile);
                string? removalStlPath = string.IsNullOrWhiteSpace(job.RemovalStlFile)
                    ? null
                    : Path.Combine(outDir, job.RemovalStlFile);

                var stepOpt = job.OutputOptions;
                stepOpt.Format = OutputFormat.Step;

                var stlOpt = job.OutputOptions;
                stlOpt.Format = OutputFormat.Stl;

                kernel.ExportShape(finalResult.ResultShapeId, stepOpt, stepPath);
                kernel.ExportShape(finalResult.ResultShapeId, stlOpt,  stlPath);
                kernel.ExportShape(finalResult.DeltaShapeId,  stepOpt, deltaStepPath);
                kernel.ExportShape(finalResult.DeltaShapeId,  stlOpt,  deltaStlPath);
                if (removalStepPath is not null)
                    kernel.ExportShape(finalResult.RemovalShapeId, stepOpt, removalStepPath);
                if (removalStlPath is not null)
                    kernel.ExportShape(finalResult.RemovalShapeId, stlOpt, removalStlPath);

                Console.WriteLine($"Generated: {stepPath}");
                Console.WriteLine($"Generated: {stlPath}");
                Console.WriteLine($"Generated: {deltaStepPath}");
                Console.WriteLine($"Generated: {deltaStlPath}");
                if (removalStepPath is not null)
                    Console.WriteLine($"Generated: {removalStepPath}");
                if (removalStlPath is not null)
                    Console.WriteLine($"Generated: {removalStlPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

        }

        private static OperationResult ApplyFeature(L1Kernel kernel, int stockId, FeatureJsonModel feature)
        {
            var type = (feature.Type ?? string.Empty).ToUpperInvariant();
            return type switch
            {
                "MILL_HOLE"   => kernel.ApplyMillHole(stockId, feature.MillHole!.ToKernel()),
                "POCKET_RECT" => kernel.ApplyPocketRect(stockId, feature.PocketRect!.ToKernel()),
                "TURN_OD"     => ApplyTurn(kernel, stockId, feature.TurnOd!,
                                           (id, axis, segs, closed) => kernel.ApplyTurnOd(id, axis, segs, closed)),
                "TURN_ID"     => ApplyTurn(kernel, stockId, feature.TurnId!,
                                           (id, axis, segs, closed) => kernel.ApplyTurnId(id, axis, segs, closed)),
                "MILL_CONTOUR" => ApplyMillContour(kernel, stockId, feature.MillContour!),
                _ => throw new InvalidOperationException($"Unsupported feature.type: {feature.Type}"),
            };
        }

        private static OperationResult ApplyTurn(L1Kernel kernel, int stockId, TurnJsonModel turn,
            Func<int, AxisDto, Path2DSegmentDto[], bool, OperationResult> applyFn)
        {
            if (turn.Profile is null)
                throw new InvalidOperationException("turn profile is required.");
            return applyFn(stockId, turn.Axis.ToKernel(), turn.Profile.ToKernelSegments(), turn.Profile.Closed);
        }

        private static OperationResult ApplyMillContour(L1Kernel kernel, int stockId, MillContourJsonModel mc)
        {
            if (mc.Profile is null)
                throw new InvalidOperationException("millContour profile is required.");
            return kernel.ApplyMillContour(stockId, mc.Axis.ToKernel(),
                                           mc.Profile.ToKernelSegments(), mc.Profile.Closed, mc.Depth);
        }
    }

}
