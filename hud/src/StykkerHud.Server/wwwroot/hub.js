// Die Hülle der Zentrale: Leiste, Übersicht und der Frame mit dem gewählten Modul.
// Die Module kommen aus /api/hub (stykker.json oder die Vorgabe); gewählt wird über #<id> in der Adresse.
(function () {
  "use strict";
  var root = document.documentElement;
  var nav = document.getElementById("nav");
  var frame = document.getElementById("frame");
  var overview = document.getElementById("overview");
  var small = matchMedia("(max-width:640px),(display-mode:standalone)");
  var modules = [];
  var shown = null;   // Adresse, die der Frame gerade zeigt: ein Statusabruf lädt das Modul nicht neu
  var lastAccess = null;   // der letzte Stand von /api/hub/access (für die Wahl der Adresse des QR-Codes)
  var qrKey = "", qrRound = 0, devicesKey = "", waitingKey = "";

  function el(tag, attrs, text) {
    var node = document.createElement(tag);
    for (var name in attrs || {}) node.setAttribute(name, attrs[name]);
    if (text != null) node.textContent = text;
    return node;
  }

  function selected() {
    var id = location.hash.replace("#", "");
    return modules.some(function (m) { return m.id === id; }) ? id : "";
  }

  // Eigene Module zeigen sich im Frame ohne eigene Marke und im Thema der Zentrale; ein fremder Dienst bleibt, wie er ist.
  function frameAddress(module) {
    if (module.external) return module.url;
    var url = new URL(module.url, location.href);
    url.searchParams.set("embed", "1");
    url.searchParams.set("theme", root.dataset.theme || "dark");
    return url.toString();
  }

  function drawNav() {
    var id = selected();
    nav.replaceChildren();
    var hub = el("button", { type: "button", "data-id": "" }, "Hub");
    if (!id) hub.setAttribute("aria-current", "page");
    nav.append(hub, el("span", { class: "sep" }));
    var externalStarted = false;
    modules.forEach(function (m) {
      if (m.external && !externalStarted) { nav.append(el("span", { class: "sep" })); externalStarted = true; }
      var button = el("button", { type: "button", "data-id": m.id, class: "mod" + (m.up ? " up" : "") + (m.external ? " ext" : "") }, m.name);
      button.title = m.up ? m.url : m.name + " does not answer";
      if (m.id === id) button.setAttribute("aria-current", "page");
      nav.append(button);
    });
  }

  function drawTiles() {
    var tiles = document.getElementById("tiles");
    tiles.replaceChildren();
    modules.forEach(function (m) {
      var tile = el("article", { class: "tile " + (m.up ? "up" : "down") });
      var top = el("div", { class: "tile-top" });
      top.append(el("h2", null, m.name.toUpperCase()));
      if (m.external) top.append(el("span", { class: "muted" }, "external"));
      var state = el("span", { class: "state" });
      state.append(el("i"), document.createTextNode(m.up ? "Running" : "Not running"));
      top.append(state);
      var foot = el("div", { class: "tile-foot" });
      foot.append(el("span", { class: "mono" }, m.url.replace(/^https?:\/\//, "").replace(/\/$/, "")));
      var open = el("button", { type: "button", class: "btn", "data-id": m.id }, "Open");
      open.disabled = !m.up;
      foot.append(open);
      tile.append(top);
      if (!m.up) tile.append(el("div", { class: "muted" }, "Start " + m.name + " and it appears here."));
      tile.append(foot);
      tiles.append(tile);
    });
    var up = modules.filter(function (m) { return m.up; }).length;
    document.getElementById("summary").textContent = up + " of " + modules.length + " running";
  }

  function show() {
    var id = selected();
    var module = modules.filter(function (m) { return m.id === id; })[0];
    overview.hidden = !!module;
    frame.hidden = !module;
    var address = module ? frameAddress(module) : null;
    if (address !== shown) {
      shown = address;
      if (address) frame.src = address; else frame.removeAttribute("src");
    }
    document.title = module ? module.name + " – Stykker Hub" : "Stykker Hub";
    drawNav();
    if (!module) drawTiles();
  }

  function load() {
    return fetch("/api/hub", { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (data) { modules = data.modules || []; show(); return loadAccess(); })
      .catch(function () { /* die Zentrale antwortet gerade nicht: der letzte Stand bleibt stehen */ });
  }

  // ── Geräte und Netz: nur an der Maschine selbst (die Antwort sagt, ob wir dort sind) ──
  function post(url, method) {
    return fetch(url, { method: method || "POST", headers: { "Content-Type": "application/json" } }).then(loadAccess);
  }
  function when(iso) {
    if (!iso) return "never";
    var date = new Date(iso);
    return isNaN(date) ? "" : date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
  function roleSwitch(device, module) {
    var seg = el("span", { class: "seg" });
    ["viewer", "admin"].forEach(function (role) {
      var button = el("button", { type: "button", "data-role": role }, role);
      if (device.roles[module] === role) button.className = "on";
      button.addEventListener("click", function () {
        post("/api/hub/devices/" + encodeURIComponent(device.id) + "/role?module=" + encodeURIComponent(module) + "&role=" + role);
      });
      seg.append(button);
    });
    return seg;
  }
  function drawAccess(access) {
    var section = document.getElementById("access");
    section.hidden = !access.local;
    if (!access.local) return;
    lastAccess = access;
    document.getElementById("remote-state").textContent = access.remote ? "On: paired devices in your network" : "Off: only this PC";
    var toggle = document.getElementById("remote-toggle");
    toggle.textContent = access.remote ? "Turn off" : "Turn on";
    toggle.onclick = function () { post("/api/hub/remote?on=" + !access.remote); };
    document.getElementById("remote-note").textContent = access.remote !== access.listening
      ? "Restart StykkerHUD and StykkerSYS for this to take effect: the port is opened or closed at start."
      : access.remote ? "Open one of these on the device:" : "Nothing outside this PC can reach the hub.";
    var addresses = document.getElementById("addresses");
    addresses.replaceChildren();
    if (access.remote && access.listening) access.addresses.forEach(function (a) { addresses.append(el("span", null, a)); });

    document.getElementById("code").textContent = access.code;
    document.getElementById("code-left").textContent = Math.floor(access.expiresIn / 60) + ":" + String(access.expiresIn % 60).padStart(2, "0");
    document.getElementById("code-new").onclick = function () { post("/api/hub/code"); };
    drawQr(access);
    drawWaiting(access.requests || []);

    // Die Geräteliste wird nur neu aufgebaut, wenn sich etwas geändert hat: der Abruf kommt alle paar Sekunden
    var devices = document.getElementById("devices");
    var key = JSON.stringify([access.devices, access.modules]);
    if (key === devicesKey) return;
    devicesKey = key;
    devices.replaceChildren();
    if (!access.devices.length) {
      devices.append(el("span", { class: "muted" }, "No device is paired yet. Turn on network access, open an address on the phone and type the code."));
      return;
    }
    var table = el("table");
    var head = el("tr");
    // Die Spalten kommen aus der Konfiguration (Monitor, SYS, LLM und die übrigen Module aus stykker.json)
    var columns = access.modules || [];
    ["Device"].concat(columns.map(function (c) { return c.name; }), ["Last seen", ""]).forEach(function (h) { head.append(el("th", null, h)); });
    var thead = el("thead");
    thead.append(head);
    table.append(thead);
    var body = el("tbody");
    access.devices.forEach(function (device) {
      var row = el("tr");
      var name = el("td");
      name.append(el("b", null, device.name), el("small", { class: "mono" }, device.address || ""));
      row.append(name);
      columns.forEach(function (c) {
        var cell = el("td", { "data-k": c.name });
        cell.append(roleSwitch(device, c.id));
        row.append(cell);
      });
      var remove = el("button", { type: "button", class: "btn" }, "Remove");
      remove.addEventListener("click", function () { post("/api/hub/devices/" + encodeURIComponent(device.id), "DELETE"); });
      var last = el("td"); last.append(remove);
      row.append(el("td", { "data-k": "Last seen" }, when(device.lastSeen)), last);
      body.append(row);
    });
    table.append(body);
    devices.append(table);
  }
  // ── QR-Code: die Adresse wählt man aus der Liste (Vorgabe: die erste); neu geholt, wenn der Code wechselt ──
  function drawQr(access) {
    var box = document.getElementById("qr-box");
    var names = access.qrAddresses || [];
    var show = access.remote && access.listening && names.length > 0;
    box.hidden = !show;
    if (!show) return;
    var select = document.getElementById("qr-address");
    var list = names.join("|");
    if (select.dataset.list !== list) {
      select.dataset.list = list;
      select.replaceChildren();
      names.forEach(function (n) { select.append(el("option", { value: n }, n)); });
    }
    var key = select.value + "|" + access.code;
    if (key === qrKey) return;
    qrKey = key;
    qrRound += 1;
    document.getElementById("qr").src = "/api/hub/qr?address=" + encodeURIComponent(select.value) + "&r=" + qrRound;
  }
  document.getElementById("qr-address").addEventListener("change", function () { if (lastAccess) drawQr(lastAccess); });

  // ── Wartende Geräte: Name, Modul, Ziffern und Restzeit; „Deny“ je Anfrage ──
  function drawWaiting(requests) {
    var key = JSON.stringify(requests.map(function (r) { return [r.id, r.name, r.moduleName, r.code]; }));
    if (key === waitingKey) return;   // dieselbe Liste: nichts neu aufbauen, die Restzeit läuft in tickWaiting
    waitingKey = key;
    var list = document.getElementById("waiting-list");
    list.replaceChildren();
    if (!requests.length) {
      list.append(el("span", { class: "muted" }, "No device is waiting."));
      return;
    }
    requests.forEach(function (r) {
      var row = el("div", { class: "waiting-row" });
      var info = el("div", { class: "waiting-info" });
      var left = el("span", { class: "muted left" });
      left.dataset.end = String(Date.now() + r.expiresIn * 1000);
      info.append(el("b", null, r.name), document.createTextNode(" for " + r.moduleName + " · "),
        el("span", { class: "mono waiting-code" }, r.code), document.createTextNode(" · "), left);
      var deny = el("button", { type: "button", class: "btn" }, "Deny");
      deny.addEventListener("click", function () { post("/api/hub/requests/" + encodeURIComponent(r.id) + "/deny"); });
      row.append(info, deny);
      list.append(row);
    });
    tickWaiting();
  }
  function tickWaiting() {
    document.querySelectorAll("#waiting-list .left").forEach(function (node) {
      var left = Math.max(0, Math.floor((Number(node.dataset.end) - Date.now()) / 1000));
      node.textContent = Math.floor(left / 60) + ":" + String(left % 60).padStart(2, "0") + " left";
    });
  }

  // Die Ziffern in das Feld, Freigabe als Admin oder als Zuschauer. Die Ziffern gehen im Körper, nicht in der Adresse.
  function approve(role) {
    var input = document.getElementById("approve-code");
    var note = document.getElementById("approve-note");
    note.textContent = "";
    fetch("/api/hub/requests/approve", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code: input.value, role: role }) })
      .then(function (r) { return r.json().then(function (answer) { return { ok: r.ok, answer: answer }; }); })
      .then(function (res) {
        if (res.ok) {
          input.value = "";
          note.textContent = res.answer.name + " is signed in as " + role + ".";
        } else {
          note.textContent = res.answer.message || "Could not approve.";
        }
        return loadAccess();
      })
      .catch(function () { note.textContent = "The hub did not answer. Try again."; });
  }
  document.getElementById("approve-admin").addEventListener("click", function () { approve("admin"); });
  document.getElementById("approve-viewer").addEventListener("click", function () { approve("viewer"); });

  function loadAccess() {
    return fetch("/api/hub/access", { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(drawAccess)
      .catch(function () { });
  }

  // ── Leiste ──
  function setFolded(folded, remember) {
    root.classList.toggle("folded", folded);
    document.getElementById("unfold").setAttribute("aria-expanded", String(!folded));
    // Am Handy ist das Menü eine Aktion, kein Zustand: gemerkt wird nur die Wahl am PC.
    if (remember && !small.matches) { try { localStorage.setItem("stykkerhub.bar", folded ? "folded" : "open"); } catch (e) { } }
  }
  document.getElementById("fold").addEventListener("click", function () { setFolded(true, true); });
  document.getElementById("unfold").addEventListener("click", function () { setFolded(!root.classList.contains("folded"), true); });
  small.addEventListener("change", function () {
    var folded = small.matches;
    try { if (!folded) folded = localStorage.getItem("stykkerhub.bar") === "folded"; } catch (e) { }
    setFolded(folded, false);
  });

  document.addEventListener("click", function (e) {
    var button = e.target.closest("button[data-id]");
    if (!button) return;
    location.hash = button.dataset.id;
    if (!button.dataset.id) history.replaceState(null, "", location.pathname);
    show();
    if (small.matches) setFolded(true, false);   // gewählt: Menü zu, das Modul hat wieder den ganzen Bildschirm
  });
  window.addEventListener("hashchange", show);

  // ── Thema: dasselbe wie im Monitor, und das Modul im Frame folgt ──
  var themeButton = document.getElementById("theme");
  function drawTheme() { themeButton.textContent = root.dataset.theme === "titan" ? "Dark" : "Titan"; }
  themeButton.addEventListener("click", function () {
    root.dataset.theme = root.dataset.theme === "titan" ? "dark" : "titan";
    try { localStorage.setItem("stykkerhud.theme", root.dataset.theme); } catch (e) { }
    drawTheme();
    show();
  });
  drawTheme();

  load();
  setInterval(load, 15000);
  // Auf der Übersicht am PC kommen Geräte und Wartende schneller: das Handy wartet auf die Freigabe
  setInterval(function () { if (!overview.hidden && !document.getElementById("access").hidden) loadAccess(); }, 3000);
  setInterval(tickWaiting, 1000);
})();
