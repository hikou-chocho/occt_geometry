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
    sessionId: "sess-browser-stage-preview"
  }
};

const editorEl = document.getElementById("editor");
const editorViewEl = document.getElementById("editorView");
const listViewEl = document.getElementById("listView");
const itemListEl = document.getElementById("itemList");
const statusEl = document.getElementById("status");
const btnModeJson = document.getElementById("btnModeJson");
const btnModeList = document.getElementById("btnModeList");
const btnFormat = document.getElementById("btnFormat");
const btnRun = document.getElementById("btnRun");
const btnReset = document.getElementById("btnReset");
const btnToggleCompare = document.getElementById("btnToggleCompare");
const viewerLayout = document.getElementById("viewerLayout");
const referenceDropZone = document.getElementById("referenceDropZone");
const referenceFileInput = document.getElementById("referenceFileInput");
const mainPaneHeader = document.querySelector("#mainPane .viewerPaneHeader");
const referencePaneHeader = document.querySelector("#referencePane .viewerPaneHeader");
const featureEditOverlayEl = document.getElementById("featureEditOverlay");

const EDITABLE_FEATURE_KEYS = {
  MILL_HOLE: { path: "millHole", fields: ["radius", "depth"] },
  POCKET_RECT: { path: "pocketRect", fields: ["width", "height", "depth"] }
};

const mainViewerTitle = ensureHeaderLine("mainViewerTitle", "viewerPaneHeaderStrong", "Preview");
const mainViewerSubtitle = ensureHeaderLine("mainViewerSubtitle", "viewerPaneHeaderSub", "Final model");
replaceHeaderText(referencePaneHeader, "Reference", "Drop STEP to compare");

btnModeJson.addEventListener("click", () => void setMode("json"));
btnModeList.addEventListener("click", () => void setMode("list"));
btnFormat.addEventListener("click", formatJson);
btnRun.addEventListener("click", runPipeline);
btnReset.addEventListener("click", resetEditor);
btnToggleCompare.addEventListener("click", toggleCompareViewer);

const worldAxesSize = 120;
const viewerStates = {
  main: null,
  reference: null
};

const state = {
  activeMode: "json",
  jobState: cloneJob(defaultJob),
  selectedItemKey: "output",
  previewCache: new Map(),
  previewRequestToken: 0,
  previewDebounceTimer: null
};

let compareVisible = false;
let syncCleanup = null;
let isSyncingCamera = false;

function ensureHeaderLine(id, className, fallbackText) {
  let element = document.getElementById(id);
  if (!element) {
    const wrapper = mainPaneHeader.querySelector(".viewerPaneHeaderText") ?? createHeaderTextWrapper();
    element = document.createElement("span");
    element.id = id;
    element.className = className;
    element.textContent = fallbackText;
    wrapper.appendChild(element);
  }
  return element;
}

function createHeaderTextWrapper() {
  const wrapper = document.createElement("div");
  wrapper.className = "viewerPaneHeaderText";
  mainPaneHeader.replaceChildren(wrapper);
  return wrapper;
}

function replaceHeaderText(header, title, subtitle) {
  if (!header) {
    return;
  }

  const wrapper = document.createElement("div");
  wrapper.className = "viewerPaneHeaderText";

  const titleEl = document.createElement("span");
  titleEl.className = "viewerPaneHeaderStrong";
  titleEl.textContent = title;

  const subtitleEl = document.createElement("span");
  subtitleEl.className = "viewerPaneHeaderSub";
  subtitleEl.textContent = subtitle;

  wrapper.append(titleEl, subtitleEl);
  header.replaceChildren(wrapper);
}

function cloneJob(job) {
  return JSON.parse(JSON.stringify(job));
}

function setStatus(message) {
  const timestamp = new Date().toLocaleTimeString();
  statusEl.textContent = `[${timestamp}] ${message}`;
}

