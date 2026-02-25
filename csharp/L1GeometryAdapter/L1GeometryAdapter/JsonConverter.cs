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

	public KernelJobModel ToKernel()
	{
		if (Features.Count == 0)
			throw new InvalidOperationException("features must contain at least one item.");

		var kernelFeatures = new List<FeatureDto>(Features.Count);
		foreach (var feature in Features)
			kernelFeatures.Add(feature.ToKernel());

		return new KernelJobModel
		{
			Stock = Stock.ToKernel(),
			Features = kernelFeatures,
			OutputOptions = Output.ToKernelOptions(),
			OutputDir = Output.Dir,
			StepFile = Output.StepFile,
			StlFile = Output.StlFile,
			DeltaStepFile = Output.DeltaStepFile,
			DeltaStlFile = Output.DeltaStlFile,
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
			"BOX" => StockType.Box,
			"CYLINDER" => StockType.Cylinder,
			_ => throw new InvalidOperationException($"Unsupported stock.type: {Type}"),
		};

		return new StockDto
		{
			Type = stockType,
			P1 = P1,
			P2 = P2,
			P3 = P3,
			Axis = Axis.ToKernel(),
		};
	}
}

public sealed class FeatureJsonModel
{
	[JsonPropertyAttribute("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyAttribute("drill")]
	public DrillJsonModel? Drill { get; set; }

	[JsonPropertyAttribute("pocketRect")]
	public PocketRectJsonModel? PocketRect { get; set; }

	[JsonPropertyAttribute("turnOd")]
	public TurnJsonModel? TurnOd { get; set; }

	[JsonPropertyAttribute("turnId")]
	public TurnJsonModel? TurnId { get; set; }

	public FeatureDto ToKernel()
	{
		var type = Type.ToUpperInvariant();
		return type switch
		{
			"DRILL" => BuildDrill(),
			"POCKET_RECT" => BuildPocketRect(),
			"TURN_OD" => BuildTurnOd(),
			"TURN_ID" => BuildTurnId(),
			_ => throw new InvalidOperationException($"Unsupported feature.type: {Type}"),
		};
	}

	private FeatureDto BuildDrill()
	{
		if (Drill is null)
			throw new InvalidOperationException("feature.drill is required for type DRILL.");

		return new FeatureDto
		{
			Type = FeatureType.Drill,
			Drill = Drill.ToKernel(),
		};
	}

	private FeatureDto BuildPocketRect()
	{
		if (PocketRect is null)
			throw new InvalidOperationException("feature.pocketRect is required for type POCKET_RECT.");

		return new FeatureDto
		{
			Type = FeatureType.PocketRect,
			PocketRect = PocketRect.ToKernel(),
		};
	}

	private FeatureDto BuildTurnOd()
	{
		if (TurnOd is null)
			throw new InvalidOperationException("feature.turnOd is required for type TURN_OD.");

		unsafe
		{
			var turn = TurnOd.ToKernelOd();
			return new FeatureDto
			{
				Type = FeatureType.TurnOd,
				TurnOd = turn,
			};
		}
	}

	private FeatureDto BuildTurnId()
	{
		if (TurnId is null)
			throw new InvalidOperationException("feature.turnId is required for type TURN_ID.");

		unsafe
		{
			var turn = TurnId.ToKernelId();
			return new FeatureDto
			{
				Type = FeatureType.TurnId,
				TurnId = turn,
			};
		}
	}
}

public sealed class DrillJsonModel
{
	[JsonPropertyAttribute("radius")]
	public double Radius { get; set; }

	[JsonPropertyAttribute("depth")]
	public double Depth { get; set; }

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();

	public DrillFeatureDto ToKernel()
	{
		return new DrillFeatureDto
		{
			Radius = Radius,
			Depth = Depth,
			Axis = Axis.ToKernel(),
		};
	}
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

	public PocketRectFeatureDto ToKernel()
	{
		return new PocketRectFeatureDto
		{
			Width = Width,
			Height = Height,
			Depth = Depth,
			Axis = Axis.ToKernel(),
		};
	}
}

public sealed class TurnJsonModel
{
	[JsonPropertyAttribute("profile")]
	public List<TurnProfilePointJsonModel> Profile { get; set; } = new();

	[JsonPropertyAttribute("axis")]
	public AxisJsonModel Axis { get; set; } = new();

	public unsafe TurnOdFeatureDto ToKernelOd()
	{
		ValidateProfile();

		var dto = new TurnOdFeatureDto
		{
			ProfileCount = Profile.Count,
			Axis = Axis.ToKernel(),
			TargetDiameter = Profile[0].Radius * 2.0,
			Length = Profile[^1].Z - Profile[0].Z,
		};

		for (int i = 0; i < Profile.Count; i++)
		{
			dto.ProfileZ[i] = Profile[i].Z;
			dto.ProfileRadius[i] = Profile[i].Radius;
		}

		return dto;
	}

	public unsafe TurnIdFeatureDto ToKernelId()
	{
		ValidateProfile();

		var dto = new TurnIdFeatureDto
		{
			ProfileCount = Profile.Count,
			Axis = Axis.ToKernel(),
			TargetDiameter = Profile[0].Radius * 2.0,
			Length = Profile[^1].Z - Profile[0].Z,
		};

		for (int i = 0; i < Profile.Count; i++)
		{
			dto.ProfileZ[i] = Profile[i].Z;
			dto.ProfileRadius[i] = Profile[i].Radius;
		}

		return dto;
	}

	private void ValidateProfile()
	{
		if (Profile.Count < 2)
			throw new InvalidOperationException("turn profile requires at least 2 points.");

		if (Profile.Count > L1GeometryKernelNative.TurnOdProfileMax)
			throw new InvalidOperationException($"turn profile supports at most {L1GeometryKernelNative.TurnOdProfileMax} points.");
	}
}

public sealed class TurnProfilePointJsonModel
{
	[JsonPropertyAttribute("z")]
	public double Z { get; set; }

	[JsonPropertyAttribute("radius")]
	public double Radius { get; set; }
}

public sealed class AxisJsonModel
{
	[JsonPropertyAttribute("origin")]
	public double[] Origin { get; set; } = Array.Empty<double>();

	[JsonPropertyAttribute("dir")]
	public double[] Dir { get; set; } = Array.Empty<double>();

	[JsonPropertyAttribute("xdir")]
	public double[] Xdir { get; set; } = Array.Empty<double>();

	public unsafe AxisDto ToKernel()
	{
		ValidateLength(Origin, "origin");
		ValidateLength(Dir, "dir");
		ValidateLength(Xdir, "xdir");

		var axis = new AxisDto();
		for (int i = 0; i < 3; i++)
		{
			axis.Origin[i] = Origin[i];
			axis.Dir[i] = Dir[i];
			axis.Xdir[i] = Xdir[i];
		}

		return axis;
	}

	private static void ValidateLength(double[] value, string name)
	{
		if (value.Length != 3)
			throw new InvalidOperationException($"axis.{name} must have exactly 3 elements.");
	}
}

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

	public OutputOptions ToKernelOptions()
	{
		return new OutputOptions
		{
			LinearDeflection = LinearDeflection,
			AngularDeflection = AngularDeflection,
			Parallel = Parallel,
		};
	}
}

public sealed class KernelJobModel
{
	public StockDto Stock { get; set; }
	public List<FeatureDto> Features { get; set; } = new();
	public OutputOptions OutputOptions { get; set; }
	public string OutputDir { get; set; } = string.Empty;
	public string StepFile { get; set; } = string.Empty;
	public string StlFile { get; set; } = string.Empty;
	public string DeltaStepFile { get; set; } = string.Empty;
	public string DeltaStlFile { get; set; } = string.Empty;
}

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

		var json = File.ReadAllText(jsonPath);
		return Deserialize(json);
	}
}
