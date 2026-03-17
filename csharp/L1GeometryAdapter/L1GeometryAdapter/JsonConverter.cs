using System.Text.Json;
using JsonPropertyAttribute = System.Text.Json.Serialization.JsonPropertyNameAttribute;

namespace L1GeometryAdapter;

public sealed class JobJsonModel
{
	[JsonPropertyAttribute("stock")]
	public StockJsonModel Stock { get; set; } = new();

	[JsonPropertyAttribute("features")]
	public List<FeatureJsonModel> Features { get; set; } = new();

	[JsonPropertyAttribute("output")]
	public OutputJsonModel Output { get; set; } = new();

	[JsonPropertyAttribute("meta")]
	public MetaJsonModel Meta { get; set; } = new();

	public KernelJobModel ToKernel()
	{
		if (Features.Count == 0)
			throw new InvalidOperationException("features must contain at least one item.");

		return new KernelJobModel
		{
			Stock         = Stock.ToKernel(),
			Features      = Features,
			OutputOptions = Output.ToKernelOptions(),
			OutputDir     = Output.Dir,
			StepFile      = Output.StepFile,
			StlFile       = Output.StlFile,
			DeltaStepFile = Output.DeltaStepFile,
			DeltaStlFile  = Output.DeltaStlFile,
			RemovalStepFile = Output.RemovalStepFile,
			RemovalStlFile  = Output.RemovalStlFile,
		};
	}
}