function getEditorJson() {
  try {
    return { ok: true, data: JSON.parse(editorEl.value) };
  } catch (error) {
    return { ok: false, error };
  }
}

function syncEditorFromState() {
  editorEl.value = JSON.stringify(state.jobState, null, 2);
}

function cacheKeyForStage(job, stageIndex) {
  return `${stageIndex}|${JSON.stringify(job)}`;
}

function getListItems(job) {
  const items = [
    { key: "stock", label: "stock", detail: `${job.stock?.type ?? "UNKNOWN"} stock`, stageIndex: -1 }
  ];

  job.features.forEach((feature, index) => {
    items.push({
      key: `feature-${index}`,
      label: `feature${index + 1}`,
      detail: feature.type ?? "UNKNOWN",
      stageIndex: index
    });
  });

  items.push({
    key: "output",
    label: "output",
    detail: "Final stage",
    stageIndex: job.features.length
  });

  return items;
}

function getSelectedItem() {
  const items = getListItems(state.jobState);
  return items.find((item) => item.key === state.selectedItemKey) ?? items[items.length - 1];
}

function getFeatureIndexFromKey(itemKey) {
  const match = /^feature-(\d+)$/.exec(itemKey ?? "");
  return match ? Number.parseInt(match[1], 10) : null;
}

function getSelectedFeatureTarget() {
  const featureIndex = getFeatureIndexFromKey(state.selectedItemKey);
  if (featureIndex == null) {
    return null;
  }

  const feature = state.jobState.features?.[featureIndex];
  if (!feature) {
    return null;
  }

  const editableConfig = EDITABLE_FEATURE_KEYS[feature.type];
  return { feature, featureIndex, editableConfig };
}

function clearOverlayInputErrorState() {
  featureEditOverlayEl?.querySelectorAll(".featureEditInput").forEach((input) => {
    input.classList.remove("input-error");
  });
}

function markOverlayInputsError() {
  featureEditOverlayEl?.querySelectorAll(".featureEditInput").forEach((input) => {
    input.classList.add("input-error");
  });
}

function schedulePreviewRefresh() {
  if (state.previewDebounceTimer) {
    window.clearTimeout(state.previewDebounceTimer);
  }

  state.previewDebounceTimer = window.setTimeout(() => {
    state.previewDebounceTimer = null;
    if (state.activeMode === "list") {
      void loadSelectedPreview();
    }
  }, 220);
}

function renderFeatureEditOverlay() {
  if (!featureEditOverlayEl) {
    return;
  }

  const target = getSelectedFeatureTarget();
  if (!target) {
    featureEditOverlayEl.hidden = true;
    featureEditOverlayEl.replaceChildren();
    return;
  }

  const { feature, featureIndex, editableConfig } = target;
  featureEditOverlayEl.hidden = false;
  featureEditOverlayEl.replaceChildren();

  const title = document.createElement("div");
  title.className = "featureEditTitle";
  title.textContent = `feature${featureIndex + 1}: ${feature.type}`;
  featureEditOverlayEl.appendChild(title);

  if (!editableConfig) {
    const readOnly = document.createElement("div");
    readOnly.className = "featureEditReadonly";
    readOnly.textContent = "This feature type is read-only in this editor.";
    featureEditOverlayEl.appendChild(readOnly);
    return;
  }

  const container = feature[editableConfig.path];
  if (!container) {
    const readOnly = document.createElement("div");
    readOnly.className = "featureEditReadonly";
    readOnly.textContent = "Editable payload is missing for this feature.";
    featureEditOverlayEl.appendChild(readOnly);
    return;
  }

  editableConfig.fields.forEach((fieldName) => {
    const row = document.createElement("div");
    row.className = "featureEditRow";

    const label = document.createElement("label");
    label.textContent = fieldName;
    label.htmlFor = `feature-edit-${featureIndex}-${fieldName}`;

    const input = document.createElement("input");
    input.id = label.htmlFor;
    input.type = "number";
    input.step = "any";
    input.min = "0";
    input.className = "featureEditInput";
    input.value = container[fieldName];

    input.addEventListener("input", () => {
      const nextValue = Number.parseFloat(input.value);
      const isValid = Number.isFinite(nextValue) && nextValue > 0;
      input.classList.toggle("input-error", !isValid);
      if (!isValid) {
        setStatus(`Invalid value for ${fieldName}: positive number required.`);
        return;
      }

      container[fieldName] = nextValue;
      state.previewCache.clear();
      clearOverlayInputErrorState();
      schedulePreviewRefresh();
      setStatus(`Updated ${fieldName} for feature${featureIndex + 1}.`);
    });

    row.append(label, input);
    featureEditOverlayEl.appendChild(row);
  });
}

