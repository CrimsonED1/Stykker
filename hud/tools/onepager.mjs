// Baut aus einem Entwurf eine eigenständige Seite ("Onepager"): das Design-System wird eingebettet statt verlinkt,
// das Ergebnis ist ein Body-Fragment ohne <html>/<head>/<body>. So eine Datei lässt sich direkt im Browser öffnen
// und als Artefakt veröffentlichen.
//
//   node tools/onepager.mjs [--src docs/design/mockup-2.html] [--out docs/design/mockup-2-onepager.html]
//                           [--ds C:/_AI/Stykker/MonoRepo/shared/design-system]
import { readFileSync, writeFileSync } from "node:fs";

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const src = value("--src", "docs/design/mockup-2.html");
const dst = value("--out", src.replace(/\.html$/, "-onepager.html"));
const ds = value("--ds", "C:/_AI/Stykker/MonoRepo/shared/design-system");

const read = (file) => readFileSync(file, "utf8");
// Ein "</script" im eingebetteten Code würde das umgebende <script> beenden.
const safe = (js) => js.replace(/<\/script/gi, "<\\/script");

const html = read(src);
const tokens = read(`${ds}/tokens.css`);
const bundle = read(`${ds}/components/bundle.css`);
const sprite = safe(read(`${ds}/components/bundle.js`));

const style = html.match(/<style>([\s\S]*?)<\/style>/);
const body = html.match(/<body([^>]*)>([\s\S]*?)<\/body>/);
const own = html.match(/<script>([\s\S]*?)<\/script>\s*<\/body>/);
if (!style || !body || !own) throw new Error(`${src}: <style>, <body> oder das eigene <script> fehlt`);

const inner = body[2].replace(/<script src="[^"]*bundle\.js"><\/script>/, "").trim();

// Nur für die eigenständige Fassung: eine breite Tabelle scrollt in ihrem eigenen Kasten, statt zu klicken.
const extra = `
#procs{overflow-x:auto}
#procs .req{min-width:900px}
`;

const out = `<style>
/* ── aus dem Design-System (C:\\_AI\\Stykker\\MonoRepo\\shared\\design-system), eingebettet statt verlinkt ── */
${tokens}
${bundle}
/* ── die eigenen Regeln des Entwurfs ── */
${style[1].trim()}
/* ── nur für diese eigenständige Fassung ── */
${extra.trim()}
</style>

<div class="hud">
${inner}
</div>

<script>
/* ── Symbol-Vorrat des Design-Systems (components/bundle.js) ── */
${sprite}
</script>
<script>
${own[1].trim()}
</script>
`;

writeFileSync(dst, out);
const kb = (s) => Math.round(s.length / 1024) + " kB";
console.log(`${dst}  ${kb(out)}  (tokens ${kb(tokens)}, bundle ${kb(bundle)}, sprite ${kb(sprite)})`);
if (out.includes("</script>\n<script>\n") === false) console.log("Achtung: Skriptblöcke prüfen");
if (out.includes("<script src=") || out.includes("<link rel=\"stylesheet\"")) throw new Error("externe Verweise sind übrig geblieben");