// Viewer for the NanoCut demo: keeps the current objects and draws them with three.js or Babylon.js.
// Buffers arrive as raw bytes from .NET and are reinterpreted as Float32Array / Uint32Array.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { toThreeGeometry } from './nanocut-three/index.js';

const BABYLON_URL = 'https://cdn.babylonjs.com/babylon.js';
const objects = [];       // { name, kind: 'mesh'|'lines', positions, normals, indices, color, opacity }
let host = null;
let engineName = 'three';
let impl = null;
let viewName = 'iso';
// A movable tool (mesh in its own frame + pose) with an optional drag gizmo that reports the new pose to .NET.
let tool = null;          // { positions, normals, indices, color, opacity, pose: [px,py,pz,qx,qy,qz,qw] }
let gizmoMode = 'off';
let gizmoCallback = null; // DotNetObjectReference
let locked = false;
// Optional spin of the tool about an axis in its own frame (slow-motion display of a spinning spindle).
const spin = { axis: [0, 0, 1], rate: 0, angle: 0, raf: 0, last: 0 };

const f32 = (bytes) => new Float32Array(bytes.slice().buffer);
const u32 = (bytes) => new Uint32Array(bytes.slice().buffer);

export async function attach(elementId, engine) {
  host = document.getElementById(elementId);
  await setEngine(engine);
}

export async function setEngine(engine) {
  impl?.dispose();
  impl = null;
  engineName = engine;
  impl = engine === 'babylon' ? await createBabylon(host) : createThree(host);
  for (const o of objects) impl.add(o);
  if (tool) { impl.setTool(tool); if (tool.parts) impl.setToolParts(tool.parts); applyToolPose(); impl.setGizmo(gizmoMode); }
  impl.fit(bounds(), viewName);
}

export function setTool(positions, normals, indices, color, opacity, pose) {
  tool = { name: 'tool', kind: 'mesh', positions: f32(positions), normals: f32(normals), indices: u32(indices), color, opacity, pose };
  impl?.setTool(tool);
  applyToolPose();
  impl?.setGizmo(gizmoMode);
}

export function setToolPose(pose) {
  if (!tool) return;
  tool.pose = pose;
  applyToolPose();
}

/**
 * Replaces the extra parts of the tool (meshes in the tool's frame that move and spin with it, e.g. grains coloured
 * by state). Each part: { positions, normals, indices (bytes), color, opacity }.
 */
export function setToolParts(parts) {
  if (!tool) return;
  tool.parts = parts.map((p) => ({ kind: 'mesh', positions: f32(p.positions), normals: f32(p.normals), indices: u32(p.indices), color: p.color, opacity: p.opacity }));
  impl?.setToolParts(tool.parts);
}

/** Removes the movable tool (and stops its spin). */
export function removeTool() {
  tool = null;
  setToolSpin([0, 0, 1], 0, 0);
  impl?.setGizmo('off');
  impl?.removeTool();
}

/**
 * Spins the tool about `axis` (in the tool's own frame) at `radPerSecond` (0 stops). `angle` (rad), if given, sets
 * the current spin angle, e.g. to show the spindle phase of a computed state. The spin is applied on top of the pose.
 */
export function setToolSpin(axis, radPerSecond, angle) {
  if (axis) spin.axis = axis;
  spin.rate = radPerSecond || 0;
  if (angle !== null && angle !== undefined) spin.angle = angle;
  applyToolPose();
  if (spin.rate !== 0 && !spin.raf) {
    spin.last = performance.now();
    spin.raf = requestAnimationFrame(spinStep);
  }
}

function spinStep(now) {
  spin.angle = (spin.angle + spin.rate * (now - spin.last) / 1000) % (2 * Math.PI);
  spin.last = now;
  applyToolPose();
  spin.raf = spin.rate !== 0 ? requestAnimationFrame(spinStep) : 0;
}

