namespace L1GeometryAdapter;

internal static class Path2DProfileResolver
{
	private const double Tolerance = 1.0e-7;

	internal static Path2DSegmentDto[] ResolveConcreteSegments(Path2DProfileJsonModel profile)
	{
		if (!TryResolveConcreteSegments(profile, "profile", out var segments, out var errors))
			throw new InvalidOperationException(errors[0].Message);
		return segments;
	}

	internal static bool TryResolveConcreteSegments(Path2DProfileJsonModel profile, string profilePath,
		out Path2DSegmentDto[] segments, out List<ValidationError> errors)
	{
		errors = new List<ValidationError>();
		segments = Array.Empty<Path2DSegmentDto>();

		if (profile.Segments.Count == 0)
		{
			errors.Add(Error("PROFILE_EMPTY", profilePath, $"{profilePath} must contain at least one segment."));
			return false;
		}

		var rawSegments = new RawSegment[profile.Segments.Count];
		for (int i = 0; i < profile.Segments.Count; i++)
		{
			var segPath = $"{profilePath}.segments[{i}]";
			if (!TryParseSegment(profile.Segments[i], segPath, out rawSegments[i], out var segmentErrors))
				errors.AddRange(segmentErrors);
		}

		if (errors.Count > 0)
			return false;

		for (int i = 0; i < rawSegments.Length; i++)
		{
			var current = rawSegments[i];
			var segPath = $"{profilePath}.segments[{i}]";
			var cornerPath = $"{segPath}.corner";

			if (i > 0)
			{
				var prev = rawSegments[i - 1];
				if (!PointsAlmostEqual(prev.To, current.From))
				{
					errors.Add(Error("PROFILE_DISCONNECTED", $"{segPath}.from",
						$"{segPath}.from must match {profilePath}.segments[{i - 1}].to."));
				}
			}

			if (current.Type == Path2DSegmentType.Line && PointsAlmostEqual(current.From, current.To))
			{
				errors.Add(Error("DEGENERATE_LINE_SEGMENT", segPath,
					$"{segPath} must not have identical from/to points."));
			}

			if (current.Corner is null)
				continue;

			if (current.Type != Path2DSegmentType.Line)
			{
				errors.Add(Error("CORNER_ON_NON_LINE", cornerPath,
					$"{cornerPath} is only supported on LINE segments."));
				continue;
			}

			if (current.Corner.Radius <= 0.0)
			{
				errors.Add(Error("INVALID_CORNER_RADIUS", $"{cornerPath}.radius",
					$"{cornerPath}.radius must be greater than 0."));
			}

			if (i == rawSegments.Length - 1 && !profile.Closed)
			{
				errors.Add(Error("OPEN_PROFILE_TRAILING_CORNER", cornerPath,
					$"{cornerPath} requires a following segment when profile.closed is false."));
				continue;
			}

			int nextIndex = (i + 1) % rawSegments.Length;
			var next = rawSegments[nextIndex];
			if (next.Type != Path2DSegmentType.Line)
			{
				errors.Add(Error("UNSUPPORTED_CORNER_NEIGHBOR", cornerPath,
					$"{cornerPath} only supports LINE-LINE fillets in v1."));
				continue;
			}

			if (!PointsAlmostEqual(current.To, next.From))
			{
				errors.Add(Error("PROFILE_DISCONNECTED", cornerPath,
					$"{cornerPath} requires the current segment end to match the next segment start."));
			}
		}

		if (profile.Closed)
		{
			var last = rawSegments[^1];
			var first = rawSegments[0];
			if (!PointsAlmostEqual(last.To, first.From))
			{
				errors.Add(Error("PROFILE_DISCONNECTED", $"{profilePath}.segments[{rawSegments.Length - 1}].to",
					$"{profilePath}.segments[{rawSegments.Length - 1}].to must match {profilePath}.segments[0].from for a closed profile."));
			}
		}

		if (errors.Count > 0)
			return false;

		var startTrim = new Path2DPointDto[rawSegments.Length];
		var hasStartTrim = new bool[rawSegments.Length];
		var endTrim = new Path2DPointDto[rawSegments.Length];
		var hasEndTrim = new bool[rawSegments.Length];
		var cornerArcs = new Path2DSegmentDto?[rawSegments.Length];

		for (int i = 0; i < rawSegments.Length; i++)
		{
			var current = rawSegments[i];
			if (current.Corner is null)
				continue;

			int nextIndex = (i + 1) % rawSegments.Length;
			var next = rawSegments[nextIndex];
			var cornerPath = $"{profilePath}.segments[{i}].corner";

			if (!TryResolveNativeFillet(current.From, current.To, next.To, current.Corner.Radius,
				out var tangentFrom, out var tangentTo, out var center, out var arcDirection))
			{
				errors.Add(Error("FILLET_NOT_FEASIBLE", cornerPath,
					$"{cornerPath} could not be resolved for the given radius and adjacent LINE segments."));
				continue;
			}

			hasEndTrim[i] = true;
			endTrim[i] = tangentFrom;
			hasStartTrim[nextIndex] = true;
			startTrim[nextIndex] = tangentTo;
			cornerArcs[i] = new Path2DSegmentDto
			{
				From = tangentFrom,
				To = tangentTo,
				Center = center,
				Type = Path2DSegmentType.Arc,
				ArcDirection = arcDirection,
			};
		}

		if (errors.Count > 0)
			return false;

		var concrete = new List<Path2DSegmentDto>(rawSegments.Length * 2);
		for (int i = 0; i < rawSegments.Length; i++)
		{
			var current = rawSegments[i];
			if (current.Type == Path2DSegmentType.Line)
			{
				var actualFrom = hasStartTrim[i] ? startTrim[i] : current.From;
				var actualTo = hasEndTrim[i] ? endTrim[i] : current.To;
				if (PointsAlmostEqual(actualFrom, actualTo))
				{
					var ownerCornerIndex = current.Corner is not null ? i : (i - 1 + rawSegments.Length) % rawSegments.Length;
					var ownerCornerPath = $"{profilePath}.segments[{ownerCornerIndex}].corner";
					errors.Add(Error("FILLET_NOT_FEASIBLE", ownerCornerPath,
						$"{ownerCornerPath} trims away an entire LINE segment."));
					continue;
				}

				concrete.Add(new Path2DSegmentDto
				{
					From = actualFrom,
					To = actualTo,
					Center = default,
					Type = Path2DSegmentType.Line,
					ArcDirection = ArcDirection.CW,
				});
			}
			else
			{
				concrete.Add(new Path2DSegmentDto
				{
					From = current.From,
					To = current.To,
					Center = current.Center,
					Type = current.Type,
					ArcDirection = current.ArcDirection,
				});
			}

			if (cornerArcs[i] is Path2DSegmentDto arc)
				concrete.Add(arc);
		}

		if (errors.Count > 0)
			return false;

		for (int i = 1; i < concrete.Count; i++)
		{
			if (!PointsAlmostEqual(concrete[i - 1].To, concrete[i].From))
			{
				errors.Add(Error("PROFILE_DISCONNECTED", $"{profilePath}.segments[{i}]",
					$"{profilePath} must remain connected after fillet resolution."));
				return false;
			}
		}

		if (profile.Closed && !PointsAlmostEqual(concrete[^1].To, concrete[0].From))
		{
			errors.Add(Error("PROFILE_DISCONNECTED", $"{profilePath}.closed",
				$"{profilePath}.closed requires the resolved path to end where it starts."));
			return false;
		}

		segments = concrete.ToArray();
		return true;
	}

