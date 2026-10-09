'use strict';
// Chatterbox frontend. Outbound {action,...} via window.external.sendMessage, inbound
// {type,payload} via window.external.receiveMessage. All state lives in the
// backend; this file renders payloads and sends actions.

const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const DEMO = location.search.includes('demo');

function send(obj) {
  if (DEMO) return;
  try { window.external.sendMessage(JSON.stringify(obj)); } catch (e) { /* host not ready */ }
}

// ── view switching ─────────────────────────────────────────────
let firstRunNeeded = false;
function currentView() { return document.querySelector('.rail button.on')?.dataset.view ?? 'captions'; }
function showView(v) {
  document.querySelectorAll('.rail button').forEach(b => {
    const on = b.dataset.view === v;
    b.classList.toggle('on', on);
    if (on) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
  const effective = (v === 'captions' && firstRunNeeded) ? 'firstrun' : v;
  document.querySelectorAll('.view').forEach(el =>
    el.classList.toggle('on', el.id === 'view-' + effective));
}
document.querySelectorAll('.rail button').forEach(b =>
  b.addEventListener('click', () => showView(b.dataset.view)));

// ── captions stage ─────────────────────────────────────────────
let running = false;
function setSpeech(active) {
  $('pulse').hidden = !active || !running;
  $('listenLabel').textContent = running ? (active ? 'Listening' : 'Waiting for speech') : '';
}
function onPartial(committed, pending) {
  $('stageIdle').hidden = true;
  $('capCurrent').hidden = false;
  $('capCommitted').textContent = committed;
  $('capPending').textContent = pending ? ' ' + pending : '';
}
function onTranslated(p) {
  $('capTranslated').textContent = p.translated;
  $('capTranslated').hidden = false;
  $('transPreview').innerHTML = `<div class="orig">${esc(p.original)}</div><div class="tr">${esc(p.translated)}</div>`;
}
function foldCaption() {
  const cur = $('capCommitted').textContent;
  if (!cur) return;
  $('capOlder').textContent = cur;
  $('capOlder').hidden = false;
  $('capCommitted').textContent = '';
  $('capPending').textContent = '';
  $('capCurrent').hidden = true;
}
function resetStage() {
  $('capOlder').hidden = true;
  $('capCurrent').hidden = true;
  $('capCommitted').textContent = '';
  $('capPending').textContent = '';
  $('capTranslated').textContent = '';
  $('capTranslated').hidden = true;
  $('gameChip').hidden = true;
  $('stageIdle').hidden = false;
  $('meterBar').style.width = '0%';
}

// ── status / start-stop ────────────────────────────────────────
let loading = false; // an engine load in progress (Start pressed, model not up yet)
function setState(p) {
  running = !!p.running;
  loading = !!p.loading && !running;
  const chipDot = $('statusChip').querySelector('.dot');
  if (running) {
    chipDot.className = 'dot';
    $('statusText').textContent = p.engineName || 'running';
  } else if (loading) {
    chipDot.className = 'dot busy';
    $('statusText').textContent = 'Loading ' + (p.engineName || 'model') + '…';
  } else {
    chipDot.className = p.error ? 'dot err' : 'dot off';
    $('statusText').textContent = p.error ? 'error' : 'idle';
    setSpeech(false);
    $('meterBar').style.width = '0%';
    $('gameChip').hidden = true; // stale in-game count must not survive a stop
  }
  setPace(null); // a fresh session reports its own pace
  const btn = $('btnStartStop');
  btn.disabled = firstRunNeeded || !dev || loading; // stays off until the device list arrives
  btn.classList.toggle('stop', running);
  $('icoPlay').classList.toggle('hide', running);
  $('icoStop').classList.toggle('hide', !running);
  $('btnStartStopText').textContent = running ? 'Stop captions' : loading ? 'Loading model…' : 'Start captions';
  setSpeech(false);
}

// ── pace: is recognition keeping up with speech? ───────────────
// Green while passes finish inside the audio they cover; amber when it's
// close; red with the numbers once captions are actually late. The
// backend follows a sustained red with a toast that names the fix.
function setPace(p) {
  const chip = $('paceChip');
  if (!p || !running || p.status === 'Unknown') { chip.hidden = true; return; }
  const behind = p.status === 'Behind', strained = p.status === 'Strained';
  chip.hidden = false;
  chip.className = 'pacechip' + (behind ? ' behind' : strained ? ' strained' : '');
  chip.querySelector('.dot').className = 'dot' + (behind ? ' err' : strained ? ' busy' : '');
  $('paceText').textContent = behind
    ? `falling behind ${(p.load ?? 0).toFixed(1)}× · ${((p.lagMs ?? 0) / 1000).toFixed(1)} s late`
    : strained ? 'barely keeping up' : 'keeping up';
  chip.title = `last recognition pass: ${p.passMs ?? 0} ms over ${((p.windowMs ?? 0) / 1000).toFixed(1)} s of audio`;
}
$('btnStartStop').addEventListener('click', () => {
  if (running) { send({ action: 'sttStop' }); }
  else { resetStage(); send({ action: 'sttStart', deviceIndex: parseInt($('selDevice').value ?? '0', 10) }); }
});

// ── devices / settings payload ─────────────────────────────────
let dev = null; // last sttDevices payload
function renderDevices(p) {
  dev = p;
  // The voice detector is not a first-run matter: the backend fetches it on
  // its own when it is missing (at boot for a returning user, or at Start).
  firstRunNeeded = !p.parakeetAvailable && !p.whisperAvailable;
  if (currentView() === 'captions') showView('captions'); // re-resolve first-run swap
  $('btnStartStop').disabled = firstRunNeeded || loading;

  const sel = $('selDevice');
  sel.innerHTML = (p.devices || []).map((d, i) =>
    `<option value="${i}" ${i === p.savedIndex ? 'selected' : ''}>${esc(d)}</option>`).join('');
  const rateSel = $('selRate');
  if (![...rateSel.options].some(o => parseInt(o.value, 10) === p.intervalMs))
    rateSel.add(new Option((p.intervalMs / 1000).toFixed(1).replace(/\.0$/, '') + ' s', p.intervalMs));
  rateSel.value = String(p.intervalMs);

  // settings screen
  document.querySelectorAll('#segEngine button').forEach(b =>
    b.classList.toggle('on', b.dataset.v === p.engine));
  document.querySelectorAll('#segRate button').forEach(b =>
    b.classList.toggle('on', parseInt(b.dataset.v, 10) === p.intervalMs));
  document.querySelectorAll('#segNewline button').forEach(b =>
    b.classList.toggle('on', parseInt(b.dataset.v, 10) === p.newLineGapMs));
  document.querySelectorAll('#segClear button').forEach(b =>
    b.classList.toggle('on', parseInt(b.dataset.v, 10) === p.clearGapMs));
  setToggle($('tglTyping'), p.typingIndicator);
  setToggle($('tglNames'), p.nameBoost);
  setToggle($('tglTranslate'), p.translateEnabled);
  setToggle($('tglTransOrig'), p.translateShowOriginal);
  $('selTransLang').innerHTML = (p.translateLanguages || []).map(l =>
    `<option value="${esc(l.code)}" ${l.code === p.translateTarget ? 'selected' : ''}>${esc(l.name)}</option>`).join('');
  $('transStatus').textContent = p.translateReady
    ? (p.translateGpu ? 'Translation model and engine installed — runs on your GPU (Vulkan).' : 'Translation model and engine installed — runs on your CPU.')
    : 'Needs the translation model and the engine pack from the Models screen.';
  $('transDot').className = 'dot ' + (p.translateReady ? 'ok' : 'warn');
  $('btnTransModels').hidden = !!p.translateReady;
  const wm = $('selWhisperModel');
  wm.innerHTML = `<option value="">Auto${p.whisperModelName ? ` (${esc(p.whisperModelName)})` : ''}</option>` +
    (p.whisperModels || []).map(m =>
      `<option value="${esc(m)}" ${m === p.whisperModelSetting ? 'selected' : ''}>${esc(m)}</option>`).join('');

  // players screen: watch list + auto toggle
  setToggle($('tglAuto'), p.autoStartEnabled);
  const rows = (p.autoStartFriends || []).map(f => `
    <div class="row">
      <span class="grow"><span class="name">${esc(f.name || f.id)}</span>&nbsp;
        <span class="uid">${f.id ? esc(shortUid(f.id)) : 'name only — id fills in when seen'}</span></span>
      <button class="x" data-uw-id="${esc(f.id || '')}" data-uw-name="${esc(f.name || '')}" aria-label="Remove ${esc(f.name || f.id)} from auto-start">✕</button>
    </div>`).join('');
  $('watchRows').innerHTML = rows || '<div class="empty">No auto-start players yet — add one below, or press Auto-Start on someone in the list underneath.</div>';
  $('watchRows').querySelectorAll('[data-uw-name],[data-uw-id]').forEach(b =>
    b.addEventListener('click', () => unwatch(b.dataset.uwId, b.dataset.uwName)));
  renderSeen(lastPlayers); // watched-state of Watch buttons may have changed
}
const shortUid = id => id.length > 14 ? id.slice(0, 9) + '…' + id.slice(-4) : id;

function setToggle(el, on) { el.classList.toggle('on', !!on); el.setAttribute('aria-checked', String(!!on)); }
function toggleHandler(el, fn) {
  el.addEventListener('click', fn);
  el.addEventListener('keydown', e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); fn(); } });
}

