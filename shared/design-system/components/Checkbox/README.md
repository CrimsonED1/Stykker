# Checkbox

Every `<input type="checkbox">` is a round check like on iOS: an empty ring, and when chosen a filled circle in the
accent colour with a check. It applies everywhere on its own (`bundle.css`), in Dark and Titan; nothing to add to the
markup. Inside a `.switch` the input stays hidden and the switch draws itself.

Use it to pick items (files in a list, models for a run) and for options in a dialog. Use the [Switch](../Switch/README.md)
for a setting that takes effect right away.

```html
<label><input type="checkbox" checked> Remove here once it arrived</label>
```

`indeterminate` (some of a group chosen) shows a bar instead of the check. Disabled is faded.
