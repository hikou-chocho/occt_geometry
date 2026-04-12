using System.IO;
using System.Linq;
using L1GeometryAdapter;

namespace L1GeometryAdapter.Tests;

[TestClass]
public sealed class FilletTests
{
	[TestMethod]
	public void ResolvesCcwFilletCorner()
	{
		var profile = new Path2DProfileJsonModel
		{
			Closed = false,
			Segments =
			{
				Line(0, 0, 10, 0, cornerRadius: 2),
				Line(10, 0, 10, 10),
			},
		};

		var segments = profile.ToKernelSegments();
		Assert.AreEqual(3, segments.Length);
		Assert.AreEqual(Path2DSegmentType.Line, segments[0].Type);
		AssertNearly(8, segments[0].To.U);
		AssertNearly(0, segments[0].To.V);
		Assert.AreEqual(Path2DSegmentType.Arc, segments[1].Type);
		Assert.AreEqual(ArcDirection.CCW, segments[1].ArcDirection);
		AssertNearly(8, segments[1].Center.U);
		AssertNearly(2, segments[1].Center.V);
		Assert.AreEqual(Path2DSegmentType.Line, segments[2].Type);
		AssertNearly(10, segments[2].From.U);
		AssertNearly(2, segments[2].From.V);
	}

	[TestMethod]
	public void ResolvesCwFilletCorner()
	{
		var profile = new Path2DProfileJsonModel
		{
			Closed = false,
			Segments =
			{
				Line(0, 0, 10, 0, cornerRadius: 2),
				Line(10, 0, 10, -10),
			},
		};

		var segments = profile.ToKernelSegments();
		Assert.AreEqual(3, segments.Length);
		Assert.AreEqual(Path2DSegmentType.Arc, segments[1].Type);
		Assert.AreEqual(ArcDirection.CW, segments[1].ArcDirection);
		AssertNearly(8, segments[1].Center.U);
		AssertNearly(-2, segments[1].Center.V);
		AssertNearly(10, segments[2].From.U);
		AssertNearly(-2, segments[2].From.V);
	}

	[TestMethod]
	public void PreservesExplicitArcAndResolvesCorner()
	{
		var profile = new Path2DProfileJsonModel
		{
			Closed = false,
			Segments =
			{
				Arc(0, 0, 5, 5, 0, 5, ArcDirection.CCW),
				Line(5, 5, 10, 5, cornerRadius: 1),
				Line(10, 5, 10, 10),
			},
		};

		var segments = profile.ToKernelSegments();
		Assert.AreEqual(4, segments.Length);
		Assert.AreEqual(Path2DSegmentType.Arc, segments[0].Type);
		Assert.AreEqual(Path2DSegmentType.Arc, segments[2].Type);
		Assert.AreEqual(ArcDirection.CCW, segments[2].ArcDirection);
	}

	[TestMethod]
	public void WrapsClosedProfileFillet()
	{
		var profile = new Path2DProfileJsonModel
		{
			Closed = true,
			Segments =
			{
				Line(0, 0, 10, 0),
				Line(10, 0, 10, 10),
				Line(10, 10, 0, 10),
				Line(0, 10, 0, 0, cornerRadius: 1),
			},
		};

		var segments = profile.ToKernelSegments();
		AssertNearly(1, segments[0].From.U);
		AssertNearly(0, segments[0].From.V);
		Assert.AreEqual(Path2DSegmentType.Arc, segments[^1].Type);
		AssertNearly(1, segments[^1].Center.U);
		AssertNearly(1, segments[^1].Center.V);
	}

	[TestMethod]
	public void ValidatesCornerErrors()
	{
		AssertHasError(CreateJob(new Path2DProfileJsonModel
		{
			Closed = false,
			Segments =
			{
				Line(0, 0, 5, 0, cornerRadius: 1),
			},
		}), "OPEN_PROFILE_TRAILING_CORNER");

		AssertHasError(CreateJob(new Path2DProfileJsonModel
		{
			Closed = true,
			Segments =
			{
				Line(0, 0, 1, 0, cornerRadius: 5),
				Line(1, 0, 1, 1),
				Line(1, 1, 0, 0),
			},
		}), "FILLET_NOT_FEASIBLE");

		AssertHasError(CreateJob(new Path2DProfileJsonModel
		{
			Closed = true,
			Segments =
			{
				ArcSegmentWithCorner(),
				Line(5, 5, 10, 5),
				Line(10, 5, 0, 0),
			},
		}), "CORNER_ON_NON_LINE");

		AssertHasError(CreateJob(new Path2DProfileJsonModel
		{
			Closed = true,
			Segments =
			{
				Line(0, 0, 10, 0, cornerRadius: 1),
				Arc(10, 0, 10, 10, 5, 5, ArcDirection.CCW),
				Line(10, 10, 0, 0),
			},
		}), "UNSUPPORTED_CORNER_NEIGHBOR");
	}

