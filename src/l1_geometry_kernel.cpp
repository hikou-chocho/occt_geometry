#include "l1_geometry_kernel.h"

#include <algorithm>
#include <map>
#include <memory>
#include <stdexcept>

#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBndLib.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <Bnd_Box.hxx>
#include <gp_Ax2.hxx>
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

    TopoDS_Shape shape;
    switch (dto->type) {
      case STOCK_BOX:
        shape = BRepPrimAPI_MakeBox(dto->p1, dto->p2, dto->p3).Shape();
        break;
      case STOCK_CYLINDER:
        shape = BRepPrimAPI_MakeCylinder(dto->p1, dto->p2).Shape();
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
      case FEAT_DRILL: {
        const DrillFeatureDto& drill = feature->u.drill;
        if (drill.radius <= 0.0 || drill.depth <= 0.0) {
          outResult->errorCode = ERROR_INVALID_ARGUMENT;
          return ERROR_INVALID_ARGUMENT;
        }

        gp_Pnt origin(drill.axis.origin[0], drill.axis.origin[1], drill.axis.origin[2]);
        gp_Dir dir(drill.axis.dir[0], drill.axis.dir[1], drill.axis.dir[2]);
        tool = BRepPrimAPI_MakeCylinder(gp_Ax2(origin, dir), drill.radius, drill.depth).Shape();
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
        Bnd_Box stockBox;
        BRepBndLib::Add(*stock, stockBox);
        if (stockBox.IsVoid()) {
          outResult->errorCode = ERROR_BOOLEAN_FAILED;
          return ERROR_BOOLEAN_FAILED;
        }

        Standard_Real xmin = 0.0;
        Standard_Real ymin = 0.0;
        Standard_Real zmin = 0.0;
        Standard_Real xmax = 0.0;
        Standard_Real ymax = 0.0;
        Standard_Real zmax = 0.0;
        stockBox.Get(xmin, ymin, zmin, xmax, ymax, zmax);
        const double spanX = xmax - xmin;
        const double spanY = ymax - ymin;
        const double spanZ = zmax - zmin;
        const double stockOuterRadius = std::max({spanX, spanY, spanZ}) * 2.0;

		// TODO: create 2d profile and revolve instead of creating multiple annulus segments and fusing them together.
        if (turn.profileCount > 1) {
          gp_Pnt origin(turn.axis.origin[0], turn.axis.origin[1], turn.axis.origin[2]);
          gp_Dir dir(turn.axis.dir[0], turn.axis.dir[1], turn.axis.dir[2]);

          TopoDS_Shape removalTool;
          bool hasTool = false;
          for (int index = 0; index < turn.profileCount - 1; ++index) {
            const double z0 = turn.profileZ[index];
            const double z1 = turn.profileZ[index + 1];
            const double targetRadius = turn.profileRadius[index];
            if (targetRadius <= 0.0 || z1 < z0) {
              outResult->errorCode = ERROR_INVALID_ARGUMENT;
              return ERROR_INVALID_ARGUMENT;
            }

            const double segmentLength = z1 - z0;
            if (segmentLength <= 1.0e-9) {
              continue;
            }

            if (targetRadius >= stockOuterRadius - 1.0e-9) {
              continue;
            }

            gp_Pnt segmentOrigin = origin.Translated(gp_Vec(dir) * z0);
            gp_Ax2 segmentAxis(segmentOrigin, dir);
            TopoDS_Shape outerCylinder =
                BRepPrimAPI_MakeCylinder(segmentAxis, stockOuterRadius, segmentLength).Shape();
            TopoDS_Shape innerCylinder =
                BRepPrimAPI_MakeCylinder(segmentAxis, targetRadius, segmentLength).Shape();
            BRepAlgoAPI_Cut annulus(outerCylinder, innerCylinder);
            if (!annulus.IsDone()) {
              outResult->errorCode = ERROR_BOOLEAN_FAILED;
              return ERROR_BOOLEAN_FAILED;
            }
            TopoDS_Shape segmentTool = annulus.Shape();

            if (!hasTool) {
              removalTool = segmentTool;
              hasTool = true;
            } else {
              BRepAlgoAPI_Fuse fuse(removalTool, segmentTool);
              if (!fuse.IsDone()) {
                outResult->errorCode = ERROR_BOOLEAN_FAILED;
                return ERROR_BOOLEAN_FAILED;
              }
              removalTool = fuse.Shape();
            }
          }

          if (!hasTool) {
            outResult->errorCode = ERROR_INVALID_ARGUMENT;
            return ERROR_INVALID_ARGUMENT;
          }

          tool = removalTool;
          break;
        }

        if (turn.targetDiameter <= 0.0 || turn.length <= 0.0) {
          outResult->errorCode = ERROR_INVALID_ARGUMENT;
          return ERROR_INVALID_ARGUMENT;
        }

        const double targetRadius = turn.targetDiameter * 0.5;
        gp_Pnt origin(turn.axis.origin[0], turn.axis.origin[1], turn.axis.origin[2]);
        gp_Dir dir(turn.axis.dir[0], turn.axis.dir[1], turn.axis.dir[2]);
        gp_Ax2 axis(origin, dir);

        TopoDS_Shape outerCylinder = BRepPrimAPI_MakeCylinder(axis, stockOuterRadius, turn.length).Shape();
        TopoDS_Shape innerCylinder = BRepPrimAPI_MakeCylinder(axis, targetRadius, turn.length).Shape();

        BRepAlgoAPI_Cut annulusCut(outerCylinder, innerCylinder);
        if (!annulusCut.IsDone()) {
          outResult->errorCode = ERROR_BOOLEAN_FAILED;
          return ERROR_BOOLEAN_FAILED;
        }

        tool = annulusCut.Shape();
        break;
      }
      case FEAT_TURN_ID: {
        const TurnIdFeatureDto& turn = feature->u.turnId;
        if (turn.profileCount > 1) {
          gp_Pnt origin(turn.axis.origin[0], turn.axis.origin[1], turn.axis.origin[2]);
          gp_Dir dir(turn.axis.dir[0], turn.axis.dir[1], turn.axis.dir[2]);

          TopoDS_Shape removalTool;
          bool hasTool = false;
          for (int index = 0; index < turn.profileCount - 1; ++index) {
            const double z0 = turn.profileZ[index];
            const double z1 = turn.profileZ[index + 1];
            const double targetRadius = turn.profileRadius[index];
            if (targetRadius <= 0.0 || z1 < z0) {
              outResult->errorCode = ERROR_INVALID_ARGUMENT;
              return ERROR_INVALID_ARGUMENT;
            }

            const double segmentLength = z1 - z0;
            if (segmentLength <= 1.0e-9) {
              continue;
            }

            gp_Pnt segmentOrigin = origin.Translated(gp_Vec(dir) * z0);
            gp_Ax2 segmentAxis(segmentOrigin, dir);
            TopoDS_Shape segmentTool =
                BRepPrimAPI_MakeCylinder(segmentAxis, targetRadius, segmentLength).Shape();

            if (!hasTool) {
              removalTool = segmentTool;
              hasTool = true;
            } else {
              BRepAlgoAPI_Fuse fuse(removalTool, segmentTool);
              if (!fuse.IsDone()) {
                outResult->errorCode = ERROR_BOOLEAN_FAILED;
                return ERROR_BOOLEAN_FAILED;
              }
              removalTool = fuse.Shape();
            }
          }

          if (!hasTool) {
            outResult->errorCode = ERROR_INVALID_ARGUMENT;
            return ERROR_INVALID_ARGUMENT;
          }

          tool = removalTool;
          break;
        }

        if (turn.targetDiameter <= 0.0 || turn.length <= 0.0) {
          outResult->errorCode = ERROR_INVALID_ARGUMENT;
          return ERROR_INVALID_ARGUMENT;
        }

        const double targetRadius = turn.targetDiameter * 0.5;
        gp_Pnt origin(turn.axis.origin[0], turn.axis.origin[1], turn.axis.origin[2]);
        gp_Dir dir(turn.axis.dir[0], turn.axis.dir[1], turn.axis.dir[2]);
        gp_Ax2 axis(origin, dir);

        tool = BRepPrimAPI_MakeCylinder(axis, targetRadius, turn.length).Shape();
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
