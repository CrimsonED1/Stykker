// Stykker Starter – die Seite. Sie zeigt nur an, was der Starter ihr schickt (JSON), und schickt Befehle zurück.
// Kein Server, kein Netz: window.external ist die Brücke des Fensters.
(function () {
  'use strict';

  var state = null;          // letzter Zustand vom Starter
  var found = null;          // Ergebnis der Prüfung der gewählten Datei
  var pendingSeq = null;     // Vorgang angestoßen: Zähler vorher; wenn er sich ändert, ist der Vorgang fertig
  var dirty = false;         // Einstellungen wurden geändert und noch nicht gespeichert
  var armed = {};            // Programm-Id → Entfernen bestätigt?
  var views = ['apps', 'add', 'copy', 'update', 'settings'];

  // Schickt eine Nachricht an den Starter. String → so wie er ist, sonst JSON.
  function send(msg) { try { window.external.sendMessage(typeof msg === 'string' ? msg : JSON.stringify(msg)); } catch (e) { } }
  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function size(b) {
    if (b === null || b === undefined) return '–';
    if (b < 1e6) return (b / 1e3).toFixed(0) + ' KB';
    if (b < 1e9) return (b / 1e6).toFixed(0) + ' MB';
    return (b / 1e9).toFixed(1) + ' GB';
  }
  function initials(name) {
    var parts = String(name || '?').trim().split(/\s+/);
    var s = parts.length > 1 ? parts[0][0] + parts[1][0] : String(name || '??').slice(0, 2);
    return esc(s.toUpperCase());
  }

  function show(view) {
    views.forEach(function (v) { $('v-' + v).classList.toggle('show', v === view); });
    document.querySelectorAll('#nav button').forEach(function (b) {
      if (b.dataset.v === view) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
    });
  }

  // ---------- Fenster-Chrome ----------

  function updateWindowControls(maximized) {
    var b = $('t-max');
    if (b) {
      b.textContent = maximized ? '─' : '□';
      b.title = maximized ? 'Restore' : 'Maximize';
    }
  }
  $('t-min').addEventListener('click', function () { send('window:minimize'); });
  $('t-max').addEventListener('click', function () { send('window:maximize'); });
  $('t-close').addEventListener('click', function () { send('window:close'); });

  // Fenster per Titelleiste ziehen (Delta-Meldung an den Starter)
  var dragging = false, lastX = 0, lastY = 0;
  $('tbar').addEventListener('mousedown', function (ev) {
    if (ev.target.closest('.t-btn')) return;
    dragging = true;
    lastX = ev.screenX;
    lastY = ev.screenY;
    document.body.style.cursor = 'grabbing';
    document.body.style.userSelect = 'none';
    ev.preventDefault();
  });
  document.addEventListener('mousemove', function (ev) {
    if (!dragging) return;
    var dx = ev.screenX - lastX;
    var dy = ev.screenY - lastY;
    lastX = ev.screenX;
    lastY = ev.screenY;
    if (dx !== 0 || dy !== 0) send('window:move ' + dx + ',' + dy);
  });
  document.addEventListener('mouseup', function () {
    if (!dragging) return;
    dragging = false;
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
  });

  // ---------- Programme ----------

  function renderPrograms() {
    var list = $('apps-list');
    var programs = (state && state.programs) || [];
    $('apps-sub').textContent = programs.length === 0 ? '' :
      programs.length + (programs.length === 1 ? ' program' : ' programs') + (state.checkedText ? ' · checked ' + state.checkedText : '');
    if (programs.length === 0) {
      list.innerHTML = '<div class="empty"><span>No programs yet. Add a program: it is copied here and runs from the copy, so the source can be replaced while it runs.</span>' +
        '<button class="btn primary" data-go="add">+ Add a program</button></div>';
      return;
    }
    list.innerHTML = programs.map(function (p) {
      var chips = [];
      // Ein kaputter Eintrag (Grund in p.broken) läuft nie; er lässt sich nur entfernen
      if (p.broken) chips.push('<span class="badge bad">Broken entry</span>');
      else chips.push(p.running ? '<span class="badge good">Running</span>' : '<span class="badge plain">Stopped</span>');
      if (!p.broken && !p.reachable) chips.push('<span class="badge bad">Source not reachable</span>');
      if (p.updateVersion) chips.push('<span class="badge">Update ' + esc(p.updateVersion) + '</span>');
      var buttons = [];
      if (p.updateVersion) buttons.push('<button class="btn sm primary" data-act="update" data-id="' + esc(p.id) + '">' +
        (p.running ? 'Update and restart' : 'Update to ' + esc(p.updateVersion)) + '</button>');
      if (p.changedSinceStart && !p.updateVersion) chips.push('<span class="badge">Changed since last start</span>');
      if (p.running) {
        buttons.push('<button class="btn sm" data-act="restart" data-id="' + esc(p.id) + '">Restart</button>');
        buttons.push('<button class="btn sm danger" data-act="stop" data-id="' + esc(p.id) + '">Stop</button>');
      } else if (!p.broken && (p.hasCopy || !p.copyToLocal)) {
        buttons.push('<button class="btn sm primary" data-act="start" data-id="' + esc(p.id) + '">Start</button>');
      }
      buttons.push('<button class="btn sm quiet danger" data-act="remove" data-id="' + esc(p.id) + '">' +
        (armed[p.id] ? 'Confirm remove' : 'Remove') + '</button>');
      var pids = p.pids && p.pids.length ? p.pids.join(', ') : '–';
      return '<div class="app">' +
        '<div class="icon">' + initials(p.name) + '</div>' +
        '<div style="min-width:0"><h3>' + esc(p.name || '(no name)') + ' ' + chips.join(' ') + '</h3>' +
        '<div class="path" title="' + esc(p.source) + '">' + esc(p.source || '(no source file)') + '</div>' +
        '<div class="meta">' + (p.broken ? '<span><b>' + esc(p.broken) + '</b></span>' :
          (p.copyToLocal ? '<span>Local <b>' + esc(p.local || '–') + '</b></span>' : '<span><b>runs from source</b></span>')) +
        '<span>Source <b>' + esc(p.sourceVersion || '–') + '</b></span>' +
        '<span>PID <b class="mono">' + esc(pids) + '</b></span></div></div>' +
        '<div class="actions">' + buttons.join('') + '</div></div>';
    }).join('');
  }

  // ---------- Kopieren ----------

  function renderCopy() {
    var body = $('copy-body');
    var c = state && state.copy;
    if (!c) {
      body.innerHTML = '<p class="muted" style="margin:0">Nothing is being copied.</p>';
      return;
    }
    body.innerHTML =
      '<div style="display:grid;gap:14px">' +
      '<div><div class="row" style="margin-bottom:6px"><b>Copying ' + esc(c.name) + '</b>' +
      '<span class="muted small">next to the version that runs, which keeps running</span><span class="sp"></span>' +
      '<span class="mono">' + c.percent.toFixed(0) + ' %</span></div>' +
      '<div class="bar"><i style="width:' + c.percent + '%"></i></div></div>' +
      '<dl class="kv"><dt>Files</dt><dd>' + c.filesDone + ' of ' + c.filesTotal + '</dd>' +
      '<dt>Data</dt><dd>' + size(c.bytesDone) + ' of ' + size(c.bytesTotal) + '</dd>' +
      '<dt>Speed</dt><dd>' + esc(c.speed) + '</dd>' +
      '<dt>Left</dt><dd>' + esc(c.left) + '</dd>' +
      '<dt>Now</dt><dd>' + esc(c.current) + '</dd></dl>' +
      '<div class="row"><span class="muted small">Copied into a new folder; nothing is replaced until every file is there and checked.</span>' +
      '<span class="sp"></span><button class="btn" data-act="cancel">Cancel</button></div></div>';
  }

  // ---------- Ablauf ----------

  function renderUpdate() {
    var body = $('update-body');
    var last = state && state.last;
    if (!last) {
      body.innerHTML = '<p class="muted" style="margin:0">No update or copy has run yet.</p>';
      return;
    }
    body.innerHTML = '<div style="display:grid;gap:12px"><div><b>' + esc(last.title) + '</b>' +
      '<div class="muted small">' + esc(last.message) + '</div></div>' +
      '<ol class="steps">' + last.steps.map(function (s) {
        var cls = s.state === 'done' ? 'done' : s.state === 'fail' ? 'fail' : '';
        return '<li class="' + cls + '"><span class="dot"></span><div><b>' + esc(s.label) + '</b>' +
          (s.detail ? '<div class="small muted">' + esc(s.detail) + '</div>' : '') + '</div></li>';
      }).join('') + '</ol>' +
      '<div class="muted small">If a new version does not start, the Starter starts the previous one again and says so.</div></div>';
  }

  // ---------- Einstellungen ----------

  function renderSettings() {
    if (!state || !state.settings) return;
    $('s-dir').value = state.dataDir || '';
    if (dirty) return;   // nicht überschreiben, was gerade getippt oder gewählt wird
    var s = state.settings;
    $('s-interval').value = String(s.checkMinutes);
    $('s-start').checked = s.startWithWindows;
    $('s-ask').checked = s.askBeforeUpdate;
    $('s-stopped').checked = s.updateStoppedWithoutAsking;
    $('s-kept').value = String(s.versionsKept);
    $('s-start').disabled = !s.autostartApplies;
    $('s-save').title = s.autostartApplies ? '' : 'A test data folder does not change the autostart';
  }

  function renderFooter() {
    if (!state) return;
    var parts = [
      'Local copies in ' + esc(state.dataDir) + ' · ' + size(state.localBytes),
      'Checks every ' + esc(state.checkEvery),
      state.nextCheckText ? 'next at ' + esc(state.nextCheckText) : '',
      state.settings && state.settings.startWithWindows ? 'Starts with Windows' : '',
    ].filter(Boolean);
    $('apps-foot').innerHTML = parts.map(function (p) { return '<span>' + p + '</span>'; }).join('<span class="sp"></span>');
    $('btn-check').disabled = false;
  }

  function renderNotice() {
    var el = $('toasts');
    var notice = state && state.notice;
    if (!notice) { el.innerHTML = ''; return; }
    var text = typeof notice === 'string' ? notice : (notice && notice.text) || '';
    var level = typeof notice === 'string' ? 'acc' : (notice && notice.level) || 'acc';
    el.innerHTML = '<div class="toast" style="--level:var(--' + level + ')">' + esc(text) + '</div>';
  }

  function render() {
    if (!state) return;
    renderPrograms();
    renderCopy();
    renderUpdate();
    renderSettings();
    renderFooter();
    renderNotice();
    // Nach einem Vorgang, den der Benutzer angestoßen hat, zeigt der Starter das Ergebnis; sonst bleibt die Seite stehen
    if (pendingSeq !== null && !state.copy && state.opSeq !== pendingSeq) {
      pendingSeq = null;
      show('update');
    }
  }

  function startOp() {
    pendingSeq = state ? state.opSeq : 0;
    show('copy');
  }

  // ---------- Hinzufügen ----------

  var inspectTimer = null;
  function inspect() {
    var path = $('f-path').value.trim();
    found = null;
    $('f-go').disabled = true;
    if (!path) { $('f-info').innerHTML = '<span class="muted">Choose a program file to see what is found.</span>'; return; }
    $('f-info').innerHTML = '<span class="muted">Reading the folder…</span>';
    send({ cmd: 'inspect', path: path });
  }

  function drawFound() {
    var m = found;
    if (!m) return;
    var where = $('f-copy').checked
      ? '<div class="muted">Copy goes to <span class="mono">' + esc(m.copyTo) + '</span></div>'
      : '<div class="muted">Runs from <span class="mono">' + esc(m.sourceFolder) + '</span>, no copy</div>';
    $('f-info').innerHTML =
      '<div><b>Found:</b> ' + esc(m.name) + ' · version <b>' + esc(m.version) + '</b></div>' +
      '<div class="muted">Folder: ' + m.files + ' files, ' + size(m.bytes) + (m.largest ? ' · largest: ' + esc(m.largest) : '') + '</div>' +
      where;
  }

  function renderFound(m) {
    if (m.type === 'error') {
      found = null;
      $('f-info').innerHTML = '<span class="muted">' + esc(m.text) + '</span>';
      $('f-go').disabled = true;
      return;
    }
    found = m;
    if (!$('f-name').value.trim()) $('f-name').value = m.name;
    drawFound();
    $('f-go').disabled = false;
  }

  // Der Knopf sagt, was passiert: mit Kopie kopieren und starten, ohne Kopie nur starten
  function updateCopyChoice() {
    $('f-go').textContent = $('f-copy').checked ? 'Copy and start' : 'Start from the source folder';
    drawFound();
  }

  // ---------- Ereignisse ----------

  function onMessage(raw) {
    var m;
    try { m = JSON.parse(raw); } catch (e) {
      // Fenster-Chrome: einfache Zeichenketten (nicht JSON)
      if (raw && raw.startsWith('window:state ')) {
        updateWindowControls(raw.indexOf('maximized') !== -1);
      }
      return;
    }
    if (m.type === 'state') { state = m; render(); }
    else if (m.type === 'found' || m.type === 'error') renderFound(m);
    else if (m.type === 'picked') { $('f-path').value = m.path; inspect(); }
  }

  document.addEventListener('click', function (ev) {
    var t = ev.target.closest('button, [data-go]');
    if (!t) return;
    if (t.dataset.v) { show(t.dataset.v); return; }
    if (t.dataset.go) { show(t.dataset.go); return; }
    var act = t.dataset.act, id = t.dataset.id;
    if (act === 'remove') {
      if (!armed[id]) { armed[id] = true; renderPrograms(); return; }
      armed[id] = false;
      send({ cmd: 'remove', id: id });
      return;
    }
    if (act === 'update') { startOp(); send({ cmd: 'update', id: id }); return; }
    if (act === 'start' || act === 'stop' || act === 'restart') { send({ cmd: act, id: id }); return; }
    if (act === 'cancel') { send({ cmd: 'cancel' }); return; }
  });

  $('nav').addEventListener('click', function (ev) {
    var b = ev.target.closest('button[data-v]');
    if (b) show(b.dataset.v);
  });
  $('btn-add').addEventListener('click', function () { show('add'); });
  $('btn-check').addEventListener('click', function () { send({ cmd: 'check' }); });
  $('f-browse').addEventListener('click', function () { send({ cmd: 'pick' }); });
  $('f-path').addEventListener('input', function () {
    clearTimeout(inspectTimer);
    inspectTimer = setTimeout(inspect, 350);
  });
  $('f-cancel').addEventListener('click', function () { show('apps'); });
  $('f-copy').addEventListener('change', updateCopyChoice);
  $('f-go').addEventListener('click', function () {
    if (!found) return;
    startOp();
    send({
      cmd: 'add',
      path: $('f-path').value.trim(),
      name: $('f-name').value.trim(),
      args: $('f-args').value.trim(),
      startWithStarter: $('f-start').checked,
      updateWithoutAsking: $('f-noask').checked,
      keepPrevious: $('f-keep').checked,
      copyToLocal: $('f-copy').checked,
    });
  });

  ['s-interval', 's-start', 's-ask', 's-stopped', 's-kept'].forEach(function (id) {
    $(id).addEventListener('change', function () { dirty = true; });
  });
  $('s-save').addEventListener('click', function () {
    dirty = false;
    send({
      cmd: 'settings',
      checkMinutes: parseInt($('s-interval').value, 10),
      startWithWindows: $('s-start').checked,
      askBeforeUpdate: $('s-ask').checked,
      updateStoppedWithoutAsking: $('s-stopped').checked,
      versionsKept: parseInt($('s-kept').value, 10),
    });
  });
  $('s-cleanup').addEventListener('click', function () { send({ cmd: 'cleanup' }); });
  $('s-open').addEventListener('click', function () { send({ cmd: 'openFolder' }); });

  // Titan oder Dark. Die Wahl merkt sich die Ansicht; ohne Speicher geht es trotzdem.
  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    $('theme').textContent = theme === 'titan' ? 'Dark' : 'Titan';
    try { localStorage.setItem('stykker-starter-theme', theme); } catch (e) { }
  }
  $('theme').addEventListener('click', function () {
    applyTheme(document.documentElement.dataset.theme === 'titan' ? 'dark' : 'titan');
  });
  try {
    var saved = localStorage.getItem('stykker-starter-theme');
    if (saved === 'titan' || saved === 'dark') applyTheme(saved);
  } catch (e) { }

  // Die Seite meldet sich, sobald ihr Skript läuft. Erst danach schickt der Starter etwas.
  try {
    window.external.receiveMessage(onMessage);
    window.external.sendMessage('shell.ready');
  } catch (e) { }
  show('apps');
})();