// ── watch list actions ─────────────────────────────────────────
// Optimistic: the local mirror is updated before sending, so rapid
// consecutive interactions build on the pending state instead of a stale
// payload snapshot (the backend echo then confirms).
function sendWatchList(list, enabled) {
  if (dev) { dev.autoStartFriends = list; dev.autoStartEnabled = enabled; }
  send({ action: 'sttAutoSet', enabled, friends: list.map(f => ({ Id: f.id || '', Name: f.name || '' })) });
}
function watchAdd(text) {
  const t = text.trim();
  if (!t || !dev) return;
  const list = (dev.autoStartFriends || []).slice();
  const entry = t.startsWith('usr_') ? { id: t, name: '' } : { id: '', name: t };
  if (list.some(f => (entry.id && f.id === entry.id) || (entry.name && f.name.toLowerCase() === entry.name.toLowerCase()))) return;
  list.push(entry);
  sendWatchList(list, dev.autoStartEnabled);
  $('inpWatch').value = '';
}
// Removal keys on entry identity, not row index — indexes go stale between
// the render and the backend echo.
function unwatch(id, name) {
  if (!dev) return;
  const list = (dev.autoStartFriends || []).filter(f => !(f.id === id && f.name === name));
  sendWatchList(list, dev.autoStartEnabled);
}
$('btnWatch').addEventListener('click', () => watchAdd($('inpWatch').value));
$('inpWatch').addEventListener('keydown', e => { if (e.key === 'Enter') watchAdd($('inpWatch').value); });
toggleHandler($('tglAuto'), () => { if (dev) sendWatchList(dev.autoStartFriends || [], !dev.autoStartEnabled); });

