function roundValue(value, digits) {
  const scale = 10 ** digits;
  return Math.round(value * scale) / scale;
}

function normalizeVector([x, y, z]) {
  const length = Math.hypot(x, y, z);
  if (length <= Number.EPSILON) {
    return [0, 0, 0];
  }

  return [x / length, y / length, z / length];
}

function rotateVectorByQuaternion([vx, vy, vz], quaternion) {
  const qx = quaternion.x;
  const qy = quaternion.y;
  const qz = quaternion.z;
  const qw = quaternion.w;

  const tx = 2 * (qy * vz - qz * vy);
  const ty = 2 * (qz * vx - qx * vz);
  const tz = 2 * (qx * vy - qy * vx);

  return [
    vx + qw * tx + (qy * tz - qz * ty),
    vy + qw * ty + (qz * tx - qx * tz),
    vz + qw * tz + (qx * ty - qy * tx)
  ];
}

function toRoundedArray([x, y, z], digits) {
  return [roundValue(x, digits), roundValue(y, digits), roundValue(z, digits)];
}

export function buildCameraAxisCsys(camera, digits = 3) {
  const origin = [camera.position.x, camera.position.y, camera.position.z];
  const dir = normalizeVector(rotateVectorByQuaternion([0, 0, -1], camera.quaternion));
  const xdir = normalizeVector(rotateVectorByQuaternion([1, 0, 0], camera.quaternion));

  return {
    axis: {
      origin: toRoundedArray(origin, digits),
      dir: toRoundedArray(dir, digits),
      xdir: toRoundedArray(xdir, digits)
    }
  };
}

export function attachCameraAxisOverlay(container, title = "camera.axis") {
  const overlay = document.createElement("pre");
  overlay.className = "cameraAxisOverlay";
  overlay.textContent = `${title}\n{\n  \"axis\": {\n    \"origin\": [0, 0, 0],\n    \"dir\": [0, 0, -1],\n    \"xdir\": [1, 0, 0]\n  }\n}`;
  container.appendChild(overlay);
  return overlay;
}

export function updateCameraAxisOverlay(overlayEl, camera, title = "camera.axis", digits = 3) {
  if (!overlayEl || !camera) {
    return;
  }

  const csys = buildCameraAxisCsys(camera, digits);
  overlayEl.textContent = `${title}\n${JSON.stringify(csys, null, 2)}`;
}