function renderItemList() {
  const items = getListItems(state.jobState);
  itemListEl.replaceChildren();

  items.forEach((item) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "listItemButton";
    if (item.key === state.selectedItemKey) {
      button.classList.add("active");
    }

    const primary = document.createElement("span");
    primary.className = "listItemPrimary";
    primary.textContent = item.label;

    const secondary = document.createElement("span");
    secondary.className = "listItemSecondary";
    secondary.textContent = item.detail;

    button.append(primary, secondary);
    button.addEventListener("click", () => {
      void selectListItem(item.key);
    });

    const li = document.createElement("li");
    li.appendChild(button);
    itemListEl.appendChild(li);
  });
}

async function setMode(mode) {
  if (mode === state.activeMode) {
    return;
  }

  if (mode === "list") {
    const parsed = getEditorJson();
    if (!parsed.ok) {
      setStatus(`JSON parse error: ${parsed.error.message}`);
      return;
    }
    state.jobState = parsed.data;
    renderItemList();
    renderFeatureEditOverlay();
  } else {
    syncEditorFromState();
  }

  state.activeMode = mode;
  editorViewEl.classList.toggle("active", mode === "json");
  listViewEl.classList.toggle("active", mode === "list");
  btnModeJson.classList.toggle("active", mode === "json");
  btnModeList.classList.toggle("active", mode === "list");

  if (mode === "list") {
    renderFeatureEditOverlay();
    await loadSelectedPreview();
  } else {
    renderFeatureEditOverlay();
  }
}

function formatJson() {
  const parsed = getEditorJson();
  if (!parsed.ok) {
    setStatus(`JSON parse error: ${parsed.error.message}`);
    return;
  }

  state.jobState = parsed.data;
  syncEditorFromState();
  state.previewCache.clear();
  renderItemList();
  setStatus("JSON formatted.");
}

function resetEditor() {
  state.jobState = cloneJob(defaultJob);
  state.selectedItemKey = "output";
  state.previewCache.clear();
  if (state.previewDebounceTimer) {
    window.clearTimeout(state.previewDebounceTimer);
    state.previewDebounceTimer = null;
  }
  syncEditorFromState();
  renderItemList();
  renderFeatureEditOverlay();
  updateMainViewerLabels(getSelectedItem(), false);

  if (state.activeMode === "list") {
    void loadSelectedPreview();
  }

  setStatus("Default job loaded.");
}

function currentJobOrError() {
  if (state.activeMode === "json") {
    const parsed = getEditorJson();
    if (!parsed.ok) {
      setStatus(`JSON parse error: ${parsed.error.message}`);
      return null;
    }
    state.jobState = parsed.data;
  }

  return state.jobState;
}

async function runPipeline() {
  const job = currentJobOrError();
  if (!job) {
    return;
  }

  setStatus("Running /pipeline/run ...");

  try {
    const response = await fetch("/pipeline/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(job)
    });

    const body = await response.json();
    if (!response.ok) {
      const msg = body?.error || body?.detail || `HTTP ${response.status}`;
      setStatus(`Run failed: ${msg}`);
      return;
    }

    updateMainViewerLabels({ label: "output", detail: "Run result" }, false);
    await setViewerMeshes(viewerStates.main, { modelUrl: body.finalStlUrl, deltaUrl: null });
    setStatus(`Run finished: ${body.finalStlUrl}`);
  } catch (error) {
    setStatus(`Runtime error: ${error.message}`);
  }
}