// ── players seen ───────────────────────────────────────────────
let lastPlayers = null;
function isWatched(p) {
  if (!dev) return false;
  return (dev.autoStartFriends || []).some(f =>
    (f.id && p.id && f.id === p.id) || (f.name && p.name && f.name.toLowerCase() === p.name.toLowerCase()));
}
// Why nobody is listed: VRChat closed, VRChat running with its own logging
// switched off (the cause a user can fix), a log folder Chatterbox never
// found inside the Proton prefix, or no world joined yet.
const LOGGING_FIX = 'In VRChat, open <b>Settings → Debug</b> and turn <b>Logging</b> on, then rejoin your world. If Logging is already on, restart VRChat.';
const FOLDER_FIX = '<b>last_boot.log</b> names the folder Chatterbox watches. If VRChat’s Proton prefix is somewhere else, start Chatterbox with <b>--vrchat-log-dir &lt;folder&gt;</b>.';
function seenStatus(pl, n) {
  const idle = 'Nobody detected yet. This fills in from VRChat’s log while you’re in a world.';
  if (pl.world || n > 0)
    return { head: `In your instance — ${n} player${n === 1 ? '' : 's'}${pl.worldName ? ` · ${pl.worldName}` : ''}`, note: idle };
  if (pl.game === false)
    return { head: 'In your instance — VRChat is not running', note: idle };
  if (pl.log === 'empty')
    return { warn: true, head: 'In your instance — VRChat’s log is empty',
             note: `VRChat is running but isn’t writing its log, and that log is how Chatterbox sees your world and who’s in it. ${LOGGING_FIX}` };
  if (pl.log === 'missing')
    return { warn: true, head: 'In your instance — VRChat has no log file',
             note: `VRChat is running, but its log folder holds no log file, and that log is how Chatterbox sees your world and who’s in it. ${LOGGING_FIX} Still nothing? VRChat may run from another Steam library: ${FOLDER_FIX}` };
  if (pl.log === 'nofolder')
    return { warn: true, head: 'In your instance — VRChat’s log folder wasn’t found',
             note: `VRChat is running, but Chatterbox can’t find its log folder in any Steam library, and that log is how Chatterbox sees your world and who’s in it. ${FOLDER_FIX}` };
  return { head: 'In your instance — waiting for a world',
           note: 'Nobody detected yet. This fills in when you join a world. Already in one? Rejoin it. Still nothing? Check that <b>Logging</b> is on in VRChat’s <b>Settings → Debug</b>.' };
}
function renderSeen(pl) {
  lastPlayers = pl;
  if (!pl) return;
  const n = (pl.players || []).length;
  const seen = seenStatus(pl, n);
  $('seenHead').textContent = seen.head;
  $('seenRows').innerHTML = n === 0
    ? `<div class="empty${seen.warn ? ' warn' : ''}">${seen.note}</div>`
    : pl.players.map(p => `
      <div class="row">
        <span class="grow"><span class="name">${esc(p.name)}</span>&nbsp;<span class="uid">${esc(shortUid(p.id || ''))}</span></span>
        ${isWatched(p) ? '<span class="badge installed">Auto-Start</span>'
                       : `<button class="chipbtn" data-w-id="${esc(p.id || '')}" data-w-name="${esc(p.name)}">Auto-Start</button>`}
      </div>`).join('');
  $('seenRows').querySelectorAll('[data-w-name]').forEach(b =>
    b.addEventListener('click', () => {
      if (!dev) return;
      const list = (dev.autoStartFriends || []).slice();
      list.push({ id: b.dataset.wId, name: b.dataset.wName });
      sendWatchList(list, dev.autoStartEnabled);
    }));
}

