import * as THREE from "https://unpkg.com/three@0.160.0/build/three.module.js";
import { OrbitControls } from "https://unpkg.com/three@0.160.0/examples/jsm/controls/OrbitControls.js";
import { STLLoader } from "https://unpkg.com/three@0.160.0/examples/jsm/loaders/STLLoader.js";

const stageListEl = document.getElementById("stageList");
const statusEl = document.getElementById("status");
const viewerTitleEl = document.getElementById("viewerTitle");
const viewerSubtitleEl = document.getElementById("viewerSubtitle");
const sessionSummaryEl = document.getElementById("sessionSummary");
const btnRefresh = document.getElementById("btnRefresh");

const sessionId = new URLSearchParams(window.location.search).get("sessionId");
const state = {
  sessionId,
  job: null,
  selectedKey: "output",
  updatedAtUtc: null,
  previewRequestToken: 0,
  previewCache: new Map()
};

const viewerState = createViewer("viewer");

btnRefresh.addEventListener("click", () => {
  void loadSession(true);
});

window.addEventListener("resize", () => resizeViewer(viewerState));

if (!sessionId) {
  setStatus("Missing sessionId query parameter.");
  sessionSummaryEl.textContent = "Preview session was not specified.";
} else {
  void loadSession(false);
}

animate();

function setStatus(message) {
  const timestamp = new Date().toLocaleTimeString();
  statusEl.textContent = `[${timestamp}] ${message}`;
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
    detail: "Final model",
    stageIndex: job.features.length
  });

  return items;
}

function getSelectedItem() {
  if (!state.job) {
    return null;
  }

  const items = getListItems(state.job);
  return items.find((item) => item.key === state.selectedKey) ?? items[items.length - 1];
}

function renderStageList() {
  stageListEl.replaceChildren();
  if (!state.job) {
    return;
  }

  getListItems(state.job).forEach((item) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "stageButton";
    if (item.key === state.selectedKey) {
      button.classList.add("active");
    }

    const label = document.createElement("span");
    label.className = "stageLabel";
    label.textContent = item.label;

    const detail = document.createElement("span");
    detail.className = "stageDetail";
    detail.textContent = item.detail;

    button.append(label, detail);
    button.addEventListener("click", () => {
      state.selectedKey = item.key;
      renderStageList();
      void loadSelectedPreview();
    });

    const li = document.createElement("li");
    li.appendChild(button);
    stageListEl.appendChild(li);
  });
}

async function loadSession(forceReload) {
  if (!state.sessionId) {
    return;
  }

  if (forceReload) {
    state.previewCache.clear();
  }

  setStatus(`Loading preview session ${state.sessionId} ...`);

  try {
    const response = await fetch(`/preview-api/session/${encodeURIComponent(state.sessionId)}?t=${Date.now()}`);
    const body = await response.json();
    if (!response.ok || !body?.ok) {
      const message = body?.error || `HTTP ${response.status}`;
      setStatus(`Failed to load preview session: ${message}`);
      sessionSummaryEl.textContent = "Preview session is unavailable.";
      return;
    }

    state.job = body.job;
    state.updatedAtUtc = body.updatedAtUtc;
    sessionSummaryEl.textContent = `sessionId: ${body.sessionId}\nupdated: ${new Date(body.updatedAtUtc).toLocaleString()}`;

    if (!getListItems(state.job).some((item) => item.key === state.selectedKey)) {
      state.selectedKey = "output";
    }

    renderStageList();
    await loadSelectedPreview();
  } catch (error) {
    setStatus(`Failed to load preview session: ${error.message}`);
  }
}

