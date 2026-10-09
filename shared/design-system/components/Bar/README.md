# Bar

Bars show a share: context used, VRAM, RAM, durations. 6px tall, `track` behind, a `second → acc` gradient fill that glides over `bar-glide`.

`.bar i.hot` turns `bad` when the context is nearly full; `.bar i.writing` adds a light sweep while a slot writes. Titan bars are square with a `fuge` inset and a solid `acc` fill. `.stack` splits VRAM per program with a legend. The consumer provides the width in percent and the figure beside it.
