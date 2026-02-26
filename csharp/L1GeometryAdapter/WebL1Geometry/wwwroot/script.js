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
      type: "DRILL",
      drill: {
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
      type: "DRILL",
      drill: {
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
  }
};

const editorEl = document.getElementById("editor");
const statusEl = document.getElementById("status");
const btnFormat = document.getElementById("btnFormat");
const btnRun = document.getElementById("btnRun");
const btnReset = document.getElementById("btnReset");

btnFormat.addEventListener("click", formatJson);
btnRun.addEventListener("click", runPipeline);
btnReset.addEventListener("click", resetEditor);

let scene;
let camera;
let renderer;
let controls;
let currentMesh = null;
const worldAxesSize = 120;

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
    await loadStlFromApi(body.finalStlUrl);
  } catch (error) {
    setStatus(`通信エラー: ${error.message}`);
  }
}

async function loadStlFromApi(url) {
  const response = await fetch(`${url}?t=${Date.now()}`);
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  loadStl(objectUrl, true);
}

function initThree() {
  const container = document.getElementById("viewer");

  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x111111);

  camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 1, 5000);
  camera.position.set(180, 140, 180);

  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  renderer.setSize(container.clientWidth, container.clientHeight);
  container.appendChild(renderer.domElement);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;

  scene.add(new THREE.AmbientLight(0xffffff, 0.6));
  const directional = new THREE.DirectionalLight(0xffffff, 0.8);
  directional.position.set(120, 180, 80);
  scene.add(directional);

  const grid = new THREE.GridHelper(400, 20, 0x666666, 0x333333);
  scene.add(grid);

  const axes = new THREE.AxesHelper(worldAxesSize);
  scene.add(axes);

  window.addEventListener("resize", onResize);
  animate();
}

function onResize() {
  const container = document.getElementById("viewer");
  const width = container.clientWidth;
  const height = container.clientHeight;
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  renderer.setSize(width, height);
}

function animate() {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
}

function loadStl(url, revokeOnDone = false) {
  const loader = new STLLoader();
  loader.load(
    url,
    (geometry) => {
      if (currentMesh) {
        scene.remove(currentMesh);
        currentMesh.geometry.dispose();
      }

      geometry.computeVertexNormals();
      geometry.center();

      const material = new THREE.MeshPhongMaterial({ color: 0xcccccc });
      const mesh = new THREE.Mesh(geometry, material);
      mesh.rotation.x = -Math.PI / 2;

      scene.add(mesh);
      currentMesh = mesh;

      const box = new THREE.Box3().setFromObject(mesh);
      const size = box.getSize(new THREE.Vector3()).length() || 120;
      const center = box.getCenter(new THREE.Vector3());
      controls.target.copy(center);
      camera.position.set(center.x + size, center.y + size * 0.8, center.z + size);
      camera.lookAt(center);

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

resetEditor();
initThree();