// Pose with the spin about the tool's own axis applied: q = q_pose · q_spin.
function spunPose(p) {
  if (spin.angle === 0) return p;
  const [ax, ay, az] = spin.axis, n = Math.hypot(ax, ay, az) || 1, h = spin.angle / 2, s = Math.sin(h) / n;
  const bx = ax * s, by = ay * s, bz = az * s, bw = Math.cos(h);
  const [, , , x, y, z, w] = p;
  return [p[0], p[1], p[2],
    w * bx + x * bw + y * bz - z * by,
    w * by - x * bz + y * bw + z * bx,
    w * bz + x * by - y * bx + z * bw,
    w * bw - x * bx - y * by - z * bz];
}

function applyToolPose() {
  if (tool) impl?.setToolPose(spunPose(tool.pose));
}

export function setGizmo(mode, dotnetRef) {
  gizmoMode = mode;
  if (dotnetRef) gizmoCallback = dotnetRef;
  impl?.setGizmo(mode);
}

/** While locked (a cut is being computed) the gizmo ignores drags. */
export function setLocked(value) { locked = value; impl?.setLocked?.(value); }

function toolMoved(pose) {
  if (!tool) return;
  tool.pose = pose;
  gizmoCallback?.invokeMethodAsync('OnToolMoved', ...pose);
}

export function clear() {
  objects.length = 0;
  impl?.clear();
}

export function addMesh(name, positions, normals, indices, color, opacity, colors) {
  const o = { name, kind: 'mesh', positions: f32(positions), normals: f32(normals), indices: u32(indices), color, opacity, colors: colors ? f32(colors) : null };
  objects.push(o);
  impl?.add(o);
}

/**
 * Picks the vertex nearest to a point of the viewport. `clientX`/`clientY` are browser coordinates; the returned
 * `vertex` is an index into the buffer that was last passed to addMesh, which is the index the .NET side kept its
 * exact nanometre coordinates in.
 */
export function pick(clientX, clientY) { return impl?.pick(clientX, clientY) ?? null; }

export function addLines(name, xyz, color) {
  const o = { name, kind: 'lines', positions: f32(xyz), color, opacity: 1 };
  objects.push(o);
  impl?.add(o);
}

/** Fits the camera to the visible objects, or to `box` = [minX, minY, minZ, maxX, maxY, maxZ] if given. */
export function fit(view, box) {
  viewName = view ?? viewName;
  fitBox = box ?? null;
  impl?.fit(bounds(), viewName);
}

export function download(fileName, bytes) {
  const blob = new Blob([bytes], { type: 'application/octet-stream' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

let fitBox = null;

function bounds() {
  if (fitBox) {
    const center = [0, 1, 2].map((k) => (fitBox[k] + fitBox[k + 3]) / 2);
    return { center, radius: fitRadius(fitBox) };
  }
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (const o of objects) {
    if (o.kind !== 'mesh' || o.opacity < 0.5) continue;
    const p = o.positions;
    for (let i = 0; i < p.length; i += 3)
      for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], p[i + k]); max[k] = Math.max(max[k], p[i + k]); }
  }
  if (!isFinite(min[0])) return { center: [0, 0, 0], radius: 20 };
  const center = [0, 1, 2].map((k) => (min[k] + max[k]) / 2);
  return { center, radius: fitRadius([min[0], min[1], min[2], max[0], max[1], max[2]]) };
}

// The floor keeps a degenerate box from collapsing the camera; it is 1 µm, not 1 mm, so a 0.14 mm staircase can still
// be framed and a 1 nm step is inside the depth range.
function fitRadius(box) {
  return Math.max(1e-3, Math.hypot(box[3] - box[0], box[4] - box[1], box[5] - box[2]) / 2);
}

