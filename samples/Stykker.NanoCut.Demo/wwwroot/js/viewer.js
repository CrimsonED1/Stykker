// Viewer for the NanoCut demo: keeps the current objects and draws them with three.js or Babylon.js.
// Buffers arrive as raw bytes from .NET and are reinterpreted as Float32Array / Uint32Array.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { toThreeGeometry } from './nanocut-three/index.js';

const BABYLON_URL = 'https://cdn.babylonjs.com/babylon.js';
const objects = [];       // { name, kind: 'mesh'|'lines', positions, normals, indices, color, opacity }
let host = null;
let engineName = 'three';
let impl = null;
let viewName = 'iso';

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
  impl.fit(bounds(), viewName);
}

export function clear() {
  objects.length = 0;
  impl?.clear();
}

export function addMesh(name, positions, normals, indices, color, opacity) {
  const o = { name, kind: 'mesh', positions: f32(positions), normals: f32(normals), indices: u32(indices), color, opacity };
  objects.push(o);
  impl?.add(o);
}

export function addLines(name, xyz, color) {
  const o = { name, kind: 'lines', positions: f32(xyz), color, opacity: 1 };
  objects.push(o);
  impl?.add(o);
}

export function fit(view) {
  viewName = view ?? viewName;
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

function bounds() {
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
  for (const o of objects) {
    if (o.kind !== 'mesh' || o.opacity < 0.5) continue;
    const p = o.positions;
    for (let i = 0; i < p.length; i += 3)
      for (let k = 0; k < 3; k++) { min[k] = Math.min(min[k], p[i + k]); max[k] = Math.max(max[k], p[i + k]); }
  }
  if (!isFinite(min[0])) return { center: [0, 0, 0], radius: 20 };
  const center = [0, 1, 2].map((k) => (min[k] + max[k]) / 2);
  const radius = Math.max(1, Math.hypot(max[0] - min[0], max[1] - min[1], max[2] - min[2]) / 2);
  return { center, radius };
}

// Camera directions per view (z up).
const VIEWS = {
  iso: [-0.62, -0.42, 0.66], top: [0, -0.0001, 1], gear: [-0.38, -0.62, 0.68],
  lathe: [0.05, -1, 0.25], mill: [-0.42, -0.62, 0.66],
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

  function resize() {
    const w = el.clientWidth, h = el.clientHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / Math.max(1, h);
    camera.updateProjectionMatrix();
  }
  const ro = new ResizeObserver(resize);
  ro.observe(el);
  resize();
  (function loop() {
    if (!running) return;
    controls.update();
    renderer.render(scene, camera);
    requestAnimationFrame(loop);
  })();

  return {
    add(o) {
      if (o.kind === 'mesh') {
        const mat = new THREE.MeshStandardMaterial({
          color: o.color, metalness: 0.3, roughness: 0.5, side: THREE.DoubleSide,
          transparent: o.opacity < 1, opacity: o.opacity, depthWrite: o.opacity >= 1,
        });
        group.add(new THREE.Mesh(toThreeGeometry(o), mat));
      } else {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.BufferAttribute(o.positions, 3));
        group.add(new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: o.color })));
      }
    },
    clear() {
      for (const c of [...group.children]) { group.remove(c); c.geometry?.dispose(); c.material?.dispose(); }
    },
    fit(b, view) {
      camera.position.set(...eyeFor(b, view));
      controls.target.set(...b.center);
      camera.near = b.radius / 100;
      camera.far = b.radius * 50;
      camera.updateProjectionMatrix();
      controls.update();
    },
    dispose() {
      running = false;
      ro.disconnect();
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
  engine.runRenderLoop(() => scene.render());
  const ro = new ResizeObserver(() => engine.resize());
  ro.observe(el);

  return {
    add(o) {
      if (o.kind === 'mesh') {
        const mesh = toBabylonMesh(o, o.name, scene);
        const mat = new B.StandardMaterial(o.name + '-mat', scene);
        mat.diffuseColor = B.Color3.FromHexString(o.color);
        mat.specularColor = new B.Color3(0.25, 0.25, 0.25);
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
    clear() {
      for (const m of meshes) m.dispose(false, true);
      meshes.length = 0;
    },
    fit(b, view) {
      const eye = eyeFor(b, view);
      camera.setTarget(new B.Vector3(...b.center));
      camera.setPosition(new B.Vector3(...eye));
      camera.minZ = b.radius / 100;
      camera.maxZ = b.radius * 50;
    },
    dispose() {
      ro.disconnect();
      this.clear();
      engine.dispose();
      canvas.remove();
    },
  };
}
