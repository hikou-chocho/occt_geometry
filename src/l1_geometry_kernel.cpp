#include "l1_geometry_kernel.h"

#include <algorithm>
#include <cstdlib>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <string>
#include <stdexcept>
#include <vector>

#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeRevol.hxx>
#include <GC_MakeArcOfCircle.hxx>
#include <Geom2dAdaptor_Curve.hxx>
#include <Geom2dGcc.hxx>
#include <Geom2dGcc_Circ2d2TanRad.hxx>
#include <Geom2dGcc_QualifiedCurve.hxx>
#include <Geom2d_Line.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <gp_Ax2.hxx>
#include <gp_Ax1.hxx>
#include <gp_Circ2d.hxx>
#include <gp_Dir2d.hxx>
#include <gp_Dir.hxx>
#include <gp_Lin2d.hxx>
#include <gp_Pnt2d.hxx>
#include <gp_Pnt.hxx>
#include <gp_Vec.hxx>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <StlAPI_Writer.hxx>
#include <TopoDS_Shape.hxx>

enum ErrorCode {
  ERROR_OK                    = 0,
  ERROR_INVALID_ARGUMENT      = 1,
  ERROR_SHAPE_NOT_FOUND       = 2,
  ERROR_FEATURE_NOT_SUPPORTED = 3,
  ERROR_OCCT_EXCEPTION        = 4,
  ERROR_BOOLEAN_FAILED        = 5,
  ERROR_DELTA_FAILED          = 6,
  ERROR_EXPORT_FAILED         = 7,
  ERROR_IMPORT_FAILED         = 8
};

namespace {

class ShapeRegistry {
 public:
  int Add(const TopoDS_Shape& shape) {
    int id = ++next_id_;
    shapes_[id] = shape;
    return id;
  }

  bool Remove(int id) { return shapes_.erase(id) > 0; }

  const TopoDS_Shape* Find(int id) const {
    auto it = shapes_.find(id);
    if (it == shapes_.end()) return nullptr;
    return &it->second;
  }

 private:
  int next_id_ = 0;
  std::map<int, TopoDS_Shape> shapes_;
};

class OcctKernelImpl {
 public:
  ShapeRegistry& Registry() { return registry_; }