async function selectListItem(itemKey) {
  state.selectedItemKey = itemKey;
  renderItemList();
  renderFeatureEditOverlay();

  if (state.activeMode === "list") {
    await loadSelectedPreview();
  }
}

function updateMainViewerLabels(item, hasDelta) {
  const label = item?.label ?? "preview";
  mainViewerTitle.textContent = `Preview: ${label}`;

  if (label === "stock") {
    mainViewerSubtitle.textContent = "Stock only";
  } else if (label === "output") {
    mainViewerSubtitle.textContent = "Final model";
  } else {
    mainViewerSubtitle.textContent = hasDelta ? `${item.detail} with removal delta` : item.detail;
  }
}

async function loadSelectedPreview() {
  const item = getSelectedItem();
  const job = cloneJob(state.jobState);
  const cacheKey = cacheKeyForStage(job, item.stageIndex);
  const token = ++state.previewRequestToken;

  updateMainViewerLabels(item, item.key.startsWith("feature"));
  clearOverlayInputErrorState();

  try {
    let preview = state.previewCache.get(cacheKey);
    if (!preview) {
      setStatus(`Generating preview for ${item.label} ...`);
      const response = await fetch("/pipeline/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ job, stageIndex: item.stageIndex })
      });

      const body = await response.json();
      if (!response.ok) {
        const msg = body?.error || body?.detail || `HTTP ${response.status}`;
        setStatus(`Preview failed: ${msg}`);
        markOverlayInputsError();
        return;
      }

      preview = body;
      state.previewCache.set(cacheKey, preview);
    }

    if (token !== state.previewRequestToken) {
      return;
    }

    const shouldShowDelta = item.key.startsWith("feature");
    await setViewerMeshes(viewerStates.main, {
      modelUrl: preview.modelStlUrl,
      deltaUrl: shouldShowDelta ? preview.deltaStlUrl ?? null : null
    });

    updateMainViewerLabels(item, shouldShowDelta && Boolean(preview.deltaStlUrl));
    setStatus(`Preview ready: ${item.label}`);
  } catch (error) {
    markOverlayInputsError();
    setStatus(`Preview failed: ${error.message}`);
  }
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

  const meshGroup = new THREE.Group();
  scene.add(meshGroup);

  return {
    container,
    scene,
    camera,
    renderer,
    controls,
    meshGroup
  };
}

