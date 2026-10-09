# Sparkline

The sparkline is tokens/s over the last minutes with a top value, a time axis and a glowing tip at the newest value.

Its stroke takes the card's backend colour (`--b`). In Titan it sits in a display window: `disp` glass, `phos` line, `amber` labels; this is the only place Titan glows. Motion is bound to data: the line shifts one step per tick; nothing moves in `html.shell`. The consumer provides the points (viewBox 0 0 100 40) and the labels.
