#!/usr/bin/env node
// Starts the packaged StykkerLLM (app/StykkerLLM.exe from @stykker-llm/win32-x64), detached from this process.
"use strict";

const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");

const EXIT_NOT_WINDOWS = 1;
const EXIT_NOT_FOUND = 2;
const EXIT_SPAWN_FAILED = 3;

if (process.platform !== "win32") {
    console.error("stykker-llm: this program only runs on Windows (win32-x64).");
    console.error("You are on: " + process.platform + "/" + process.arch);
    process.exit(EXIT_NOT_WINDOWS);
}

if (process.arch !== "x64") {
    console.error("stykker-llm: only 64-bit Windows (x64) is supported, you are on: " + process.arch);
    process.exit(EXIT_NOT_WINDOWS);
}

let exe;
try {
    const pkgJson = require.resolve("@stykker-llm/win32-x64/package.json", { paths: [__dirname, process.cwd(), ...module.paths] });
    exe = path.join(path.dirname(pkgJson), "app", "StykkerLLM.exe");
} catch {
    console.error("stykker-llm: platform package '@stykker-llm/win32-x64' is not installed.");
    console.error("This optional dependency normally arrives automatically. Please reinstall:");
    console.error("  npm install -g stykker-llm --force");
    process.exit(EXIT_NOT_FOUND);
}

if (!fs.existsSync(exe)) {
    console.error("stykker-llm: binary is missing: " + exe);
    console.error("The platform package seems incomplete. Please reinstall:");
    console.error("  npm install -g stykker-llm --force");
    process.exit(EXIT_NOT_FOUND);
}

const args = process.argv.slice(2);
const child = spawn(exe, args, { detached: true, stdio: "ignore", windowsHide: false });
child.on("error", (err) => {
    console.error("stykker-llm: could not start " + exe + ": " + err.message);
    process.exit(EXIT_SPAWN_FAILED);
});
child.unref();