function resizeViewer(viewerState) {
  if (!viewerState) {
    return;
  }

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

function clearViewerMeshes(viewerState) {
  if (!viewerState) {
    return;
  }

  const children = [...viewerState.meshGroup.children];
  children.forEach((child) => {
    viewerState.meshGroup.remove(child);
    child.geometry?.dispose?.();
    if (Array.isArray(child.material)) {
      child.material.forEach((material) => material.dispose());
    } else {
      child.material?.dispose?.();
    }
  });
}

async function fetchStlBlobUrl(url) {
  const response = await fetch(`${url}${url.includes("?") ? "&" : "?"}t=${Date.now()}`);
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  const blob = await response.blob();
  return URL.createObjectURL(blob);
}

function loadMesh(url, material) {
  const loader = new STLLoader();
  return new Promise((resolve, reject) => {
    loader.load(
      url,
      (geometry) => {
        geometry.computeVertexNormals();
        const mesh = new THREE.Mesh(geometry, material);
        mesh.rotation.x = -Math.PI / 2;
        resolve(mesh);
      },
      undefined,
      reject
    );
  });
}

function fitViewerToGroup(viewerState) {
  const box = new THREE.Box3().setFromObject(viewerState.meshGroup);
  if (box.isEmpty()) {
    return;
  }

  const size = box.getSize(new THREE.Vector3()).length() || 120;
  const center = box.getCenter(new THREE.Vector3());
  viewerState.controls.target.copy(center);
  viewerState.camera.position.set(center.x + size, center.y + size * 0.8, center.z + size);
  viewerState.camera.lookAt(center);
  viewerState.controls.update();
}

async function setViewerMeshes(viewerState, { modelUrl, deltaUrl }) {
  if (!viewerState) {
    throw new Error("Viewer is not initialized.");
  }

  const objectUrls = [];
  try {
    const modelBlobUrl = await fetchStlBlobUrl(modelUrl);
    objectUrls.push(modelBlobUrl);

    let deltaBlobUrl = null;
    if (deltaUrl) {
      deltaBlobUrl = await fetchStlBlobUrl(deltaUrl);
      objectUrls.push(deltaBlobUrl);
    }

    const modelMaterial = new THREE.MeshPhongMaterial({ color: 0xcccccc });
    const deltaMaterial = new THREE.MeshPhongMaterial({
      color: 0x7fcfff,
      transparent: true,
      opacity: 0.45,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -2
    });

    const meshPromises = [loadMesh(modelBlobUrl, modelMaterial)];
    if (deltaBlobUrl) {
      meshPromises.push(loadMesh(deltaBlobUrl, deltaMaterial));
    }

    const meshes = await Promise.all(meshPromises);
    clearViewerMeshes(viewerState);
    meshes.forEach((mesh) => viewerState.meshGroup.add(mesh));
    fitViewerToGroup(viewerState);

    if (compareVisible && viewerStates.main && viewerStates.reference) {
      syncCameraState(viewerState, viewerState === viewerStates.main ? viewerStates.reference : viewerStates.main);
    }
  } finally {
    objectUrls.forEach((url) => URL.revokeObjectURL(url));
  }
}

function syncCameraState(sourceViewer, targetViewer) {
  if (!sourceViewer || !targetViewer || isSyncingCamera) {
    return;
  }

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

  if (!viewerStates.main || !viewerStates.reference) {
    return;
  }

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
  btnToggleCompare.textContent = visible ? "Compare Off" : "Compare";

  if (visible) {
    ensureReferenceViewer();
    resizeViewer(viewerStates.main);
    resizeViewer(viewerStates.reference);
    startCameraSync();
    setStatus("Compare view enabled.");
  } else {
    stopCameraSync();
    resizeViewer(viewerStates.main);
    setStatus("Compare view hidden.");
  }
}

function toggleCompareViewer() {
  setCompareVisibility(!compareVisible);
}

function validateStepFile(file) {
  if (!file) {
    return "Select a STEP file.";
  }

  const lower = file.name.toLowerCase();
  if (!(lower.endsWith(".step") || lower.endsWith(".stp"))) {
    return "Only .step/.stp files are supported.";
  }

  const maxUploadBytes = 50 * 1024 * 1024;
  if (file.size > maxUploadBytes) {
    return "STEP file is too large (max 50MB).";
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
  setStatus(`Importing reference STEP: ${file.name}`);

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
      setStatus(`Reference import failed: ${msg}`);
      return;
    }

    await setViewerMeshes(viewerStates.reference, { modelUrl: body.referenceStlUrl, deltaUrl: null });
    setStatus("Reference STEP loaded.");
  } catch (error) {
    setStatus(`Reference import error: ${error.message}`);
  }
}

function setupReferenceDropZone() {
  referenceDropZone.textContent = "Drop STEP here or click to select (.step / .stp)";

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
btnFormat.textContent = "Format";
btnRun.textContent = "Run";
btnToggleCompare.textContent = "Compare";
btnReset.textContent = "Reset";
viewerStates.main = createViewer("viewer");
window.addEventListener("resize", onResize);
setupReferenceDropZone();
onResize();
animate();
