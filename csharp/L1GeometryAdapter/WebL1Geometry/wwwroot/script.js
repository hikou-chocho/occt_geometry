import * as THREE from "https://unpkg.com/three@0.160.0/build/three.module.js";
import { OrbitControls } from "https://unpkg.com/three@0.160.0/examples/jsm/controls/OrbitControls.js";
import { STLLoader } from "https://unpkg.com/three@0.160.0/examples/jsm/loaders/STLLoader.js";

const defaultJob = {
  stock: {
    type: "BOX",
    p1: 100.0,
    p2: 80.0,
    p3: 20.0,
    axis: {
      origin: [0.0, 0.0, 0.0],
      dir: [0.0, 0.0, 1.0],
      xdir: [1.0, 0.0, 0.0]
    }
  },
  features: [
    {
      type: "MILL_HOLE",
      millHole: {
        radius: 8.0,
        depth: 12.0,
        axis: {
          origin: [30.0, 20.0, 20.0],
          dir: [0.0, 0.0, -1.0],
          xdir: [1.0, 0.0, 0.0]
        }
      }
    },
    {
      type: "MILL_HOLE",
      millHole: {
        radius: 8.0,
        depth: 12.0,
        axis: {
          origin: [70.0, 20.0, 20.0],
          dir: [0.0, 0.0, -1.0],
          xdir: [1.0, 0.0, 0.0]
        }
      }
    },
    {
      type: "POCKET_RECT",
      pocketRect: {
        width: 40.0,
        height: 30.0,
        depth: 8.0,
        axis: {
          origin: [50.0, 55.0, 20.0],
          dir: [0.0, 0.0, -1.0],
          xdir: [1.0, 0.0, 0.0]
        }
      }
    }
  ],
  output: {
    linearDeflection: 0.1,
    angularDeflection: 0.5,
    parallel: 1,
    dir: "out",
    stepFile: "result.step",
    stlFile: "result.stl",
    deltaStepFile: "delta.step",
    deltaStlFile: "delta.stl"
  },
  meta: {
    sessionId: "sess-browser-script"
  }
};

const editorEl = document.getElementById("editor");
const statusEl = document.getElementById("status");
const btnFormat = document.getElementById("btnFormat");
const btnRun = document.getElementById("btnRun");
const btnReset = document.getElementById("btnReset");
const btnToggleCompare = document.getElementById("btnToggleCompare");
const viewerLayout = document.getElementById("viewerLayout");
const referenceDropZone = document.getElementById("referenceDropZone");
const referenceFileInput = document.getElementById("referenceFileInput");

btnFormat.addEventListener("click", formatJson);
btnRun.addEventListener("click", runPipeline);
btnReset.addEventListener("click", resetEditor);
btnToggleCompare.addEventListener("click", toggleCompareViewer);

const worldAxesSize = 120;
const viewerStates = {
  main: null,
  reference: null
};

let compareVisible = false;
let syncCleanup = null;
let isSyncingCamera = false;

function setStatus(message) {
  const timestamp = new Date().toLocaleTimeString();
  statusEl.textContent = `[${timestamp}] ${message}`;
}

function getEditorJson() {
  try {
    const data = JSON.parse(editorEl.value);
    return { ok: true, data };
  } catch (error) {
    return { ok: false, error };
  }
}

function formatJson() {
  const parsed = getEditorJson();
  if (!parsed.ok) {
    setStatus(`JSON parse error: ${parsed.error.message}`);
    return;
  }
  editorEl.value = JSON.stringify(parsed.data, null, 2);
  setStatus("JSON を整形しました。");
}

function resetEditor() {
  editorEl.value = JSON.stringify(defaultJob, null, 2);
  setStatus("初期 JSON を読み込みました。");
}

async function runPipeline() {
  const parsed = getEditorJson();
  if (!parsed.ok) {
    setStatus(`JSON parse error: ${parsed.error.message}`);
    return;
  }

  setStatus("/pipeline/run 実行中...");

  try {
    const response = await fetch("/pipeline/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(parsed.data)
    });

    const body = await response.json();
    if (!response.ok) {
      const msg = body?.error || body?.detail || `HTTP ${response.status}`;
      setStatus(`実行失敗: ${msg}`);
      return;
    }

    setStatus(`STL 読み込み: ${body.finalStlUrl}`);
    await loadStlFromApi(body.finalStlUrl, viewerStates.main);
  } catch (error) {
    setStatus(`通信エラー: ${error.message}`);
  }
}