	private static bool TryParseSegment(Path2DSegmentJsonModel segment, string segmentPath,
		out RawSegment raw, out List<ValidationError> errors)
	{
		errors = new List<ValidationError>();
		raw = default;

		var segType = (segment.Type ?? string.Empty).Trim().ToUpperInvariant();
		raw.Type = segType switch
		{
			"LINE" => Path2DSegmentType.Line,
			"ARC" => Path2DSegmentType.Arc,
			"SPLINE" => Path2DSegmentType.Spline,
			_ => 0,
		};

		if (raw.Type == 0)
		{
			errors.Add(Error("INVALID_SEGMENT_TYPE", $"{segmentPath}.type",
				$"{segmentPath}.type must be LINE or ARC."));
			return false;
		}

		if (!TryGetRequiredPoint(segment.From, $"{segmentPath}.from", out raw.From, out var fromError))
			errors.Add(fromError);
		if (!TryGetRequiredPoint(segment.To, $"{segmentPath}.to", out raw.To, out var toError))
			errors.Add(toError);

		raw.Corner = segment.Corner;
		raw.Index = ExtractIndex(segmentPath);

		if (raw.Type == Path2DSegmentType.Arc)
		{
			if (!TryGetRequiredPoint(segment.Center, $"{segmentPath}.center", out raw.Center, out var centerError))
				errors.Add(centerError);

			var arcDir = (segment.ArcDirection ?? string.Empty).Trim().ToUpperInvariant();
			raw.ArcDirection = arcDir switch
			{
				"CW" => ArcDirection.CW,
				"CCW" => ArcDirection.CCW,
				_ => 0,
			};
			if (raw.ArcDirection == 0)
			{
				errors.Add(Error("INVALID_ARC_DIRECTION", $"{segmentPath}.arcDirection",
					$"{segmentPath}.arcDirection must be CW or CCW."));
			}
		}
		else
		{
			raw.Center = default;
			raw.ArcDirection = ArcDirection.CW;
		}

		return errors.Count == 0;
	}