 private:
  ShapeRegistry registry_;
};

int MapExceptionToError() { return ERROR_OCCT_EXCEPTION; }

constexpr double kGeomTol = 1.0e-7;
constexpr double kPi      = 3.14159265358979323846;
constexpr double kTwoPi   = 2.0 * kPi;

struct UvPoint { double u, v; };

enum class PathFrameMode { kTurnUv, kPlanarUv };

// ---------------------------------------------------------------------------
// Debug dump
// ---------------------------------------------------------------------------

const char* kDebugPath2dDirEnv      = "L1_DEBUG_PATH2D_DIR";
int         gDebugPath2dDumpCounter = 0;

const char* PathFrameModeName(PathFrameMode mode) {
  return mode == PathFrameMode::kTurnUv ? "turn_uv" : "planar_uv";
}

void DumpPath2dSegmentsForDebug(const Path2DSegmentDto* segments, int segmentCount,
                                PathFrameMode mode) {
  const char* rawDir = std::getenv(kDebugPath2dDirEnv);
  if (!rawDir || *rawDir == '\0') return;

  try {
    std::filesystem::path outDir(rawDir);
    std::filesystem::create_directories(outDir);

    const int dumpIndex = ++gDebugPath2dDumpCounter;
    const std::string fileName =
        std::string("path2d_") + PathFrameModeName(mode) + "_" +
        std::to_string(dumpIndex) + ".csv";

    std::ofstream ofs(outDir / fileName, std::ios::trunc);
    if (!ofs) return;

    ofs << "index,type,from_u,from_v,to_u,to_v,center_u,center_v,arc_dir\n";
    for (int i = 0; i < segmentCount; ++i) {
      const Path2DSegmentDto& s = segments[i];
      ofs << i << "," << s.type << ","
          << s.from.u   << "," << s.from.v   << ","
          << s.to.u     << "," << s.to.v     << ","
          << s.center.u << "," << s.center.v << ","
          << s.arcDirection << "\n";
    }
  } catch (...) {}
}

// ---------------------------------------------------------------------------
// 2D geometry helpers
// ---------------------------------------------------------------------------

double Distance2D(const UvPoint& a, const UvPoint& b) {
  const double du = a.u - b.u, dv = a.v - b.v;
  return std::sqrt(du * du + dv * dv);
}

bool NearlyEqual(const UvPoint& a, const UvPoint& b) {
  return Distance2D(a, b) <= kGeomTol;
}

UvPoint ToUvPoint(const Path2DPointDto& point) {
  return UvPoint{point.u, point.v};
}

Path2DPointDto ToDtoPoint(const UvPoint& point) {
  return Path2DPointDto{point.u, point.v};
}

double Dot2D(const UvPoint& a, const UvPoint& b) {
  return a.u * b.u + a.v * b.v;
}

double Cross2D(const UvPoint& a, const UvPoint& b) {
  return a.u * b.v - a.v * b.u;
}

UvPoint Subtract2D(const UvPoint& a, const UvPoint& b) {
  return UvPoint{a.u - b.u, a.v - b.v};
}

double LengthSquared2D(const UvPoint& vector) {
  return Dot2D(vector, vector);
}

bool PointOnSegment(const UvPoint& start, const UvPoint& end, const UvPoint& point, double* outParameter) {
  const UvPoint edge = Subtract2D(end, start);
  const double lengthSquared = LengthSquared2D(edge);
  if (lengthSquared <= kGeomTol * kGeomTol) return false;

  const UvPoint offset = Subtract2D(point, start);
  const double parameter = Dot2D(offset, edge) / lengthSquared;
  const UvPoint projected{start.u + edge.u * parameter, start.v + edge.v * parameter};
  if (Distance2D(projected, point) > 1.0e-6) return false;
  if (parameter < -1.0e-6 || parameter > 1.0 + 1.0e-6) return false;

  if (outParameter) *outParameter = parameter;
  return true;
}

bool BuildFilletFromLineLine(const UvPoint& previousStart,
                             const UvPoint& corner,
                             const UvPoint& nextEnd,
                             double radius,
                             UvPoint* outTangentFrom,
                             UvPoint* outTangentTo,
                             UvPoint* outCenter,
                             ArcDirection* outArcDirection,
                             int* outErrorCode) {
  if (radius <= kGeomTol) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  if (NearlyEqual(previousStart, corner) || NearlyEqual(corner, nextEnd)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const UvPoint prevDirection = Subtract2D(corner, previousStart);
  const UvPoint nextDirection = Subtract2D(nextEnd, corner);
  const double prevLengthSquared = LengthSquared2D(prevDirection);
  const double nextLengthSquared = LengthSquared2D(nextDirection);
  if (prevLengthSquared <= kGeomTol * kGeomTol || nextLengthSquared <= kGeomTol * kGeomTol) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const double normalizedCross =
      Cross2D(prevDirection, nextDirection) /
      std::sqrt(prevLengthSquared * nextLengthSquared);
  if (std::fabs(normalizedCross) <= 1.0e-9) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  try {
    Handle(Geom2d_Line) prevLine =
        new Geom2d_Line(gp_Lin2d(gp_Pnt2d(previousStart.u, previousStart.v),
                                 gp_Dir2d(prevDirection.u, prevDirection.v)));
    Handle(Geom2d_Line) nextLine =
        new Geom2d_Line(gp_Lin2d(gp_Pnt2d(corner.u, corner.v),
                                 gp_Dir2d(nextDirection.u, nextDirection.v)));

    Geom2dAdaptor_Curve prevAdaptor(prevLine);
    Geom2dAdaptor_Curve nextAdaptor(nextLine);
    Geom2dGcc_Circ2d2TanRad solver(Geom2dGcc::Unqualified(prevAdaptor),
                                   Geom2dGcc::Unqualified(nextAdaptor),
                                   radius, kGeomTol);
    if (!solver.IsDone()) {
      *outErrorCode = ERROR_BOOLEAN_FAILED;
      return false;
    }

    bool   found = false;
    double bestScore = 0.0;
    for (int solutionIndex = 1; solutionIndex <= solver.NbSolutions(); ++solutionIndex) {
      Standard_Real prevParSol = 0.0, prevParArg = 0.0;
      Standard_Real nextParSol = 0.0, nextParArg = 0.0;
      gp_Pnt2d prevTangency;
      gp_Pnt2d nextTangency;
      solver.Tangency1(solutionIndex, prevParSol, prevParArg, prevTangency);
      solver.Tangency2(solutionIndex, nextParSol, nextParArg, nextTangency);

      const UvPoint tangentFrom{prevTangency.X(), prevTangency.Y()};
      const UvPoint tangentTo{nextTangency.X(), nextTangency.Y()};

      double prevParameter = 0.0;
      double nextParameter = 0.0;
      if (!PointOnSegment(previousStart, corner, tangentFrom, &prevParameter)) continue;
      if (!PointOnSegment(corner, nextEnd, tangentTo, &nextParameter)) continue;
      if (prevParameter <= 1.0e-6 || prevParameter >= 1.0 - 1.0e-6) continue;
      if (nextParameter <= 1.0e-6 || nextParameter >= 1.0 - 1.0e-6) continue;

      const gp_Circ2d solution = solver.ThisSolution(solutionIndex);
      const UvPoint center{solution.Location().X(), solution.Location().Y()};
      const UvPoint radialFrom = Subtract2D(tangentFrom, center);
      const UvPoint radialTo = Subtract2D(tangentTo, center);
      const ArcDirection direction = Cross2D(radialFrom, radialTo) >= 0.0 ? ARC_DIR_CCW : ARC_DIR_CW;

      const double score = Distance2D(tangentFrom, corner) + Distance2D(tangentTo, corner);
      if (!found || score < bestScore) {
        found = true;
        bestScore = score;
        *outTangentFrom = tangentFrom;
        *outTangentTo = tangentTo;
        *outCenter = center;
        *outArcDirection = direction;
      }
    }

    *outErrorCode = found ? ERROR_OK : ERROR_BOOLEAN_FAILED;
    return found;
  } catch (...) {
    *outErrorCode = ERROR_OCCT_EXCEPTION;
    return false;
  }
}

gp_Pnt To3DPoint(const UvPoint& uv, const AxisDto& axis, PathFrameMode mode) {
  gp_Pnt origin(axis.origin[0], axis.origin[1], axis.origin[2]);
  gp_Vec offset;
  if (mode == PathFrameMode::kTurnUv) {
    gp_Dir udir(axis.dir[0],  axis.dir[1],  axis.dir[2]);
    gp_Dir vdir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
    offset = gp_Vec(udir) * uv.u + gp_Vec(vdir) * uv.v;
  } else {
    gp_Dir xdir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
    gp_Dir dir (axis.dir[0],  axis.dir[1],  axis.dir[2]);
    gp_Dir ydir = dir.Crossed(xdir);
    offset = gp_Vec(xdir) * uv.u + gp_Vec(ydir) * uv.v;
  }
  return origin.Translated(offset);
}

bool ComputeArcGeometry(const Path2DSegmentDto& segment,
                        double* outRadius, double* outStartAngle, double* outEndAngle,
                        int* outErrorCode) {
  const UvPoint from  {segment.from.u,   segment.from.v};
  const UvPoint to    {segment.to.u,     segment.to.v};
  const UvPoint center{segment.center.u, segment.center.v};

  if (NearlyEqual(from, to)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const double r0 = Distance2D(from, center);
  const double r1 = Distance2D(to,   center);
  if (r0 <= kGeomTol || r1 <= kGeomTol) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const double radiusTolerance = std::max({kGeomTol, r0, r1}) * 1.0e-6;
  if (std::fabs(r0 - r1) > radiusTolerance) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const double a0 = std::atan2(from.v - center.v, from.u - center.u);
  double       a1 = std::atan2(to.v   - center.v, to.u   - center.u);

  double sweep = 0.0;
  if (segment.arcDirection == ARC_DIR_CCW) {
    while (a1 <= a0) a1 += kTwoPi;
    sweep = a1 - a0;
  } else if (segment.arcDirection == ARC_DIR_CW) {
    while (a1 >= a0) a1 -= kTwoPi;
    sweep = a1 - a0;
  } else {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  if (std::fabs(sweep) <= 1.0e-9) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  *outRadius     = r0;
  *outStartAngle = a0;
  *outEndAngle   = a0 + sweep;
  *outErrorCode  = ERROR_OK;
  return true;
}

bool BuildArcEdge(const Path2DSegmentDto& segment,
                  const AxisDto& axis, PathFrameMode mode,
                  TopoDS_Edge* outEdge, int* outErrorCode) {
  double radius = 0.0, startAngle = 0.0, endAngle = 0.0;
  if (!ComputeArcGeometry(segment, &radius, &startAngle, &endAngle, outErrorCode))
    return false;

  const UvPoint from  {segment.from.u,   segment.from.v};
  const UvPoint to    {segment.to.u,     segment.to.v};
  const UvPoint center{segment.center.u, segment.center.v};
  const double  sweep    = endAngle - startAngle;
  const double  midAngle = startAngle + 0.5 * sweep;
  const UvPoint mid{center.u + radius * std::cos(midAngle),
                    center.v + radius * std::sin(midAngle)};

  GC_MakeArcOfCircle arcBuilder(To3DPoint(from, axis, mode),
                                To3DPoint(mid,  axis, mode),
                                To3DPoint(to,   axis, mode));
  if (!arcBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  BRepBuilderAPI_MakeEdge edgeBuilder(arcBuilder.Value());
  if (!edgeBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outEdge      = edgeBuilder.Edge();
  *outErrorCode = ERROR_OK;
  return true;
}

// ---------------------------------------------------------------------------
// Segment validation and face building
// ---------------------------------------------------------------------------

bool ValidateSegments(const Path2DSegmentDto* segments, int segmentCount,
                      int closed, int* outErrorCode) {
  if (!segments || segmentCount <= 0) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const UvPoint start{segments[0].from.u, segments[0].from.v};
  UvPoint current = start;

  for (int i = 0; i < segmentCount; ++i) {
    const Path2DSegmentDto& seg = segments[i];
    const UvPoint from{seg.from.u, seg.from.v};
    const UvPoint to  {seg.to.u,   seg.to.v};

    if (!NearlyEqual(from, current)) {
      *outErrorCode = ERROR_INVALID_ARGUMENT;
      return false;
    }

    if (seg.type == PATH_SEGMENT_LINE) {
      if (NearlyEqual(from, to)) {
        *outErrorCode = ERROR_INVALID_ARGUMENT;
        return false;
      }
    } else if (seg.type == PATH_SEGMENT_ARC) {
      double r = 0.0, a0 = 0.0, a1 = 0.0;
      if (!ComputeArcGeometry(seg, &r, &a0, &a1, outErrorCode)) return false;
    } else {
      *outErrorCode = ERROR_FEATURE_NOT_SUPPORTED;
      return false;
    }

    current = to;
  }

  if (closed && !NearlyEqual(current, start)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildFaceFromSegments(const Path2DSegmentDto* segments, int segmentCount,
                           int closed, const AxisDto& axis, PathFrameMode mode,
                           TopoDS_Face* outFace, int* outErrorCode) {
  if (!ValidateSegments(segments, segmentCount, closed, outErrorCode)) return false;
  DumpPath2dSegmentsForDebug(segments, segmentCount, mode);

  UvPoint current{segments[0].from.u, segments[0].from.v};
  BRepBuilderAPI_MakeWire wireBuilder;

  for (int i = 0; i < segmentCount; ++i) {
    const Path2DSegmentDto& seg = segments[i];
    const UvPoint from{seg.from.u, seg.from.v};
    const UvPoint to  {seg.to.u,   seg.to.v};

    if (!NearlyEqual(from, current)) {
      *outErrorCode = ERROR_INVALID_ARGUMENT;
      return false;
    }

    TopoDS_Edge edge;
    if (seg.type == PATH_SEGMENT_LINE) {
      BRepBuilderAPI_MakeEdge edgeBuilder(To3DPoint(from, axis, mode),
                                          To3DPoint(to,   axis, mode));
      if (!edgeBuilder.IsDone()) {
        *outErrorCode = ERROR_BOOLEAN_FAILED;
        return false;
      }
      edge = edgeBuilder.Edge();
    } else if (seg.type == PATH_SEGMENT_ARC) {
      if (!BuildArcEdge(seg, axis, mode, &edge, outErrorCode)) return false;
    } else {
      *outErrorCode = ERROR_FEATURE_NOT_SUPPORTED;
      return false;
    }

    wireBuilder.Add(edge);
    current = to;
  }

  if (!wireBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  BRepBuilderAPI_MakeFace faceBuilder(wireBuilder.Wire());
  if (!faceBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outFace      = faceBuilder.Face();
  *outErrorCode = ERROR_OK;
  return true;
}

// ---------------------------------------------------------------------------
// Tool builders
// ---------------------------------------------------------------------------

bool BuildTurnTool(const Path2DSegmentDto* segments, int segmentCount, int closed,
                   const AxisDto& axis,
                   TopoDS_Shape* outTool, int* outErrorCode) {
  TopoDS_Face face;
  if (!BuildFaceFromSegments(segments, segmentCount, closed, axis,
                             PathFrameMode::kTurnUv, &face, outErrorCode))
    return false;

  gp_Pnt origin(axis.origin[0], axis.origin[1], axis.origin[2]);
  gp_Dir dir   (axis.dir[0],    axis.dir[1],    axis.dir[2]);
  BRepPrimAPI_MakeRevol revol(face, gp_Ax1(origin, dir), kTwoPi, true);
  if (!revol.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outTool      = revol.Shape();
  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildMillContourTool(const Path2DSegmentDto* segments, int segmentCount, int closed,
                          double depth, const AxisDto& axis,
                          TopoDS_Shape* outTool, int* outErrorCode) {
  if (depth <= 0.0) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  TopoDS_Face face;
  if (!BuildFaceFromSegments(segments, segmentCount, closed, axis,
                             PathFrameMode::kPlanarUv, &face, outErrorCode))
    return false;

  gp_Dir dir(axis.dir[0], axis.dir[1], axis.dir[2]);
  BRepPrimAPI_MakePrism prism(face, gp_Vec(dir) * depth, true, true);
  if (!prism.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outTool      = prism.Shape();
  *outErrorCode = ERROR_OK;
  return true;
}

// ---------------------------------------------------------------------------
// Common boolean cut + common helper
// ---------------------------------------------------------------------------

int ApplyBooleanOp(OcctKernelImpl* impl, int stockId, const TopoDS_Shape& tool,
                   OperationResult* outResult) {
  const TopoDS_Shape* stock = impl->Registry().Find(stockId);
  if (!stock) {
    outResult->errorCode = ERROR_SHAPE_NOT_FOUND;
    return ERROR_SHAPE_NOT_FOUND;
  }

  BRepAlgoAPI_Cut cut(*stock, tool);
  if (!cut.IsDone()) {
    outResult->errorCode = ERROR_BOOLEAN_FAILED;
    return ERROR_BOOLEAN_FAILED;
  }

  BRepAlgoAPI_Common common(*stock, tool);
  if (!common.IsDone()) {
    outResult->errorCode = ERROR_DELTA_FAILED;
    return ERROR_DELTA_FAILED;
  }

  outResult->resultShapeId = impl->Registry().Add(cut.Shape());
  outResult->deltaShapeId  = impl->Registry().Add(common.Shape());
  outResult->removalShapeId = impl->Registry().Add(tool);
  outResult->errorCode     = ERROR_OK;
  return ERROR_OK;
}

}  // namespace

// ===========================================================================
// Public API
// ===========================================================================

void* L1_CreateKernel() {
  try {
    return new OcctKernelImpl();
  } catch (...) {
    return nullptr;
  }
}

int L1_DestroyKernel(void* kernel) {
  if (!kernel) return ERROR_INVALID_ARGUMENT;
  try {
    delete static_cast<OcctKernelImpl*>(kernel);
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_CreateStock(void* kernel, const StockDto* dto, int* outStockId) {
  if (!kernel || !dto || !outStockId) return ERROR_INVALID_ARGUMENT;
  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    gp_Pnt origin(dto->axis.origin[0], dto->axis.origin[1], dto->axis.origin[2]);
    gp_Dir dir   (dto->axis.dir[0],    dto->axis.dir[1],    dto->axis.dir[2]);
    gp_Dir xdir  (dto->axis.xdir[0],   dto->axis.xdir[1],   dto->axis.xdir[2]);
    gp_Ax2 axis(origin, dir, xdir);

    TopoDS_Shape shape;
    switch (dto->type) {
      case STOCK_BOX:
        shape = BRepPrimAPI_MakeBox(axis, dto->p1, dto->p2, dto->p3).Shape();
        break;
      case STOCK_CYLINDER:
        shape = BRepPrimAPI_MakeCylinder(axis, dto->p1, dto->p2).Shape();
        break;
      default:
        return ERROR_INVALID_ARGUMENT;
    }

    *outStockId = impl->Registry().Add(shape);
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_ApplyMillHole(void* kernel, int stockId,
                     const MillHoleFeatureDto* dto,
                     OperationResult* outResult) {
  if (!kernel || !dto || !outResult) return ERROR_INVALID_ARGUMENT;
  outResult->resultShapeId = outResult->deltaShapeId = outResult->removalShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  if (dto->radius <= 0.0 || dto->depth <= 0.0) return ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    gp_Pnt origin(dto->axis.origin[0], dto->axis.origin[1], dto->axis.origin[2]);
    gp_Dir dir   (dto->axis.dir[0],    dto->axis.dir[1],    dto->axis.dir[2]);
    const TopoDS_Shape tool =
        BRepPrimAPI_MakeCylinder(gp_Ax2(origin, dir), dto->radius, dto->depth).Shape();
    return ApplyBooleanOp(impl, stockId, tool, outResult);
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_ApplyPocketRect(void* kernel, int stockId,
                       const PocketRectFeatureDto* dto,
                       OperationResult* outResult) {
  if (!kernel || !dto || !outResult) return ERROR_INVALID_ARGUMENT;
  outResult->resultShapeId = outResult->deltaShapeId = outResult->removalShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  if (dto->width <= 0.0 || dto->height <= 0.0 || dto->depth <= 0.0)
    return ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    gp_Pnt origin(dto->axis.origin[0], dto->axis.origin[1], dto->axis.origin[2]);
    gp_Dir dir   (dto->axis.dir[0],    dto->axis.dir[1],    dto->axis.dir[2]);
    gp_Dir xdir  (dto->axis.xdir[0],   dto->axis.xdir[1],   dto->axis.xdir[2]);
    gp_Dir ydir = dir.Crossed(xdir);
    gp_Pnt corner = origin.Translated(
        gp_Vec(xdir) * (-0.5 * dto->width) + gp_Vec(ydir) * (-0.5 * dto->height));
    const TopoDS_Shape tool =
        BRepPrimAPI_MakeBox(gp_Ax2(corner, dir, xdir),
                            dto->width, dto->height, dto->depth).Shape();
    return ApplyBooleanOp(impl, stockId, tool, outResult);
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_ApplyTurnOd(void* kernel, int stockId,
                   const AxisDto* axis,
                   const Path2DSegmentDto* segments, int segmentCount, int closed,
                   OperationResult* outResult) {
  if (!kernel || !axis || !segments || !outResult) return ERROR_INVALID_ARGUMENT;
  outResult->resultShapeId = outResult->deltaShapeId = outResult->removalShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    TopoDS_Shape tool;
    int buildError = ERROR_OK;
    if (!BuildTurnTool(segments, segmentCount, closed, *axis, &tool, &buildError)) {
      outResult->errorCode = buildError;
      return buildError;
    }
    return ApplyBooleanOp(impl, stockId, tool, outResult);
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_ApplyTurnId(void* kernel, int stockId,
                   const AxisDto* axis,
                   const Path2DSegmentDto* segments, int segmentCount, int closed,
                   OperationResult* outResult) {
  if (!kernel || !axis || !segments || !outResult) return ERROR_INVALID_ARGUMENT;
  outResult->resultShapeId = outResult->deltaShapeId = outResult->removalShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    TopoDS_Shape tool;
    int buildError = ERROR_OK;
    if (!BuildTurnTool(segments, segmentCount, closed, *axis, &tool, &buildError)) {
      outResult->errorCode = buildError;
      return buildError;
    }
    return ApplyBooleanOp(impl, stockId, tool, outResult);
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_ApplyMillContour(void* kernel, int stockId,
                        const AxisDto* axis,
                        const Path2DSegmentDto* segments, int segmentCount, int closed,
                        double depth,
                        OperationResult* outResult) {
  if (!kernel || !axis || !segments || !outResult) return ERROR_INVALID_ARGUMENT;
  outResult->resultShapeId = outResult->deltaShapeId = outResult->removalShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    TopoDS_Shape tool;
    int buildError = ERROR_OK;
    if (!BuildMillContourTool(segments, segmentCount, closed, depth, *axis,
                              &tool, &buildError)) {
      outResult->errorCode = buildError;
      return buildError;
    }
    return ApplyBooleanOp(impl, stockId, tool, outResult);
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_DeleteShape(void* kernel, int shapeId) {
  if (!kernel) return ERROR_INVALID_ARGUMENT;
  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    return impl->Registry().Remove(shapeId) ? ERROR_OK : ERROR_SHAPE_NOT_FOUND;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_TryResolveLineLineFillet(const Path2DPointDto* previousStart,
                                const Path2DPointDto* corner,
                                const Path2DPointDto* nextEnd,
                                double radius,
                                Path2DPointDto* outTangentFrom,
                                Path2DPointDto* outTangentTo,
                                Path2DPointDto* outCenter,
                                ArcDirection* outArcDirection) {
  if (!previousStart || !corner || !nextEnd ||
      !outTangentFrom || !outTangentTo || !outCenter || !outArcDirection) {
    return ERROR_INVALID_ARGUMENT;
  }

  try {
    UvPoint tangentFrom{};
    UvPoint tangentTo{};
    UvPoint centerPoint{};
    int errorCode = ERROR_OK;
    if (!BuildFilletFromLineLine(ToUvPoint(*previousStart),
                                 ToUvPoint(*corner),
                                 ToUvPoint(*nextEnd),
                                 radius,
                                 &tangentFrom,
                                 &tangentTo,
                                 &centerPoint,
                                 outArcDirection,
                                 &errorCode)) {
      return errorCode;
    }

    *outTangentFrom = ToDtoPoint(tangentFrom);
    *outTangentTo = ToDtoPoint(tangentTo);
    *outCenter = ToDtoPoint(centerPoint);
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_ImportStepAsShape(void* kernel,
                         const char* filePathUtf8,
                         int* outShapeId) {
  if (!kernel || !filePathUtf8 || !outShapeId) return ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);

    STEPControl_Reader reader;
    if (reader.ReadFile(filePathUtf8) != IFSelect_RetDone)
      return ERROR_IMPORT_FAILED;

    if (!reader.TransferRoots())
      return ERROR_IMPORT_FAILED;

    const TopoDS_Shape shape = reader.OneShape();
    if (shape.IsNull())
      return ERROR_IMPORT_FAILED;

    *outShapeId = impl->Registry().Add(shape);
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_ExportShape(void* kernel, int shapeId,
                   const OutputOptions* opt,
                   const char* filePathUtf8) {
  if (!kernel || !opt || !filePathUtf8) return ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    const TopoDS_Shape* shape = impl->Registry().Find(shapeId);
    if (!shape) return ERROR_SHAPE_NOT_FOUND;

    if (opt->format == OUT_STEP) {
      STEPControl_Writer writer;
      if (writer.Transfer(*shape, STEPControl_AsIs) != IFSelect_RetDone)
        return ERROR_EXPORT_FAILED;
      if (writer.Write(filePathUtf8) != IFSelect_RetDone)
        return ERROR_EXPORT_FAILED;
      return ERROR_OK;
    }

    if (opt->format == OUT_STL) {
      BRepMesh_IncrementalMesh mesher(*shape, opt->linearDeflection,
                                      opt->parallel != 0, opt->angularDeflection, true);
      if (!mesher.IsDone()) return ERROR_EXPORT_FAILED;
      StlAPI_Writer writer;
      writer.Write(*shape, filePathUtf8);
      return ERROR_OK;
    }

    return ERROR_INVALID_ARGUMENT;
  } catch (...) {
    return MapExceptionToError();
  }
}