// ── models ─────────────────────────────────────────────────────
let mods = null;
let firstRunQueue = [];
let firstRunKicked = null;
let firstRunKickSeen = false; // a payload has shown the kicked id downloading
function resetFirstRunCards() {
  firstRunKicked = null;
  firstRunKickSeen = false;
  $('chQuick').disabled = $('chBest').disabled = false;
  $('frProgress').hidden = true;
}
const MB = b => b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.round(b / 1e6) + ' MB';
function renderModels(p) {
  mods = p;
  $('hwText').textContent = `Your hardware: ${p.tierLabel} — recommended: ${p.recommendedId}` + (p.tierNote ? ` — ${p.tierNote}` : '');
  updateFirstRunCards();

  // first-run queue: when nothing is downloading, kick the next item.
  // A re-seen kicked id only means failure once a payload has actually
  // shown it downloading (kickSeen) — the backend sends several payloads
  // with downloading:'' before a kick lands, and treating those as
  // failures aborted the whole queue on every click.
  if (firstRunQueue.length) {
    if (firstRunKicked && p.downloading === firstRunKicked) firstRunKickSeen = true;
    if (!p.downloading) {
      const next = firstRunQueue.find(id => !p.models.find(m => m.id === id)?.installed);
      if (!next) { firstRunQueue = []; resetFirstRunCards(); }        // all installed
      else if (next !== firstRunKicked) {
        firstRunKicked = next; firstRunKickSeen = false;
        send({ action: 'sttDownloadModel', id: next });
      }
      else if (firstRunKickSeen) { firstRunQueue = []; resetFirstRunCards(); } // ran and failed
      // else: kicked, backend hasn't picked it up yet — wait
    }
  }

  const rowFor = m => {
    const badges = [
      m.installed ? '<span class="badge installed">Installed</span>' : '',
      m.active ? '<span class="badge active">Active</span>' : '',
      m.id === p.recommendedId ? '<span class="badge reco">Recommended</span>' : '',
      m.isVad ? '<span class="badge required">Required</span>' : '',
    ].join(' ');
    const downloading = p.downloading === m.id;
    const btn = downloading
      ? `<button class="chipbtn" data-cancel="1">Cancel</button>`
      : m.installed
        ? (m.isVad ? '' : `<button class="chipbtn danger" data-del="${m.id}">Delete</button>`)
        : `<button class="chipbtn amber" data-dl="${m.id}">Download</button>`;
    return `
      <div class="row" data-model="${m.id}">
        <span class="grow">
          <span class="name">${esc(m.displayName)}</span> ${badges}
          <span class="meta">${MB(m.sizeBytes)} · ${esc(m.license)} · ${esc(m.attribution)}</span>
          ${downloading ? `<div class="progress"><i data-bar="${m.id}" style="width:0%"></i></div><span class="meta" data-plabel="${m.id}">starting…</span>` : ''}
        </span>${btn}
      </div>`;
  };

  const isComponent = m => m.isVad || m.kind === 'translation' || m.id === 'cuda-gpu-pack' ||
    m.id === 'translate-engine' || m.id === 'translate-gpu-pack';
  $('modelRows').innerHTML = p.models.filter(m => !isComponent(m)).map(rowFor).join('');
  $('componentRows').innerHTML = p.models.filter(isComponent).map(rowFor).join('');
  document.querySelectorAll('[data-dl]').forEach(b =>
    b.addEventListener('click', () => send({ action: 'sttDownloadModel', id: b.dataset.dl })));
  document.querySelectorAll('[data-del]').forEach(b =>
    b.addEventListener('click', () => send({ action: 'sttDeleteModel', id: b.dataset.del })));
  document.querySelectorAll('[data-cancel]').forEach(b =>
    b.addEventListener('click', () => { firstRunQueue = []; resetFirstRunCards(); send({ action: 'sttCancelDownload' }); }));
}
function onProgress(p) {
  const pct = p.total ? Math.round(p.received / p.total * 100) : 0;
  const bar = document.querySelector(`[data-bar="${p.id}"]`);
  if (bar) bar.style.width = pct + '%';
  const label = document.querySelector(`[data-plabel="${p.id}"]`);
  if (label) label.textContent = `${MB(p.received)} of ${MB(p.total)} (${pct}%)`;
  if (firstRunQueue.length) {
    $('frProgress').hidden = false;
    $('frBar').style.width = pct + '%';
    const m = mods?.models.find(x => x.id === p.id);
    $('frLabel').textContent = `Downloading ${m ? m.displayName : p.id} — ${pct}%`;
  }
}
$('btnVerify').addEventListener('click', () => send({ action: 'sttVerifyModels' }));
$('btnDefaults').addEventListener('click', () => send({ action: 'sttRecommendedDefaults' }));

