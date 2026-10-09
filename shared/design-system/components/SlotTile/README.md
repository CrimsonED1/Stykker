# SlotTile

A slot tile is one parallel request slot of a server: the fill height is the context used, the colour is its state.

`.slot[data-s=…]` with `--f` as the fill height; `gen` gets an upward sweep; `.hot` turns `bad` with a "!" when the context is nearly full; `.sel` marks the slot whose details show below. Titan tiles are panel squares labelled "SLOT n". Clicking a tile shows that slot's details.
