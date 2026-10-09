# StateLamp

The state lamp shows what a server or slot is doing with colour **and** symbol, never colour alone; the word goes in the tooltip.

Set `data-s` to `idle`, `read`, `gen`, `load` or `off` on `.st`; the colour comes from `st-*` (Dark) or the Titan lamp tokens `l-*` with a ring in `fuge`. Only `gen` breathes and only `load` spins; both stop in `html.shell` and with reduced motion. The consumer provides the state and the label.