// ── speed check ────────────────────────────────────────────────
$('btnBench').addEventListener('click', () => send({ action: 'sttRunBench' }));
$('btnInstall').addEventListener('click', () => send({ action: 'sttInstallDesktop' }));
// Settings > Desktop: whether the app-grid entry exists, from the docs
// payload at load and again after an install.
function renderInstall(p) {
  if (p.installedAt === undefined) return;
  $('installLine').textContent = p.installedAt ? 'Installed at ' + p.installedAt : 'Not in your app grid yet.';
  $('btnInstall').textContent = p.installedAt ? 'Refresh app-grid entry' : 'Add to app grid';
}
function renderBench(p) {
  const btn = $('btnBench'), out = $('benchOut');
  if (p.running) {
    btn.disabled = true;
    btn.textContent = 'Running… ' + (p.note || '');
    return;
  }
  btn.disabled = false;
  btn.textContent = 'Run speed check';
  out.hidden = false;
  if (p.error) { out.textContent = 'Speed check failed: ' + p.error; return; }
  const cls = v => 'v-' + String(v || '').replace(/[^a-z]+/gi, '-');
  const rows = (p.rows || []).map(r =>
    `<tr><td>${esc(r.engine)}</td><td>${r.loadMs} ms</td><td>${r.pass6sMs} ms</td><td>${r.passFullMs} ms</td>` +
    `<td>${r.accuracyPct}%</td><td class="${cls(r.verdict)}">${esc(r.verdict)}</td></tr>`).join('');
  out.innerHTML =
    `<table class="benchtable"><thead><tr><th>Engine</th><th>Load</th><th>6 s pass</th><th>Full clip</th><th>Words right</th><th>Verdict</th></tr></thead>` +
    `<tbody>${rows}</tbody></table><div class="benchsum">${esc(p.summary || '')}</div>`;
}

// ── updates (Settings → Updates; the backend talks to GitHub) ──
let upd = null; // last sttUpdate payload
function renderUpdate(p) {
  upd = p;
  $('updCurrent').textContent = p.current ? 'v' + p.current : '';
  setToggle($('tglUpdAuto'), p.checkAtStartup);
  const desc = $('updDesc'), check = $('btnUpdCheck'), inst = $('btnUpdInstall'), prog = $('updProg'), notes = $('updNotes');
  check.disabled = false; check.textContent = 'Check for updates';
  inst.hidden = true; inst.disabled = false; prog.hidden = true; notes.hidden = true;
  const pct = p.total ? Math.round(p.received / p.total * 100) : 0;
  switch (p.state) {
    case 'unconfigured':
      desc.textContent = 'This build has no update source configured, so it never contacts GitHub.';
      check.disabled = true; break;
    case 'checking':
      desc.textContent = 'Checking GitHub…'; check.disabled = true; break;
    case 'upToDate':
      desc.textContent = `You have the latest version${p.latest ? ` (${p.latest})` : ''}.`; break;
    case 'available':
      desc.textContent = `Version ${p.latest} is available — ${MB(p.size)} download. Installing replaces the running file and restarts Chatterbox; captions must be stopped.`;
      if (p.notes) { notes.textContent = p.notes; notes.hidden = false; }
      inst.hidden = false; check.textContent = 'Check again'; break;
    case 'downloading':
      desc.textContent = `Downloading ${p.latest}… ${MB(p.received || 0)} of ${MB(p.total || p.size || 0)} (${pct}%)`;
      prog.hidden = false; $('updBar').style.width = pct + '%';
      inst.hidden = false; inst.disabled = true; check.disabled = true; break;
    case 'installing':
      desc.textContent = `Installing ${p.latest} — Chatterbox restarts in a moment…`;
      inst.hidden = false; inst.disabled = true; check.disabled = true; break;
    case 'error':
      desc.textContent = p.error || 'Update failed'; break;
    default:
      desc.textContent = 'Looks up the latest release on GitHub. One small request; nothing about you is sent.';
  }
}
$('btnUpdCheck').addEventListener('click', () => send({ action: 'sttCheckUpdate' }));
$('btnUpdInstall').addEventListener('click', () => send({ action: 'sttInstallUpdate' }));
toggleHandler($('tglUpdAuto'), () => {
  if (!upd) return;
  upd.checkAtStartup = !upd.checkAtStartup; // optimistic
  setToggle($('tglUpdAuto'), upd.checkAtStartup);
  send({ action: 'sttUpdateConfig', checkAtStartup: upd.checkAtStartup });
});

// ── about / license docs (embedded in the exe, fetched once) ───
function renderDocs(p) {
  $('appVer').textContent = p.version ? 'v' + p.version : '';
  $('machineLine').textContent = p.machine ? 'This machine: ' + p.machine : '';
  $('docReadme').textContent = p.readme || '';
  $('docLicense').textContent = p.license || '';
  $('docNotice').textContent = p.notice || '';
  renderInstall(p);
}
[['btnDocReadme', 'docReadme'], ['btnDocLicense', 'docLicense'], ['btnDocNotice', 'docNotice']].forEach(([b, d]) =>
  $(b).addEventListener('click', () => {
    const open = $(d).classList.toggle('open');
    $(b).textContent = open ? 'Hide' : 'View';
  }));

