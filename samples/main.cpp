#include "l1_geometry_kernel.h"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
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
  if (featureType == "DRILL") {
    sample.feature.type = FEAT_DRILL;
    sample.feature.u.drill.radius = std::stod(Require(kv, "feature.drill.radius"));
    sample.feature.u.drill.depth = std::stod(Require(kv, "feature.drill.depth"));
    ParseVector3(Require(kv, "feature.drill.axis.origin"), sample.feature.u.drill.axis.origin);
    ParseVector3(Require(kv, "feature.drill.axis.dir"), sample.feature.u.drill.axis.dir);
    ParseVector3(Require(kv, "feature.drill.axis.xdir"), sample.feature.u.drill.axis.xdir);
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
    sample.feature.u.turnOd.profileCount = 0;
    const std::string* profileCount = Find(kv, "feature.turnOd.profile.count");
    if (profileCount) {
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > L1_TURN_OD_PROFILE_MAX) {
        throw std::runtime_error("feature.turnOd.profile.count out of range");
      }
      sample.feature.u.turnOd.profileCount = count;
      for (int index = 0; index < count; ++index) {
        const std::string zKey = "feature.turnOd.profile." + std::to_string(index) + ".z";
        const std::string rKey = "feature.turnOd.profile." + std::to_string(index) + ".radius";
        sample.feature.u.turnOd.profileZ[index] = std::stod(Require(kv, zKey));
        sample.feature.u.turnOd.profileRadius[index] = std::stod(Require(kv, rKey));
      }
      sample.feature.u.turnOd.targetDiameter = sample.feature.u.turnOd.profileRadius[0] * 2.0;
      sample.feature.u.turnOd.length = sample.feature.u.turnOd.profileZ[count - 1] - sample.feature.u.turnOd.profileZ[0];
    } else {
      sample.feature.u.turnOd.targetDiameter = std::stod(Require(kv, "feature.turnOd.targetDiameter"));
      sample.feature.u.turnOd.length = std::stod(Require(kv, "feature.turnOd.length"));
    }
    ParseVector3(Require(kv, "feature.turnOd.axis.origin"), sample.feature.u.turnOd.axis.origin);
    ParseVector3(Require(kv, "feature.turnOd.axis.dir"), sample.feature.u.turnOd.axis.dir);
    ParseVector3(Require(kv, "feature.turnOd.axis.xdir"), sample.feature.u.turnOd.axis.xdir);
  } else if (featureType == "TURN_ID") {
    sample.feature.type = FEAT_TURN_ID;
    sample.feature.u.turnId.profileCount = 0;
    const std::string* profileCount = Find(kv, "feature.turnId.profile.count");
    if (profileCount) {
      const int count = std::stoi(*profileCount);
      if (count < 2 || count > L1_TURN_OD_PROFILE_MAX) {
        throw std::runtime_error("feature.turnId.profile.count out of range");
      }
      sample.feature.u.turnId.profileCount = count;
      for (int index = 0; index < count; ++index) {
        const std::string zKey = "feature.turnId.profile." + std::to_string(index) + ".z";
        const std::string rKey = "feature.turnId.profile." + std::to_string(index) + ".radius";
        sample.feature.u.turnId.profileZ[index] = std::stod(Require(kv, zKey));
        sample.feature.u.turnId.profileRadius[index] = std::stod(Require(kv, rKey));
      }
      sample.feature.u.turnId.targetDiameter = sample.feature.u.turnId.profileRadius[0] * 2.0;
      sample.feature.u.turnId.length = sample.feature.u.turnId.profileZ[count - 1] - sample.feature.u.turnId.profileZ[0];
    } else {
      sample.feature.u.turnId.targetDiameter = std::stod(Require(kv, "feature.turnId.targetDiameter"));
      sample.feature.u.turnId.length = std::stod(Require(kv, "feature.turnId.length"));
    }
    ParseVector3(Require(kv, "feature.turnId.axis.origin"), sample.feature.u.turnId.axis.origin);
    ParseVector3(Require(kv, "feature.turnId.axis.dir"), sample.feature.u.turnId.axis.dir);
    ParseVector3(Require(kv, "feature.turnId.axis.xdir"), sample.feature.u.turnId.axis.xdir);
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
  const std::filesystem::path casePath = (argc >= 2)
      ? std::filesystem::path(argv[1])
      : std::filesystem::path("samples") / "box_drill_case.txt";

  SampleCase sample{};
  try {
    sample = LoadCaseFile(casePath);
  } catch (const std::exception& ex) {
    std::cerr << "Failed to load case file: " << casePath << std::endl;
    std::cerr << ex.what() << std::endl;
    return 1;
  }

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

  OperationResult result{};
  if (!Check(L1_ApplyFeature(kernel, stockId, &sample.feature, &result), "L1_ApplyFeature")) {
    L1_DeleteShape(kernel, stockId);
    L1_DestroyKernel(kernel);
    return 1;
  }

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

  L1_DeleteShape(kernel, result.deltaShapeId);
  L1_DeleteShape(kernel, result.resultShapeId);
  L1_DeleteShape(kernel, stockId);
  L1_DestroyKernel(kernel);

  std::cout << "Generated: " << stepPath << std::endl;
  std::cout << "Generated: " << stlPath << std::endl;
  std::cout << "Generated: " << deltaStepPath << std::endl;
  std::cout << "Generated: " << deltaStlPath << std::endl;
  return 0;
}
