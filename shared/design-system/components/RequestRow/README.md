# RequestRow

Recent requests are rows with fixed columns: client kind, model with backend dot, host, duration bar, prompt tokens, t/s on the speed scale, tools and thinking, result, age.

t/s takes `sp-1`…`sp-4` by speed; the result is a symbol (`r-ok`, `r-abort`, `r-err`, `r-full`) in `good`, `warn` or `bad`. A new row slides in and flashes once. On phones the duration, host and extras columns drop.
