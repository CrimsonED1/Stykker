# MetricGrid

The metric row gives every server card the same fixed fields in the same place: a symbol, a mono value, a small unit.

`.mgrid` fits its own columns to the card: `repeat(auto-fit, minmax(72px, 1fr))`. A 1100px card carries twelve `.mc` cells in one row; a 340px rail wraps them into rows of three at ~97px each; no cell is ever narrower than 72px. A card narrower than roughly 985px (twelve cells need 12×72px plus the gaps) wraps its last cells onto a second row instead of squeezing them — a fixed count of twelve once crushed the rail's cells to 18px and the figures ran over their neighbours. `.mc.sep` starts a new group with a hairline; the hairline is dropped below 1180px, where cards are narrow anyway. A value the backend does not report shows a muted "–" (`.mc.na`, colour `na`) and keeps its place. `.mc.warn` turns a value `warn`. The word for each symbol goes in `title`.
