# Plate

Plates (`.chipi`) carry a symbol and a short value: host, port, queue, recording. In Titan they become stamped plates (HOST, PORT) in mono capitals.

- `.chipi.host`: a remote model host (Titan: filled with `b-host`, white text); `.chipi.local`: this machine, muted.
- `.chipi.warnc`: a warning plate, `warn` text.
- `.recchip`: recording with a running timer, `bad`, the dot blinks.
The consumer provides the symbol id and the value.