// ---------- scale bar ----------
// A 1-2-5 length that always spans a readable part of the viewport. Without it a 1 nm step on a 20 mm part is an
// image nobody can read, because the same picture also has to work at 20 mm.
function makeScaleBar(el) {
  const root = document.createElement('div');
  root.className = 'scalebar';
  const bar = document.createElement('i'), label = document.createElement('span');
  root.append(bar, label);
  el.appendChild(root);
  return {
    update(worldPerPx, widthPx) {
      if (!(worldPerPx > 0) || widthPx < 80) return;
      const len = niceLength(worldPerPx, 70, Math.min(200, widthPx - 60));
      bar.style.width = (len / worldPerPx).toFixed(1) + 'px';
      label.textContent = fmtLength(len);
    },
    dispose() { root.remove(); },
  };
}

const NICE = [1, 2, 5];

function niceLength(worldPerPx, minPx, maxPx) {
  const from = Math.floor(Math.log10(minPx * worldPerPx));
  let best = worldPerPx * minPx;
  for (let p = from; p <= from + 6; p++) {
    for (const m of NICE) {
      const len = m * Math.pow(10, p), px = len / worldPerPx;
      if (px > maxPx) return best;
      best = len;
      if (px >= minPx) return len;
    }
  }
  return best;
}

function fmtLength(mm) {
  for (const [name, k] of [['mm', 1], ['µm', 1e-3], ['nm', 1e-6]]) {
    const v = mm / k;
    if (v >= 1 || k === 1e-6)
      return String(Number(v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2))) + ' ' + name;
  }
  return '';
}

/** The vertex of triangle `tri` that is nearest to the hit point — the vertex under the cursor, not the cursor. */
function nearestVertex(o, tri, p) {
  let vertex = -1, best = Infinity;
  for (let k = 0; k < 3; k++) {
    const v = o.indices[3 * tri + k];
    const dx = o.positions[3 * v] - p.x, dy = o.positions[3 * v + 1] - p.y, dz = o.positions[3 * v + 2] - p.z;
    const d = dx * dx + dy * dy + dz * dz;
    if (d < best) { best = d; vertex = v; }
  }
  return { object: objects.indexOf(o), name: o.name, triangle: tri, vertex };
}

// Camera directions per view (z up).
const VIEWS = {
  iso: [-0.62, -0.42, 0.66], top: [0, -0.0001, 1], gear: [-0.38, -0.62, 0.68],
  lathe: [0.05, -1, 0.25], mill: [-0.42, -0.62, 0.66], cubes: [-0.55, -0.85, 0.55], spin: [-0.3, -0.8, 0.75],
  inspect: [-0.42, -0.86, 0.5],
};

function eyeFor(b, view) {
  const d = VIEWS[view] ?? VIEWS.iso;
  const len = Math.hypot(...d), dist = b.radius * 3.3;
  return b.center.map((c, k) => c + (d[k] / len) * dist);
}