// first-run choices — GPU machines get whisper turbo + the CUDA pack as
// "Best quality" (it beats Parakeet once accelerated); CPU machines get
// Parakeet. Each path also sets the matching engine, or the downloaded
// model wouldn't be the one that starts.
const isGpuTier = () => mods?.tier === 'Gpu';
function updateFirstRunCards() {
  if (isGpuTier()) {
    $('chBestDesc').textContent = 'Whisper large-v3-turbo + GPU acceleration for your NVIDIA card — the most accurate live setup.';
    $('chBestSize').textContent = '~1.2 GB download (model, GPU build and its CUDA runtime) · full speed after one restart';
  } else {
    $('chBestDesc').textContent = 'NVIDIA Parakeet — leads the open accuracy leaderboard, runs fast on your CPU.';
    $('chBestSize').textContent = '~670 MB download';
  }
}
$('chQuick').addEventListener('click', () => {
  send({ action: 'sttConfig', engine: 'whisper', whisperModel: '' });
  startFirstRun(['vad', 'tiny.en-q5']);
});
$('chBest').addEventListener('click', () => {
  const gpu = isGpuTier();
  send({ action: 'sttConfig', engine: gpu ? 'whisper' : 'parakeet', whisperModel: '' });
  startFirstRun(gpu ? ['vad', 'large-v3-turbo-q5', 'cuda-gpu-pack'] : ['vad', 'parakeet-engine', 'parakeet-tdt-0.6b-v2']);
});
function startFirstRun(ids) {
  firstRunQueue = ids;
  $('chQuick').disabled = $('chBest').disabled = true;
  $('frProgress').hidden = false;
  $('frLabel').textContent = 'Starting download…';
  send({ action: 'sttGetModels' }); // triggers the queue via renderModels
}

// ── settings actions ───────────────────────────────────────────
document.querySelectorAll('#segEngine button').forEach(b =>
  b.addEventListener('click', () => send({ action: 'sttConfig', engine: b.dataset.v })));
document.querySelectorAll('#segRate button').forEach(b =>
  b.addEventListener('click', () => send({ action: 'sttConfig', intervalMs: parseInt(b.dataset.v, 10) })));
document.querySelectorAll('#segNewline button').forEach(b =>
  b.addEventListener('click', () => send({ action: 'sttConfig', newLineGapMs: parseInt(b.dataset.v, 10) })));
document.querySelectorAll('#segClear button').forEach(b =>
  b.addEventListener('click', () => send({ action: 'sttConfig', clearGapMs: parseInt(b.dataset.v, 10) })));
toggleHandler($('tglTyping'), () => {
  if (!dev) return;
  dev.typingIndicator = !dev.typingIndicator; // optimistic — see sendWatchList
  send({ action: 'sttConfig', typingIndicator: dev.typingIndicator });
});
toggleHandler($('tglNames'), () => {
  if (!dev) return;
  dev.nameBoost = !dev.nameBoost; // optimistic
  send({ action: 'sttConfig', nameBoost: dev.nameBoost });
});
toggleHandler($('tglTranslate'), () => {
  if (!dev) return;
  dev.translateEnabled = !dev.translateEnabled; // optimistic
  send({ action: 'sttConfig', translateEnabled: dev.translateEnabled });
});
toggleHandler($('tglTransOrig'), () => {
  if (!dev) return;
  dev.translateShowOriginal = !dev.translateShowOriginal; // optimistic
  send({ action: 'sttConfig', translateShowOriginal: dev.translateShowOriginal });
});
$('selTransLang').addEventListener('change', () =>
  send({ action: 'sttConfig', translateTarget: $('selTransLang').value }));
$('btnTransModels').addEventListener('click', () => showView('models'));
$('selWhisperModel').addEventListener('change', () =>
  send({ action: 'sttConfig', whisperModel: $('selWhisperModel').value }));
$('selDevice').addEventListener('change', () =>
  send({ action: 'sttSetInputDevice', deviceIndex: parseInt($('selDevice').value, 10) }));
$('selRate').addEventListener('change', () =>
  send({ action: 'sttConfig', intervalMs: parseInt($('selRate').value, 10) }));

