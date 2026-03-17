#include "l1_geometry_kernel.h"

#include <algorithm>
#include <chrono>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

// フィーチャ別の中間データ
struct TurnData {
  AxisDto                      axis{};
  std::vector<Path2DSegmentDto> segments;
  int                          closed = 1;
};

struct MillContourData {
  AxisDto                      axis{};
  std::vector<Path2DSegmentDto> segments;
  int                          closed = 1;
  double                       depth  = 0.0;
};

struct SampleCase {
  StockDto           stock{};
  std::string        featureType;
  MillHoleFeatureDto   millHole{};
  PocketRectFeatureDto pocketRect{};
  TurnData           turnOd{};
  TurnData           turnId{};
  MillContourData    millContour{};
  OutputOptions      outputOptions{};
  std::filesystem::path outputDir;
  std::string        stepFileName;
  std::string        stlFileName;
  std::string        deltaStepFileName;
  std::string        deltaStlFileName;
  std::string        removalStepFileName;
  std::string        removalStlFileName;
};

constexpr int kMaxSegments = 128;

double ElapsedMs(const std::chrono::steady_clock::time_point& begin,
                 const std::chrono::steady_clock::time_point& end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

bool Check(int code, const char* step) {
  if (code == 0) return true;
  std::cerr << step << " failed: errorCode=" << code << std::endl;
  return false;
}

std::string Trim(const std::string& value) {
  auto begin = value.begin();
  while (begin != value.end() && std::isspace(static_cast<unsigned char>(*begin))) ++begin;
  auto end = value.end();
  while (end != begin && std::isspace(static_cast<unsigned char>(*(end - 1)))) --end;
  return std::string(begin, end);
}

bool ParseBool01(const std::string& text) {
  if (text == "1") return true;
  if (text == "0") return false;
  throw std::runtime_error("Expected 0 or 1 but got: " + text);
}

void ParseVector3(const std::string& text, double dst[3]) {
  std::stringstream ss(text);
  std::string token;
  for (int i = 0; i < 3; ++i) {
    if (!std::getline(ss, token, ','))
      throw std::runtime_error("Expected 3 components: " + text);
    dst[i] = std::stod(Trim(token));
  }
  if (std::getline(ss, token, ','))
    throw std::runtime_error("Too many components: " + text);
}

void ParseUvPoint(const std::string& text, Path2DPointDto* dst) {
  std::stringstream ss(text);
  std::string token;
  if (!std::getline(ss, token, ','))
    throw std::runtime_error("Expected u,v point: " + text);
  dst->u = std::stod(Trim(token));
  if (!std::getline(ss, token, ','))
    throw std::runtime_error("Expected u,v point: " + text);
  dst->v = std::stod(Trim(token));
  if (std::getline(ss, token, ','))
    throw std::runtime_error("Too many u,v components: " + text);
}

std::unordered_map<std::string, std::string> LoadKeyValues(const std::filesystem::path& filePath) {
  std::ifstream ifs(filePath);
  if (!ifs)
    throw std::runtime_error("Failed to open config file: " + filePath.string());

  std::unordered_map<std::string, std::string> kv;
  std::string line;
  int lineNo = 0;
  while (std::getline(ifs, line)) {
    ++lineNo;
    const std::string trimmed = Trim(line);
    if (trimmed.empty() || trimmed[0] == '#') continue;
    const std::size_t pos = trimmed.find('=');
    if (pos == std::string::npos)
      throw std::runtime_error("Invalid line (missing '=') at line " + std::to_string(lineNo));
    const std::string key   = Trim(trimmed.substr(0, pos));
    const std::string value = Trim(trimmed.substr(pos + 1));
    if (key.empty())
      throw std::runtime_error("Empty key at line " + std::to_string(lineNo));
    kv[key] = value;
  }
  return kv;
}

const std::string& Require(const std::unordered_map<std::string, std::string>& kv,
                            const std::string& key) {
  auto it = kv.find(key);
  if (it == kv.end())
    throw std::runtime_error("Missing key: " + key);
  return it->second;
}

const std::string* Find(const std::unordered_map<std::string, std::string>& kv,
                        const std::string& key) {
  auto it = kv.find(key);
  return (it == kv.end()) ? nullptr : &it->second;
}

// ---------------------------------------------------------------------------
// Path2D セグメント構築ヘルパー
// ---------------------------------------------------------------------------

void AppendLine(std::vector<Path2DSegmentDto>& segments,
                const Path2DPointDto& from, const Path2DPointDto& to) {
  Path2DSegmentDto seg{};
  seg.from         = from;
  seg.to           = to;
  seg.center       = {0.0, 0.0};
  seg.type         = PATH_SEGMENT_LINE;
  seg.arcDirection = ARC_DIR_CCW;
  segments.push_back(seg);
}

// レガシー (Z, Radius) 点列から閉 Path2D プロファイルを構築する
bool BuildTurnProfileFromLegacy(const double* profileZ, const double* profileRadius, int count,
                                 bool isOuterDiameter, double boundaryRadius,
                                 std::vector<Path2DSegmentDto>& outSegments, int& outClosed) {
  if (count < 2 || count > kMaxSegments - 3) return false;

  outSegments.clear();
  outClosed = 1;

  const Path2DPointDto start{profileZ[0], isOuterDiameter ? boundaryRadius : 0.0};
  Path2DPointDto current = start;

  // 軸方向端部へ
  Path2DPointDto next{profileZ[count - 1], current.v};
  AppendLine(outSegments, current, next); current = next;

  // 最終プロファイル点へ
  next = {profileZ[count - 1], profileRadius[count - 1]};
  AppendLine(outSegments, current, next); current = next;

  // プロファイル点を逆順でたどる
  for (int i = count - 2; i >= 0; --i) {
    next = {profileZ[i], profileRadius[i]};
    AppendLine(outSegments, current, next); current = next;
  }

  // 始点に閉じる
  AppendLine(outSegments, current, start);
  return true;
}

// キーバリューファイルのセグメント定義から Path2D プロファイルを構築する
bool BuildProfileFromSegments(const std::unordered_map<std::string, std::string>& kv,
                               const std::string& prefix,
                               std::vector<Path2DSegmentDto>& outSegments, int& outClosed) {
  const std::string* segmentCountText = Find(kv, prefix + ".profile.segment.count");
  if (!segmentCountText) return false;

  const int segmentCount = std::stoi(*segmentCountText);
  if (segmentCount <= 0 || segmentCount > kMaxSegments)
    throw std::runtime_error(prefix + ".profile.segment.count out of range");

  const std::string profileType = Require(kv, prefix + ".profile.type");
  if (profileType != "PATH_2D")
    throw std::runtime_error(prefix + ".profile.type must be PATH_2D");
  const std::string plane = Require(kv, prefix + ".profile.plane");
  if (plane != "UV")
    throw std::runtime_error(prefix + ".profile.plane must be UV");

  outClosed = ParseBool01(Require(kv, prefix + ".profile.closed")) ? 1 : 0;
  outSegments.resize(segmentCount);

  for (int i = 0; i < segmentCount; ++i) {
    const std::string sp = prefix + ".profile.segment." + std::to_string(i);
    Path2DSegmentDto& seg = outSegments[i];

    const std::string segType = Require(kv, sp + ".type");
    if (segType == "LINE") {
      seg.type         = PATH_SEGMENT_LINE;
      seg.arcDirection = ARC_DIR_CCW;
      seg.center       = {0.0, 0.0};
    } else if (segType == "ARC") {
      seg.type = PATH_SEGMENT_ARC;
      const std::string arcDir = Require(kv, sp + ".arcDirection");
      if      (arcDir == "CW")  seg.arcDirection = ARC_DIR_CW;
      else if (arcDir == "CCW") seg.arcDirection = ARC_DIR_CCW;
      else throw std::runtime_error(sp + ".arcDirection must be CW or CCW");
      ParseUvPoint(Require(kv, sp + ".center"), &seg.center);
    } else {
      throw std::runtime_error(sp + ".type must be LINE or ARC");
    }

    ParseUvPoint(Require(kv, sp + ".from"), &seg.from);
    ParseUvPoint(Require(kv, sp + ".to"),   &seg.to);
  }

  return true;
}

// ---------------------------------------------------------------------------
// ケースファイルのロード
// ---------------------------------------------------------------------------

SampleCase LoadCaseFile(const std::filesystem::path& filePath) {
  const auto kv = LoadKeyValues(filePath);

  SampleCase sample{};

  const std::string stockType = Require(kv, "stock.type");
  if      (stockType == "BOX")      sample.stock.type = STOCK_BOX;
  else if (stockType == "CYLINDER") sample.stock.type = STOCK_CYLINDER;
  else throw std::runtime_error("Unsupported stock.type: " + stockType);

  sample.stock.p1 = std::stod(Require(kv, "stock.p1"));
  sample.stock.p2 = std::stod(Require(kv, "stock.p2"));
  sample.stock.p3 = std::stod(Require(kv, "stock.p3"));
  ParseVector3(Require(kv, "stock.axis.origin"), sample.stock.axis.origin);
  ParseVector3(Require(kv, "stock.axis.dir"),    sample.stock.axis.dir);
  ParseVector3(Require(kv, "stock.axis.xdir"),   sample.stock.axis.xdir);

  sample.featureType = Require(kv, "feature.type");

  if (sample.featureType == "MILL_HOLE") {
    sample.millHole.radius = std::stod(Require(kv, "feature.millHole.radius"));
    sample.millHole.depth  = std::stod(Require(kv, "feature.millHole.depth"));
    ParseVector3(Require(kv, "feature.millHole.axis.origin"), sample.millHole.axis.origin);
    ParseVector3(Require(kv, "feature.millHole.axis.dir"),    sample.millHole.axis.dir);
    ParseVector3(Require(kv, "feature.millHole.axis.xdir"),   sample.millHole.axis.xdir);

  } else if (sample.featureType == "POCKET_RECT") {
    sample.pocketRect.width  = std::stod(Require(kv, "feature.pocketRect.width"));
    sample.pocketRect.height = std::stod(Require(kv, "feature.pocketRect.height"));
    sample.pocketRect.depth  = std::stod(Require(kv, "feature.pocketRect.depth"));
    ParseVector3(Require(kv, "feature.pocketRect.axis.origin"), sample.pocketRect.axis.origin);
    ParseVector3(Require(kv, "feature.pocketRect.axis.dir"),    sample.pocketRect.axis.dir);
    ParseVector3(Require(kv, "feature.pocketRect.axis.xdir"),   sample.pocketRect.axis.xdir);

  } else if (sample.featureType == "TURN_OD") {
    if (!BuildProfileFromSegments(kv, "feature.turnOd",
                                   sample.turnOd.segments, sample.turnOd.closed)) {
      const std::string* profileCount = Find(kv, "feature.turnOd.profile.count");
      if (!profileCount)
        throw std::runtime_error("feature.turnOd.profile.segment.count or feature.turnOd.profile.count is required");
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > kMaxSegments - 3)
        throw std::runtime_error("feature.turnOd.profile.count out of range");

      std::vector<double> profileZ(count), profileRadius(count);
      for (int i = 0; i < count; ++i) {
        profileZ[i]      = std::stod(Require(kv, "feature.turnOd.profile." + std::to_string(i) + ".z"));
        profileRadius[i] = std::stod(Require(kv, "feature.turnOd.profile." + std::to_string(i) + ".radius"));
      }
      double maxR = *std::max_element(profileRadius.begin(), profileRadius.end());
      double stockR = (sample.stock.type == STOCK_CYLINDER) ? sample.stock.p1 : maxR;
      if (stockR <= maxR) stockR = maxR + std::max(1.0, maxR * 0.1);

      if (!BuildTurnProfileFromLegacy(profileZ.data(), profileRadius.data(), count,
                                       true, stockR,
                                       sample.turnOd.segments, sample.turnOd.closed))
        throw std::runtime_error("Failed to build TURN_OD PATH_2D profile");
    }
    ParseVector3(Require(kv, "feature.turnOd.axis.origin"), sample.turnOd.axis.origin);
    ParseVector3(Require(kv, "feature.turnOd.axis.dir"),    sample.turnOd.axis.dir);
    ParseVector3(Require(kv, "feature.turnOd.axis.xdir"),   sample.turnOd.axis.xdir);

  } else if (sample.featureType == "TURN_ID") {
    if (!BuildProfileFromSegments(kv, "feature.turnId",
                                   sample.turnId.segments, sample.turnId.closed)) {
      const std::string* profileCount = Find(kv, "feature.turnId.profile.count");
      if (!profileCount)
        throw std::runtime_error("feature.turnId.profile.segment.count or feature.turnId.profile.count is required");
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > kMaxSegments - 3)
        throw std::runtime_error("feature.turnId.profile.count out of range");

      std::vector<double> profileZ(count), profileRadius(count);
      for (int i = 0; i < count; ++i) {
        profileZ[i]      = std::stod(Require(kv, "feature.turnId.profile." + std::to_string(i) + ".z"));
        profileRadius[i] = std::stod(Require(kv, "feature.turnId.profile." + std::to_string(i) + ".radius"));
      }
      if (!BuildTurnProfileFromLegacy(profileZ.data(), profileRadius.data(), count,
                                       false, 0.0,
                                       sample.turnId.segments, sample.turnId.closed))
        throw std::runtime_error("Failed to build TURN_ID PATH_2D profile");
    }
    ParseVector3(Require(kv, "feature.turnId.axis.origin"), sample.turnId.axis.origin);
    ParseVector3(Require(kv, "feature.turnId.axis.dir"),    sample.turnId.axis.dir);
    ParseVector3(Require(kv, "feature.turnId.axis.xdir"),   sample.turnId.axis.xdir);

  } else if (sample.featureType == "MILL_CONTOUR") {
    if (!BuildProfileFromSegments(kv, "feature.millContour",
                                   sample.millContour.segments, sample.millContour.closed))
      throw std::runtime_error("feature.millContour.profile.segment.count is required");
    sample.millContour.depth = std::stod(Require(kv, "feature.millContour.depth"));
    ParseVector3(Require(kv, "feature.millContour.axis.origin"), sample.millContour.axis.origin);
    ParseVector3(Require(kv, "feature.millContour.axis.dir"),    sample.millContour.axis.dir);
    ParseVector3(Require(kv, "feature.millContour.axis.xdir"),   sample.millContour.axis.xdir);

  } else {
    throw std::runtime_error("Unsupported feature.type in sample: " + sample.featureType);
  }

  sample.outputOptions.linearDeflection  = std::stod(Require(kv, "output.linearDeflection"));
  sample.outputOptions.angularDeflection = std::stod(Require(kv, "output.angularDeflection"));
  sample.outputOptions.parallel          = ParseBool01(Require(kv, "output.parallel")) ? 1 : 0;

  sample.outputDir       = Require(kv, "output.dir");
  sample.stepFileName    = Require(kv, "output.stepFile");
  sample.stlFileName     = Require(kv, "output.stlFile");
  sample.deltaStepFileName = Require(kv, "output.deltaStepFile");
  sample.deltaStlFileName  = Require(kv, "output.deltaStlFile");
  if (const std::string* removalStepFile = Find(kv, "output.removalStepFile"))
    sample.removalStepFileName = *removalStepFile;
  if (const std::string* removalStlFile = Find(kv, "output.removalStlFile"))
    sample.removalStlFileName = *removalStlFile;

  return sample;
}

}  // namespace

