# StatChip

Header status chips summarise the whole system on every page: proxy, GPU VRAM with a mini bar, free RAM, servers busy.

`.stat` is 26px tall with a hairline border; the value sits in `<b>` (mono, ink). `.stat.on` turns the proxy chip `good`. The proxy chip is a button that opens the proxy panel. Hide the lesser chips on phones with `.hide-s`.
