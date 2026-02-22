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
  STOCK_BOX = 1,
  STOCK_CYLINDER = 2
} StockType;

typedef struct StockDto {
  StockType type;
  double p1;
  double p2;
  double p3;
  AxisDto axis;
} StockDto;

typedef enum FeatureType {
  FEAT_DRILL = 1,
  FEAT_POCKET_RECT = 2,
  FEAT_TURN_OD = 3,
  FEAT_TURN_ID = 4
} FeatureType;

typedef struct DrillFeatureDto {
  double radius;
  double depth;
  AxisDto axis;
} DrillFeatureDto;

typedef struct PocketRectFeatureDto {
  double width;
  double height;
  double depth;
  AxisDto axis;
} PocketRectFeatureDto;

#define L1_TURN_OD_PROFILE_MAX 64

typedef struct TurnOdFeatureDto {
  double targetDiameter;
  double length;
  int profileCount;
  double profileZ[L1_TURN_OD_PROFILE_MAX];
  double profileRadius[L1_TURN_OD_PROFILE_MAX];
  AxisDto axis;
} TurnOdFeatureDto;

typedef struct TurnIdFeatureDto {
  double targetDiameter;
  double length;
  int profileCount;
  double profileZ[L1_TURN_OD_PROFILE_MAX];
  double profileRadius[L1_TURN_OD_PROFILE_MAX];
  AxisDto axis;
} TurnIdFeatureDto;

typedef struct FeatureDto {
  FeatureType type;
  union {
    DrillFeatureDto drill;
    PocketRectFeatureDto pocketRect;
    TurnOdFeatureDto turnOd;
    TurnIdFeatureDto turnId;
  } u;
} FeatureDto;

typedef struct OperationResult {
  int resultShapeId;
  int deltaShapeId;
  int errorCode;
} OperationResult;

typedef enum OutputFormat {
  OUT_STEP = 1,
  OUT_STL = 2
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

L1_API int   L1_ApplyFeature(void* kernel,
                            int stockId,
                            const FeatureDto* feature,
                            OperationResult* outResult);

L1_API int   L1_DeleteShape(void* kernel, int shapeId);

L1_API int   L1_ExportShape(void* kernel,
                            int shapeId,
                            const OutputOptions* opt,
                            const char* filePathUtf8);

#ifdef __cplusplus
}
#endif
