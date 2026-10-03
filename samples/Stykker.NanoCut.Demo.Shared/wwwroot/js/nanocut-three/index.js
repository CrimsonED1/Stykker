// three.js adapter for Stykker.NanoCut buffers.
// buffers: { positions: Float32Array|number[], normals: Float32Array|number[], indices: Uint32Array|number[], origin: [x, y, z] }
import * as THREE from 'three';

/** Creates a THREE.BufferGeometry from NanoCut mesh buffers (coordinates in mm relative to buffers.origin). */
export function toThreeGeometry(buffers) {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(asFloat32(buffers.positions), 3));
  geometry.setAttribute('normal', new THREE.BufferAttribute(asFloat32(buffers.normals), 3));
  geometry.setIndex(new THREE.BufferAttribute(asUint32(buffers.indices), 1));
  geometry.computeBoundingSphere();
  return geometry;
}

/** Creates THREE.LineLoop objects from 2D polylines (arrays of interleaved x, y in mm). */
export function toThreeLineLoops(polylines, material = new THREE.LineBasicMaterial()) {
  return polylines.map((line) => {
    const pts = asFloat32(line);
    const xyz = new Float32Array((pts.length / 2) * 3);
    for (let i = 0, j = 0; i < pts.length; i += 2, j += 3) {
      xyz[j] = pts[i];
      xyz[j + 1] = pts[i + 1];
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(xyz, 3));
    return new THREE.LineLoop(g, material);
  });
}

function asFloat32(a) {
  return a instanceof Float32Array ? a : Float32Array.from(a);
}

function asUint32(a) {
  return a instanceof Uint32Array ? a : Uint32Array.from(a);
}