// ---------- three.js ----------
function createThree(el) {
  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setClearColor(0xf4f5f7);
  el.appendChild(renderer.domElement);
  renderer.domElement.classList.add('canvas');
  const scene = new THREE.Scene();
  scene.add(new THREE.HemisphereLight(0xffffff, 0x8899aa, 1.6));
  const sun = new THREE.DirectionalLight(0xffffff, 2.2);
  sun.position.set(-1, -1.6, 2.4);
  scene.add(sun);
  const fill = new THREE.DirectionalLight(0xffffff, 0.7);
  fill.position.set(2, 1, 0.7);
  scene.add(fill);
  const camera = new THREE.PerspectiveCamera(32, 1, 0.1, 5000);
  camera.up.set(0, 0, 1);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  const group = new THREE.Group();
  scene.add(group);
  let running = true;
  let toolMesh = null;
  const gizmo = new TransformControls(camera, renderer.domElement);
  gizmo.setSpace('world');
  scene.add(gizmo.getHelper());
  gizmo.addEventListener('dragging-changed', (e) => {
    controls.enabled = !e.value;
    if (!e.value && toolMesh) {
      const p = toolMesh.position, q = toolMesh.quaternion;
      toolMoved([p.x, p.y, p.z, q.x, q.y, q.z, q.w]);
    }
  });

  function resize() {
    const w = el.clientWidth, h = el.clientHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / Math.max(1, h);
    camera.updateProjectionMatrix();
  }
  const ro = new ResizeObserver(resize);
  ro.observe(el);
  resize();
  const bar = makeScaleBar(el);
  const ray = new THREE.Raycaster();
  (function loop() {
    if (!running) return;
    controls.update();
    if (el.clientHeight > 0)
      bar.update(2 * camera.position.distanceTo(controls.target) * Math.tan(camera.fov * Math.PI / 360) / el.clientHeight, el.clientWidth);
    renderer.render(scene, camera);
    requestAnimationFrame(loop);
  })();

  return {
    add(o) {
      if (o.kind === 'mesh') {
        const mat = o.colors
          // A deviation colour is a measurement, not a surface: no light may touch it.
          ? new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide, transparent: o.opacity < 1, opacity: o.opacity, depthWrite: o.opacity >= 1 })
          : new THREE.MeshStandardMaterial({
              color: o.color, metalness: 0.3, roughness: 0.5, side: THREE.DoubleSide,
              transparent: o.opacity < 1, opacity: o.opacity, depthWrite: o.opacity >= 1,
            });
        const mesh = new THREE.Mesh(toThreeGeometry(o), mat);
        mesh.__nanocut = o;
        group.add(mesh);
      } else {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.BufferAttribute(o.positions, 3));
        group.add(new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: o.color })));
      }
    },
    pick(clientX, clientY) {
      const rect = el.getBoundingClientRect();
      ray.setFromCamera(new THREE.Vector2(
        ((clientX - rect.left) / rect.width) * 2 - 1,
        -((clientY - rect.top) / rect.height) * 2 + 1), camera);
      const hit = ray.intersectObjects(group.children, false).find(h => h.object.__nanocut?.kind === 'mesh');
      return hit ? nearestVertex(hit.object.__nanocut, hit.faceIndex, hit.point) : null;
    },
    clear() {
      for (const c of [...group.children]) { group.remove(c); c.geometry?.dispose(); c.material?.dispose(); }
    },
    setTool(t) {
      if (toolMesh) { gizmo.detach(); this.setToolParts([]); scene.remove(toolMesh); toolMesh.geometry.dispose(); toolMesh.material.dispose(); }
      const mat = new THREE.MeshStandardMaterial({ color: t.color, metalness: 0.3, roughness: 0.45, transparent: t.opacity < 1, opacity: t.opacity });
      toolMesh = new THREE.Mesh(toThreeGeometry(t), mat);
      scene.add(toolMesh);
      this.setToolPose(t.pose);
    },
    setToolPose(p) {
      if (!toolMesh) return;
      toolMesh.position.set(p[0], p[1], p[2]);
      toolMesh.quaternion.set(p[3], p[4], p[5], p[6]);
    },
    setToolParts(parts) {
      if (!toolMesh) return;
      for (const c of [...toolMesh.children]) { toolMesh.remove(c); c.geometry.dispose(); c.material.dispose(); }
      for (const p of parts) {
        const mat = new THREE.MeshStandardMaterial({ color: p.color, metalness: 0.2, roughness: 0.5, transparent: p.opacity < 1, opacity: p.opacity });
        toolMesh.add(new THREE.Mesh(toThreeGeometry(p), mat));
      }
    },
    removeTool() {
      if (!toolMesh) return;
      gizmo.detach();
      this.setToolParts([]);
      scene.remove(toolMesh);
      toolMesh.geometry.dispose();
      toolMesh.material.dispose();
      toolMesh = null;
    },
    setGizmo(mode) {
      if (!toolMesh || mode === 'off') { gizmo.detach(); return; }
      gizmo.setMode(mode);
      gizmo.attach(toolMesh);
    },
    setLocked(v) { gizmo.enabled = !v; },
    fit(b, view) {
      camera.position.set(...eyeFor(b, view));
      controls.target.set(...b.center);
      // near/far are a thousandth / fifty times the radius: a 1 nm feature on a 0.14 mm part sits inside that range.
      camera.near = b.radius / 1000;
      camera.far = b.radius * 50;
      camera.updateProjectionMatrix();
      controls.update();
    },
    dispose() {
      running = false;
      ro.disconnect();
      bar.dispose();
      gizmo.detach();
      gizmo.dispose();
      if (toolMesh) scene.remove(toolMesh);
      controls.dispose();
      this.clear();
      renderer.dispose();
      renderer.domElement.remove();
    },
  };
}

