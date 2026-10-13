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
      .then(function (data) { modules = data.modules || []; show(); })
      .catch(function () { /* die Zentrale antwortet gerade nicht: der letzte Stand bleibt stehen */ });
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
})();