async function loadStlFromApi(url, viewerState) {
  if (!viewerState) {
    throw new Error("Viewer is not initialized.");
  }

  const response = await fetch(`${url}?t=${Date.now()}`);
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  loadStl(viewerState, objectUrl, true);
}

function createViewer(containerId) {
  const container = document.getElementById(containerId);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x111111);

  const camera = new THREE.PerspectiveCamera(45, 1, 1, 5000);
  camera.position.set(180, 140, 180);

  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  container.appendChild(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;

  scene.add(new THREE.AmbientLight(0xffffff, 0.6));
  const directional = new THREE.DirectionalLight(0xffffff, 0.8);
  directional.position.set(120, 180, 80);
  scene.add(directional);

  const grid = new THREE.GridHelper(400, 20, 0x666666, 0x333333);
  scene.add(grid);

  const axes = new THREE.AxesHelper(worldAxesSize);
  scene.add(axes);

  return {
    container,
    scene,
    camera,
    renderer,
    controls,
    currentMesh: null
  };
}

function resizeViewer(viewerState) {
  if (!viewerState) return;
  const width = Math.max(1, viewerState.container.clientWidth);
  const height = Math.max(1, viewerState.container.clientHeight);
  viewerState.camera.aspect = width / height;
  viewerState.camera.updateProjectionMatrix();
  viewerState.renderer.setSize(width, height);
}

function onResize() {
  resizeViewer(viewerStates.main);
  if (compareVisible) {
    resizeViewer(viewerStates.reference);
  }
}

function animate() {
  requestAnimationFrame(animate);

  if (viewerStates.main) {
    viewerStates.main.controls.update();
    viewerStates.main.renderer.render(viewerStates.main.scene, viewerStates.main.camera);
  }

  if (compareVisible && viewerStates.reference) {
    viewerStates.reference.controls.update();
    viewerStates.reference.renderer.render(viewerStates.reference.scene, viewerStates.reference.camera);
  }
}

function disposeCurrentMesh(viewerState) {
  if (!viewerState || !viewerState.currentMesh) return;

  viewerState.scene.remove(viewerState.currentMesh);
  viewerState.currentMesh.geometry.dispose();
  if (Array.isArray(viewerState.currentMesh.material)) {
    viewerState.currentMesh.material.forEach((mat) => mat.dispose());
  } else if (viewerState.currentMesh.material) {
    viewerState.currentMesh.material.dispose();
  }
  viewerState.currentMesh = null;
}

function loadStl(viewerState, url, revokeOnDone = false) {
  const loader = new STLLoader();
  loader.load(
    url,
    (geometry) => {
      disposeCurrentMesh(viewerState);

      geometry.computeVertexNormals();
      geometry.center();

      const material = new THREE.MeshPhongMaterial({ color: 0xcccccc });
      const mesh = new THREE.Mesh(geometry, material);
      mesh.rotation.x = -Math.PI / 2;

      viewerState.scene.add(mesh);
      viewerState.currentMesh = mesh;

      const box = new THREE.Box3().setFromObject(mesh);
      const size = box.getSize(new THREE.Vector3()).length() || 120;
      const center = box.getCenter(new THREE.Vector3());
      viewerState.controls.target.copy(center);
      viewerState.camera.position.set(center.x + size, center.y + size * 0.8, center.z + size);
      viewerState.camera.lookAt(center);
      viewerState.controls.update();

      if (compareVisible && viewerStates.main && viewerStates.reference) {
        syncCameraState(viewerState, viewerState === viewerStates.main ? viewerStates.reference : viewerStates.main);
      }

      if (revokeOnDone) {
        URL.revokeObjectURL(url);
      }
    },
    undefined,
    (error) => {
      if (revokeOnDone) {
        URL.revokeObjectURL(url);
      }
      setStatus(`STL load error: ${error.message}`);
    }
  );
}

function syncCameraState(sourceViewer, targetViewer) {
  if (!sourceViewer || !targetViewer || isSyncingCamera) return;

  isSyncingCamera = true;
  targetViewer.camera.position.copy(sourceViewer.camera.position);
  targetViewer.camera.quaternion.copy(sourceViewer.camera.quaternion);
  targetViewer.camera.zoom = sourceViewer.camera.zoom;
  targetViewer.camera.updateProjectionMatrix();
  targetViewer.controls.target.copy(sourceViewer.controls.target);
  targetViewer.controls.update();
  isSyncingCamera = false;
}

