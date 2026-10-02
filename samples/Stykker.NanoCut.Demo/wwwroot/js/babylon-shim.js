// Maps the ES-module imports of @stykker/nanocut-babylon onto the Babylon.js UMD build (global BABYLON),
// which the demo loads on demand.
const B = globalThis.BABYLON;
export const Mesh = B.Mesh;
export const VertexData = B.VertexData;
export const MeshBuilder = B.MeshBuilder;
export const Vector3 = B.Vector3;
