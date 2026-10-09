# ServerCard

The server card is one card for every backend: stripe and glyph in the backend colour, state lamp, name, the tokens/s window, actions, sparkline with slot tiles, and the fixed metric row.

- `--b` is one of `b-llama`, `b-ollama`, `b-lms`, `b-vllm`; it colours the left stripe, the glyph plate (`.bk`), the sparkline and the busy rim.
- `.busy` adds the breathing halo (Dark) or an inset joint (Titan; Titan cards also get one chamfered corner).
- Actions: Prompt, Details, Record (with timer), Save as profile, Stop (asks in a popover). Viewer role hides them.
- The consumer provides backend, state, name, port, host, figures, slots and points.