public sealed class StockJsonModel
{
	[JsonPropertyAttribute("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyAttribute("p1")]
	public double P1 { get; set; }

	[JsonPropertyAttribute("p2")]
	public double P2 { get; set; }

	[JsonPropertyAttribute("p3")]
	public double P3 { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();

	public StockDto ToKernel()
	{
		var stockType = Type.ToUpperInvariant() switch
		{
			"BOX"      => StockType.Box,
			"CYLINDER" => StockType.Cylinder,
			_ => throw new InvalidOperationException($"Unsupported stock.type: {Type}"),
		};

		return new StockDto
		{
			Type = stockType,
			P1   = P1,
			P2   = P2,
			P3   = P3,
			Axis = Axis.ToKernel(),
		};
	}
}

public sealed class FeatureJsonModel
{
	[JsonPropertyAttribute("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyAttribute("millHole")]
	public MillHoleJsonModel? MillHole { get; set; }

	[JsonPropertyAttribute("pocketRect")]
	public PocketRectJsonModel? PocketRect { get; set; }

	[JsonPropertyAttribute("turnOd")]
	public TurnJsonModel? TurnOd { get; set; }

	[JsonPropertyAttribute("turnId")]
	public TurnJsonModel? TurnId { get; set; }

	[JsonPropertyAttribute("millContour")]
	public MillContourJsonModel? MillContour { get; set; }
}

public sealed class MillHoleJsonModel
{
	[JsonPropertyAttribute("radius")]
	public double Radius { get; set; }

	[JsonPropertyAttribute("depth")]
	public double Depth { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();

	public MillHoleFeatureDto ToKernel() => new()
	{
		Radius = Radius,
		Depth  = Depth,
		Axis   = Axis.ToKernel(),
	};
}

public sealed class PocketRectJsonModel
{
	[JsonPropertyAttribute("width")]
	public double Width { get; set; }

	[JsonPropertyAttribute("height")]
	public double Height { get; set; }

	[JsonPropertyAttribute("depth")]
	public double Depth { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();

	public PocketRectFeatureDto ToKernel() => new()
	{
		Width  = Width,
		Height = Height,
		Depth  = Depth,
		Axis   = Axis.ToKernel(),
	};
}

// ---------------------------------------------------------------------------
// Path2D JSON モデル
// ---------------------------------------------------------------------------

public sealed class Path2DPointJsonModel
{
	[JsonPropertyAttribute("u")]
	public double U { get; set; }

	[JsonPropertyAttribute("v")]
	public double V { get; set; }

	public Path2DPointDto ToKernel() => new() { U = U, V = V };
}

public sealed class Path2DSegmentJsonModel
{
	[JsonPropertyAttribute("type")]
	public string? Type { get; set; }

	[JsonPropertyAttribute("from")]
	public Path2DPointJsonModel? From { get; set; }

	[JsonPropertyAttribute("to")]
	public Path2DPointJsonModel? To { get; set; }

	[JsonPropertyAttribute("center")]
	public Path2DPointJsonModel? Center { get; set; }

	[JsonPropertyAttribute("arcDirection")]
	public string? ArcDirection { get; set; }

	public Path2DSegmentDto ToKernel()
	{
		var segType = (Type ?? string.Empty).Trim().ToUpperInvariant() switch
		{
			"LINE" => Path2DSegmentType.Line,
			"ARC"  => Path2DSegmentType.Arc,
			_ => throw new InvalidOperationException($"Unsupported segment type: {Type}"),
		};

		var arcDir = (ArcDirection ?? string.Empty).Trim().ToUpperInvariant() switch
		{
			"CW"  => L1GeometryAdapter.ArcDirection.CW,
			"CCW" => L1GeometryAdapter.ArcDirection.CCW,
			""    => L1GeometryAdapter.ArcDirection.CW,  // LINE セグメントでは無視される
			_ => throw new InvalidOperationException($"Unsupported arcDirection: {ArcDirection}"),
		};

		return new Path2DSegmentDto
		{
			From         = From?.ToKernel()   ?? default,
			To           = To?.ToKernel()     ?? default,
			Center       = Center?.ToKernel() ?? default,
			Type         = segType,
			ArcDirection = arcDir,
		};
	}
}

public sealed class Path2DProfileJsonModel
{
	[JsonPropertyAttribute("start")]
	public Path2DPointJsonModel? Start { get; set; }

	[JsonPropertyAttribute("segments")]
	public List<Path2DSegmentJsonModel> Segments { get; set; } = new();

	[JsonPropertyAttribute("closed")]
	public bool Closed { get; set; }

	public Path2DSegmentDto[] ToKernelSegments()
	{
		var result = new Path2DSegmentDto[Segments.Count];
		for (int i = 0; i < Segments.Count; i++)
			result[i] = Segments[i].ToKernel();
		return result;
	}
}

// ---------------------------------------------------------------------------
// Turn / MillContour JSON モデル
// ---------------------------------------------------------------------------

public sealed class TurnJsonModel
{
	[JsonPropertyAttribute("profile")]
	public Path2DProfileJsonModel? Profile { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();
}

public sealed class MillContourJsonModel
{
	[JsonPropertyAttribute("profile")]
	public Path2DProfileJsonModel? Profile { get; set; }

	[JsonPropertyAttribute("depth")]
	public double Depth { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Axis JSON モデル
// ---------------------------------------------------------------------------

public sealed class AxisJsonModel
{
	[JsonPropertyAttribute("origin")]
	public double[] Origin { get; set; } = Array.Empty<double>();

	[JsonPropertyAttribute("dir")]
	public double[] Dir { get; set; } = Array.Empty<double>();

	[JsonPropertyAttribute("xdir")]
	public double[] Xdir { get; set; } = Array.Empty<double>();

	public AxisDto ToKernel()
	{
		ValidateLength(Origin, "origin");
		ValidateLength(Dir,    "dir");
		ValidateLength(Xdir,   "xdir");

		return new AxisDto
		{
			Origin = new Vec3(Origin[0], Origin[1], Origin[2]),
			Dir    = new Vec3(Dir[0],    Dir[1],    Dir[2]),
			Xdir   = new Vec3(Xdir[0],   Xdir[1],   Xdir[2]),
		};
	}

	private static void ValidateLength(double[] value, string name)
	{
		if (value.Length != 3)
			throw new InvalidOperationException($"axis.{name} must have exactly 3 elements.");
	}
}

// ---------------------------------------------------------------------------
// Output JSON モデル
// ---------------------------------------------------------------------------

public sealed class OutputJsonModel
{
	[JsonPropertyAttribute("linearDeflection")]
	public double LinearDeflection { get; set; }

	[JsonPropertyAttribute("angularDeflection")]
	public double AngularDeflection { get; set; }

	[JsonPropertyAttribute("parallel")]
	public int Parallel { get; set; }

	[JsonPropertyAttribute("dir")]
	public string Dir { get; set; } = string.Empty;

	[JsonPropertyAttribute("stepFile")]
	public string StepFile { get; set; } = string.Empty;

	[JsonPropertyAttribute("stlFile")]
	public string StlFile { get; set; } = string.Empty;

	[JsonPropertyAttribute("deltaStepFile")]
	public string DeltaStepFile { get; set; } = string.Empty;

	[JsonPropertyAttribute("deltaStlFile")]
	public string DeltaStlFile { get; set; } = string.Empty;

	[JsonPropertyAttribute("removalStepFile")]
	public string RemovalStepFile { get; set; } = string.Empty;

	[JsonPropertyAttribute("removalStlFile")]
	public string RemovalStlFile { get; set; } = string.Empty;

	public OutputOptions ToKernelOptions() => new()
	{
		LinearDeflection  = LinearDeflection,
		AngularDeflection = AngularDeflection,
		Parallel          = Parallel,
	};
}

public sealed class MetaJsonModel
{
	[JsonPropertyAttribute("sessionId")]
	public string SessionId { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Kernel job model（L1Kernel への入力）
// ---------------------------------------------------------------------------

public sealed class KernelJobModel
{
	public StockDto             Stock         { get; set; }
	public List<FeatureJsonModel> Features    { get; set; } = new();
	public OutputOptions        OutputOptions { get; set; }
	public string               OutputDir     { get; set; } = string.Empty;
	public string               StepFile      { get; set; } = string.Empty;
	public string               StlFile       { get; set; } = string.Empty;
	public string               DeltaStepFile { get; set; } = string.Empty;
	public string               DeltaStlFile  { get; set; } = string.Empty;
	public string               RemovalStepFile { get; set; } = string.Empty;
	public string               RemovalStlFile  { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// JSON デシリアライズ ユーティリティ
// ---------------------------------------------------------------------------

public static class JsonJobConverter
{
	public static JobJsonModel Deserialize(string json)
	{
		var model = JsonSerializer.Deserialize<JobJsonModel>(json)
			?? throw new InvalidOperationException("Failed to deserialize job json.");
		return model;
	}

	public static JobJsonModel DeserializeFile(string jsonPath)
	{
		if (!File.Exists(jsonPath))
			throw new FileNotFoundException("JSON file not found.", jsonPath);
		return Deserialize(File.ReadAllText(jsonPath));
	}
}