	private static bool TryResolveNativeFillet(Path2DPointDto previousStart, Path2DPointDto corner,
		Path2DPointDto nextEnd, double radius,
		out Path2DPointDto tangentFrom, out Path2DPointDto tangentTo,
		out Path2DPointDto center, out ArcDirection arcDirection)
	{
		tangentFrom = default;
		tangentTo = default;
		center = default;
		arcDirection = default;

		try
		{
			var rc = L1GeometryKernelNative.L1_TryResolveLineLineFillet(
				ref previousStart, ref corner, ref nextEnd, radius,
				out tangentFrom, out tangentTo, out center, out arcDirection);
			return rc == 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static bool TryGetRequiredPoint(Path2DPointJsonModel? point, string path,
		out Path2DPointDto dto, out ValidationError error)
	{
		if (point is null)
		{
			dto = default;
			error = Error("MISSING_POINT", path, $"{path} is required.");
			return false;
		}

		dto = point.ToKernel();
		error = Error(string.Empty, string.Empty, string.Empty);
		return true;
	}

	private static bool PointsAlmostEqual(Path2DPointDto left, Path2DPointDto right)
	{
		var du = left.U - right.U;
		var dv = left.V - right.V;
		return (du * du) + (dv * dv) <= Tolerance * Tolerance;
	}

	private static int ExtractIndex(string segmentPath)
	{
		var start = segmentPath.LastIndexOf('[');
		var end = segmentPath.LastIndexOf(']');
		if (start < 0 || end <= start)
			return -1;
		return int.TryParse(segmentPath[(start + 1)..end], out var index) ? index : -1;
	}

	private static ValidationError Error(string code, string path, string message)
	{
		return new ValidationError
		{
			Code = code,
			Path = path,
			Message = message,
		};
	}

	private struct RawSegment
	{
		public int Index;
		public Path2DSegmentType Type;
		public Path2DPointDto From;
		public Path2DPointDto To;
		public Path2DPointDto Center;
		public ArcDirection ArcDirection;
		public CornerJsonModel? Corner;
	}
}
