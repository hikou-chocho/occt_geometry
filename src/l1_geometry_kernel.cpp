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
#include <TopoDS_Edge.hxx>
#include <TopoDS_Face.hxx>
#include <gp_Ax2.hxx>
#include <gp_Ax1.hxx>
#include <gp_Dir.hxx>
#include <gp_Pnt.hxx>
#include <gp_Vec.hxx>
#include <STEPControl_Writer.hxx>
#include <StlAPI_Writer.hxx>
#include <TopoDS_Shape.hxx>

enum ErrorCode {
  ERROR_OK = 0,
  ERROR_INVALID_ARGUMENT = 1,
  ERROR_SHAPE_NOT_FOUND = 2,
  ERROR_FEATURE_NOT_SUPPORTED = 3,
  ERROR_OCCT_EXCEPTION = 4,
  ERROR_BOOLEAN_FAILED = 5,
  ERROR_DELTA_FAILED = 6,
  ERROR_EXPORT_FAILED = 7
};

namespace {
class ShapeRegistry {
 public:
  int Add(const TopoDS_Shape& shape) {
    int id = ++next_id_;
    shapes_[id] = shape;
    return id;
  }

  bool Remove(int id) {
    return shapes_.erase(id) > 0;
  }

  const TopoDS_Shape* Find(int id) const {
    auto it = shapes_.find(id);
    if (it == shapes_.end()) {
      return nullptr;
    }
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

int MapExceptionToError() {
  return ERROR_OCCT_EXCEPTION;
}

constexpr double kGeomTol = 1.0e-7;
constexpr double kPi = 3.14159265358979323846;
constexpr double kTwoPi = 2.0 * kPi;

struct UvPoint {
  double u;
  double v;
};

enum class PathFrameMode {
  kTurnUv,
  kPlanarUv
};

const char* kDebugPath2dDirEnv = "L1_DEBUG_PATH2D_DIR";
int gDebugPath2dDumpCounter = 0;

const char* PathFrameModeName(PathFrameMode mode) {
  return mode == PathFrameMode::kTurnUv ? "turn_uv" : "planar_uv";
}

void DumpPath2dSegmentsForDebug(const Path2DProfileDto& profile, PathFrameMode mode) {
  const char* rawDir = std::getenv(kDebugPath2dDirEnv);
  if (!rawDir || *rawDir == '\0') {
    return;
  }

  try {
    std::filesystem::path outDir(rawDir);
    std::filesystem::create_directories(outDir);

    const int dumpIndex = ++gDebugPath2dDumpCounter;
    const std::string fileName =
        std::string("path2d_") + PathFrameModeName(mode) + "_" + std::to_string(dumpIndex) + ".csv";
    const std::filesystem::path filePath = outDir / fileName;

    std::ofstream ofs(filePath, std::ios::trunc);
    if (!ofs) {
      return;
    }

    ofs << "index,type,from_u,from_v,to_u,to_v,center_u,center_v,arc_dir\n";
    for (int index = 0; index < profile.segmentCount; ++index) {
      const Path2DSegmentDto& segment = profile.segments[index];
      ofs << index << "," << segment.type << ","
          << segment.from.u << "," << segment.from.v << ","
          << segment.to.u << "," << segment.to.v << ","
          << segment.center.u << "," << segment.center.v << ","
          << segment.arcDirection << "\n";
    }
  } catch (...) {
  }
}

double Distance2D(const UvPoint& a, const UvPoint& b) {
  const double du = a.u - b.u;
  const double dv = a.v - b.v;
  return std::sqrt(du * du + dv * dv);
}

bool NearlyEqual(const UvPoint& a, const UvPoint& b) {
  return Distance2D(a, b) <= kGeomTol;
}

gp_Pnt To3DPoint(const UvPoint& uv, const AxisDto& axis, PathFrameMode mode) {
  gp_Pnt origin(axis.origin[0], axis.origin[1], axis.origin[2]);
  gp_Vec offset;
  if (mode == PathFrameMode::kTurnUv) {
    gp_Dir udir(axis.dir[0], axis.dir[1], axis.dir[2]);
    gp_Dir vdir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
    offset = gp_Vec(udir) * uv.u + gp_Vec(vdir) * uv.v;
  } else {
    gp_Dir xdir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
    gp_Dir dir(axis.dir[0], axis.dir[1], axis.dir[2]);
    gp_Dir ydir = dir.Crossed(xdir);
    offset = gp_Vec(xdir) * uv.u + gp_Vec(ydir) * uv.v;
  }
  return origin.Translated(offset);
}

void GetFrameDirections(const AxisDto& axis, PathFrameMode mode, gp_Dir* outU, gp_Dir* outV) {
  if (mode == PathFrameMode::kTurnUv) {
    *outU = gp_Dir(axis.dir[0], axis.dir[1], axis.dir[2]);
    *outV = gp_Dir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
    return;
  }

  const gp_Dir dir(axis.dir[0], axis.dir[1], axis.dir[2]);
  const gp_Dir xdir(axis.xdir[0], axis.xdir[1], axis.xdir[2]);
  *outU = xdir;
  *outV = dir.Crossed(xdir);
}

bool ComputeArcGeometry(const Path2DSegmentDto& segment,
                        double* outRadius,
                        double* outStartAngle,
                        double* outEndAngle,
                        int* outErrorCode) {
  const UvPoint from{segment.from.u, segment.from.v};
  const UvPoint to{segment.to.u, segment.to.v};
  const UvPoint center{segment.center.u, segment.center.v};

  if (NearlyEqual(from, to)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const double r0 = Distance2D(from, center);
  const double r1 = Distance2D(to, center);
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
  double a1 = std::atan2(to.v - center.v, to.u - center.u);

  double sweep = 0.0;
  if (segment.arcDirection == ARC_DIR_CCW) {
    while (a1 <= a0) {
      a1 += kTwoPi;
    }
    sweep = a1 - a0;
  } else if (segment.arcDirection == ARC_DIR_CW) {
    while (a1 >= a0) {
      a1 -= kTwoPi;
    }
    sweep = a1 - a0;
  } else {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  if (std::fabs(sweep) <= 1.0e-9) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  *outRadius = r0;
  *outStartAngle = a0;
  *outEndAngle = a0 + sweep;

  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildArcEdge(const Path2DSegmentDto& segment,
                  const AxisDto& axis,
                  PathFrameMode mode,
                  TopoDS_Edge* outEdge,
                  int* outErrorCode) {
  double radius = 0.0;
  double startAngle = 0.0;
  double endAngle = 0.0;
  if (!ComputeArcGeometry(segment, &radius, &startAngle, &endAngle, outErrorCode)) {
    return false;
  }

  const UvPoint from{segment.from.u, segment.from.v};
  const UvPoint to{segment.to.u, segment.to.v};
  const UvPoint center{segment.center.u, segment.center.v};
  const double sweep = endAngle - startAngle;
  const double midAngle = startAngle + 0.5 * sweep;
  const UvPoint mid{center.u + radius * std::cos(midAngle), center.v + radius * std::sin(midAngle)};

  const gp_Pnt p0 = To3DPoint(from, axis, mode);
  const gp_Pnt p1 = To3DPoint(mid, axis, mode);
  const gp_Pnt p2 = To3DPoint(to, axis, mode);

  GC_MakeArcOfCircle arcBuilder(p0, p1, p2);
  if (!arcBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  BRepBuilderAPI_MakeEdge edgeBuilder(arcBuilder.Value());
  if (!edgeBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outEdge = edgeBuilder.Edge();
  *outErrorCode = ERROR_OK;
  return true;
}

bool ValidateClosedPathProfile(const Path2DProfileDto& profile,
                               int* outErrorCode) {
  if (profile.type != PROFILE_PATH_2D || profile.plane != PROFILE_PLANE_UV) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }
  if (!profile.closed || profile.segmentCount <= 0 || profile.segmentCount > L1_PATH2D_SEGMENT_MAX) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  const UvPoint start{profile.start.u, profile.start.v};
  UvPoint current = start;

  for (int index = 0; index < profile.segmentCount; ++index) {
    const Path2DSegmentDto& segment = profile.segments[index];
    const UvPoint from{segment.from.u, segment.from.v};
    const UvPoint to{segment.to.u, segment.to.v};

    if (!NearlyEqual(from, current)) {
      *outErrorCode = ERROR_INVALID_ARGUMENT;
      return false;
    }

    if (segment.type == PATH_SEGMENT_LINE) {
      if (NearlyEqual(from, to)) {
        *outErrorCode = ERROR_INVALID_ARGUMENT;
        return false;
      }
    } else if (segment.type == PATH_SEGMENT_ARC) {
      double radius = 0.0;
      double startAngle = 0.0;
      double endAngle = 0.0;
      if (!ComputeArcGeometry(segment, &radius, &startAngle, &endAngle, outErrorCode)) {
        return false;
      }
    } else {
      *outErrorCode = ERROR_FEATURE_NOT_SUPPORTED;
      return false;
    }

    current = to;
  }

  if (!NearlyEqual(current, start)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildFaceFromPath(const Path2DProfileDto& profile,
                       const AxisDto& axis,
                       PathFrameMode mode,
                       TopoDS_Face* outFace,
                       int* outErrorCode) {
  if (!ValidateClosedPathProfile(profile, outErrorCode)) {
    return false;
  }
  DumpPath2dSegmentsForDebug(profile, mode);

  const UvPoint start{profile.start.u, profile.start.v};
  UvPoint current = start;

  BRepBuilderAPI_MakeWire wireBuilder;
  for (int index = 0; index < profile.segmentCount; ++index) {
    const Path2DSegmentDto& segment = profile.segments[index];
    const UvPoint from{segment.from.u, segment.from.v};
    const UvPoint to{segment.to.u, segment.to.v};

    if (!NearlyEqual(from, current)) {
      *outErrorCode = ERROR_INVALID_ARGUMENT;
      return false;
    }

    TopoDS_Edge edge;
    if (segment.type == PATH_SEGMENT_LINE) {
      const gp_Pnt p0 = To3DPoint(from, axis, mode);
      const gp_Pnt p1 = To3DPoint(to, axis, mode);
      BRepBuilderAPI_MakeEdge edgeBuilder(p0, p1);
      if (!edgeBuilder.IsDone()) {
        *outErrorCode = ERROR_BOOLEAN_FAILED;
        return false;
      }
      edge = edgeBuilder.Edge();
    } else if (segment.type == PATH_SEGMENT_ARC) {
      if (!BuildArcEdge(segment, axis, mode, &edge, outErrorCode)) {
        return false;
      }
    } else {
      *outErrorCode = ERROR_FEATURE_NOT_SUPPORTED;
      return false;
    }

    wireBuilder.Add(edge);
    current = to;
  }

  if (!NearlyEqual(current, start)) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  if (!wireBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  const TopoDS_Wire wire = wireBuilder.Wire();
  BRepBuilderAPI_MakeFace faceBuilder(wire);
  if (!faceBuilder.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outFace = faceBuilder.Face();
  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildTurnToolFromPath(const Path2DProfileDto& profile,
                           const AxisDto& axis,
                           TopoDS_Shape* outTool,
                           int* outErrorCode) {
  TopoDS_Face face;
  if (!BuildFaceFromPath(profile, axis, PathFrameMode::kTurnUv, &face, outErrorCode)) {
    return false;
  }

  gp_Pnt origin(axis.origin[0], axis.origin[1], axis.origin[2]);
  gp_Dir dir(axis.dir[0], axis.dir[1], axis.dir[2]);
  BRepPrimAPI_MakeRevol revol(face, gp_Ax1(origin, dir), kTwoPi, true);
  if (!revol.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outTool = revol.Shape();
  *outErrorCode = ERROR_OK;
  return true;
}

bool BuildMillContourToolFromPath(const Path2DProfileDto& profile,
                                  double depth,
                                  const AxisDto& axis,
                                  TopoDS_Shape* outTool,
                                  int* outErrorCode) {
  if (depth <= 0.0) {
    *outErrorCode = ERROR_INVALID_ARGUMENT;
    return false;
  }

  TopoDS_Face face;
  if (!BuildFaceFromPath(profile, axis, PathFrameMode::kPlanarUv, &face, outErrorCode)) {
    return false;
  }

  gp_Dir dir(axis.dir[0], axis.dir[1], axis.dir[2]);
  gp_Vec prismVec = gp_Vec(dir) * depth;
  BRepPrimAPI_MakePrism prism(face, prismVec, true, true);
  if (!prism.IsDone()) {
    *outErrorCode = ERROR_BOOLEAN_FAILED;
    return false;
  }

  *outTool = prism.Shape();
  *outErrorCode = ERROR_OK;
  return true;
}
}  // namespace

void* L1_CreateKernel() {
  try {
    return new OcctKernelImpl();
  } catch (...) {
    return nullptr;
  }
}

int L1_DestroyKernel(void* kernel) {
  if (!kernel) {
    return ERROR_INVALID_ARGUMENT;
  }
  try {
    delete static_cast<OcctKernelImpl*>(kernel);
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_CreateStock(void* kernel, const StockDto* dto, int* outStockId) {
  if (!kernel || !dto || !outStockId) {
    return ERROR_INVALID_ARGUMENT;
  }
  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    gp_Pnt origin(dto->axis.origin[0], dto->axis.origin[1], dto->axis.origin[2]);
    gp_Dir dir(dto->axis.dir[0], dto->axis.dir[1], dto->axis.dir[2]);
    gp_Dir xdir(dto->axis.xdir[0], dto->axis.xdir[1], dto->axis.xdir[2]);
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

int L1_ApplyFeature(void* kernel,
                    int stockId,
                    const FeatureDto* feature,
                    OperationResult* outResult) {
  if (!kernel || !feature || !outResult) {
    return ERROR_INVALID_ARGUMENT;
  }

  outResult->resultShapeId = 0;
  outResult->deltaShapeId = 0;
  outResult->errorCode = ERROR_INVALID_ARGUMENT;

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    const TopoDS_Shape* stock = impl->Registry().Find(stockId);
    if (!stock) {
      outResult->errorCode = ERROR_SHAPE_NOT_FOUND;
      return ERROR_SHAPE_NOT_FOUND;
    }

    TopoDS_Shape tool;
    switch (feature->type) {
      case FEAT_MILL_HOLE: {
        const MillHoleFeatureDto& millHole = feature->u.millHole;
        if (millHole.radius <= 0.0 || millHole.depth <= 0.0) {
          outResult->errorCode = ERROR_INVALID_ARGUMENT;
          return ERROR_INVALID_ARGUMENT;
        }

        gp_Pnt origin(millHole.axis.origin[0], millHole.axis.origin[1], millHole.axis.origin[2]);
        gp_Dir dir(millHole.axis.dir[0], millHole.axis.dir[1], millHole.axis.dir[2]);
        tool = BRepPrimAPI_MakeCylinder(gp_Ax2(origin, dir), millHole.radius, millHole.depth).Shape();
        break;
      }
      case FEAT_POCKET_RECT: {
        const PocketRectFeatureDto& pocket = feature->u.pocketRect;
        if (pocket.width <= 0.0 || pocket.height <= 0.0 || pocket.depth <= 0.0) {
          outResult->errorCode = ERROR_INVALID_ARGUMENT;
          return ERROR_INVALID_ARGUMENT;
        }

        gp_Pnt origin(pocket.axis.origin[0], pocket.axis.origin[1], pocket.axis.origin[2]);
        gp_Dir dir(pocket.axis.dir[0], pocket.axis.dir[1], pocket.axis.dir[2]);
        gp_Dir xdir(pocket.axis.xdir[0], pocket.axis.xdir[1], pocket.axis.xdir[2]);
        gp_Dir ydir = dir.Crossed(xdir);
        gp_Vec shift = gp_Vec(xdir) * (-0.5 * pocket.width) +
                       gp_Vec(ydir) * (-0.5 * pocket.height);
        gp_Pnt corner = origin.Translated(shift);
        tool = BRepPrimAPI_MakeBox(gp_Ax2(corner, dir, xdir), pocket.width, pocket.height,
                                   pocket.depth)
                   .Shape();
        break;
      }
      case FEAT_TURN_OD: {
        const TurnOdFeatureDto& turn = feature->u.turnOd;
        int buildError = ERROR_OK;
        if (!BuildTurnToolFromPath(turn.profile, turn.axis, &tool, &buildError)) {
          outResult->errorCode = buildError;
          return buildError;
        }
        break;
      }
      case FEAT_TURN_ID: {
        const TurnIdFeatureDto& turn = feature->u.turnId;
        int buildError = ERROR_OK;
        if (!BuildTurnToolFromPath(turn.profile, turn.axis, &tool, &buildError)) {
          outResult->errorCode = buildError;
          return buildError;
        }
        break;
      }
      case FEAT_MILL_CONTOUR: {
        const MillContourFeatureDto& millContour = feature->u.millContour;
        int buildError = ERROR_OK;
        if (!BuildMillContourToolFromPath(
                millContour.profile, millContour.depth, millContour.axis, &tool, &buildError)) {
          outResult->errorCode = buildError;
          return buildError;
        }
        break;
      }
      default:
        outResult->errorCode = ERROR_FEATURE_NOT_SUPPORTED;
        return ERROR_FEATURE_NOT_SUPPORTED;
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
    outResult->deltaShapeId = impl->Registry().Add(common.Shape());
    outResult->errorCode = ERROR_OK;
    return ERROR_OK;
  } catch (...) {
    outResult->errorCode = ERROR_OCCT_EXCEPTION;
    return MapExceptionToError();
  }
}

int L1_DeleteShape(void* kernel, int shapeId) {
  if (!kernel) {
    return ERROR_INVALID_ARGUMENT;
  }

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    if (!impl->Registry().Remove(shapeId)) {
      return ERROR_SHAPE_NOT_FOUND;
    }
    return ERROR_OK;
  } catch (...) {
    return MapExceptionToError();
  }
}

int L1_ExportShape(void* kernel,
                   int shapeId,
                   const OutputOptions* opt,
                   const char* filePathUtf8) {
  if (!kernel || !opt || !filePathUtf8) {
    return ERROR_INVALID_ARGUMENT;
  }

  try {
    auto* impl = static_cast<OcctKernelImpl*>(kernel);
    const TopoDS_Shape* shape = impl->Registry().Find(shapeId);
    if (!shape) {
      return ERROR_SHAPE_NOT_FOUND;
    }

    if (opt->format == OUT_STEP) {
      STEPControl_Writer writer;
      if (writer.Transfer(*shape, STEPControl_AsIs) != IFSelect_RetDone) {
        return ERROR_EXPORT_FAILED;
      }
      if (writer.Write(filePathUtf8) != IFSelect_RetDone) {
        return ERROR_EXPORT_FAILED;
      }
      return ERROR_OK;
    }

    if (opt->format == OUT_STL) {
      BRepMesh_IncrementalMesh mesher(*shape, opt->linearDeflection, opt->parallel != 0,
                                      opt->angularDeflection, true);
      if (!mesher.IsDone()) {
        return ERROR_EXPORT_FAILED;
      }
      StlAPI_Writer writer;
      writer.Write(*shape, filePathUtf8);
      return ERROR_OK;
    }

    return ERROR_INVALID_ARGUMENT;
  } catch (...) {
    return MapExceptionToError();
  }
}
