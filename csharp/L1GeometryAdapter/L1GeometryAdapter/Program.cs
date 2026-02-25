// See https://aka.ms/new-console-template for more information
using System;
using System.Collections.Generic;
using System.IO;
using L1GeometryAdapter;

// ---------------------------------------------------------------
// Utility functions
// ---------------------------------------------------------------

static bool ParseBool01(string text) => text switch
{
    "1" => true,
    "0" => false,
    _   => throw new InvalidDataException($"Expected 0 or 1 but got: {text}"),
};

static unsafe void ParseVector3(string text, double* buf)
{
    var parts = text.Split(',');
    if (parts.Length != 3)
        throw new InvalidDataException($"Expected 3 components: {text}");
    buf[0] = double.Parse(parts[0].Trim());
    buf[1] = double.Parse(parts[1].Trim());
    buf[2] = double.Parse(parts[2].Trim());
}

static Dictionary<string, string> LoadKeyValues(string filePath)
{
    var kv = new Dictionary<string, string>();
    int lineNo = 0;
    foreach (var raw in File.ReadLines(filePath))
    {
        ++lineNo;
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        int pos = line.IndexOf('=');
        if (pos < 0)
            throw new InvalidDataException($"Invalid line (missing '=') at line {lineNo}");
        var key   = line[..pos].Trim();
        var value = line[(pos + 1)..].Trim();
        if (key.Length == 0)
            throw new InvalidDataException($"Empty key at line {lineNo}");
        kv[key] = value;
    }
    return kv;
}

static string Require(Dictionary<string, string> kv, string key)
{
    if (!kv.TryGetValue(key, out var v))
        throw new InvalidDataException($"Missing key: {key}");
    return v;
}

static string? Find(Dictionary<string, string> kv, string key)
    => kv.TryGetValue(key, out var v) ? v : null;

// ---------------------------------------------------------------
// Case file loader
// ---------------------------------------------------------------