// ── toasts ─────────────────────────────────────────────────────
// Screen readers get announcements via the container's aria-live; error
// toasts additionally use role=alert and stay until dismissed — a 5 s
// flash of the only error text would be useless.
// An optional action {label, send} posts a message to the backend, or
// {label, view} opens a section — the one-click fix on advice toasts.
function toast(ok, msg, action) {
  const el = document.createElement('div');
  el.className = 'toast' + (ok ? '' : ' err');
  if (!ok) el.setAttribute('role', 'alert');
  el.innerHTML = `<span class="dot ${ok ? '' : 'err'}"></span><span class="msg">${esc(msg)}</span>`;
  if (action && action.label) {
    const b = document.createElement('button');
    b.className = 'chipbtn amber act';
    b.textContent = action.label;
    b.addEventListener('click', ev => {
      ev.stopPropagation();
      if (action.send) send(action.send);
      if (action.view) showView(action.view);
      if (action.scrollTo) document.getElementById(action.scrollTo)?.scrollIntoView({ block: 'start' });
      el.remove();
    });
    el.appendChild(b);
  }
  el.addEventListener('click', () => el.remove());
  // An evening of auto-start cycles must not stack the same advice: a
  // repeated message replaces its older copy, and at most four stay.
  const box = $('toasts');
  [...box.children].filter(t => t.querySelector('.msg')?.textContent === msg).forEach(t => t.remove());
  while (box.children.length >= 4) box.firstElementChild.remove();
  box.appendChild(el);
  if (ok) setTimeout(() => el.remove(), 5000);
}

// ── inbound dispatch ───────────────────────────────────────────
function onMessage(raw) {
  let m; try { m = JSON.parse(raw); } catch { return; }
  const p = m.payload || {};
  switch (m.type) {
    case 'sttState': setState(p); break;
    case 'sttDevices': stateReceived = true; renderDevices(p); break;
    case 'sttModels': renderModels(p); break;
    case 'sttPlayers': renderSeen(p); break;
    case 'sttPartial': onPartial(p.committed, p.pending); break;
    case 'sttTranslated': onTranslated(p); break;
    case 'sttSpeech': setSpeech(p.active); if (!p.active) foldCaption(); break;
    case 'sttMeter': $('meterBar').style.width = Math.round((p.level ?? 0) * 100) + '%'; break;
    case 'sttSent': $('gameChip').hidden = false; $('gameChip').textContent = `in-game window ${(p.text ?? '').length}/144`; break;
    case 'sttModelProgress': onProgress(p); break;
    case 'sttDocs': renderDocs(p); break;
    case 'sttInstall': renderInstall(p); break;
    case 'sttPace': setPace(p); break;
    case 'sttBench': renderBench(p); break;
    case 'sttUpdate': renderUpdate(p); break;
    case 'toast': toast(!!p.ok, p.msg, p.action); break;
    // 'log' intentionally ignored in the UI
  }
}
// ── host bridge + boot handshake ───────────────────────────────
// Both were one-shot once: if the host bridge was a beat late (a heavy
// simultaneous VRChat launch), the state request vanished and the UI
// stayed blank — no mic list, no auto-start players — with nothing
// logged. Now the hook and the request retry until the state arrives.
let bridgeHooked = false;
let stateReceived = false;
function hookBridge() {
  if (bridgeHooked || DEMO) return true;
  try {
    if (window.external && typeof window.external.receiveMessage === 'function') {
      window.external.receiveMessage(onMessage);
      bridgeHooked = true;
    }
  } catch (e) { /* not ready yet */ }
  return bridgeHooked;
}
function requestState(attempt) {
  if (DEMO || stateReceived) return;
  hookBridge();
  send({ action: 'sttGetState' });
  // Heartbeat: a web process that crashed stops sending these, and the
  // host reloads the page (Program.cs) instead of leaving a dead window
  // over running captions.
  setInterval(() => send({ action: 'sttPing' }), 5000);
  send({ action: 'sttGetDocs' });
  if (attempt < 40) setTimeout(() => requestState(attempt + 1), attempt < 10 ? 300 : 1000);
}

// ── boot ───────────────────────────────────────────────────────
hookBridge();
requestState(0);
resetStage();
setState({ running: false });

