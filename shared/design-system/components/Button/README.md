# Button

Buttons come in three levels plus quiet, with 6px corners so they never look like a status badge.

- **Default** (`button`): most actions (Details, Save as profile, Record).
- **Primary** (`button.primary`): the one action a view is for (Start, Prompt). Dark: acc tint at 16% with a 55% acc border, glows on hover. Titan: solid `acc` with a white label.
- **Danger** (`button.danger`): Stop, Delete, Free VRAM confirm. `bad` text and border; a stop always asks in a small popover first.
- **Quiet** (`button.quiet`): toolbar and header helpers (Search, menu).
- **Icon button** (`button.ib` or `.sq`): 28×28, one symbol, the word in `title` and `aria-label`.
- Busy: `aria-busy="true"` adds a spinner; after an action `.done` flashes `good`, `.fail` flashes `warn` once.
- The consumer provides the label, the symbol and the click handler. Viewer role: hide (`.w-hide`) or disable with a reason in `title`.
