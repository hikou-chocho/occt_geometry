#pragma once

#ifdef _WIN32
  #ifdef L1_GEOMETRY_KERNEL_BUILD
    #define L1_API __declspec(dllexport)
  #else
    #define L1_API __declspec(dllimport)
  #endif
#else
  #define L1_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AxisDto {
  double origin[3];
  double dir[3];
  double xdir[3];
} AxisDto;

typedef enum StockType {
  STOCK_BOX      = 1,
  STOCK_CYLINDER = 2
} StockType;

typedef struct StockDto {
  StockType type;
  double p1;
  double p2;
  double p3;
  AxisDto axis;
} StockDto;

typedef struct MillHoleFeatureDto {
  double radius;
  double depth;
  AxisDto axis;
} MillHoleFeatureDto;

typedef struct PocketRectFeatureDto {
  double width;
  double height;
  double depth;
  AxisDto axis;
} PocketRectFeatureDto;

typedef struct Path2DPointDto {
  double u;
  double v;
} Path2DPointDto;

typedef enum Path2DSegmentType {
  PATH_SEGMENT_LINE   = 1,
  PATH_SEGMENT_ARC    = 2,
  PATH_SEGMENT_SPLINE = 3
} Path2DSegmentType;

typedef enum ArcDirection {
  ARC_DIR_CW  = 1,
  ARC_DIR_CCW = 2
} ArcDirection;

/* doubles を先頭に並べてパディングなし: 56 bytes */
typedef struct Path2DSegmentDto {
  Path2DPointDto    from;         /* offset  0, 16 bytes */
  Path2DPointDto    to;           /* offset 16, 16 bytes */
  Path2DPointDto    center;       /* offset 32, 16 bytes */
  Path2DSegmentType type;         /* offset 48,  4 bytes */
  ArcDirection      arcDirection; /* offset 52,  4 bytes */
} Path2DSegmentDto;               /* total: 56 bytes     */

typedef struct OperationResult {
  int resultShapeId;
  int deltaShapeId;
  int removalShapeId;
  int errorCode;
} OperationResult;

typedef enum OutputFormat {
  OUT_STEP = 1,
  OUT_STL  = 2
} OutputFormat;

typedef struct OutputOptions {
  OutputFormat format;
  double linearDeflection;
  double angularDeflection;
  int parallel;
} OutputOptions;

L1_API void* L1_CreateKernel();
L1_API int   L1_DestroyKernel(void* kernel);

L1_API int   L1_CreateStock(void* kernel, const StockDto* dto, int* outStockId);

L1_API int   L1_ApplyMillHole(void* kernel, int stockId,
                              const MillHoleFeatureDto* dto,
                              OperationResult* outResult);

L1_API int   L1_ApplyPocketRect(void* kernel, int stockId,
                                const PocketRectFeatureDto* dto,
                                OperationResult* outResult);

L1_API int   L1_ApplyTurnOd(void* kernel, int stockId,
                            const AxisDto* axis,
                            const Path2DSegmentDto* segments, int segmentCount, int closed,
                            OperationResult* outResult);

L1_API int   L1_ApplyTurnId(void* kernel, int stockId,
                            const AxisDto* axis,
                            const Path2DSegmentDto* segments, int segmentCount, int closed,
                            OperationResult* outResult);

L1_API int   L1_ApplyMillContour(void* kernel, int stockId,
                                 const AxisDto* axis,
                                 const Path2DSegmentDto* segments, int segmentCount, int closed,
                                 double depth,
                                 OperationResult* outResult);

L1_API int   L1_DeleteShape(void* kernel, int shapeId);

L1_API int   L1_ImportStepAsShape(void* kernel,
                                  const char* filePathUtf8,
                                  int* outShapeId);

L1_API int   L1_ExportShape(void* kernel,
                            int shapeId,
                            const OutputOptions* opt,
                            const char* filePathUtf8);

#ifdef __cplusplus
}
#endif