// ── demo mode for design preview (?demo) ───────────────────────
if (DEMO) {
  firstRunNeeded = false;
  onMessage(JSON.stringify({ type: 'sttDevices', payload: {
    devices: ['Headset Microphone (Index)', 'USB Desk Mic'], savedIndex: 0, engine: 'whisper',
    intervalMs: 1000, typingIndicator: true, autoStartEnabled: true,
    autoStartFriends: [{ id: 'usr_2fa4aaaa-1111-2222-3333-4444555591c3', name: 'Nova_Signs' }, { id: '', name: 'Moth_man42' }],
    whisperAvailable: true, whisperModelName: 'ggml-large-v3-turbo.bin', whisperModels: ['ggml-large-v3-turbo.bin'],
    whisperModelSetting: '', vadAvailable: true, parakeetAvailable: true, modelDir: '',
    translateEnabled: true, translateTarget: 'ja', translateShowOriginal: false, translateReady: true, translateGpu: true,
    translateLanguages: [{ code: 'ja', name: 'Japanese' }, { code: 'ko', name: 'Korean' }, { code: 'es', name: 'Spanish' }, { code: 'de', name: 'German' }] } }));
  onMessage(JSON.stringify({ type: 'sttPlayers', payload: { game: true, log: 'writing', world: 'wrld_demo', worldName: 'The Black Cat', players: [
    { id: 'usr_c48faaaa-0000-0000-0000-00000000a0c8', name: 'PixelFerret' },
    { id: 'usr_2fa4aaaa-1111-2222-3333-4444555591c3', name: 'Nova_Signs' },
    { id: '', name: 'geosk' }] } }));
  onMessage(JSON.stringify({ type: 'sttModels', payload: { tier: 'Gpu', tierLabel: 'GPU (NVIDIA)', recommendedId: 'large-v3-turbo-q5', downloading: 'large-v3-turbo-q5', models: [
    { id: 'parakeet-tdt-0.6b-v2', displayName: 'NVIDIA Parakeet TDT 0.6B v2 (int8)', sizeBytes: 661e6, license: 'CC-BY-4.0', attribution: 'NVIDIA / sherpa-onnx', installed: true, active: false, isVad: false },
    { id: 'large-v3-turbo', displayName: 'Whisper large-v3-turbo', sizeBytes: 1.62e9, license: 'MIT', attribution: 'OpenAI / whisper.cpp', installed: true, active: true, isVad: false },
    { id: 'large-v3-turbo-q5', displayName: 'Whisper large-v3-turbo (q5)', sizeBytes: 574e6, license: 'MIT', attribution: 'OpenAI / whisper.cpp', installed: false, active: false, isVad: false },
    { id: 'tiny.en-q5', displayName: 'Whisper tiny.en (q5)', sizeBytes: 32e6, license: 'MIT', attribution: 'OpenAI / whisper.cpp', installed: false, active: false, isVad: false },
    { id: 'vad', displayName: 'Silero VAD v6.2.0', sizeBytes: 885098, license: 'MIT', attribution: 'snakers4 / ggml-org', installed: true, active: false, isVad: true },
    { id: 'hy-mt2-1.8b', displayName: 'Hy-MT2 1.8B translation model (Q4)', sizeBytes: 1133080448, license: 'Apache-2.0', attribution: 'Tencent Hunyuan Hy-MT2 1.8B (Apache-2.0), GGUF by Tencent', installed: true, active: false, isVad: false, kind: 'translation' },
    { id: 'cuda-gpu-pack', displayName: 'GPU acceleration for Whisper (CUDA)', sizeBytes: 142586522, license: 'MIT', attribution: 'whisper.cpp CUDA build, packaged by Whisper.net', installed: false, active: false, isVad: false },
    { id: 'translate-engine', displayName: 'Translation engine (llama.cpp, CPU)', sizeBytes: 36337071, license: 'MIT', attribution: 'llama.cpp (MIT, ggml-org), packaged by LLamaSharp (MIT)', installed: true, active: false, isVad: false, kind: 'pack' },
    { id: 'translate-gpu-pack', displayName: 'GPU acceleration for translation (Vulkan)', sizeBytes: 20194168, license: 'MIT', attribution: 'llama.cpp Vulkan build (MIT, ggml-org), packaged by LLamaSharp (MIT)', installed: true, active: false, isVad: false, kind: 'pack' }] } }));
  onMessage(JSON.stringify({ type: 'sttState', payload: { running: true, engineName: 'Whisper.net (ggml-large-v3-turbo, Cuda)' } }));
  onMessage(JSON.stringify({ type: 'sttPartial', payload: { committed: 'They said the new area opens on the left', pending: 'past the fountain' } }));
  onMessage(JSON.stringify({ type: 'sttSpeech', payload: { active: true } }));
  onMessage(JSON.stringify({ type: 'sttMeter', payload: { level: 0.52 } }));
  onMessage(JSON.stringify({ type: 'sttSent', payload: { text: 'They said the new area opens on the left' } }));
  onMessage(JSON.stringify({ type: 'sttModelProgress', payload: { id: 'large-v3-turbo-q5', received: 368e6, total: 574e6 } }));
  $('capOlder').textContent = 'Sure, we can head over there in a minute.';
  $('capOlder').hidden = false;
  onMessage(JSON.stringify({ type: 'sttTranslated', payload: { original: 'Sure, we can head over there in a minute.', translated: 'いいよ、少ししたらそっちに向かおう。' } }));
  onMessage(JSON.stringify({ type: 'sttUpdate', payload: { state: 'available', current: '1.6.0', configured: true, checkAtStartup: true,
    latest: '1.6.1', title: 'Chatterbox 1.6.1 for Linux', pageUrl: '', size: 39.7e6, received: 0, total: 0,
    notes: 'Live local speech-to-text captions for the VRChat chatbox. Linux x64, one self-contained file.\nSHA-256: 0000000000000000000000000000000000000000000000000000000000000000' } }));
  onMessage(JSON.stringify({ type: 'sttDocs', payload: { version: '1.7.1',
    readme: '# Chatterbox\n\nLive captions for VRChat. (Demo preview text.)',
    license: 'MIT License. (Demo preview text.)',
    notice: 'Third-party notices. (Demo preview text.)' } }));
  const demoView = new URLSearchParams(location.search).get('view');
  if (demoView) showView(demoView);
}