function startCameraSync() {
  stopCameraSync();

  if (!viewerStates.main || !viewerStates.reference) return;

  const onMainChanged = () => syncCameraState(viewerStates.main, viewerStates.reference);
  const onReferenceChanged = () => syncCameraState(viewerStates.reference, viewerStates.main);

  viewerStates.main.controls.addEventListener("change", onMainChanged);
  viewerStates.reference.controls.addEventListener("change", onReferenceChanged);

  syncCleanup = () => {
    viewerStates.main?.controls.removeEventListener("change", onMainChanged);
    viewerStates.reference?.controls.removeEventListener("change", onReferenceChanged);
    syncCleanup = null;
  };
}

function stopCameraSync() {
  if (syncCleanup) {
    syncCleanup();
  }
}

function ensureReferenceViewer() {
  if (!viewerStates.reference) {
    viewerStates.reference = createViewer("referenceViewer");
  }
  resizeViewer(viewerStates.reference);
}

function setCompareVisibility(visible) {
  compareVisible = visible;

  viewerLayout.classList.toggle("compare-active", visible);
  btnToggleCompare.textContent = visible ? "比較ビュー非表示" : "比較ビュー表示";

  if (visible) {
    ensureReferenceViewer();
    resizeViewer(viewerStates.main);
    resizeViewer(viewerStates.reference);
    startCameraSync();
    setStatus("比較ビューを表示しました。STEPをドロップしてください。");
  } else {
    stopCameraSync();
    resizeViewer(viewerStates.main);
    setStatus("比較ビューを非表示にしました。");
  }
}

function toggleCompareViewer() {
  setCompareVisibility(!compareVisible);
}

function validateStepFile(file) {
  if (!file) return "ファイルが選択されていません。";

  const lower = file.name.toLowerCase();
  if (!(lower.endsWith(".step") || lower.endsWith(".stp"))) {
    return "STEPファイル（.step/.stp）のみ対応しています。";
  }

  const maxUploadBytes = 50 * 1024 * 1024;
  if (file.size > maxUploadBytes) {
    return "STEPファイルが大きすぎます（最大50MB）。";
  }

  return null;
}

async function importReferenceStep(file) {
  const fileError = validateStepFile(file);
  if (fileError) {
    setStatus(fileError);
    return;
  }

  if (!compareVisible) {
    setCompareVisibility(true);
  }

  ensureReferenceViewer();
  setStatus(`正解STEP取込中: ${file.name}`);

  try {
    const formData = new FormData();
    formData.append("file", file);

    const response = await fetch("/pipeline/reference-step", {
      method: "POST",
      body: formData
    });

    const body = await response.json();
    if (!response.ok) {
      const msg = body?.error || body?.detail || `HTTP ${response.status}`;
      setStatus(`取込失敗: ${msg}`);
      return;
    }

    setStatus(`参照STL 読み込み: ${body.referenceStlUrl}`);
    await loadStlFromApi(body.referenceStlUrl, viewerStates.reference);
    setStatus("正解STEPを読み込みました。");
  } catch (error) {
    setStatus(`取込エラー: ${error.message}`);
  }
}

function setupReferenceDropZone() {
  const preventDefaults = (event) => {
    event.preventDefault();
    event.stopPropagation();
  };

  ["dragenter", "dragover", "dragleave", "drop"].forEach((eventName) => {
    referenceDropZone.addEventListener(eventName, preventDefaults);
  });

  ["dragenter", "dragover"].forEach((eventName) => {
    referenceDropZone.addEventListener(eventName, () => {
      referenceDropZone.classList.add("drag-over");
    });
  });

  ["dragleave", "drop"].forEach((eventName) => {
    referenceDropZone.addEventListener(eventName, () => {
      referenceDropZone.classList.remove("drag-over");
    });
  });

  referenceDropZone.addEventListener("drop", (event) => {
    const file = event.dataTransfer?.files?.[0];
    void importReferenceStep(file);
  });

  referenceDropZone.addEventListener("click", () => {
    referenceFileInput.click();
  });

  referenceFileInput.addEventListener("change", () => {
    const file = referenceFileInput.files?.[0];
    void importReferenceStep(file);
    referenceFileInput.value = "";
  });
}

resetEditor();
viewerStates.main = createViewer("viewer");
window.addEventListener("resize", onResize);
setupReferenceDropZone();
onResize();
animate();