	[TestMethod]
	public void ExecutesExplicitArcTurn()
	{
		using var kernel = new L1Kernel();
		var stock = new StockDto
		{
			Type = StockType.Cylinder,
			P1 = 30,
			P2 = 80,
			Axis = Axis().ToKernel(),
		};

		int stockId = kernel.CreateStock(ref stock);

		var profile = new Path2DProfileJsonModel
		{
			Closed = false,
			Segments =
			{
				Line(0, 0, 20, 0),
				Arc(20, 0, 30, 10, 20, 10, ArcDirection.CCW),
				Line(30, 10, 30, 20),
				Line(30, 20, 0, 20),
			},
		};

		var result = kernel.ApplyTurnOd(stockId, Axis().ToKernel(), profile.ToKernelSegments(), profile.Closed);
		Assert.AreNotEqual(0, result.ResultShapeId);
	}

	[TestMethod]
	public void ExecutesFilletMillContourExport()
	{
		using var kernel = new L1Kernel();
		var stock = new StockDto
		{
			Type = StockType.Box,
			P1 = 40,
			P2 = 40,
			P3 = 20,
			Axis = Axis().ToKernel(),
		};

		int stockId = kernel.CreateStock(ref stock);

		var profile = new Path2DProfileJsonModel
		{
			Closed = true,
			Segments =
			{
				Line(-10, -10, 10, -10, cornerRadius: 2),
				Line(10, -10, 10, 10, cornerRadius: 2),
				Line(10, 10, -10, 10, cornerRadius: 2),
				Line(-10, 10, -10, -10, cornerRadius: 2),
			},
		};

		var result = kernel.ApplyMillContour(stockId, Axis().ToKernel(), profile.ToKernelSegments(), profile.Closed, depth: 5);
		Assert.AreNotEqual(0, result.ResultShapeId);

		var outDir = Path.Combine(AppContext.BaseDirectory, "fillet-test-output");
		Directory.CreateDirectory(outDir);
		var stepPath = Path.Combine(outDir, "mill_contour_fillet_result.step");
		var stlPath = Path.Combine(outDir, "mill_contour_fillet_result.stl");

		var stepOptions = new OutputOptions
		{
			Format = OutputFormat.Step,
			LinearDeflection = 0.1,
			AngularDeflection = 0.5,
			Parallel = 1,
		};
		var stlOptions = stepOptions;
		stlOptions.Format = OutputFormat.Stl;

		kernel.ExportShape(result.ResultShapeId, stepOptions, stepPath);
		kernel.ExportShape(result.ResultShapeId, stlOptions, stlPath);

		Assert.IsTrue(File.Exists(stepPath));
		Assert.IsTrue(File.Exists(stlPath));
	}

	private static JobJsonModel CreateJob(Path2DProfileJsonModel profile)
	{
		return new JobJsonModel
		{
			Meta = new MetaJsonModel { SessionId = "sess-fillet-tests" },
			Stock = new StockJsonModel
			{
				Type = "BOX",
				P1 = 20,
				P2 = 20,
				P3 = 10,
				Axis = Axis(),
			},
			Features =
			{
				new FeatureJsonModel
				{
					Type = "MILL_CONTOUR",
					MillContour = new MillContourJsonModel
					{
						Depth = 2,
						Axis = Axis(),
						Profile = profile,
					},
				},
			},
			Output = new OutputJsonModel
			{
				Dir = "out",
				StepFile = "result.step",
				StlFile = "result.stl",
				DeltaStepFile = "delta.step",
				DeltaStlFile = "delta.stl",
			},
		};
	}

	private static AxisJsonModel Axis()
	{
		return new AxisJsonModel
		{
			Origin = new[] { 0.0, 0.0, 0.0 },
			Dir = new[] { 0.0, 0.0, 1.0 },
			Xdir = new[] { 1.0, 0.0, 0.0 },
		};
	}

	private static Path2DSegmentJsonModel Line(double fromU, double fromV, double toU, double toV, double? cornerRadius = null)
	{
		return new Path2DSegmentJsonModel
		{
			Type = "LINE",
			From = Point(fromU, fromV),
			To = Point(toU, toV),
			Corner = cornerRadius is null ? null : new CornerJsonModel { Radius = cornerRadius.Value },
		};
	}

	private static Path2DSegmentJsonModel Arc(double fromU, double fromV, double toU, double toV, double centerU, double centerV, ArcDirection direction)
	{
		return new Path2DSegmentJsonModel
		{
			Type = "ARC",
			From = Point(fromU, fromV),
			To = Point(toU, toV),
			Center = Point(centerU, centerV),
			ArcDirection = direction == ArcDirection.CCW ? "CCW" : "CW",
		};
	}

	private static Path2DSegmentJsonModel ArcSegmentWithCorner()
	{
		return new Path2DSegmentJsonModel
		{
			Type = "ARC",
			From = Point(0, 0),
			To = Point(5, 5),
			Center = Point(0, 5),
			ArcDirection = "CCW",
			Corner = new CornerJsonModel { Radius = 1 },
		};
	}

	private static Path2DPointJsonModel Point(double u, double v) => new() { U = u, V = v };

	private static void AssertHasError(JobJsonModel job, string code)
	{
		var errors = JobValidator.Validate(job);
		Assert.IsTrue(errors.Any(error => error.Code == code), $"expected validation error {code}");
	}

	private static void AssertNearly(double expected, double actual)
	{
		Assert.IsTrue(Math.Abs(expected - actual) <= 1.0e-6, $"expected {expected}, actual {actual}");
	}
}
