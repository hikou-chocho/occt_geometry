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

namespace {
struct SampleCase {
  StockDto stock{};
  FeatureDto feature{};
  OutputOptions outputOptions{};
  std::filesystem::path outputDir;
  std::string stepFileName;
  std::string stlFileName;
  std::string deltaStepFileName;
  std::string deltaStlFileName;
};

double ElapsedMs(const std::chrono::steady_clock::time_point& begin,
                 const std::chrono::steady_clock::time_point& end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

bool Check(int code, const char* step) {
  if (code == 0) {
    return true;
  }
  std::cerr << step << " failed: errorCode=" << code << std::endl;
  return false;
}

std::string Trim(const std::string& value) {
  auto begin = value.begin();
  while (begin != value.end() && std::isspace(static_cast<unsigned char>(*begin))) {
    ++begin;
  }
  auto end = value.end();
  while (end != begin && std::isspace(static_cast<unsigned char>(*(end - 1)))) {
    --end;
  }
  return std::string(begin, end);
}

bool ParseBool01(const std::string& text) {
  if (text == "1") {
    return true;
  }
  if (text == "0") {
    return false;
  }
  throw std::runtime_error("Expected 0 or 1 but got: " + text);
}

void ParseVector3(const std::string& text, double dst[3]) {
  std::stringstream ss(text);
  std::string token;
  for (int index = 0; index < 3; ++index) {
    if (!std::getline(ss, token, ',')) {
      throw std::runtime_error("Expected 3 components: " + text);
    }
    dst[index] = std::stod(Trim(token));
  }
  if (std::getline(ss, token, ',')) {
    throw std::runtime_error("Too many components: " + text);
  }
}

void ParseUvPoint(const std::string& text, Path2DPointDto* dst) {
  std::stringstream ss(text);
  std::string token;
  if (!std::getline(ss, token, ',')) {
    throw std::runtime_error("Expected u,v point: " + text);
  }
  dst->u = std::stod(Trim(token));

  if (!std::getline(ss, token, ',')) {
    throw std::runtime_error("Expected u,v point: " + text);
  }
  dst->v = std::stod(Trim(token));

  if (std::getline(ss, token, ',')) {
    throw std::runtime_error("Too many u,v components: " + text);
  }
}

std::unordered_map<std::string, std::string> LoadKeyValues(const std::filesystem::path& filePath) {
  std::ifstream ifs(filePath);
  if (!ifs) {
    throw std::runtime_error("Failed to open config file: " + filePath.string());
  }

  std::unordered_map<std::string, std::string> kv;
  std::string line;
  int lineNo = 0;
  while (std::getline(ifs, line)) {
    ++lineNo;
    const std::string trimmed = Trim(line);
    if (trimmed.empty() || trimmed[0] == '#') {
      continue;
    }
    const std::size_t pos = trimmed.find('=');
    if (pos == std::string::npos) {
      throw std::runtime_error("Invalid line (missing '=') at line " + std::to_string(lineNo));
    }
    const std::string key = Trim(trimmed.substr(0, pos));
    const std::string value = Trim(trimmed.substr(pos + 1));
    if (key.empty()) {
      throw std::runtime_error("Empty key at line " + std::to_string(lineNo));
    }
    kv[key] = value;
  }
  return kv;
}

const std::string& Require(const std::unordered_map<std::string, std::string>& kv, const std::string& key) {
  auto it = kv.find(key);
  if (it == kv.end()) {
    throw std::runtime_error("Missing key: " + key);
  }
  return it->second;
}

const std::string* Find(const std::unordered_map<std::string, std::string>& kv, const std::string& key) {
  auto it = kv.find(key);
  if (it == kv.end()) {
    return nullptr;
  }
  return &it->second;
}

bool AppendLine(Path2DProfileDto* profile, const Path2DPointDto& from, const Path2DPointDto& to) {
  if (profile->segmentCount >= L1_PATH2D_SEGMENT_MAX) {
    return false;
  }
  Path2DSegmentDto& segment = profile->segments[profile->segmentCount++];
  segment.type = PATH_SEGMENT_LINE;
  segment.from = from;
  segment.to = to;
  segment.center = {0.0, 0.0};
  segment.arcDirection = ARC_DIR_CCW;
  return true;
}

bool BuildTurnProfileFromLegacy(const double* profileZ,
                                const double* profileRadius,
                                int count,
                                bool isOuterDiameter,
                                double boundaryRadius,
                                Path2DProfileDto* outProfile) {
  if (count < 2 || count > L1_PATH2D_SEGMENT_MAX - 3) {
    return false;
  }

  outProfile->type = PROFILE_PATH_2D;
  outProfile->plane = PROFILE_PLANE_UV;
  outProfile->closed = 1;
  outProfile->segmentCount = 0;
  outProfile->start = {profileZ[0], isOuterDiameter ? boundaryRadius : 0.0};

  Path2DPointDto current = outProfile->start;
  Path2DPointDto next{profileZ[count - 1], current.v};
  if (!AppendLine(outProfile, current, next)) {
    return false;
  }
  current = next;

  next = {profileZ[count - 1], profileRadius[count - 1]};
  if (!AppendLine(outProfile, current, next)) {
    return false;
  }
  current = next;

  for (int index = count - 2; index >= 0; --index) {
    next = {profileZ[index], profileRadius[index]};
    if (!AppendLine(outProfile, current, next)) {
      return false;
    }
    current = next;
  }

  if (!AppendLine(outProfile, current, outProfile->start)) {
    return false;
  }

  return true;
}

bool BuildTurnProfileFromSegments(const std::unordered_map<std::string, std::string>& kv,
                                  const std::string& prefix,
                                  Path2DProfileDto* outProfile) {
  const std::string* segmentCountText = Find(kv, prefix + ".profile.segment.count");
  if (!segmentCountText) {
    return false;
  }

  const int segmentCount = std::stoi(*segmentCountText);
  if (segmentCount <= 0 || segmentCount > L1_PATH2D_SEGMENT_MAX) {
    throw std::runtime_error(prefix + ".profile.segment.count out of range");
  }

  const std::string profileType = Require(kv, prefix + ".profile.type");
  if (profileType != "PATH_2D") {
    throw std::runtime_error(prefix + ".profile.type must be PATH_2D");
  }
  const std::string plane = Require(kv, prefix + ".profile.plane");
  if (plane != "UV") {
    throw std::runtime_error(prefix + ".profile.plane must be UV");
  }

  outProfile->type = PROFILE_PATH_2D;
  outProfile->plane = PROFILE_PLANE_UV;
  outProfile->segmentCount = segmentCount;
  outProfile->closed = ParseBool01(Require(kv, prefix + ".profile.closed")) ? 1 : 0;

  ParseUvPoint(Require(kv, prefix + ".profile.start"), &outProfile->start);

  for (int index = 0; index < segmentCount; ++index) {
    const std::string segmentPrefix = prefix + ".profile.segment." + std::to_string(index);
    Path2DSegmentDto& segment = outProfile->segments[index];

    const std::string segmentType = Require(kv, segmentPrefix + ".type");
    if (segmentType == "LINE") {
      segment.type = PATH_SEGMENT_LINE;
      segment.arcDirection = ARC_DIR_CCW;
      segment.center = {0.0, 0.0};
    } else if (segmentType == "ARC") {
      segment.type = PATH_SEGMENT_ARC;
      const std::string arcDirection = Require(kv, segmentPrefix + ".arcDirection");
      if (arcDirection == "CW") {
        segment.arcDirection = ARC_DIR_CW;
      } else if (arcDirection == "CCW") {
        segment.arcDirection = ARC_DIR_CCW;
      } else {
        throw std::runtime_error(segmentPrefix + ".arcDirection must be CW or CCW");
      }
      ParseUvPoint(Require(kv, segmentPrefix + ".center"), &segment.center);
    } else {
      throw std::runtime_error(segmentPrefix + ".type must be LINE or ARC");
    }

    ParseUvPoint(Require(kv, segmentPrefix + ".from"), &segment.from);
    ParseUvPoint(Require(kv, segmentPrefix + ".to"), &segment.to);
  }

  return true;
}

SampleCase LoadCaseFile(const std::filesystem::path& filePath) {
  const auto kv = LoadKeyValues(filePath);

  SampleCase sample{};

  const std::string stockType = Require(kv, "stock.type");
  if (stockType == "BOX") {
    sample.stock.type = STOCK_BOX;
  } else if (stockType == "CYLINDER") {
    sample.stock.type = STOCK_CYLINDER;
  } else {
    throw std::runtime_error("Unsupported stock.type: " + stockType);
  }
  sample.stock.p1 = std::stod(Require(kv, "stock.p1"));
  sample.stock.p2 = std::stod(Require(kv, "stock.p2"));
  sample.stock.p3 = std::stod(Require(kv, "stock.p3"));
  ParseVector3(Require(kv, "stock.axis.origin"), sample.stock.axis.origin);
  ParseVector3(Require(kv, "stock.axis.dir"), sample.stock.axis.dir);
  ParseVector3(Require(kv, "stock.axis.xdir"), sample.stock.axis.xdir);

  const std::string featureType = Require(kv, "feature.type");
  if (featureType == "MILL_HOLE") {
    sample.feature.type = FEAT_MILL_HOLE;
    sample.feature.u.millHole.radius = std::stod(Require(kv, "feature.millHole.radius"));
    sample.feature.u.millHole.depth = std::stod(Require(kv, "feature.millHole.depth"));
    ParseVector3(Require(kv, "feature.millHole.axis.origin"), sample.feature.u.millHole.axis.origin);
    ParseVector3(Require(kv, "feature.millHole.axis.dir"), sample.feature.u.millHole.axis.dir);
    ParseVector3(Require(kv, "feature.millHole.axis.xdir"), sample.feature.u.millHole.axis.xdir);
  } else if (featureType == "POCKET_RECT") {
    sample.feature.type = FEAT_POCKET_RECT;
    sample.feature.u.pocketRect.width = std::stod(Require(kv, "feature.pocketRect.width"));
    sample.feature.u.pocketRect.height = std::stod(Require(kv, "feature.pocketRect.height"));
    sample.feature.u.pocketRect.depth = std::stod(Require(kv, "feature.pocketRect.depth"));
    ParseVector3(Require(kv, "feature.pocketRect.axis.origin"), sample.feature.u.pocketRect.axis.origin);
    ParseVector3(Require(kv, "feature.pocketRect.axis.dir"), sample.feature.u.pocketRect.axis.dir);
    ParseVector3(Require(kv, "feature.pocketRect.axis.xdir"), sample.feature.u.pocketRect.axis.xdir);
  } else if (featureType == "TURN_OD") {
    sample.feature.type = FEAT_TURN_OD;
    if (!BuildTurnProfileFromSegments(kv, "feature.turnOd", &sample.feature.u.turnOd.profile)) {
      const std::string* profileCount = Find(kv, "feature.turnOd.profile.count");
      if (!profileCount) {
        throw std::runtime_error("feature.turnOd.profile.segment.count or feature.turnOd.profile.count is required");
      }
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > L1_PATH2D_SEGMENT_MAX - 3) {
        throw std::runtime_error("feature.turnOd.profile.count out of range");
      }
      double profileZ[L1_PATH2D_SEGMENT_MAX]{};
      double profileRadius[L1_PATH2D_SEGMENT_MAX]{};
      for (int index = 0; index < count; ++index) {
        const std::string zKey = "feature.turnOd.profile." + std::to_string(index) + ".z";
        const std::string rKey = "feature.turnOd.profile." + std::to_string(index) + ".radius";
        profileZ[index] = std::stod(Require(kv, zKey));
        profileRadius[index] = std::stod(Require(kv, rKey));
      }

      double maxProfileRadius = 0.0;
      for (int index = 0; index < count; ++index) {
        maxProfileRadius = std::max(maxProfileRadius, profileRadius[index]);
      }
      double stockRadius = sample.stock.type == STOCK_CYLINDER ? sample.stock.p1 : maxProfileRadius;
      if (stockRadius <= maxProfileRadius) {
        stockRadius = maxProfileRadius + std::max(1.0, maxProfileRadius * 0.1);
      }

      if (!BuildTurnProfileFromLegacy(profileZ,
                                      profileRadius,
                                      count,
                                      true,
                                      stockRadius,
                                      &sample.feature.u.turnOd.profile)) {
        throw std::runtime_error("Failed to build TURN_OD PATH_2D profile");
      }
    }
    ParseVector3(Require(kv, "feature.turnOd.axis.origin"), sample.feature.u.turnOd.axis.origin);
    ParseVector3(Require(kv, "feature.turnOd.axis.dir"), sample.feature.u.turnOd.axis.dir);
    ParseVector3(Require(kv, "feature.turnOd.axis.xdir"), sample.feature.u.turnOd.axis.xdir);
  } else if (featureType == "TURN_ID") {
    sample.feature.type = FEAT_TURN_ID;
    if (!BuildTurnProfileFromSegments(kv, "feature.turnId", &sample.feature.u.turnId.profile)) {
      const std::string* profileCount = Find(kv, "feature.turnId.profile.count");
      if (!profileCount) {
        throw std::runtime_error("feature.turnId.profile.segment.count or feature.turnId.profile.count is required");
      }
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > L1_PATH2D_SEGMENT_MAX - 3) {
        throw std::runtime_error("feature.turnId.profile.count out of range");
      }
      double profileZ[L1_PATH2D_SEGMENT_MAX]{};
      double profileRadius[L1_PATH2D_SEGMENT_MAX]{};
      for (int index = 0; index < count; ++index) {
        const std::string zKey = "feature.turnId.profile." + std::to_string(index) + ".z";
        const std::string rKey = "feature.turnId.profile." + std::to_string(index) + ".radius";
        profileZ[index] = std::stod(Require(kv, zKey));
        profileRadius[index] = std::stod(Require(kv, rKey));
      }

      if (!BuildTurnProfileFromLegacy(profileZ,
                                      profileRadius,
                                      count,
                                      false,
                                      0.0,
                                      &sample.feature.u.turnId.profile)) {
        throw std::runtime_error("Failed to build TURN_ID PATH_2D profile");
      }
    }
    ParseVector3(Require(kv, "feature.turnId.axis.origin"), sample.feature.u.turnId.axis.origin);
    ParseVector3(Require(kv, "feature.turnId.axis.dir"), sample.feature.u.turnId.axis.dir);
    ParseVector3(Require(kv, "feature.turnId.axis.xdir"), sample.feature.u.turnId.axis.xdir);
  } else if (featureType == "MILL_CONTOUR") {
    sample.feature.type = FEAT_MILL_CONTOUR;
    if (!BuildTurnProfileFromSegments(kv,
                                      "feature.millContour",
                                      &sample.feature.u.millContour.profile)) {
      throw std::runtime_error("feature.millContour.profile.segment.count is required");
    }
    sample.feature.u.millContour.depth = std::stod(Require(kv, "feature.millContour.depth"));
    ParseVector3(Require(kv, "feature.millContour.axis.origin"),
                 sample.feature.u.millContour.axis.origin);
    ParseVector3(Require(kv, "feature.millContour.axis.dir"),
                 sample.feature.u.millContour.axis.dir);
    ParseVector3(Require(kv, "feature.millContour.axis.xdir"),
                 sample.feature.u.millContour.axis.xdir);
  } else {
    throw std::runtime_error("Unsupported feature.type in sample: " + featureType);
  }

