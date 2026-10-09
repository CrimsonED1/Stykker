# Icons

StykkerLLM's own line icons: 16px grid, 1.5px stroke, round caps and joins. In the product they are `<symbol>`s drawn in `currentColor` (sprite in `components/bundle.js`); these single files are drawn in `#7a87ab` (`na` in Dark) for display only — recolour through the sprite, not by editing the files.

- `i-*` metrics and actions: vram, ram, cpu, file, port, ctx, peak, avg, sum, draft, users, host, proxy, chat, info, more, stop, rec, layers, timer, restart, play, open, edit, trash, save, slots, queue, down, eject, lock, x, **arrow-up, arrow-down, disk**
  - `i-arrow-up` / `i-arrow-down` / `i-disk` added 2026-10-08 for StykkerHUD: direction of transfer (network, disk read/write) and the drive itself. `i-down` and `i-peak` are **trend** glyphs (a line with a knee), not direction arrows — do not use them for up/down.
- `s-*` server and slot states: idle, read, gen, load, off
- `r-*` request results: ok, abort, err, full (context full)
- `c-*` client kinds: cli, editor, web, agent
- `x-*` request extras: tool (tool calls), think (thinking tokens)
- `e-*` end reasons in history: crash, kill (stopped from outside)
- `b-*` backend glyphs (own abstract marks, not product logos): llama, ollama, lms, vllm
