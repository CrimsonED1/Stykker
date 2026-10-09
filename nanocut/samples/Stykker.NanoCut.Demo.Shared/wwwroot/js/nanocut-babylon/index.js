// Babylon.js adapter for Stykker.NanoCut buffers.
import { Mesh, VertexData, MeshBuilder, Vector3 } from '@babylonjs/core';

/**
 * Creates a BABYLON.Mesh from NanoCut mesh buffers. NanoCut triangles are counter-clockwise seen from
 * outside (right-handed); Babylon.js is left-handed by default, so the winding is flipped unless the
 * scene uses useRightHandedSystem.
 */
export function toBabylonMesh(buffers, name, scene) {
  const indices = Uint32Array.from(buffers.indices);
  if (!scene.useRightHandedSystem) {
    for (let i = 0; i < indices.length; i += 3) {
      const t = indices[i + 1];
      indices[i + 1] = indices[i + 2];
      indices[i + 2] = t;
    }
  }
  const data = new VertexData();
  data.positions = Float32Array.from(buffers.positions);
  data.normals = Float32Array.from(buffers.normals);
  data.indices = indices;
  if (buffers.colors) data.colors = toColor4(Float32Array.from(buffers.colors));
  const mesh = new Mesh(name, scene);
  data.applyToMesh(mesh);
  return mesh;
}

/** Babylon wants RGBA per vertex; the caller sends RGB. */
function toColor4(rgb) {
  const rgba = new Float32Array((rgb.length / 3) * 4);
  for (let i = 0, j = 0; i < rgb.length; i += 3, j += 4) {
    rgba[j] = rgb[i]; rgba[j + 1] = rgb[i + 1]; rgba[j + 2] = rgb[i + 2]; rgba[j + 3] = 1;
  }
  return rgba;
}

/** Creates line meshes from 2D polylines (arrays of interleaved x, y in mm), closing each loop. */
export function toBabylonLines(polylines, name, scene) {
  return polylines.map((line, k) => {
    const points = [];
    for (let i = 0; i < line.length; i += 2) points.push(new Vector3(line[i], line[i + 1], 0));
    if (points.length > 0) points.push(points[0]);
    return MeshBuilder.CreateLines(`${name}-${k}`, { points }, scene);
  });
}