  sample.outputOptions.linearDeflection = std::stod(Require(kv, "output.linearDeflection"));
  sample.outputOptions.angularDeflection = std::stod(Require(kv, "output.angularDeflection"));
  sample.outputOptions.parallel = ParseBool01(Require(kv, "output.parallel")) ? 1 : 0;

  sample.outputDir = Require(kv, "output.dir");
  sample.stepFileName = Require(kv, "output.stepFile");
  sample.stlFileName = Require(kv, "output.stlFile");
  sample.deltaStepFileName = Require(kv, "output.deltaStepFile");
  sample.deltaStlFileName = Require(kv, "output.deltaStlFile");

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
    std::cerr << "Failed to load case file: " << casePath << std::endl;
    std::cerr << ex.what() << std::endl;
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
  if (!Check(L1_ApplyFeature(kernel, stockId, &sample.feature, &result), "L1_ApplyFeature")) {
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

  const std::string stepPath = (outDir / sample.stepFileName).string();
  const std::string stlPath = (outDir / sample.stlFileName).string();
  const std::string deltaStepPath = (outDir / sample.deltaStepFileName).string();
  const std::string deltaStlPath = (outDir / sample.deltaStlFileName).string();

  const auto exportStart = Clock::now();

  if (!Check(L1_ExportShape(kernel, result.resultShapeId, &stepOpt, stepPath.c_str()), "L1_ExportShape(STEP)")) {
    L1_DeleteShape(kernel, result.deltaShapeId);
    L1_DeleteShape(kernel, result.resultShapeId);
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }

  if (!Check(L1_ExportShape(kernel, result.resultShapeId, &stlOpt, stlPath.c_str()), "L1_ExportShape(STL)")) {
    L1_DeleteShape(kernel, result.deltaShapeId);
    L1_DeleteShape(kernel, result.resultShapeId);
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }

  if (!Check(L1_ExportShape(kernel, result.deltaShapeId, &stepOpt, deltaStepPath.c_str()), "L1_ExportShape(DELTA STEP)")) {
    L1_DeleteShape(kernel, result.deltaShapeId);
    L1_DeleteShape(kernel, result.resultShapeId);
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }

  if (!Check(L1_ExportShape(kernel, result.deltaShapeId, &stlOpt, deltaStlPath.c_str()), "L1_ExportShape(DELTA STL)")) {
    L1_DeleteShape(kernel, result.deltaShapeId);
    L1_DeleteShape(kernel, result.resultShapeId);
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }
  const auto exportEnd = Clock::now();

  L1_DeleteShape(kernel, result.deltaShapeId);
  L1_DeleteShape(kernel, result.resultShapeId);
  L1_DeleteShape(kernel, stockId);
  L1_DestroyKernel(kernel);

  std::cout << "Generated: " << stepPath << std::endl;
  std::cout << "Generated: " << stlPath << std::endl;
  std::cout << "Generated: " << deltaStepPath << std::endl;
  std::cout << "Generated: " << deltaStlPath << std::endl;

  const auto totalEnd = Clock::now();
  std::cout << std::fixed << std::setprecision(3);
  std::cout << "Timing(ms): LoadCaseFile=" << ElapsedMs(loadStart, loadEnd)
            << ", CreateKernel+CreateStock=" << ElapsedMs(kernelStart, kernelEnd)
            << ", ApplyFeature=" << ElapsedMs(applyStart, applyEnd)
            << ", Export=" << ElapsedMs(exportStart, exportEnd)
            << ", Total=" << ElapsedMs(totalStart, totalEnd) << std::endl;

  return 0;
}
