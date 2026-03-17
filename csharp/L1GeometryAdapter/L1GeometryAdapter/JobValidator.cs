namespace L1GeometryAdapter;

public static class JobValidator
{
	private const int Path2DSegmentMax = 128;

	public static List<ValidationError> Validate(JobJsonModel job)
	{
		var errors = new List<ValidationError>();

		if (string.IsNullOrWhiteSpace(job.Meta?.SessionId))
			errors.Add(Error("EMPTY_SESSION_ID", "meta.sessionId", "meta.sessionId must not be empty."));

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
			var feature     = job.Features[i];
			var featureType = (feature.Type ?? string.Empty).ToUpperInvariant();
			var basePath    = $"features[{i}]";

			switch (featureType)
			{
				case "MILL_HOLE":
					if (feature.MillHole is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.millHole", "feature.millHole is required for type MILL_HOLE."));
					else
						ValidateAxis(feature.MillHole.Axis, $"{basePath}.millHole.axis", errors);
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
						ValidatePath2DProfile(feature.TurnOd.Profile, $"{basePath}.turnOd.profile", requireClosed: true, errors);
						ValidateAxis(feature.TurnOd.Axis, $"{basePath}.turnOd.axis", errors);
					}
					break;

				case "TURN_ID":
					if (feature.TurnId is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.turnId", "feature.turnId is required for type TURN_ID."));
					else
					{
						ValidatePath2DProfile(feature.TurnId.Profile, $"{basePath}.turnId.profile", requireClosed: true, errors);
						ValidateAxis(feature.TurnId.Axis, $"{basePath}.turnId.axis", errors);
					}
					break;

				case "MILL_CONTOUR":
					if (feature.MillContour is null)
						errors.Add(Error("MISSING_PAYLOAD", $"{basePath}.millContour", "feature.millContour is required for type MILL_CONTOUR."));
					else
					{
						ValidatePath2DProfile(feature.MillContour.Profile, $"{basePath}.millContour.profile", requireClosed: true, errors);
						if (feature.MillContour.Depth <= 0)
							errors.Add(Error("INVALID_DEPTH", $"{basePath}.millContour.depth", "millContour.depth must be greater than 0."));
						ValidateAxis(feature.MillContour.Axis, $"{basePath}.millContour.axis", errors);
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

		// [D7] Deterministic order: path asc → code asc
		return errors
			.OrderBy(e => e.Path)
			.ThenBy(e => e.Code)
			.ToList();
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

	private static void ValidatePath2DProfile(Path2DProfileJsonModel? profile, string path,
	                                           bool requireClosed, List<ValidationError> errors)
	{
		if (profile is null)
		{
			errors.Add(Error("MISSING_PROFILE", path, $"{path} is required."));
			return;
		}

		if (profile.Segments.Count == 0)
			errors.Add(Error("PROFILE_EMPTY", path, $"{path} must contain at least one segment."));
		else if (profile.Segments.Count > Path2DSegmentMax)
			errors.Add(Error("PROFILE_TOO_LONG", path, $"{path} supports at most {Path2DSegmentMax} segments."));

		if (requireClosed && !profile.Closed)
			errors.Add(Error("PROFILE_NOT_CLOSED", $"{path}.closed", $"{path}.closed must be true."));

		for (int i = 0; i < profile.Segments.Count; i++)
		{
			var seg     = profile.Segments[i];
			var segPath = $"{path}.segments[{i}]";
			var segType = (seg.Type ?? string.Empty).Trim().ToUpperInvariant();
			if (segType != "LINE" && segType != "ARC")
				errors.Add(Error("INVALID_SEGMENT_TYPE", $"{segPath}.type",
				                 $"{segPath}.type must be LINE or ARC."));
		}
	}

	private static ValidationError Error(string code, string path, string message)
		=> new() { Code = code, Path = path, Message = message };
}
