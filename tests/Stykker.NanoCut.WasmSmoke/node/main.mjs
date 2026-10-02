// Node entry point for the browser-wasm smoke test.
import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.withApplicationArguments(...process.argv.slice(2)).create();
const code = await runtime.runMain();
process.exit(code);