// ---------- Babylon.js ----------
async function loadBabylon() {
  if (globalThis.BABYLON) return;
  await new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = BABYLON_URL;
    s.onload = resolve;
    s.onerror = () => reject(new Error('Babylon.js could not be loaded from ' + BABYLON_URL));
    document.head.appendChild(s);
  });
}

async function createBabylon(el) {
  await loadBabylon();
  const { toBabylonMesh } = await import('./nanocut-babylon/index.js');
  const B = globalThis.BABYLON;
  const canvas = document.createElement('canvas');
  canvas.classList.add('canvas');
  el.appendChild(canvas);
  const engine = new B.Engine(canvas, true, { preserveDrawingBuffer: true });
  const scene = new B.Scene(engine);
  scene.useRightHandedSystem = true;
  scene.clearColor = B.Color4.FromHexString('#f4f5f7ff');
  const camera = new B.ArcRotateCamera('cam', 0, 1, 50, B.Vector3.Zero(), scene);
  camera.upVector = new B.Vector3(0, 0, 1);
  camera.attachControl(canvas, true);
  camera.wheelPrecision = 20;
  const hemi = new B.HemisphericLight('hemi', new B.Vector3(0, 0, 1), scene);
  hemi.intensity = 0.55;
  hemi.groundColor = new B.Color3(0.35, 0.4, 0.45);
  const sun = new B.DirectionalLight('sun', new B.Vector3(1, 1.6, -2.4), scene);
  sun.intensity = 0.75;
  const meshes = [];
  let toolMesh = null;
  const gizmos = new B.GizmoManager(scene);
  gizmos.usePointerToAttachGizmos = false;
  gizmos.positionGizmoEnabled = false;
  gizmos.rotationGizmoEnabled = false;
  const reportPose = () => {
    if (!toolMesh) return;
    const p = toolMesh.position, q = toolMesh.rotationQuaternion;
    toolMoved([p.x, p.y, p.z, q.x, q.y, q.z, q.w]);
  };
  const bar = makeScaleBar(el);
  engine.runRenderLoop(() => {
    const h = engine.getRenderHeight();
    if (h > 0) {
      const d = B.Vector3.Distance(camera.position, camera.getTarget());
      bar.update(2 * d * Math.tan(camera.fov * Math.PI / 360) / h, canvas.clientWidth);
    }
    scene.render();
  });
  const ro = new ResizeObserver(() => engine.resize());
  ro.observe(el);

  return {
    add(o) {
      if (o.kind === 'mesh') {
        const mesh = toBabylonMesh(o, o.name, scene);
        mesh.__nanocut = o;
        const mat = new B.StandardMaterial(o.name + '-mat', scene);
        // A deviation colour is a measurement, not a surface: switch the lighting off for it.
        mat.diffuseColor = o.colors ? new B.Color3(1, 1, 1) : B.Color3.FromHexString(o.color);
        mat.specularColor = o.colors ? new B.Color3(0, 0, 0) : new B.Color3(0.25, 0.25, 0.25);
        if (o.colors) { mat.emissiveColor = new B.Color3(1, 1, 1); mat.disableLighting = true; }
        mat.alpha = o.opacity;
        mat.backFaceCulling = false;
        mesh.material = mat;
        meshes.push(mesh);
      } else {
        const lines = [];
        const p = o.positions;
        for (let i = 0; i < p.length; i += 6)
          lines.push([new B.Vector3(p[i], p[i + 1], p[i + 2]), new B.Vector3(p[i + 3], p[i + 4], p[i + 5])]);
        const ls = B.MeshBuilder.CreateLineSystem(o.name, { lines }, scene);
        ls.color = B.Color3.FromHexString(o.color);
        meshes.push(ls);
      }
    },
    pick(clientX, clientY) {
      const rect = canvas.getBoundingClientRect();
      const info = scene.pick(clientX - rect.left, clientY - rect.top);
      const o = info?.hit ? info.pickedMesh?.__nanocut : null;
      return o?.kind === 'mesh' ? nearestVertex(o, info.faceId, info.pickedPoint) : null;
    },
    clear() {
      for (const m of meshes) m.dispose(false, true);
      meshes.length = 0;
    },
    setTool(t) {
      gizmos.attachToMesh(null);
      toolMesh?.dispose(false, true);
      toolMesh = toBabylonMesh(t, 'tool', scene);
      const mat = new B.StandardMaterial('tool-mat', scene);
      mat.diffuseColor = B.Color3.FromHexString(t.color);
      mat.alpha = t.opacity;
      toolMesh.material = mat;
      toolMesh.rotationQuaternion = new B.Quaternion(0, 0, 0, 1);
      this.setToolPose(t.pose);
    },
    setToolPose(p) {
      if (!toolMesh) return;
      toolMesh.position.set(p[0], p[1], p[2]);
      toolMesh.rotationQuaternion.set(p[3], p[4], p[5], p[6]);
    },
    setToolParts(parts) {
      if (!toolMesh) return;
      for (const c of toolMesh.getChildMeshes()) c.dispose(false, true);
      parts.forEach((p, i) => {
        const m = toBabylonMesh(p, 'tool-part-' + i, scene);
        const mat = new B.StandardMaterial('tool-part-mat-' + i, scene);
        mat.diffuseColor = B.Color3.FromHexString(p.color);
        mat.alpha = p.opacity;
        m.material = mat;
        m.parent = toolMesh;
      });
    },
    removeTool() {
      gizmos.attachToMesh(null);
      toolMesh?.dispose(false, true);
      toolMesh = null;
    },
    setGizmo(mode) {
      gizmos.positionGizmoEnabled = mode === 'translate';
      gizmos.rotationGizmoEnabled = mode === 'rotate';
      gizmos.attachToMesh(mode === 'off' ? null : toolMesh);
      for (const g of [gizmos.gizmos.positionGizmo, gizmos.gizmos.rotationGizmo]) {
        if (g && !g.__nanocut) { g.onDragEndObservable.add(reportPose); g.__nanocut = true; }
      }
      if (gizmos.gizmos.rotationGizmo) gizmos.gizmos.rotationGizmo.updateGizmoRotationToMatchAttachedMesh = false;
    },
    setLocked(v) { gizmos.attachToMesh(v ? null : (gizmoMode === 'off' ? null : toolMesh)); },
    fit(b, view) {
      const eye = eyeFor(b, view);
      camera.setTarget(new B.Vector3(...b.center));
      camera.setPosition(new B.Vector3(...eye));
      // near/far are a thousandth / fifty times the radius: a 1 nm feature on a 0.14 mm part sits inside that range.
      camera.minZ = b.radius / 1000;
      camera.maxZ = b.radius * 50;
    },
    dispose() {
      ro.disconnect();
      bar.dispose();
      gizmos.dispose();
      toolMesh?.dispose(false, true);
      this.clear();
      engine.dispose();
      canvas.remove();
    },
  };
}
