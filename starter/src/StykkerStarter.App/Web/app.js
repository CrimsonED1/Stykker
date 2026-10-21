// Stykker Starter – die Seite. Sie zeigt nur an, was der Starter ihr schickt (JSON), und schickt Befehle zurück.
// Kein Server, kein Netz: window.external ist die Brücke des Fensters.
(function () {
  'use strict';

  var state = null;            // letzter Zustand vom Starter (nur lesen)
  var open = {};               // Programm-Id → Details aufgeklappt?
  var menuFor = null;          // Programm-Id, deren ⋯-Menü offen ist
  var copyOpenedFor = null;    // Programm, dessen Details beim Kopieren schon aufgeklappt wurden
  var lastNoticeSeq = null;    // Nummer der zuletzt gezeigten Meldung (null: noch kein Zustand)
  var settingsTouched = 0;     // letzte Änderung in den Einstellungen; kurz danach nichts von außen überschreiben
  var maximized = false;
  var form = null;             // das Unterfenster Add/Edit: { mode: 'add' | 'edit', id, copy }
  var found = null;            // Ergebnis der Prüfung der Programmdatei (Add)
  var confirmId = null;        // Programm, dessen Entfernen bestätigt werden soll
  var dragId = null, dragging = false, gripDown = false;
  var inspectTimer = null;
  var startupDeadline = null;  // Ende der Pause vor dem Start (Zeit der Seite), null ohne Countdown

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
    var s = parts.length > 1 ? parts[0].charAt(0) + parts[1].charAt(0) : String(name || '??').slice(0, 2);
    return esc(s.toUpperCase());
  }
  // „2026-10-21 22:40“ → „21.10. 22:40“ (sonst unverändert)
  function fmtBuilt(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}:\d{2})$/.exec(s || '');
    return m ? m[3] + '.' + m[2] + '. ' + m[4] : (s || '');
  }
  function programs() { return (state && state.programs) || []; }
  function findProgram(id) {
    var list = programs();
    for (var i = 0; i < list.length; i++) if (list[i].id === id) return list[i];
    return null;
  }
  // Ein Auswahlfeld auf einen Wert setzen; fehlt der Wert, kommt er als eigene Auswahl dazu
  function setSelect(el, value, unit) {
    var v = String(value);
    if (!Array.prototype.some.call(el.options, function (o) { return o.value === v; })) {
      var o = document.createElement('option');
      o.value = v;
      o.textContent = v + unit;
      el.appendChild(o);
    }
    el.value = v;
  }
  function clampDelay(v) {
    var n = parseInt(v, 10);
    if (isNaN(n)) return 0;
    return Math.max(0, Math.min(120, n));
  }

  function show(view) {
    ['apps', 'settings'].forEach(function (v) { $('v-' + v).classList.toggle('show', v === view); });
    document.querySelectorAll('#nav button').forEach(function (b) {
      if (b.dataset.v === view) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
    });
  }

  // ---------- Fenster: Titelleiste, Knöpfe, Ziehen, Doppelklick ----------

  function updateWindowControls(max) {
    maximized = max;
    var b = $('t-max');
    b.textContent = max ? '❐' : '□';
    b.title = max ? 'Restore' : 'Maximize';
  }
  function toggleMaximize() { send(maximized ? 'window:restore' : 'window:maximize'); }
  $('t-min').addEventListener('click', function () { send('window:minimize'); });
  $('t-max').addEventListener('click', toggleMaximize);
  $('t-close').addEventListener('click', function () { send('window:close'); });

  // Die Titelleiste zieht das Fenster; Tabs, Knöpfe und Eingabefelder nicht
  var NO_DRAG = 'button, input, select, label, a';
  var winDrag = false, lastX = 0, lastY = 0;
  $('tbar').addEventListener('mousedown', function (ev) {
    if (ev.button !== 0 || ev.target.closest(NO_DRAG)) return;
    winDrag = true;
    lastX = ev.screenX;
    lastY = ev.screenY;
    ev.preventDefault();
  });
  document.addEventListener('mousemove', function (ev) {
    if (!winDrag) return;
    var dx = ev.screenX - lastX, dy = ev.screenY - lastY;
    lastX = ev.screenX;
    lastY = ev.screenY;
    if (dx !== 0 || dy !== 0) send('window:move ' + dx + ',' + dy);
  });
  document.addEventListener('mouseup', function () { winDrag = false; });
  $('tbar').addEventListener('dblclick', function (ev) {
    if (!ev.target.closest(NO_DRAG)) toggleMaximize();
  });

  // ---------- Programme: Zeile, Status, Knöpfe ----------

  // Der Text eines laufenden Vorgangs: lang für den Knopf, kurz für das Kennzeichen
  function busyText(p, short) {
    var copy = state && state.copy && state.copy.id === p.id ? state.copy : null;
    var dots = short ? '' : '…';
    switch (p.busy) {
      case 'queued': return 'Queued' + dots;
      case 'starting': return 'Starting' + dots;
      case 'stopping':
        if (p.busySince && p.busyGraceMs) {
          var left = Math.ceil((p.busySince + p.busyGraceMs - Date.now()) / 1000);
          if (left > 0) return 'Closing' + (short ? ' ' : '… ') + left + ' s';
        }
        return 'Stopping' + dots;
      case 'restarting': return 'Restarting' + dots;
      case 'updating': return 'Updating' + dots;
      case 'copying':
        return copy ? 'Copying' + (short ? ' ' : '… ') + Math.round(copy.percent) + ' %' : 'Copying' + dots;
      case 'rollback': return 'Going back' + dots;
    }
    return p.busy || '';
  }

  function readyText(r) {
    if (r === 'waiting') return 'waiting for an answer';
    if (r === 'ready') return 'answers';
    if (r === 'timeout') return 'no answer within 60 s';
    return '';
  }

  // Die Kennzeichen: Zustand, Quelle, Update oder neuer Stand
  function chipsHtml(p) {
    if (p.broken) return '<span class="badge bad">Broken entry</span>';
    var out = [];
    if (p.busy) out.push('<span class="badge acc busy-chip" data-id="' + esc(p.id) + '">' + esc(busyText(p, true)) + '</span>');
    else if (p.running) {
      if (p.ready === 'waiting') out.push('<span class="badge acc">Starting</span>');
      else if (p.ready === 'timeout') out.push('<span class="badge warn">Not ready</span>');
      else out.push('<span class="badge good">Running</span>');
    }
    else if (p.crashState === 'crashed') out.push('<span class="badge bad">Crashed</span>');
    else if (p.crashState === 'restarting') out.push('<span class="badge acc">Restarting</span>');
    else if (p.crashState === 'gaveUp') out.push('<span class="badge bad">Gave up</span>');
    else out.push('<span class="badge plain">Stopped</span>');
    if (p.reachable === false) out.push('<span class="badge bad">Source not reachable</span>');
    if (p.updateIsNewBuild) out.push('<span class="badge warn">New build' + (p.sourceBuiltAt ? ' · ' + esc(fmtBuilt(p.sourceBuiltAt)) : '') + '</span>');
    else if (p.updateVersion) out.push('<span class="badge warn">Update ' + esc(p.updateVersion) + '</span>');
    else if (p.changedSinceStart) out.push('<span class="badge warn">Source changed</span>');
    return out.join(' ');
  }

  function btn(label, act, id, cls) {
    return '<button class="btn ' + (cls || '') + '" data-act="' + act + '" data-id="' + esc(id) + '">' + esc(label) + '</button>';
  }

  // Die Hauptknöpfe: während eines Vorgangs ein ausgegrauter Knopf mit Kreisel und Text
  function mainHtml(p) {
    if (p.busy) {
      return '<button class="btn busy" disabled><span class="spin"></span><span class="busy-label" data-id="' + esc(p.id) + '">' +
        esc(busyText(p, false)) + '</span></button>';
    }
    if (p.broken) return '';
    var update = !!(p.updateVersion || p.updateIsNewBuild);
    var copyMissing = !!p.copyToLocal && !p.hasCopy;
    var out = [];
    if (update) {
      var label = p.updateIsNewBuild ? 'New build' : 'Update';
      if (p.running) label += ' and restart';
      out.push(btn(label, 'update', p.id, 'primary'));
    }
    if (p.running) {
      out.push(btn('Restart', 'restart', p.id, ''));
      out.push(btn('Stop', 'stop', p.id, 'danger'));
    } else {
      out.push(btn(copyMissing ? 'Copy and start' : 'Start', 'start', p.id, update ? '' : 'primary'));
    }
    return out.join('');
  }

  // Das ⋯-Menü der Zeile; während eines Vorgangs sind die Änderungen aus
  function menuHtml(p) {
    if (menuFor !== p.id) return '';
    var id = esc(p.id);
    var busy = p.busy ? ' disabled' : '';
    var items = [];
    if (!p.broken) items.push('<button data-act="edit" data-id="' + id + '"' + busy + '>Edit…</button>');
    if (!p.broken && p.previous && p.copyToLocal) {
      items.push('<button data-act="rollback" data-id="' + id + '"' + busy + '>Go back to ' + esc(p.previous) + '</button>');
    }
    items.push('<button data-act="folder" data-id="' + id + '">Open folder</button>');
    items.push('<button data-act="log">Show the log</button>');
    items.push('<button class="bad" data-act="remove" data-id="' + id + '"' + ((p.busy || p.running) ? ' disabled' : '') + '>Remove…</button>');
    return '<div class="moremenu" role="menu">' + items.join('') + '</div>';
  }

  // Der Kopiervorgang eines Programms (nur im Details-Bereich dieses Programms)
  function copyHtml(p) {
    var c = state && state.copy;
    if (!c || c.id !== p.id) return '';
    return '<div class="copybox">' +
      '<div class="row"><b>Copying ' + esc(p.name) + '</b><span class="muted small">next to the version that runs, which keeps running</span>' +
      '<span class="sp"></span><span class="mono">' + Math.round(c.percent) + ' %</span></div>' +
      '<div class="progress"><i style="width:' + c.percent + '%"></i></div>' +
      '<dl class="kv"><dt>Files</dt><dd>' + c.filesDone + ' of ' + c.filesTotal + '</dd>' +
      '<dt>Data</dt><dd>' + size(c.bytesDone) + ' of ' + size(c.bytesTotal) + '</dd>' +
      '<dt>Speed</dt><dd>' + esc(c.speed) + '</dd>' +
      '<dt>Left</dt><dd>' + esc(c.left) + '</dd>' +
      '<dt>Now</dt><dd title="' + esc(c.current) + '">' + esc(c.current) + '</dd></dl>' +
      '<div class="row"><span class="muted small">Copied into a new folder; nothing is replaced until every file is there and checked.</span>' +
      '<span class="sp"></span><button class="btn" data-act="cancel">Cancel</button></div></div>';
  }

  // Die Details: Quelle, Ordner, Versionen, Startoptionen, der letzte Vorgang mit seinen Schritten
  function detailsHtml(p) {
    var rows = [
      ['Source', p.source || '–'],
      [p.copyToLocal ? 'Copy folder' : 'Folder', p.folder || '–'],
      ['Source version', p.sourceVersion || '–'],
    ];
    if (p.copyToLocal) rows.push(['Local version', p.local || '–']);
    if (p.previous) rows.push(['Previous version', p.previous]);
    rows.push(['Start', (p.startWithStarter ? 'with the Starter' : 'by hand only') + (p.startDelaySeconds ? ' · waits ' + p.startDelaySeconds + ' s' : '')]);
    rows.push(['Restart after a crash', p.restartOnCrash ? 'on' + (p.restarts ? ' · ' + p.restarts + ' in the last 10 min' : '') : 'off']);
    rows.push(['Ready check', p.readyCheck ? p.readyCheck + (p.ready ? ' · ' + readyText(p.ready) : '') : 'off']);
    var html = '<div class="det"><dl class="kv">' + rows.map(function (r) {
      return '<dt>' + esc(r[0]) + '</dt><dd title="' + esc(r[1]) + '">' + esc(r[1]) + '</dd>';
    }).join('') + '</dl>' + copyHtml(p);
    var op = state && state.ops && state.ops[p.id];
    if (op) {
      html += '<div class="ops"><div class="row"><b class="' + esc(op.kind) + '">' + esc(op.title) + '</b>' +
        '<span class="muted small">' + esc(op.message) + '</span></div>' +
        (op.steps && op.steps.length ? '<ol class="steps">' + op.steps.map(function (s) {
          return '<li class="' + esc(s.state) + '">' + esc(s.label) +
            (s.detail ? ' <span class="muted small">' + esc(s.detail) + '</span>' : '') + '</li>';
        }).join('') + '</ol>' : '') + '</div>';
    }
    return html + '</div>';
  }

  function rowHtml(p) {
    var id = esc(p.id);
    var cls = 'app' + (p.busy ? ' working' : '') + (p.broken ? ' broken' : '');
    var where = p.broken ? esc(p.broken) : (p.copyToLocal ? (p.hasCopy ? 'local copy' : 'no copy yet') : 'runs from source');
    var pids = p.running && p.pids && p.pids.length ? ' · PID ' + esc(p.pids.join(', ')) : '';
    var copy = p.busy === 'copying' && state.copy && state.copy.id === p.id ? state.copy : null;
    var progress = p.busy ? '<div class="progress' + (copy ? '' : ' indet') + '"><i style="width:' +
      (copy ? copy.percent : 30) + '%"></i></div>' : '';
    var ver = p.local || p.sourceVersion || '';
    return '<div class="' + cls + '" data-id="' + id + '" draggable="true">' +
      '<div class="grip" title="Drag to change the order" aria-hidden="true">⋮⋮</div>' +
      '<div class="ico">' + initials(p.name) + '</div>' +
      '<div class="info"><h3>' + esc(p.name || '(no name)') + (ver ? ' <span class="ver">' + esc(ver) + '</span>' : '') +
      ' ' + chipsHtml(p) + '</h3>' +
      '<div class="path" title="' + esc(p.source) + '">' + esc(p.source || '(no source file)') + ' · ' + where + pids +
      ' · <a href="#" data-act="details" data-id="' + id + '">' + (open[p.id] ? 'Hide details' : 'Details') + '</a></div></div>' +
      '<div class="acts">' + mainHtml(p) +
      '<button class="btn icon" title="More" aria-label="More" data-act="menu" data-id="' + id + '">⋯</button></div>' +
      progress + (open[p.id] ? detailsHtml(p) : '') + menuHtml(p) + '</div>';
  }

  function renderPrograms() {
    var list = programs();
    if (!list.length) {
      $('apps-list').innerHTML = '<div class="empty"><div><b>No programs yet</b>' +
        '<span class="muted">Add a program: it is copied here and runs from the copy, so the source can be replaced while it runs.</span></div>' +
        '<span class="sp"></span><button class="btn primary" data-go="add">+ Add a program</button></div>';
      return;
    }
    $('apps-list').innerHTML = list.map(rowHtml).join('');
  }

  function renderHead() {
    $('count').textContent = String(programs().length);
    $('checked').textContent = state.checkedText ? 'checked ' + state.checkedText : 'not checked yet';
    var c = $('btn-check');
    c.disabled = !!state.checking;
    c.innerHTML = state.checking ? '<span class="spin"></span>Checking…' : 'Check now';
  }

  function renderFoot() {
    var list = programs();
    var running = list.filter(function (p) { return p.running; }).length;
    $('foot-left').textContent = list.length + (list.length === 1 ? ' program' : ' programs') + ' · ' + running + ' running';
    $('foot-right').textContent = 'Checks every ' + (state.checkEvery || '–') + (state.nextCheckText ? ' · next at ' + state.nextCheckText : '');
  }

  // ---------- Pause vor dem Start der Programme ----------

  function renderStartup() {
    var left = state.startupLeftMs, next = state.startupNext;
    var counting = left !== null && left !== undefined;
    $('startup').hidden = !(counting || next);
    startupDeadline = counting ? Date.now() + left : null;
    if (!$('startup').hidden) drawStartup();
  }
  function drawStartup() {
    var next = state && state.startupNext;
    var text;
    if (startupDeadline !== null) {
      var s = Math.max(0, Math.ceil((startupDeadline - Date.now()) / 1000));
      text = (next ? 'Starting <b>' + esc(next) + '</b>' : 'Starting the programs') + ' in <b class="mono">' + s + '</b> s';
    } else {
      text = 'Starting <b>' + esc(next || 'the programs') + '</b> …';
    }
    $('startup-text').innerHTML = text;
  }

  // ---------- Einstellungen: jede Änderung wird sofort gespeichert ----------

  function renderSettings() {
    $('s-dir').value = state.dataDir || '';
    $('s-local').textContent = size(state.localBytes) + ' in copies · ' + size(state.cleanupBytes) + ' unused';
    $('s-cleanup').disabled = !state.cleanupBytes;
    var s = state.settings;
    if (!s || Date.now() - settingsTouched < 1500) return;   // gerade geändert: der Starter antwortet gleich
    setSelect($('s-interval'), s.checkMinutes, ' min');
    $('s-start').checked = !!s.startWithWindows;
    $('s-start').disabled = !s.autostartApplies;
    $('s-start').parentNode.title = s.autostartApplies ? '' : 'A test data folder does not change the autostart';
    $('s-ask').checked = !!s.askBeforeUpdate;
    $('s-stopped').checked = !!s.updateStoppedWithoutAsking;
    setSelect($('s-kept'), s.versionsKept, '');
    setSelect($('s-delay'), s.startupDelaySeconds, ' s');
    $('s-close').value = s.closeAction || 'ask';
  }
  function settingsChanged() {
    settingsTouched = Date.now();
    send({
      cmd: 'settings',
      checkMinutes: parseInt($('s-interval').value, 10),
      startWithWindows: $('s-start').checked,
      askBeforeUpdate: $('s-ask').checked,
      updateStoppedWithoutAsking: $('s-stopped').checked,
      versionsKept: parseInt($('s-kept').value, 10),
      startupDelaySeconds: parseInt($('s-delay').value, 10),
      closeAction: $('s-close').value,
    });
  }

  // ---------- Meldungen (unten rechts) ----------

  function toast(text, bad) {
    var t = document.createElement('div');
    t.className = 'toast' + (bad ? ' bad' : '');
    t.setAttribute('role', bad ? 'alert' : 'status');
    t.textContent = text;
    $('toasts').appendChild(t);
    setTimeout(function () {
      t.classList.add('out');
      setTimeout(function () { t.remove(); }, 320);
    }, 4000);
  }
  // Jede neue Meldung des Starters zeigt sich einmal; der erste Zustand zeigt keine alte Meldung
  function renderNotice() {
    if (lastNoticeSeq === null) { lastNoticeSeq = state.noticeSeq || 0; return; }
    if (state.noticeSeq !== lastNoticeSeq) {
      lastNoticeSeq = state.noticeSeq;
      if (state.notice) toast(state.notice, !!state.noticeBad);
    }
  }

  function render() {
    if (!state) return;
    // Wird ein Programm kopiert, klappen seine Details dafür auf (einmal je Kopiervorgang)
    var copy = state.copy;
    if (copy && copy.id && copyOpenedFor !== copy.id) { open[copy.id] = true; copyOpenedFor = copy.id; }
    if (!copy) copyOpenedFor = null;
    renderNotice();
    renderFoot();
    renderStartup();
    renderSettings();
    if (dragging) return;   // die Liste nicht unter dem Zeiger austauschen; nach dem Loslassen wird neu gezeichnet
    renderHead();
    renderPrograms();
  }

  // ---------- Unterfenster: Add a program / Edit ----------

  function setRunFrom(copy) {
    if (!form) return;
    form.copy = copy;
    $('f-run-copy').setAttribute('aria-pressed', String(copy));
    $('f-run-src').setAttribute('aria-pressed', String(!copy));
    if (form.mode === 'add') {
      $('f-run-hint').textContent = copy
        ? 'With a copy the source can be rebuilt while the program runs.'
        : 'Without a copy the program runs from the source folder; a build into that folder can fail while it runs, because Windows locks its files.';
      drawFound();
    }
    updateFormButtons();
  }

  // Der Knopf sagt, was passiert; ohne gültige Datei (oder bei Dopplung) ist er aus
  function updateFormButtons() {
    if (!form) return;
    var go = $('f-go');
    if (form.mode === 'add') {
      go.textContent = form.copy ? 'Copy and start' : 'Start from the source folder';
      go.disabled = !found || !!found.duplicateOf;
    } else {
      go.textContent = 'Save';
      go.disabled = !$('f-name').value.trim();
    }
  }

  function drawFound() {
    if (!found || !form) return;
    var where = form.copy
      ? 'Copy goes to <span class="mono">' + esc(found.copyTo) + '</span>'
      : 'Runs from <span class="mono">' + esc(found.sourceFolder) + '</span>, no copy';
    var html = '<div><b>Found:</b> ' + esc(found.name) + ' · version <b>' + esc(found.version) + '</b>' +
      (found.builtAt ? ' · built ' + esc(fmtBuilt(found.builtAt)) : '') + '</div>' +
      '<div class="muted">Folder: ' + found.files + ' files, ' + size(found.bytes) + (found.largest ? ' · largest: ' + esc(found.largest) : '') + '</div>' +
      '<div class="muted">' + where + '</div>';
    if (found.duplicateOf) html += '<div class="warnline">Already in the list as ' + esc(found.duplicateOf) + '</div>';
    $('f-found').innerHTML = html;
    $('f-found').className = 'found' + (found.duplicateOf ? ' bad' : '');
  }

  function inspect() {
    if (!form || form.mode !== 'add') return;
    var path = $('f-path').value.trim();
    found = null;
    updateFormButtons();
    $('f-found').className = 'found';
    if (!path) {
      $('f-found').innerHTML = '<span class="muted">Choose a program file to see what is found.</span>';
      return;
    }
    $('f-found').innerHTML = '<span class="muted">Reading the folder…</span>';
    send({ cmd: 'inspect', path: path, args: $('f-args').value.trim() });
  }

  function onInspect(m) {
    if (!form || form.mode !== 'add') return;
    if (m.type === 'error') {
      found = null;
      $('f-found').className = 'found';
      $('f-found').innerHTML = '<span class="muted">' + esc(m.text) + '</span>';
      updateFormButtons();
      return;
    }
    found = m;
    if (!$('f-name').value.trim()) $('f-name').value = m.name;
    drawFound();
    updateFormButtons();
  }

  function openAdd() {
    menuFor = null;
    found = null;
    form = { mode: 'add', id: null, copy: true };
    $('form-title').textContent = 'ADD A PROGRAM';
    $('f-path').value = '';
    $('f-path').readOnly = false;
    $('f-browse').hidden = false;
    $('f-name').value = '';
    $('f-args').value = '';
    $('f-start').checked = true;
    $('f-noask').checked = false;
    $('f-keep').checked = true;
    $('f-restart').checked = false;
    $('f-delay').value = '0';
    $('f-ready').value = '';
    $('f-run-copy').disabled = false;
    $('f-run-src').disabled = false;
    $('f-found').className = 'found';
    $('f-found').innerHTML = '<span class="muted">Choose a program file to see what is found.</span>';
    setRunFrom(true);
    $('dlg-form').hidden = false;
    $('f-path').focus();
  }

  // Bearbeiten: die Datei steht fest; die Kopie lässt sich nur umschalten, wenn das Programm steht
  function openEdit(p) {
    menuFor = null;
    found = null;
    form = { mode: 'edit', id: p.id, copy: !!p.copyToLocal };
    $('form-title').textContent = 'EDIT ' + String(p.name || '').toUpperCase();
    $('f-path').value = p.source || '';
    $('f-path').readOnly = true;
    $('f-browse').hidden = true;
    $('f-name').value = p.name || '';
    $('f-args').value = p.arguments || '';
    $('f-start').checked = !!p.startWithStarter;
    $('f-noask').checked = !!p.updateWithoutAsking;
    $('f-keep').checked = !!p.keepPrevious;
    $('f-restart').checked = !!p.restartOnCrash;
    $('f-delay').value = String(p.startDelaySeconds || 0);
    $('f-ready').value = p.readyCheck || '';
    $('f-found').className = 'found';
    $('f-found').innerHTML = '<div><b>Local copy:</b> ' + esc(p.local || 'none') + ' · <b>Source:</b> ' + esc(p.sourceVersion || '–') + '</div>';
    setRunFrom(!!p.copyToLocal);
    $('f-run-copy').disabled = !!p.running;
    $('f-run-src').disabled = !!p.running;
    $('f-run-hint').textContent = p.running
      ? 'Stop ' + (p.name || 'the program') + ' to change this.'
      : 'Only while the program is stopped.';
    updateFormButtons();
    $('dlg-form').hidden = false;
    $('f-name').focus();
  }

  function submitForm() {
    if (!form) return;
    var common = {
      name: $('f-name').value.trim(),
      args: $('f-args').value.trim(),
      startWithStarter: $('f-start').checked,
      updateWithoutAsking: $('f-noask').checked,
      keepPrevious: $('f-keep').checked,
      copyToLocal: !!form.copy,
      restartOnCrash: $('f-restart').checked,
      startDelaySeconds: clampDelay($('f-delay').value),
      readyCheck: $('f-ready').value.trim(),
    };
    if (form.mode === 'add') {
      if (!found || found.duplicateOf) return;
      send(Object.assign({ cmd: 'add', path: $('f-path').value.trim() }, common));
    } else {
      if (!common.name) return;
      send(Object.assign({ cmd: 'edit', id: form.id }, common));
    }
    $('dlg-form').hidden = true;
    form = null;
  }

  // ---------- Unterfenster: Entfernen (Bestätigung) ----------

  function openRemove(p) {
    menuFor = null;
    confirmId = p.id;
    $('confirm-title').textContent = 'REMOVE ' + String(p.name || '').toUpperCase();
    $('confirm-text').textContent = p.running
      ? 'This program is running. Stop it first, then remove it.'
      : 'The entry is removed from the list and its local copies are deleted. The source folder is not touched.';
    $('confirm-ok').disabled = !!p.running || !!p.busy;
    $('dlg-confirm').hidden = false;
  }

  // ---------- Unterfenster: Fragen des Starters (Schließen, Beenden) ----------

  function openAsk(what) {
    var buttons = $('ask-buttons');
    var close = function () { $('ask').hidden = true; };
    buttons.innerHTML = '';
    $('ask-list').innerHTML = '';
    $('ask-remember').checked = false;
    function button(label, cls, fn) {
      var b = document.createElement('button');
      b.className = 'btn ' + cls;
      b.textContent = label;
      b.addEventListener('click', fn);
      buttons.appendChild(b);
    }
    if (what === 'close') {
      $('ask-title').textContent = 'CLOSE THE WINDOW';
      $('ask-text').textContent = 'Keep the Starter running in the tray, or quit it?';
      $('ask-remember-row').hidden = false;
      button('Cancel', 'quiet', close);
      button('Quit', '', function () { send({ cmd: 'closeChoice', choice: 'quit', remember: $('ask-remember').checked }); close(); });
      button('Keep in tray', 'primary', function () { send({ cmd: 'closeChoice', choice: 'tray', remember: $('ask-remember').checked }); close(); });
    } else {
      var running = programs().filter(function (p) { return p.running; });
      $('ask-title').textContent = 'QUIT THE STARTER';
      $('ask-text').textContent = running.length === 1
        ? 'This program is running. Stop it too?'
        : 'These programs are running. Stop them too?';
      $('ask-list').innerHTML = running.map(function (p) { return '<li>' + esc(p.name) + '</li>'; }).join('');
      $('ask-remember-row').hidden = true;
      button('Cancel', 'quiet', close);
      button('Quit, keep them running', '', function () { send({ cmd: 'quitWith', stop: false }); close(); });
      button('Stop them and quit', 'danger', function () { send({ cmd: 'quitWith', stop: true }); close(); });
    }
    $('ask').hidden = false;
    if (buttons.lastChild) buttons.lastChild.focus();
  }

  // ---------- Ereignisse ----------

  function onMessage(raw) {
    var m = null;
    try { m = JSON.parse(raw); } catch (e) {
      // Fenster-Chrome: einfache Zeichenketten (nicht JSON)
      if (typeof raw === 'string' && raw.indexOf('window:state ') === 0) updateWindowControls(raw.indexOf('maximized') !== -1);
      return;
    }
    if (!m || typeof m !== 'object') return;
    if (m.type === 'state') { state = m; render(); }
    else if (m.type === 'found' || m.type === 'error') onInspect(m);
    else if (m.type === 'ask') openAsk(m.what);
    else if (m.type === 'picked' && form && form.mode === 'add') { $('f-path').value = m.path; inspect(); }
  }

  // Alle Klicks mit einer Aktion (data-act, data-v, data-go, data-close)
  document.addEventListener('click', function (ev) {
    var t = ev.target.closest('[data-act],[data-v],[data-go],[data-close]');
    if (!t) {
      if (menuFor) { menuFor = null; renderPrograms(); }
      return;
    }
    if (t.hasAttribute('data-close')) {
      var sc = t.closest('.scrim');
      if (sc) sc.hidden = true;
      return;
    }
    if (t.dataset.v) { show(t.dataset.v); return; }
    if (t.dataset.go) { if (t.dataset.go === 'add') openAdd(); return; }
    ev.preventDefault();
    var act = t.dataset.act, id = t.dataset.id;
    var p = id ? findProgram(id) : null;
    switch (act) {
      case 'menu': menuFor = menuFor === id ? null : id; renderPrograms(); return;
      case 'details': open[id] = !open[id]; renderPrograms(); return;
      case 'edit': if (p) openEdit(p); return;
      case 'remove': if (p) openRemove(p); return;
      case 'folder': send({ cmd: 'openProgramFolder', id: id }); break;
      case 'log': send({ cmd: 'showLog' }); break;
      case 'rollback': send({ cmd: 'rollback', id: id }); break;
      case 'start': case 'stop': case 'restart': case 'update': send({ cmd: act, id: id }); break;
      case 'cancel': send({ cmd: 'cancel' }); break;
      default: return;
    }
    menuFor = null;
    renderPrograms();
  });

  $('btn-startall').addEventListener('click', function () { send({ cmd: 'startAll' }); });
  $('btn-stopall').addEventListener('click', function () { send({ cmd: 'stopAll' }); });
  $('btn-check').addEventListener('click', function () { send({ cmd: 'check' }); });
  $('btn-add').addEventListener('click', openAdd);
  $('startup-now').addEventListener('click', function () { send({ cmd: 'startNow' }); });
  $('startup-skip').addEventListener('click', function () { send({ cmd: 'skipStartup' }); });
  $('s-cleanup').addEventListener('click', function () { send({ cmd: 'cleanup' }); });
  $('s-open').addEventListener('click', function () { send({ cmd: 'openFolder' }); });
  ['s-interval', 's-start', 's-ask', 's-stopped', 's-kept', 's-delay', 's-close'].forEach(function (id) {
    $(id).addEventListener('change', settingsChanged);
  });

  $('f-run-copy').addEventListener('click', function () { if (!this.disabled) setRunFrom(true); });
  $('f-run-src').addEventListener('click', function () { if (!this.disabled) setRunFrom(false); });
  $('f-browse').addEventListener('click', function () { send({ cmd: 'pick' }); });
  $('f-path').addEventListener('input', function () { clearTimeout(inspectTimer); inspectTimer = setTimeout(inspect, 350); });
  $('f-args').addEventListener('input', function () { clearTimeout(inspectTimer); inspectTimer = setTimeout(inspect, 350); });
  $('f-name').addEventListener('input', updateFormButtons);
  $('f-go').addEventListener('click', submitForm);
  $('confirm-ok').addEventListener('click', function () {
    if (confirmId) send({ cmd: 'remove', id: confirmId });
    confirmId = null;
    $('dlg-confirm').hidden = true;
  });

  // Die Reihenfolge per Ziehen am Griff (⋮⋮). Ein Ziehen schickt move {id, index}.
  var list = $('apps-list');
  function rowsNow() { return Array.prototype.slice.call(list.querySelectorAll('.app[data-id]')); }
  // Der Einfügeplatz (0 bis Anzahl) über der Zeigerhöhe
  function slotAt(y) {
    var rows = rowsNow();
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i].getBoundingClientRect();
      if (y < r.top + r.height / 2) return i;
    }
    return rows.length;
  }
  function markDrop(slot) {
    var rows = rowsNow();
    rows.forEach(function (r) { r.classList.remove('drop-before', 'drop-after'); });
    if (!rows.length) return;
    if (slot < rows.length) rows[slot].classList.add('drop-before');
    else rows[rows.length - 1].classList.add('drop-after');
  }
  function endDrag() {
    dragging = false;
    dragId = null;
    gripDown = false;
    rowsNow().forEach(function (r) { r.classList.remove('dragging', 'drop-before', 'drop-after'); });
    render();
  }
  list.addEventListener('mousedown', function (ev) { gripDown = !!ev.target.closest('.grip'); });
  list.addEventListener('mouseup', function () { gripDown = false; });
  list.addEventListener('dragstart', function (ev) {
    var row = ev.target.closest('.app[data-id]');
    if (!row || !gripDown) { ev.preventDefault(); return; }
    dragId = row.dataset.id;
    dragging = true;
    ev.dataTransfer.effectAllowed = 'move';
    ev.dataTransfer.setData('text/plain', dragId);
    row.classList.add('dragging');
  });
  list.addEventListener('dragover', function (ev) {
    if (!dragging) return;
    ev.preventDefault();
    ev.dataTransfer.dropEffect = 'move';
    markDrop(slotAt(ev.clientY));
  });
  list.addEventListener('drop', function (ev) {
    if (!dragging) return;
    ev.preventDefault();
    var id = dragId, slot = slotAt(ev.clientY);
    var from = programs().map(function (p) { return p.id; }).indexOf(id);
    var index = slot > from ? slot - 1 : slot;
    endDrag();
    if (from >= 0 && index !== from) send({ cmd: 'move', id: id, index: index });
  });
  list.addEventListener('dragend', function () { if (dragging) endDrag(); });

  // Escape schließt das oberste Unterfenster (oder das ⋯-Menü)
  document.addEventListener('keydown', function (ev) {
    if (ev.key !== 'Escape') return;
    var shown = Array.prototype.filter.call(document.querySelectorAll('.scrim'), function (s) { return !s.hidden; });
    if (shown.length) { shown[shown.length - 1].hidden = true; return; }
    if (menuFor) { menuFor = null; renderPrograms(); }
  });

  // Die Anzeige hält den Countdown der Pause und des Schließens aktuell, ohne die Liste neu zu bauen
  setInterval(function () {
    if (!$('startup').hidden) drawStartup();
    document.querySelectorAll('.busy-label[data-id]').forEach(function (el) {
      var p = findProgram(el.dataset.id);
      if (p && p.busy) el.textContent = busyText(p, false);
    });
    document.querySelectorAll('.busy-chip[data-id]').forEach(function (el) {
      var p = findProgram(el.dataset.id);
      if (p && p.busy) el.textContent = busyText(p, true);
    });
  }, 250);

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

  show('apps');
  // Die Seite meldet sich, sobald ihr Skript läuft. Erst danach schickt der Starter etwas.
  try {
    window.external.receiveMessage(onMessage);
    window.external.sendMessage('shell.ready');
  } catch (e) { }
})();