async function loadSelectedPreview() {
  if (!state.job) {
    return;
  }

  const item = getSelectedItem();
  if (!item) {
    return;
  }

  const token = ++state.previewRequestToken;
  const cacheKey = `${item.stageIndex}|${JSON.stringify(state.job)}`;
  updateViewerLabels(item, item.key.startsWith("feature"));

  try {
    let preview = state.previewCache.get(cacheKey);
    if (!preview) {
      setStatus(`Generating preview for ${item.label} ...`);
      const response = await fetch("/pipeline/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ job: state.job, stageIndex: item.stageIndex })
      });

      const body = await response.json();
      if (!response.ok) {
        const message = body?.error || body?.detail || `HTTP ${response.status}`;
        setStatus(`Preview failed: ${message}`);
        return;
      }

      preview = body;
      state.previewCache.set(cacheKey, preview);
    }

    if (token !== state.previewRequestToken) {
      return;
    }

    const shouldShowDelta = item.key.startsWith("feature");
    await setViewerMeshes(viewerState, {
      modelUrl: preview.modelStlUrl,
      deltaUrl: shouldShowDelta ? preview.deltaStlUrl ?? null : null
    });

    updateViewerLabels(item, shouldShowDelta && Boolean(preview.deltaStlUrl));
    setStatus(`Preview ready: ${item.label}`);
  } catch (error) {
    setStatus(`Preview failed: ${error.message}`);
  }
}

function updateViewerLabels(item, hasDelta) {
  viewerTitleEl.textContent = `Preview: ${item.label}`;

  if (item.key === "stock") {
    viewerSubtitleEl.textContent = "Stock only";
  } else if (item.key === "output") {
    viewerSubtitleEl.textContent = "Final model";
  } else {
    viewerSubtitleEl.textContent = hasDelta ? `${item.detail} with delta` : item.detail;
  }
}

function createViewer(containerId) {
  const container = document.getElementById(containerId);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0f141b);

  const camera = new THREE.PerspectiveCamera(45, 1, 1, 5000);
  camera.position.set(180, 140, 180);

  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  container.appendChild(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;

  scene.add(new THREE.AmbientLight(0xffffff, 0.65));
  const directional = new THREE.DirectionalLight(0xffffff, 0.85);
  directional.position.set(120, 180, 80);
  scene.add(directional);

  const grid = new THREE.GridHelper(400, 20, 0x537188, 0x233141);
  scene.add(grid);

  const axes = new THREE.AxesHelper(120);
  scene.add(axes);

  const meshGroup = new THREE.Group();
  scene.add(meshGroup);

  resizeViewer({ container, camera, renderer });

  return {
    container,
    scene,
    camera,
    renderer,
    controls,
    meshGroup
  };
}

function resizeViewer(currentViewerState) {
  if (!currentViewerState) {
    return;
  }

  const width = Math.max(1, currentViewerState.container.clientWidth);
  const height = Math.max(1, currentViewerState.container.clientHeight);
  currentViewerState.camera.aspect = width / height;
  currentViewerState.camera.updateProjectionMatrix();
  currentViewerState.renderer.setSize(width, height);
}

function animate() {
  requestAnimationFrame(animate);
  viewerState.controls.update();
  viewerState.renderer.render(viewerState.scene, viewerState.camera);
}

function clearViewerMeshes(currentViewerState) {
  const children = [...currentViewerState.meshGroup.children];
  children.forEach((child) => {
    currentViewerState.meshGroup.remove(child);
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

function fitViewerToGroup(currentViewerState) {
  const box = new THREE.Box3().setFromObject(currentViewerState.meshGroup);
  if (box.isEmpty()) {
    return;
  }

  const size = box.getSize(new THREE.Vector3()).length() || 120;
  const center = box.getCenter(new THREE.Vector3());
  currentViewerState.controls.target.copy(center);
  currentViewerState.camera.position.set(center.x + size, center.y + size * 0.8, center.z + size);
  currentViewerState.camera.lookAt(center);
  currentViewerState.controls.update();
}

async function setViewerMeshes(currentViewerState, { modelUrl, deltaUrl }) {
  const objectUrls = [];
  try {
    const modelBlobUrl = await fetchStlBlobUrl(modelUrl);
    objectUrls.push(modelBlobUrl);

    let deltaBlobUrl = null;
    if (deltaUrl) {
      deltaBlobUrl = await fetchStlBlobUrl(deltaUrl);
      objectUrls.push(deltaBlobUrl);
    }

    const modelMaterial = new THREE.MeshPhongMaterial({ color: 0xcdd7e1 });
    const deltaMaterial = new THREE.MeshPhongMaterial({
      color: 0x7bdff2,
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
    clearViewerMeshes(currentViewerState);
    meshes.forEach((mesh) => currentViewerState.meshGroup.add(mesh));
    fitViewerToGroup(currentViewerState);
  } finally {
    objectUrls.forEach((url) => URL.revokeObjectURL(url));
  }
}