int main(int argc, char* argv[]) {
  using Clock = std::chrono::steady_clock;
  const auto totalStart = Clock::now();

  const std::filesystem::path casePath = (argc >= 2)
      ? std::filesystem::path(argv[1])
      : std::filesystem::path("samples") / "box_mill_hole_case.txt";

  const auto loadStart = Clock::now();
  SampleCase sample{};
  try {
    sample = LoadCaseFile(casePath);
  } catch (const std::exception& ex) {
    std::cerr << "Failed to load case file: " << casePath << "\n" << ex.what() << std::endl;
    return 1;
  }
  const auto loadEnd = Clock::now();

  const auto kernelStart = Clock::now();

  void* kernel = L1_CreateKernel();
  if (!kernel) {
    std::cerr << "L1_CreateKernel failed" << std::endl;
    return 1;
  }

  int stockId = 0;
  if (!Check(L1_CreateStock(kernel, &sample.stock, &stockId), "L1_CreateStock")) {
    L1_DestroyKernel(kernel);
    return 1;
  }
  const auto kernelEnd = Clock::now();

  const auto applyStart = Clock::now();

  OperationResult result{};
  int rc = 0;
  const std::string& ft = sample.featureType;

  if (ft == "MILL_HOLE") {
    rc = L1_ApplyMillHole(kernel, stockId, &sample.millHole, &result);
  } else if (ft == "POCKET_RECT") {
    rc = L1_ApplyPocketRect(kernel, stockId, &sample.pocketRect, &result);
  } else if (ft == "TURN_OD") {
    rc = L1_ApplyTurnOd(kernel, stockId,
                         &sample.turnOd.axis,
                         sample.turnOd.segments.data(),
                         static_cast<int>(sample.turnOd.segments.size()),
                         sample.turnOd.closed, &result);
  } else if (ft == "TURN_ID") {
    rc = L1_ApplyTurnId(kernel, stockId,
                         &sample.turnId.axis,
                         sample.turnId.segments.data(),
                         static_cast<int>(sample.turnId.segments.size()),
                         sample.turnId.closed, &result);
  } else if (ft == "MILL_CONTOUR") {
    rc = L1_ApplyMillContour(kernel, stockId,
                              &sample.millContour.axis,
                              sample.millContour.segments.data(),
                              static_cast<int>(sample.millContour.segments.size()),
                              sample.millContour.closed,
                              sample.millContour.depth, &result);
  } else {
    std::cerr << "Unsupported feature.type: " << ft << std::endl;
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }

  if (!Check(rc, "L1_ApplyXxx")) {
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }
  const auto applyEnd = Clock::now();

  std::filesystem::path outDir = std::filesystem::current_path() / sample.outputDir;
  std::filesystem::create_directories(outDir);

  OutputOptions stepOpt = sample.outputOptions;
  stepOpt.format = OUT_STEP;

  OutputOptions stlOpt = sample.outputOptions;
  stlOpt.format = OUT_STL;

  const std::string stepPath      = (outDir / sample.stepFileName).string();
  const std::string stlPath       = (outDir / sample.stlFileName).string();
  const std::string deltaStepPath = (outDir / sample.deltaStepFileName).string();
  const std::string deltaStlPath  = (outDir / sample.deltaStlFileName).string();
  const bool hasRemovalStepPath   = !sample.removalStepFileName.empty();
  const bool hasRemovalStlPath    = !sample.removalStlFileName.empty();
  const std::string removalStepPath = hasRemovalStepPath
      ? (outDir / sample.removalStepFileName).string()
      : std::string();
  const std::string removalStlPath = hasRemovalStlPath
      ? (outDir / sample.removalStlFileName).string()
      : std::string();

  const auto exportStart = Clock::now();

  auto cleanup = [&]() {
    L1_DeleteShape(kernel, result.removalShapeId);
    L1_DeleteShape(kernel, result.deltaShapeId);
    L1_DeleteShape(kernel, result.resultShapeId);
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
  };

  if (!Check(L1_ExportShape(kernel, result.resultShapeId, &stepOpt, stepPath.c_str()),      "L1_ExportShape(STEP)"))      { cleanup(); return 1; }
  if (!Check(L1_ExportShape(kernel, result.resultShapeId, &stlOpt,  stlPath.c_str()),       "L1_ExportShape(STL)"))       { cleanup(); return 1; }
  if (!Check(L1_ExportShape(kernel, result.deltaShapeId,  &stepOpt, deltaStepPath.c_str()), "L1_ExportShape(DELTA STEP)")) { cleanup(); return 1; }
  if (!Check(L1_ExportShape(kernel, result.deltaShapeId,  &stlOpt,  deltaStlPath.c_str()),  "L1_ExportShape(DELTA STL)"))  { cleanup(); return 1; }
  if (hasRemovalStepPath &&
      !Check(L1_ExportShape(kernel, result.removalShapeId, &stepOpt, removalStepPath.c_str()), "L1_ExportShape(REMOVAL STEP)")) { cleanup(); return 1; }
  if (hasRemovalStlPath &&
      !Check(L1_ExportShape(kernel, result.removalShapeId, &stlOpt, removalStlPath.c_str()), "L1_ExportShape(REMOVAL STL)")) { cleanup(); return 1; }

  const auto exportEnd = Clock::now();

  cleanup();

  std::cout << "Generated: " << stepPath      << "\n"
            << "Generated: " << stlPath       << "\n"
            << "Generated: " << deltaStepPath << "\n"
            << "Generated: " << deltaStlPath  << "\n";
  if (hasRemovalStepPath)
    std::cout << "Generated: " << removalStepPath << "\n";
  if (hasRemovalStlPath)
    std::cout << "Generated: " << removalStlPath << "\n";

  std::cout << std::fixed << std::setprecision(3)
            << "Timing(ms): LoadCaseFile=" << ElapsedMs(loadStart, loadEnd)
            << ", CreateKernel+CreateStock=" << ElapsedMs(kernelStart, kernelEnd)
            << ", ApplyFeature=" << ElapsedMs(applyStart, applyEnd)
            << ", Export=" << ElapsedMs(exportStart, exportEnd)
            << ", Total=" << ElapsedMs(totalStart, Clock::now()) << std::endl;

  return 0;
}