static unsafe SampleCase LoadCaseFile(string filePath)
{
    var kv     = LoadKeyValues(filePath);
    var sample = new SampleCase();

    // --- Stock ---
    sample.Stock.Type = Require(kv, "stock.type") switch
    {
        "BOX"      => StockType.Box,
        "CYLINDER" => StockType.Cylinder,
        var t      => throw new InvalidDataException($"Unsupported stock.type: {t}"),
    };
    sample.Stock.P1 = double.Parse(Require(kv, "stock.p1"));
    sample.Stock.P2 = double.Parse(Require(kv, "stock.p2"));
    sample.Stock.P3 = double.Parse(Require(kv, "stock.p3"));
    fixed (double* o = sample.Stock.Axis.Origin, d = sample.Stock.Axis.Dir, x = sample.Stock.Axis.Xdir)
    {
        ParseVector3(Require(kv, "stock.axis.origin"), o);
        ParseVector3(Require(kv, "stock.axis.dir"),    d);
        ParseVector3(Require(kv, "stock.axis.xdir"),   x);
    }

    // --- Feature ---
    switch (Require(kv, "feature.type"))
    {
        case "DRILL":
        {
            sample.Feature.Type         = FeatureType.Drill;
            sample.Feature.Drill.Radius = double.Parse(Require(kv, "feature.drill.radius"));
            sample.Feature.Drill.Depth  = double.Parse(Require(kv, "feature.drill.depth"));
            fixed (double* o = sample.Feature.Drill.Axis.Origin, d = sample.Feature.Drill.Axis.Dir, x = sample.Feature.Drill.Axis.Xdir)
            {
                ParseVector3(Require(kv, "feature.drill.axis.origin"), o);
                ParseVector3(Require(kv, "feature.drill.axis.dir"),    d);
                ParseVector3(Require(kv, "feature.drill.axis.xdir"),   x);
            }
            break;
        }
        case "POCKET_RECT":
        {
            sample.Feature.Type                = FeatureType.PocketRect;
            sample.Feature.PocketRect.Width    = double.Parse(Require(kv, "feature.pocketRect.width"));
            sample.Feature.PocketRect.Height   = double.Parse(Require(kv, "feature.pocketRect.height"));
            sample.Feature.PocketRect.Depth    = double.Parse(Require(kv, "feature.pocketRect.depth"));
            fixed (double* o = sample.Feature.PocketRect.Axis.Origin, d = sample.Feature.PocketRect.Axis.Dir, x = sample.Feature.PocketRect.Axis.Xdir)
            {
                ParseVector3(Require(kv, "feature.pocketRect.axis.origin"), o);
                ParseVector3(Require(kv, "feature.pocketRect.axis.dir"),    d);
                ParseVector3(Require(kv, "feature.pocketRect.axis.xdir"),   x);
            }
            break;
        }
        case "TURN_OD":
        {
            sample.Feature.Type = FeatureType.TurnOd;
            fixed (double* pz = sample.Feature.TurnOd.ProfileZ, pr = sample.Feature.TurnOd.ProfileRadius)
            {
                var countStr = Find(kv, "feature.turnOd.profile.count");
                if (countStr != null)
                {
                    int count = int.Parse(countStr);
                    if (count < 2 || count > L1GeometryKernelNative.TurnOdProfileMax)
                        throw new InvalidDataException("feature.turnOd.profile.count out of range");
                    sample.Feature.TurnOd.ProfileCount   = count;
                    for (int i = 0; i < count; ++i)
                    {
                        pz[i] = double.Parse(Require(kv, $"feature.turnOd.profile.{i}.z"));
                        pr[i] = double.Parse(Require(kv, $"feature.turnOd.profile.{i}.radius"));
                    }
                    sample.Feature.TurnOd.TargetDiameter = pr[0] * 2.0;
                    sample.Feature.TurnOd.Length         = pz[count - 1] - pz[0];
                }
                else
                {
                    sample.Feature.TurnOd.TargetDiameter = double.Parse(Require(kv, "feature.turnOd.targetDiameter"));
                    sample.Feature.TurnOd.Length         = double.Parse(Require(kv, "feature.turnOd.length"));
                }
            }
            fixed (double* o = sample.Feature.TurnOd.Axis.Origin, d = sample.Feature.TurnOd.Axis.Dir, x = sample.Feature.TurnOd.Axis.Xdir)
            {
                ParseVector3(Require(kv, "feature.turnOd.axis.origin"), o);
                ParseVector3(Require(kv, "feature.turnOd.axis.dir"),    d);
                ParseVector3(Require(kv, "feature.turnOd.axis.xdir"),   x);
            }
            break;
        }
        case "TURN_ID":
        {
            sample.Feature.Type = FeatureType.TurnId;
            fixed (double* pz = sample.Feature.TurnId.ProfileZ, pr = sample.Feature.TurnId.ProfileRadius)
            {
                var countStr = Find(kv, "feature.turnId.profile.count");
                if (countStr != null)
                {
                    int count = int.Parse(countStr);
                    if (count < 2 || count > L1GeometryKernelNative.TurnOdProfileMax)
                        throw new InvalidDataException("feature.turnId.profile.count out of range");
                    sample.Feature.TurnId.ProfileCount   = count;
                    for (int i = 0; i < count; ++i)
                    {
                        pz[i] = double.Parse(Require(kv, $"feature.turnId.profile.{i}.z"));
                        pr[i] = double.Parse(Require(kv, $"feature.turnId.profile.{i}.radius"));
                    }
                    sample.Feature.TurnId.TargetDiameter = pr[0] * 2.0;
                    sample.Feature.TurnId.Length         = pz[count - 1] - pz[0];
                }
                else
                {
                    sample.Feature.TurnId.TargetDiameter = double.Parse(Require(kv, "feature.turnId.targetDiameter"));
                    sample.Feature.TurnId.Length         = double.Parse(Require(kv, "feature.turnId.length"));
                }
            }
            fixed (double* o = sample.Feature.TurnId.Axis.Origin, d = sample.Feature.TurnId.Axis.Dir, x = sample.Feature.TurnId.Axis.Xdir)
            {
                ParseVector3(Require(kv, "feature.turnId.axis.origin"), o);
                ParseVector3(Require(kv, "feature.turnId.axis.dir"),    d);
                ParseVector3(Require(kv, "feature.turnId.axis.xdir"),   x);
            }
            break;
        }
        default:
            throw new InvalidDataException($"Unsupported feature.type: {Require(kv, "feature.type")}");
    }

    // --- Output options ---
    sample.OutputOptions.LinearDeflection  = double.Parse(Require(kv, "output.linearDeflection"));
    sample.OutputOptions.AngularDeflection = double.Parse(Require(kv, "output.angularDeflection"));
    sample.OutputOptions.Parallel          = ParseBool01(Require(kv, "output.parallel")) ? 1 : 0;
    sample.OutputDir     = Require(kv, "output.dir");
    sample.StepFile      = Require(kv, "output.stepFile");
    sample.StlFile       = Require(kv, "output.stlFile");
    sample.DeltaStepFile = Require(kv, "output.deltaStepFile");
    sample.DeltaStlFile  = Require(kv, "output.deltaStlFile");

    return sample;
}

// ---------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------

string casePath = args.Length >= 1
    ? args[0]
    : Path.Combine("samples", "box_drill_case.txt");

SampleCase sample;
try
{
    sample = LoadCaseFile(casePath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load case file: {casePath}");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

try
{
    using var kernel = new L1Kernel();

    int stockId = kernel.CreateStock(ref sample.Stock);

    var results = kernel.ApplyFeatures(stockId, new[] { sample.Feature });
    var result  = results[0];

    string outDir = Path.Combine(Directory.GetCurrentDirectory(), sample.OutputDir);
    Directory.CreateDirectory(outDir);

    string stepPath      = Path.Combine(outDir, sample.StepFile);
    string stlPath       = Path.Combine(outDir, sample.StlFile);
    string deltaStepPath = Path.Combine(outDir, sample.DeltaStepFile);
    string deltaStlPath  = Path.Combine(outDir, sample.DeltaStlFile);

    var stepOpt = sample.OutputOptions with { Format = OutputFormat.Step };
    var stlOpt  = sample.OutputOptions with { Format = OutputFormat.Stl  };

    kernel.ExportShape(result.ResultShapeId, stepOpt, stepPath);
    kernel.ExportShape(result.ResultShapeId, stlOpt,  stlPath);
    kernel.ExportShape(result.DeltaShapeId,  stepOpt, deltaStepPath);
    kernel.ExportShape(result.DeltaShapeId,  stlOpt,  deltaStlPath);

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

// ---------------------------------------------------------------
// Helper types
// ---------------------------------------------------------------

internal sealed class SampleCase
{
    public StockDto Stock;
    public FeatureDto Feature;
    public OutputOptions OutputOptions;
    public string OutputDir     = string.Empty;
    public string StepFile      = string.Empty;
    public string StlFile       = string.Empty;
    public string DeltaStepFile = string.Empty;
    public string DeltaStlFile  = string.Empty;
}

