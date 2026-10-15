# Hardware

The hardware blocks of StykkerLLM and StykkerSYS: the graphics card, the processor and memory, and the drives. Both apps show them the same way, so a figure looks the same in the monitor and in the process list.

Load `components/hardware.css` after `tokens.css`. Every class has the prefix `hw-` and no rule touches an element on its own, so the blocks cannot restyle the rest of a page. Colours come from the tokens (`--acc`, `--second`, `--good`, `--warn`, `--bad`, `--ink`, `--muted`, `--track`, `--line`, `--card`); a missing token falls back to the Dark value.

## Blocks

| Block | Markup |
|---|---|
| Card | `<section class="hw-card">`; a head line `<div class="hw-head">Title <span class="hw-sub">detail</span><a class="hw-more" href="…">details</a></div>`; inside a head, `hw-sep` adds a rule above (a second block in the same card) |
| Figures | `<div class="hw-meters">` with `<div class="hw-meter">`: `hw-meter-hd` (label), `hw-val` (a `<b>` value and a `<small>` unit; class `na` for a missing value), `hw-bar` (`hot` above 92 %, an `<i style="--w:NN%">` fill), `hw-note` (`warn` or `good`) |
| Share per program | `<div class="hw-stack">` with `<i style="width:NN%;--c:colour">` segments, then `<div class="hw-legend">` with `<div><i style="--c:…"></i><span>name</span><span class="hw-val"><b>1.2</b><small>GB</small></span></div>` |
| Cores | `<div class="hw-cores">` with `<i style="--h:NN%">` per logical core |
| Drive | `<div class="hw-vol">`: `hw-vol-top` (`hw-letter`, `hw-kind data-k="Nvme\|Ssd\|Hdd\|Usb\|Unknown"`, `hw-sub` label, `hw-free`), `hw-bar`, `hw-io` (read, write, busy, queue), then the history |
| History | `<div class="hw-sparks">` with a `<div>` per series: `hw-spark-hd` and `<svg class="hw-spark" viewBox="0 0 300 70" preserveAspectRatio="none"><polyline class="line [second\|warn]" points="…"/></svg>`. Points: oldest first, the newest at the right edge; a missing value is left out of the line |
| Table | `<div class="hw-scroll"><table class="hw-table">`; numbers in `<td class="num">`; a long name in `class="hw-name"` |
| Notes | `hw-hint-line` under a head (a short explanation); `hw-hint` for a warning under a card |

## Rules

- A value the source does not report shows `–`, never a made-up number.
- On a phone (under 480 px) the figures go two to a row, the tables scroll inside their own box and the page does not scroll sideways.
- In the window without a graphics card (`html.shell`) the blocks keep the numbers and the bars, with no shadows and no motion.
