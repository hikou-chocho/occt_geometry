using System.Text.Json.Serialization;
using L1GeometryAdapter;

internal sealed class ValidationError
{
	[JsonPropertyName("code")]
	public string Code { get; init; } = string.Empty;

	[JsonPropertyName("path")]
	public string Path { get; init; } = string.Empty;

	[JsonPropertyName("message")]
	public string Message { get; init; } = string.Empty;
}

internal static class JobValidator
{
	private const int TurnProfileMax = 64;

	public static List<ValidationError> Validate(JobJsonModel job)
	{
		var errors = new List<ValidationError>();

		// Rule 1: features.length >= 1
		if (job.Features.Count < 1)
			errors.Add(Error("FEATURES_EMPTY", "features", "features must contain at least one item."));

		// Rule 5: stock.type must be BOX or CYLINDER
		var stockType = (job.Stock.Type ?? string.Empty).ToUpperInvariant();
		if (stockType != "BOX" && stockType != "CYLINDER")
			errors.Add(Error("INVALID_STOCK_TYPE", "stock.type", $"Unsupported stock.type: {job.Stock.Type}"));

		// Rule 2: stock.axis arrays length 3
		ValidateAxis(job.Stock.Axis, "stock.axis", errors);

		// Per-feature validation
		for (int i = 0; i < job.Features.Count; i++)
		{
			var feature = job.Features[i];
			var featureType = (feature.Type ?? string.Empty).ToUpperInvariant();
			var basePath = $"features[{i}]";

			switch (featureType)
			{
				case "DRILL":
					if (feature.Drill is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.drill", "feature.drill is required for type DRILL."));
					else
						ValidateAxis(feature.Drill.Axis, $"{basePath}.drill.axis", errors);
					break;

				case "POCKET_RECT":
					if (feature.PocketRect is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.pocketRect", "feature.pocketRect is required for type POCKET_RECT."));
					else
						ValidateAxis(feature.PocketRect.Axis, $"{basePath}.pocketRect.axis", errors);
					break;

				case "TURN_OD":
					if (feature.TurnOd is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.turnOd", "feature.turnOd is required for type TURN_OD."));
					else
					{
						ValidateTurnProfile(feature.TurnOd.Profile, $"{basePath}.turnOd.profile", errors);
						ValidateAxis(feature.TurnOd.Axis, $"{basePath}.turnOd.axis", errors);
					}
					break;

				case "TURN_ID":
					if (feature.TurnId is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.turnId", "feature.turnId is required for type TURN_ID."));
					else
					{
						ValidateTurnProfile(feature.TurnId.Profile, $"{basePath}.turnId.profile", errors);
						ValidateAxis(feature.TurnId.Axis, $"{basePath}.turnId.axis", errors);
					}
					break;

				default:
					errors.Add(Error("INVALID_FEATURE_TYPE", $"{basePath}.type", $"Unsupported feature.type: {feature.Type}"));
					break;
			}
		}

		// Rule 6: output strings not empty
		if (string.IsNullOrWhiteSpace(job.Output.Dir))
			errors.Add(Error("EMPTY_OUTPUT_DIR", "output.dir", "output.dir must not be empty."));
		if (string.IsNullOrWhiteSpace(job.Output.StepFile))
			errors.Add(Error("EMPTY_OUTPUT_FILE", "output.stepFile", "output.stepFile must not be empty."));
		if (string.IsNullOrWhiteSpace(job.Output.StlFile))
			errors.Add(Error("EMPTY_OUTPUT_FILE", "output.stlFile", "output.stlFile must not be empty."));
		if (string.IsNullOrWhiteSpace(job.Output.DeltaStepFile))
			errors.Add(Error("EMPTY_OUTPUT_FILE", "output.deltaStepFile", "output.deltaStepFile must not be empty."));
		if (string.IsNullOrWhiteSpace(job.Output.DeltaStlFile))
			errors.Add(Error("EMPTY_OUTPUT_FILE", "output.deltaStlFile", "output.deltaStlFile must not be empty."));

		return errors;
	}

	private static void ValidateAxis(AxisJsonModel? axis, string path, List<ValidationError> errors)
	{
		if (axis is null)
		{
			errors.Add(Error("MISSING_AXIS", path, $"{path} is required."));
			return;
		}

		if (axis.Origin.Length != 3)
			errors.Add(Error("AXIS_LENGTH", $"{path}.origin", $"{path}.origin must have exactly 3 elements."));
		if (axis.Dir.Length != 3)
			errors.Add(Error("AXIS_LENGTH", $"{path}.dir", $"{path}.dir must have exactly 3 elements."));
		if (axis.Xdir.Length != 3)
			errors.Add(Error("AXIS_LENGTH", $"{path}.xdir", $"{path}.xdir must have exactly 3 elements."));
	}

	private static void ValidateTurnProfile(List<TurnProfilePointJsonModel>? profile, string path, List<ValidationError> errors)
	{
		if (profile is null)
		{
			errors.Add(Error("MISSING_PROFILE", path, $"{path} is required."));
			return;
		}

		if (profile.Count < 2)
			errors.Add(Error("TURN_PROFILE_TOO_SHORT", path, "Turn profile requires at least 2 points."));
		if (profile.Count > TurnProfileMax)
			errors.Add(Error("TURN_PROFILE_TOO_LONG", path, $"Turn profile supports at most {TurnProfileMax} points."));
	}

	private static ValidationError Error(string code, string path, string message)
		=> new() { Code = code, Path = path, Message = message };
}
