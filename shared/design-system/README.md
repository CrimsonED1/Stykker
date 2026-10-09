StykkerLLM is the control surface for local language-model servers: llama.cpp, Ollama, LM Studio and vLLM on this machine and on remote model hosts, behind one proxy. The web interface is where people start, watch and stop models; the `stykker` terminal only displays. The interface reads like an instrument panel: dense, numeric, calm until something happens.

Two themes, nothing else. **Dark** (default) is deep navy with cyan light and real glow. **Spacepunk Titan** is a ship interior under work light: matte metal grey, signal orange, lacquered backend plates, and glow only inside dark display windows. A third choice, **System**, maps light to Titan and dark to Dark.

## Content fundamentals

- **English UI, plain words, sentence case.** "Free VRAM", "Save as profile", "Proxy is off – clients on :17500 get no answer." No exclamation marks, no emoji.
- **Numbers carry the page.** Every measured value is mono with tabular digits (`num-md`, `t-xl`) and keeps its unit small beside it: `48.7 t/s`, `17.2/24 GB`. Round to what changes: one decimal for t/s and GB.
- **Symbols instead of labels.** Metric labels are 16px line symbols with the word in `title` and `aria-label`. Every field keeps its place; a value the backend does not report shows a muted "–" in `na`, never an empty gap.
- **Say what to do next.** Notices name the problem and offer the fix as a button. Empty states say what is missing and how to get it ("Start a server and save it as a profile…"), never just "none yet".
- **Confirm softly.** Stop asks in a small popover. Only *forget* (history) and *dismiss* (notices) get Undo in a toast.
- Titan prints its labels as stamped plates: `PORT 8180`, `HOST gpu-box`, `SLOT 2`, section rules like `RUNNING` in `plaque-sec`.

## Visual foundations

**Colour.** Surfaces step `bg` → `card` → `card-2` (recessed fields). Text is `ink`; labels, units and metadata `muted`; missing values `na`. `acc` is the one accent: active nav, focus, bar fills, the primary button. Status uses `good`, `warn`, `bad` and is never colour alone: each state also has a symbol (`s-idle`, `s-read`, `s-gen`, `s-load`, `s-off`; results `r-ok`, `r-abort`, `r-err`, `r-full`). Each backend owns a colour — `b-llama`, `b-ollama`, `b-lms`, `b-vllm` — set as `--b` on its card; it paints the left stripe, the glyph plate, the sparkline and the busy rim. Request speed uses the four-step scale `sp-1` (slow) … `sp-4` (fast).

**Titan specifics.** Panels are `card` plates with a 1px `fuge` joint, 2px corners, no soft shadows; a running server card gets one chamfered corner. Lamps (`l-gen`, `l-read`, `l-idle`, `l-warn`, `l-off`) carry a white or ink symbol and a ring in `fuge`. Display windows (`disp` glass, `phos` figures, `amber` units) are the only place that glows. Hazard stripes (yellow `l-warn` on ink) mark only real problems: server lost, VRAM too tight, slower than usual. Links use `acc-t`; the orange `acc` is for fills.

**Type.** System faces only: `sans` (Segoe UI Variable Text) for words, `mono` (Cascadia Mono) for every figure, port, model name and plate. Five steps: `t-xs` 11, `t-sm` 12, `t-md` 13 (body, 1.45), `t-lg` 15 (titles, 600), `t-xl` 26 (the tokens/s figure, mono). Uppercase labels use `label` and `section` with letter spacing.

**Layout.** `main` is at most `page-max` wide with `space-gutter` sides. The monitor has two columns: running servers left, a `rail` column right for hardware and saved profiles; below 1020px it stacks. Cards sit `gap` apart. On phones (< 640px) wide tables become rows and the header wraps to two lines.

**Shape.** Dark corners: `radius` for cards, `radius-md` for grids, menus and toasts, `radius-sm` for buttons, inputs and tiles, `radius-xs` for badges. Buttons are square-ish and badges have small corners so an action never looks like a state. Titan sets everything to 1–2px.

**Glow and motion.** Glow strength is one value per theme (`--glow` 1.25 in Dark, .6 in Titan). Motion is bound to data only: numbers count, bars glide over `bar-glide`, the sparkline moves one step per tick, a working card breathes over `halo-breathe`, a new request row slides in once. Nothing loops idly. `html.shell` (the window without GPU) keeps only number and bar transitions — no halos, sweeps, blur or soft shadows — and `prefers-reduced-motion` stops all animation.

**Focus.** One visible ring everywhere: 2px solid `acc` with a 2px offset in Dark; in Titan 2px `ink` with an outer `signal` halo, so it holds 3:1 on every plate.

**Depth.** Dark uses a faint top sheen on cards and `shadow-menu`, `shadow-palette`, `shadow-toast` for overlays. Titan has no shadows at all.

## Iconography

One own icon set (`assets/Icons`, also in `components/bundle.js` as a sprite): simple line drawings on a 16px grid, 1.5px stroke, round caps, `currentColor`. Draw them at 16px (14px inside chips and lamps, 12px in Titan lamps) in `muted`, or in the state or backend colour they stand for. Prefixes: `i-` metrics and actions, `s-` states, `r-` request results, `c-` client kinds, `x-` extras (tools, thinking), `e-` end reasons, `b-` backend glyphs. The backend glyphs are abstract marks of our own, not product logos. Use them through the sprite: `<svg class="ic"><use href="#i-vram"/></svg>`.

## Brand mark

There is no logo artwork beyond the app icon (`assets/Logos/app-icon.png`: a cyan diamond on a dark tile). In the interface the brand is set as type: "◆ STYKKER LLM", the diamond and LLM in `acc` with glow (Dark) or orange without glow (Titan).

## Using the components

Components are CSS classes in `components/bundle.css`, keyed to the tokens; set `data-theme="dark"` or `data-theme="titan"` on `<html>` and add the `shell` class for the GPU-less window. `components/bundle.js` only mounts the icon sprite (`window.Stykker`). Each component card shows the markup to copy.
