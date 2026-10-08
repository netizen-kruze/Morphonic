'use strict';
// Morphonic frontend. Outbound {action,...} via window.external.sendMessage, inbound
// {type,payload} via window.external.receiveMessage. All state lives in the
// backend; this file renders payloads and sends actions.

const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const DEMO = location.search.includes('demo');
const MB = b => b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.round(b / 1e6) + ' MB';

function send(obj) {
  if (DEMO) return;
  try { window.external.sendMessage(JSON.stringify(obj)); } catch (e) { /* host not ready */ }
}

// ── view switching ─────────────────────────────────────────────
let firstRunNeeded = false;
function currentView() { return document.querySelector('.rail button.on')?.dataset.view ?? 'voice'; }
function showView(v) {
  document.querySelectorAll('.rail button').forEach(b => {
    const on = b.dataset.view === v;
    b.classList.toggle('on', on);
    if (on) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
  const effective = (v === 'voice' && firstRunNeeded) ? 'firstrun' : v;
  document.querySelectorAll('.view').forEach(el =>
    el.classList.toggle('on', el.id === 'view-' + effective));
}
document.querySelectorAll('.rail button').forEach(b =>
  b.addEventListener('click', () => showView(b.dataset.view)));

// ── state / start-stop ─────────────────────────────────────────
let running = false, loading = false;
let dev = null;   // last devices payload
function setState(p) {
  running = !!p.running;
  loading = !!p.loading && !running;
  const chipDot = $('statusChip').querySelector('.dot');
  if (running) {
    chipDot.className = 'dot';
    $('statusText').textContent = p.label || 'running';
    $('stageIdle').hidden = true;
    $('stageLive').hidden = false;
    $('liveVoice').textContent = p.voiceName || 'Voice';
    $('liveMeta').textContent = `${p.accel || ''} · pipeline delay ${p.latencyMs || 0} ms (plus the device buffers)`;
    $('pulse').hidden = false;
    $('listenLabel').textContent = 'Converting';
  } else if (loading) {
    chipDot.className = 'dot busy';
    $('statusText').textContent = 'Loading ' + (p.label || 'voice') + '…';
    $('listenLabel').textContent = '';
  } else {
    chipDot.className = p.error ? 'dot err' : 'dot off';
    $('statusText').textContent = p.error ? 'error' : (p.voiceName ? p.voiceName : 'idle');
    $('stageIdle').hidden = false;
    $('stageLive').hidden = true;
    $('pulse').hidden = true;
    $('listenLabel').textContent = '';
    $('latencyChip').hidden = true;
    $('meterIn').style.width = $('meterOut').style.width = '0%';
  }
  setPace(null);
  updateStartButton();
  $('btnStartStop').classList.toggle('stop', running);
  $('icoPlay').classList.toggle('hide', running);
  $('icoStop').classList.toggle('hide', !running);
  $('btnStartStopText').textContent = running ? 'Stop voice' : loading ? 'Loading voice…' : 'Start voice';
}
function updateStartButton() {
  $('btnStartStop').disabled = firstRunNeeded || !dev || loading || (!running && !dev.voiceId);
}
$('btnStartStop').addEventListener('click', () => {
  if (running) send({ action: 'stop' });
  else if (!dev?.voiceId) { toast(false, 'Choose a voice first', { label: 'Open Voices', view: 'voices' }); }
  else send({ action: 'start', inputIndex: parseInt($('selInput').value ?? '0', 10), outputIndex: parseInt($('selOutput').value ?? '0', 10) });
});

// ── pace: is conversion keeping up? ────────────────────────────
function setPace(p) {
  const chip = $('paceChip');
  if (!p || !running || p.status === 'Unknown') { chip.hidden = true; return; }
  const behind = p.status === 'Behind', strained = p.status === 'Strained';
  chip.hidden = false;
  chip.className = 'pacechip' + (behind ? ' behind' : strained ? ' strained' : '');
  chip.querySelector('.dot').className = 'dot' + (behind ? ' err' : strained ? ' busy' : '');
  $('paceText').textContent = behind
    ? `falling behind ${(p.load ?? 0).toFixed(1)}× · ${p.lagMs ?? 0} ms late`
    : strained ? 'barely keeping up' : 'keeping up';
  chip.title = `last pass: ${p.passMs ?? 0} ms per ${p.blockMs ?? 0} ms block · ${p.underruns ?? 0} underrun(s)`;
  $('latencyChip').hidden = false;
  $('latencyChip').textContent = `≈ ${p.latencyMs ?? 0} ms behind you · pass ${p.passMs ?? 0} ms`;
}

// ── devices / settings payload ─────────────────────────────────
function renderDevices(p) {
  dev = p;
  firstRunNeeded = !p.componentsReady;
  if (currentView() === 'voice') showView('voice');
  updateStartButton();
  $('trayNote').innerHTML = p.platform === 'windows' ? '× hides to tray;<br>the voice keeps running' : '× quits Morphonic;<br>minimize to keep going';
  $('rowVirtualMic').hidden = false;
  $('groupDesktop').hidden = p.platform !== 'linux';

  const fill = (sel, names, selected, withOff) => {
    sel.innerHTML = (withOff ? '<option value="-1">Off</option>' : '') +
      (names || []).map((d, i) => `<option value="${i}" ${i === selected ? 'selected' : ''}>${esc(d)}</option>`).join('');
    if (withOff && selected < 0) sel.value = '-1';
  };
  fill($('selInput'), p.inputs, p.savedInput, false);
  fill($('selOutput'), p.outputs, p.savedOutput, false);
  // Hear yourself (sidetone): auto = on whenever the output is a virtual cable
  const side = $('selSidetone');
  side.innerHTML = '<option value="auto">Auto (on when the output is a virtual cable)</option><option value="off">Off</option>' +
    (p.outputs || []).map((d, i) => `<option value="${i}">${esc(d)}</option>`).join('');
  side.value = p.sidetone === 'device' ? String(p.savedMonitor) : (p.sidetone || 'auto');
  side.title = p.sidetone === 'auto'
    ? (p.outputIsVirtual ? 'Sidetone is on: the output is a virtual cable, so the converted voice also plays on your default output' : 'Sidetone is off while the output is a real device you already hear')
    : 'Sidetone: also play the converted voice to you, so you know what others hear';

  $('rngPitch').value = p.pitch;
  $('outPitch').textContent = (p.pitch > 0 ? '+' : '') + p.pitch + ' st';
  const seg = (id, v) => document.querySelectorAll('#' + id + ' button').forEach(b => b.classList.toggle('on', String(parseFloat(b.dataset.v)) === String(v)));
  seg('segBlock', p.blockMs); seg('segExtra', p.extraMs); seg('segCross', p.crossfadeMs);
  seg('segGate', p.noiseGateDb); seg('segRms', p.rmsMixRate); seg('segGain', p.outputGainDb);
  document.querySelectorAll('#segAccel button').forEach(b => b.classList.toggle('on', b.dataset.v === p.acceleration));
  setToggle($('tglVirtualMic'), p.virtualMic);
  if (p.platform === 'windows') {
    // Morphonic's own cable driver: installed once, then "Morphonic Voice"
    // (output) and "Morphonic Microphone" (input) exist for every program.
    $('rowVirtualMic').hidden = false;
    $('tglVirtualMic').hidden = true;
    $('btnInstallVirtualMic').hidden = p.virtualMicInstalled || !p.virtualMicPackage;
    $('btnRemoveVirtualMic').hidden = !p.virtualMicInstalled;
    $('btnInstallVirtualMic').disabled = $('btnRemoveVirtualMic').disabled = !!p.virtualMicBusy;
    $('btnInstallVirtualMic').textContent = p.virtualMicBusy ? 'Installing…' : 'Install';
    $('virtualMicDesc').textContent = p.virtualMicInstalled
      ? 'Installed: other programs see "Morphonic Microphone" as a microphone. Leave Output on System default and the voice plays into "Morphonic Voice" by itself; Hear yourself (Auto) lets you listen along.'
      : p.virtualMicPackage
        ? 'Adds "Morphonic Voice" (an output) and "Morphonic Microphone" (an input other programs can pick) with Morphonic\'s own driver. One administrator prompt; stays installed until removed here.'
        : 'This build carries no driver package, so no virtual microphone can be installed from it. With VB-CABLE installed instead, pick "CABLE Input" as the output.';
  } else {
    $('tglVirtualMic').hidden = false;
    $('btnInstallVirtualMic').hidden = $('btnRemoveVirtualMic').hidden = true;
    $('virtualMicDesc').textContent = 'Creates "Morphonic-Voice-Mic", an input other programs can pick, and plays the voice into it. Needs PipeWire (pipewire-pulse). Applies after a restart. Status: ' + (p.virtualMicStatus || 'unknown') + '.';
  }
  $('accelDesc').textContent = `Auto uses GPU acceleration when it is installed and this machine can run it; CPU forces the processor. Applies after a restart. Now: ${p.accelStatus || p.accelActive}.`;
  const sp = $('selSpeaker');
  sp.innerHTML = Array.from({ length: 16 }, (_, i) => `<option value="${i}" ${i === p.speakerId ? 'selected' : ''}>${i}</option>`).join('');
  $('pythonDesc').innerHTML = 'Standard RVC v2 voices convert inside the app in seconds. A checkpoint with an unusual configuration falls back to the bundled Python tool, which needs Python 3 with PyTorch (about 2 GB, CPU-only). '
    + (p.pythonFound
      ? 'Python 3 was found: the button installs the packages with pip.'
      : 'Python 3 was <b>not</b> found; install it first (python.org, or <span class="mono">sudo dnf install python3</span>).');
  $('btnInstallConverter').disabled = !p.pythonFound || !!p.installingConverter;
  $('btnInstallConverter').textContent = p.installingConverter ? 'Installing…' : 'Install fallback converter';
  if (p.platform === 'windows' && !p.hasCable && !cableNoted && p.componentsReady) {
    cableNoted = true;
    toast(true, 'For games and calls, install VB-CABLE and pick "CABLE Input" as the output; apps then see "CABLE Output" as a microphone (see Read me).');
  }
}
let cableNoted = false;

function setToggle(el, on) { el.classList.toggle('on', !!on); el.setAttribute('aria-checked', String(!!on)); }
function toggleHandler(el, fn) {
  el.addEventListener('click', fn);
  el.addEventListener('keydown', e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); fn(); } });
}

// ── voices ─────────────────────────────────────────────────────
function renderVoices(p) {
  $('voiceFolderText').textContent = p.folder || 'voices folder';
  const rows = (p.voices || []).map(v => {
    const active = v.id === p.active;
    const converting = v.id === p.converting;
    const badges = [
      v.kind === 'pth' ? (converting ? '<span class="badge pth">converting…</span>' : '<span class="badge pth">.pth — not converted yet</span>') : '',
      active ? '<span class="badge active">Active</span>' : '',
    ].join(' ');
    const meta = v.kind === 'onnx'
      ? `${MB(v.sizeBytes)} · ${v.sampleRate ? (v.sampleRate / 1000) + ' kHz' : 'rate detected on first use'}${v.speakers > 1 ? ` · ${v.speakers} speakers` : ''}${v.skipHead ? ' · tail decode' : ''}${v.source ? ' · from ' + esc(v.source) : ''}`
      : `${MB(v.sizeBytes)} · RVC checkpoint`;
    const acts = v.kind === 'onnx'
      ? `${active ? '' : `<button class="chipbtn amber" data-use="${esc(v.id)}">Use</button>`}<button class="chipbtn danger" data-delv="${esc(v.id)}">Delete</button>`
      : `<button class="chipbtn amber" data-conv="${esc(v.id)}" ${converting ? 'disabled' : ''}>${converting ? 'Converting…' : 'Convert'}</button><button class="chipbtn danger" data-delv="${esc(v.id)}">Delete</button>`;
    return `<div class="row${active ? ' active' : ''}" data-vid="${esc(v.id)}"><span class="grow"><span class="name">${esc(v.name)}</span> ${badges}<span class="meta">${meta}</span></span><span class="acts">${acts}</span></div>`;
  }).join('');
  $('voiceRows').innerHTML = rows || '<div class="empty">No voices yet. Open <b>Get voices</b> to search Hugging Face, add the sample voice, or drop a .pth / .onnx file.</div>';
  if (p.highlight) {
    // a voice that just arrived: show My voices, bring its row into view and flash it
    showVoiceTab('mine');
    const row = $('voiceRows').querySelector(`[data-vid="${CSS.escape(p.highlight)}"]`);
    if (row) { row.classList.add('flash'); setTimeout(() => row.classList.remove('flash'), 2600); row.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
  }
  $('voiceRows').querySelectorAll('[data-use]').forEach(b => b.addEventListener('click', () => send({ action: 'setVoice', id: b.dataset.use })));
  $('voiceRows').querySelectorAll('[data-conv]').forEach(b => b.addEventListener('click', () => send({ action: 'convertVoice', id: b.dataset.conv })));
  $('voiceRows').querySelectorAll('[data-delv]').forEach(b => b.addEventListener('click', () => send({ action: 'deleteVoice', id: b.dataset.delv })));
}
// The two halves of the Voices screen: choosing (My voices) and getting (Get voices).
function showVoiceTab(name) {
  document.querySelectorAll('#segVoices button').forEach(b => b.classList.toggle('on', b.dataset.vtab === name));
  $('vtab-mine').hidden = name !== 'mine';
  $('vtab-get').hidden = name !== 'get';
  $('voicesHint').innerHTML = name === 'mine'
    ? 'Pick a voice here, then press <b>Start voice</b> on the Voice screen.'
    : 'Downloads land in <b>My voices</b> and become the active voice.';
}
document.querySelectorAll('#segVoices button').forEach(b => b.addEventListener('click', () => showVoiceTab(b.dataset.vtab)));
$('btnGetVoices').addEventListener('click', () => showVoiceTab('get'));
$('btnImport').addEventListener('click', () => send({ action: 'importVoice' }));
$('btnOpenVoices').addEventListener('click', () => send({ action: 'openVoicesFolder' }));
$('btnOpenData').addEventListener('click', () => send({ action: 'openDataFolder' }));

// ── update check (Settings > About) ────────────────────────────
let updateUrl = '';
$('btnCheckUpdate').addEventListener('click', () => send({ action: 'checkUpdate' }));
$('btnGetUpdate').addEventListener('click', () => { if (updateUrl) send({ action: 'openUrl', url: updateUrl }); });
function renderUpdate(p) {
  const desc = $('updateDesc'), notes = $('updateNotes'), get = $('btnGetUpdate'), btn = $('btnCheckUpdate');
  btn.disabled = !!p.checking;
  if (p.checking) { desc.textContent = 'Asking github.com…'; return; }
  updateUrl = p.url || '';
  if (p.error) { desc.textContent = p.error; get.hidden = true; notes.hidden = true; return; }
  if (p.available) {
    desc.textContent = `Version ${p.latest} is available${p.published ? ' (published ' + p.published + ')' : ''}; you have ${p.current}.` +
      (p.assetName ? ` Download ${p.assetName} (${MB(p.assetSize)}) from the release page, then replace this app with it.` : '');
    notes.textContent = p.notes ? p.notes.trim().split('\n').slice(0, 6).join('\n') : '';
    notes.hidden = !notes.textContent;
    get.hidden = !updateUrl;
  } else {
    desc.textContent = `You have the latest version (${p.current}).`;
    notes.hidden = true; get.hidden = true;
  }
}

// ── drag and drop ──────────────────────────────────────────────
// The page cannot pass a dropped file's path to the app, so it streams
// the bytes: 4 MB pieces, each acknowledged before the next is sent.
const CHUNK = 4 * 1024 * 1024;
const importAcks = new Map();   // name -> resolve of the pending piece
function b64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 32768) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 32768));
  return btoa(s);
}
function ackFor(name) { return new Promise(res => importAcks.set(name, res)); }
function onImportAck(p) { const r = importAcks.get(p.name); if (r) { importAcks.delete(p.name); r(p); } }
let importing = false;
async function importFiles(files) {
  const list = Array.from(files || []).filter(f => /[.](pth|onnx|zip)$/i.test(f.name));
  if (!list.length) { toast(false, 'Drop a .pth, .onnx or .zip voice file'); return; }
  if (importing) { toast(false, 'An import is already running'); return; }
  importing = true;
  const status = $('dropStatus');
  try {
    for (const f of list) {
      status.hidden = false; status.textContent = `receiving ${f.name}… 0%`;
      let ack = ackFor(f.name); send({ action: 'importBegin', name: f.name, size: f.size }); ack = await ack;
      if (!ack.ok) continue;
      let ok = true;
      for (let off = 0; off < f.size; off += CHUNK) {
        const buf = new Uint8Array(await f.slice(off, Math.min(f.size, off + CHUNK)).arrayBuffer());
        let a = ackFor(f.name); send({ action: 'importChunk', name: f.name, data: b64(buf) }); a = await a;
        if (!a.ok) { ok = false; break; }
        status.textContent = `receiving ${f.name}… ${Math.round(Math.min(f.size, off + CHUNK) / f.size * 100)}%`;
      }
      if (ok) send({ action: 'importEnd', name: f.name }); else send({ action: 'importAbort', name: f.name });
    }
  } finally {
    importing = false; status.hidden = true; status.textContent = '';
  }
}
{
  const zone = $('dropZone'), view = $('view-voices');
  let depth = 0;
  view.addEventListener('dragenter', e => { e.preventDefault(); depth++; zone.classList.add('over'); });
  view.addEventListener('dragover', e => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
  view.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; zone.classList.remove('over'); } });
  view.addEventListener('drop', e => { e.preventDefault(); depth = 0; zone.classList.remove('over'); importFiles(e.dataTransfer.files); });
  zone.addEventListener('click', () => send({ action: 'importVoice' }));
  document.addEventListener('dragover', e => e.preventDefault());
  document.addEventListener('drop', e => e.preventDefault());
}

// ── find voices (Hugging Face) ─────────────────────────────────
let hubResults = [];
function findVoices() {
  const q = $('findQuery').value.trim();
  if (!q) return;
  $('findRows').innerHTML = '<div class="empty">searching…</div>';
  send({ action: 'searchVoices', query: q });
}
$('btnFind').addEventListener('click', findVoices);
$('findQuery').addEventListener('keydown', e => { if (e.key === 'Enter') findVoices(); });
document.querySelectorAll('[data-url]').forEach(a => a.addEventListener('click', e => { e.preventDefault(); send({ action: 'openUrl', url: a.dataset.url }); }));
function renderHubResults(p) {
  if (p.error) { $('findRows').innerHTML = `<div class="empty">${esc(p.error)}</div>`; return; }
  hubResults = p.results || [];
  if (!hubResults.length) { $('findRows').innerHTML = '<div class="empty">Nothing found. Try another spelling, or add "rvc" to the search.</div>'; return; }
  $('findRows').innerHTML = hubResults.map(r => `
    <div class="row" data-repo="${esc(r.id)}"><span class="grow"><span class="name">${esc(r.id)}</span>
      <span class="meta">${r.downloads} downloads · ${r.likes} likes · updated ${esc(r.updated)}</span><div class="files" hidden></div></span>
      <span class="acts"><button class="chipbtn" data-files="${esc(r.id)}">Show files</button></span></div>`).join('');
  $('findRows').querySelectorAll('[data-files]').forEach(b => b.addEventListener('click', () => {
    const row = b.closest('.row'); row.querySelector('.files').hidden = false;
    row.querySelector('.files').innerHTML = '<span class="file">listing…</span>'; b.disabled = true;
    send({ action: 'listVoiceFiles', repo: b.dataset.files });
  }));
}
function renderHubFiles(p) {
  const row = document.querySelector(`[data-repo="${CSS.escape(p.repo)}"]`);
  if (!row) return;
  const box = row.querySelector('.files');
  if (p.error) { box.innerHTML = `<span class="err">${esc(p.error)}</span>`; return; }
  const files = p.files || [];
  if (!files.length) { box.innerHTML = '<span class="err">no .pth, .onnx or .zip file in this repository</span>'; return; }
  box.innerHTML = files.map(f => `<span class="file" data-file="${esc(f.path)}"><span class="grow">${esc(f.path)}${f.libraryName && f.libraryName !== f.path.split('/').pop() ? ` <span class="meta">→ ${esc(f.libraryName)}</span>` : ''}</span><span>${MB(f.sizeBytes)}</span>
    <button class="chipbtn ${f.inLibrary ? '' : 'amber'}" data-dlfile="${esc(f.path)}">${f.inLibrary ? 'Use' : 'Download'}</button></span>`).join('');
  box.querySelectorAll('[data-dlfile]').forEach(b => b.addEventListener('click', () => {
    const f = files.find(x => x.path === b.dataset.dlfile);
    b.disabled = true;
    // already in the library: the app answers by choosing it (no download)
    send({ action: 'downloadVoiceFile', repo: p.repo, path: f.path, sizeBytes: f.sizeBytes, sha256: f.sha256 || '' });
  }));
}
function onVoiceProgress(p) {
  const name = p.name;
  document.querySelectorAll('[data-file]').forEach(el => {
    if (!el.dataset.file.endsWith('/' + name) && el.dataset.file !== name) return;
    let bar = el.querySelector('.progress');
    if (p.done) {
      if (bar) bar.remove();
      const b = el.querySelector('button');
      if (b) { b.disabled = false; b.textContent = p.ok ? 'Use' : 'Download'; b.classList.toggle('amber', !p.ok); }
      return;
    }
    if (!bar) { bar = document.createElement('div'); bar.className = 'progress'; bar.innerHTML = '<i style="width:0%"></i>'; el.insertBefore(bar, el.querySelector('button')); }
    const pct = p.total ? Math.round(p.received / p.total * 100) : 0;
    bar.querySelector('i').style.width = pct + '%';
  });
}

// ── models ─────────────────────────────────────────────────────
let mods = null;
let firstRunQueue = [];
let firstRunKicked = null;
let firstRunKickSeen = false;
function resetFirstRunCards() {
  firstRunKicked = null;
  firstRunKickSeen = false;
  $('chSetup').disabled = $('chSample').disabled = false;
  $('frProgress').hidden = true;
}
function renderModels(p) {
  mods = p;
  $('hwText').textContent = `Your hardware: ${p.tierLabel} — running on ${p.accelActive}${p.tierNote ? ' · ' + p.tierNote : ''}`;
  $('hwText').title = p.accelStatus || '';
  const setup = (p.models || []).filter(m => m.kind === 'component');
  $('chSetupSize').textContent = setup.length && setup.every(m => m.included)
    ? 'included in this build — no download'
    : '~' + MB(setup.filter(m => !m.included).reduce((a, m) => a + m.sizeBytes, 0)) + ' download';
  const sample = (p.models || []).find(m => m.kind === 'voice');
  if (sample) $('chSampleSize').textContent = sample.installed ? 'installed' : sample.included ? 'included in this build' : '~' + MB(sample.sizeBytes) + ' download';

  // first-run queue: when nothing is downloading, kick the next item. A
  // re-seen kicked id only means failure once a payload has actually shown
  // it downloading.
  if (firstRunQueue.length) {
    if (firstRunKicked && p.downloading === firstRunKicked) firstRunKickSeen = true;
    if (!p.downloading) {
      const next = firstRunQueue.find(id => !p.models.find(m => m.id === id)?.installed);
      if (!next) { firstRunQueue = []; resetFirstRunCards(); }
      else if (next !== firstRunKicked) {
        firstRunKicked = next; firstRunKickSeen = false;
        send({ action: 'downloadModel', id: next });
      }
      else if (firstRunKickSeen) { firstRunQueue = []; resetFirstRunCards(); }
    }
  }

  const rowFor = m => {
    const badges = [
      m.installed ? '<span class="badge installed">Installed</span>' : '',
      m.required ? '<span class="badge required">Required</span>' : '',
      m.kind === 'pack' && p.tier === 'Gpu' && !m.installed ? '<span class="badge reco">Recommended</span>' : '',
    ].join(' ');
    const downloading = p.downloading === m.id;
    const btn = downloading
      ? `<button class="chipbtn" data-cancel="1">Cancel</button>`
      : m.installed
        ? `<button class="chipbtn danger" data-del="${m.id}">Delete</button>`
        : `<button class="chipbtn amber" data-dl="${m.id}">${m.included ? 'Unpack' : 'Download'}</button>`;
    return `
      <div class="row" data-model="${m.id}">
        <span class="grow">
          <span class="name">${esc(m.displayName)}</span> ${badges}
          <span class="desc">${esc(m.description)}</span>
          <span class="meta">${m.included ? MB(m.sizeBytes) + ' · included in this build' : MB(m.sizeBytes)} · ${esc(m.license)} · ${esc(m.attribution)}</span>
          ${downloading ? `<div class="progress"><i data-bar="${m.id}" style="width:0%"></i></div><span class="meta" data-plabel="${m.id}">starting…</span>` : ''}
        </span>${btn}
      </div>`;
  };
  $('componentRows').innerHTML = p.models.filter(m => m.kind === 'component').map(rowFor).join('');
  $('packRows').innerHTML = p.models.filter(m => m.kind === 'pack').map(rowFor).join('');
  $('catalogVoiceRows').innerHTML = p.models.filter(m => m.kind === 'voice').map(rowFor).join('');
  document.querySelectorAll('[data-dl]').forEach(b =>
    b.addEventListener('click', () => send({ action: 'downloadModel', id: b.dataset.dl })));
  document.querySelectorAll('[data-del]').forEach(b =>
    b.addEventListener('click', () => send({ action: 'deleteModel', id: b.dataset.del })));
  document.querySelectorAll('[data-cancel]').forEach(b =>
    b.addEventListener('click', () => { firstRunQueue = []; resetFirstRunCards(); send({ action: 'cancelDownload' }); }));
}
function onProgress(p) {
  const pct = p.total ? Math.round(p.received / p.total * 100) : 0;
  const bar = document.querySelector(`[data-bar="${p.id}"]`);
  if (bar) bar.style.width = pct + '%';
  const label = document.querySelector(`[data-plabel="${p.id}"]`);
  const assembling = p.stage === 'assemble';
  if (label) label.textContent = assembling ? `assembling the model… ${pct}%` : `${MB(p.received)} of ${MB(p.total)} (${pct}%)`;
  if (firstRunQueue.length) {
    $('frProgress').hidden = false;
    $('frBar').style.width = pct + '%';
    const m = mods?.models.find(x => x.id === p.id);
    $('frLabel').textContent = `${assembling ? 'Assembling' : 'Downloading'} ${m ? m.displayName : p.id} — ${pct}%`;
  }
}
$('btnVerify').addEventListener('click', () => send({ action: 'verifyModels' }));
$('btnInstallConverter').addEventListener('click', () => send({ action: 'installConverter' }));
$('btnDefaults').addEventListener('click', () => send({ action: 'recommendedDefaults' }));
$('chSetup').addEventListener('click', () => startFirstRun(['contentvec', 'rmvpe']));
$('chSample').addEventListener('click', () => startFirstRun(['contentvec', 'rmvpe', 'sample-voice']));
function startFirstRun(ids) {
  firstRunQueue = ids;
  $('chSetup').disabled = $('chSample').disabled = true;
  $('frProgress').hidden = false;
  $('frLabel').textContent = 'Starting download…';
  send({ action: 'getModels' });
}

// ── speed check ────────────────────────────────────────────────
$('btnBench').addEventListener('click', () => send({ action: 'runBench' }));
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
    `<tr><td>${esc(r.label)}</td><td>${r.loadMs} ms</td><td>${r.avgPassMs} ms</td><td>${r.maxPassMs} ms</td><td>${r.blockMs} ms</td>` +
    `<td>${Number(r.load).toFixed(2)}×</td><td class="${cls(r.verdict)}">${esc(r.verdict)}</td></tr>`).join('');
  out.innerHTML =
    `<table class="benchtable"><thead><tr><th>Voice</th><th>Load</th><th>Avg pass</th><th>Max pass</th><th>Block</th><th>Load</th><th>Verdict</th></tr></thead>` +
    `<tbody>${rows}</tbody></table><div class="benchsum">${esc(p.summary || '')}</div>`;
}

// ── about / license docs ───────────────────────────────────────
function renderDocs(p) {
  $('appVer').textContent = p.version ? 'v' + p.version : '';
  $('machineLine').textContent = p.machine ? 'This machine: ' + p.machine : '';
  $('dataDirLine').textContent = p.dataDir ? 'Data folder: ' + p.dataDir : '';
  $('btnOpenData').hidden = !p.dataDir;
  $('docReadme').textContent = p.readme || '';
  $('docLicense').textContent = p.license || '';
  $('docNotice').textContent = p.notice || '';
  renderInstall(p);
}
function renderInstall(p) {
  if (p.installedAt) { $('btnInstall').textContent = 'Update app grid copy'; $('installDesc').textContent = 'Installed at ' + p.installedAt + '. Pressing again replaces it with this binary.'; }
}
[['btnDocReadme', 'docReadme'], ['btnDocLicense', 'docLicense'], ['btnDocNotice', 'docNotice']].forEach(([b, d]) =>
  $(b).addEventListener('click', () => {
    const open = $(d).classList.toggle('open');
    $(b).textContent = open ? 'Hide' : 'View';
  }));
$('btnInstall').addEventListener('click', () => send({ action: 'installDesktop' }));

// ── settings actions ───────────────────────────────────────────
const segSend = (id, key, parse) => document.querySelectorAll('#' + id + ' button').forEach(b =>
  b.addEventListener('click', () => send({ action: 'config', [key]: parse(b.dataset.v) })));
segSend('segBlock', 'blockMs', v => parseInt(v, 10));
segSend('segExtra', 'extraMs', v => parseInt(v, 10));
segSend('segCross', 'crossfadeMs', v => parseInt(v, 10));
segSend('segGate', 'noiseGateDb', v => parseInt(v, 10));
segSend('segRms', 'rmsMixRate', v => parseFloat(v));
segSend('segGain', 'outputGainDb', v => parseInt(v, 10));
segSend('segAccel', 'acceleration', v => v);
$('btnInstallVirtualMic').addEventListener('click', () => send({ action: 'installVirtualMic' }));
$('btnRemoveVirtualMic').addEventListener('click', () => send({ action: 'removeVirtualMic' }));
toggleHandler($('tglVirtualMic'), () => {
  if (!dev) return;
  dev.virtualMic = !dev.virtualMic;
  send({ action: 'config', virtualMic: dev.virtualMic });
});
$('selSpeaker').addEventListener('change', () => send({ action: 'config', speakerId: parseInt($('selSpeaker').value, 10) }));
$('selSidetone').addEventListener('change', () => {
  const v = $('selSidetone').value;
  send(v === 'auto' || v === 'off' ? { action: 'setSidetone', mode: v } : { action: 'setSidetone', mode: 'device', index: parseInt(v, 10) });
});
$('selInput').addEventListener('change', () => send({ action: 'setInputDevice', index: parseInt($('selInput').value, 10) }));
$('selOutput').addEventListener('change', () => send({ action: 'setOutputDevice', index: parseInt($('selOutput').value, 10) }));
// The pitch slider applies live; sends are throttled while dragging.
let pitchTimer = null;
$('rngPitch').addEventListener('input', () => {
  const v = parseInt($('rngPitch').value, 10);
  $('outPitch').textContent = (v > 0 ? '+' : '') + v + ' st';
  clearTimeout(pitchTimer);
  pitchTimer = setTimeout(() => send({ action: 'config', pitch: v }), 150);
});

// ── toasts ─────────────────────────────────────────────────────
// Error toasts use role=alert and stay until dismissed. An optional
// action {label, send} posts a message to the backend, or {label, view}
// opens a section. Persistent toasts never stack beyond four, and a
// repeat of the same text replaces the earlier one.
function toast(ok, msg, action) {
  const box = $('toasts');
  [...box.children].forEach(t => { if (t.dataset.msg === msg) t.remove(); });
  while (box.children.length >= 4) box.firstChild.remove();
  const el = document.createElement('div');
  el.className = 'toast' + (ok ? '' : ' err');
  el.dataset.msg = msg;
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
      el.remove();
    });
    el.appendChild(b);
  }
  el.addEventListener('click', () => el.remove());
  box.appendChild(el);
  if (ok) setTimeout(() => el.remove(), 5000);
}

// ── inbound dispatch ───────────────────────────────────────────
function onMessage(raw) {
  let m; try { m = JSON.parse(raw); } catch { return; }
  const p = m.payload || {};
  switch (m.type) {
    case 'state': setState(p); break;
    case 'devices': stateReceived = true; renderDevices(p); break;
    case 'voices': renderVoices(p); break;
    case 'importAck': onImportAck(p); break;
    case 'voiceSearch': renderHubResults(p); break;
    case 'voiceFiles': renderHubFiles(p); break;
    case 'voiceProgress': onVoiceProgress(p); break;
    case 'models': renderModels(p); break;
    case 'meter':
      $('meterIn').style.width = Math.round((p.input ?? 0) * 100) + '%';
      $('meterOut').style.width = Math.round((p.output ?? 0) * 100) + '%';
      break;
    case 'pace': setPace(p); break;
    case 'modelProgress': onProgress(p); break;
    case 'docs': renderDocs(p); break;
    case 'update': renderUpdate(p); break;
    case 'install': renderInstall(p); break;
    case 'bench': renderBench(p); break;
    case 'toast': toast(!!p.ok, p.msg, p.action); break;
  }
}

// ── host bridge + boot handshake ───────────────────────────────
// The hook and the state request retry until the state arrives: if the
// host bridge is a beat late, the first request must not be lost.
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
  send({ action: 'getState' });
  send({ action: 'getDocs' });
  if (attempt < 40) setTimeout(() => requestState(attempt + 1), attempt < 10 ? 300 : 1000);
}
// Heartbeat: the host reloads the page if these stop (a crashed web process).
setInterval(() => send({ action: 'ping' }), 5000);

// ── boot ───────────────────────────────────────────────────────
hookBridge();
requestState(0);
setState({ running: false });

// ── demo mode for design preview (?demo) ───────────────────────
if (DEMO) {
  onMessage(JSON.stringify({ type: 'devices', payload: {
    platform: 'windows', inputs: ['System default', 'Headset Microphone (Index)', 'USB Desk Mic'], outputs: ['System default', 'CABLE Input (VB-Audio Virtual Cable)', 'Speakers (Realtek)'],
    savedInput: 1, savedOutput: 1, savedMonitor: 2, hasCable: true, virtualMic: false, virtualMicStatus: 'n/a',
    pitch: 6, speakerId: 0, blockMs: 160, extraMs: 2500, crossfadeMs: 50, noiseGateDb: -60, rmsMixRate: 0.5, outputGainDb: 0,
    acceleration: 'auto', accelActive: 'DirectML', accelStatus: 'DirectML (loaded DirectML; onnxruntime.dll from the GPU pack)',
    componentsReady: true, voiceId: 'nova.onnx', voiceName: 'Nova', pythonFound: true } }));
  onMessage(JSON.stringify({ type: 'voices', payload: { active: 'nova.onnx', converting: '', folder: 'C:\\Users\\you\\AppData\\Roaming\\Morphonic\\voices', voices: [
    { id: 'nova.onnx', name: 'Nova', kind: 'onnx', sizeBytes: 110674164, sampleRate: 40000, speakers: 1, skipHead: true, source: 'nova.pth' },
    { id: 'sample-voice-40k.onnx', name: 'Sample voice (RVC base, 40 kHz)', kind: 'onnx', sizeBytes: 110674164, sampleRate: 40000, speakers: 109, skipHead: true, source: 'f0G40k.pth' },
    { id: 'moth.pth', name: 'moth', kind: 'pth', sizeBytes: 55e6 }] } }));
  onMessage(JSON.stringify({ type: 'models', payload: { tier: 'Gpu', tierLabel: 'GPU (DirectML)', tierNote: '', accelActive: 'DirectML', downloading: '', models: [
    { id: 'contentvec', displayName: 'Content encoder (ContentVec, 768-d)', description: 'Turns your speech into the phonetic features every RVC v2 voice is driven by. Required.', kind: 'component', sizeBytes: 377676692, license: 'MIT', attribution: 'ContentVec (auspicious3000); Transformers port lengyue233', installed: true, required: true },
    { id: 'rmvpe', displayName: 'Pitch model (RMVPE)', description: 'Tracks the pitch of your voice so the converted voice follows your melody. Required.', kind: 'component', sizeBytes: 361688443, license: 'MIT', attribution: 'RMVPE (yxlllc); ONNX by the RVC project', installed: true, required: true },
    { id: 'sample-voice', displayName: 'Sample voice (RVC base, 40 kHz)', description: 'A generic voice from the RVC v2 pretrained generator.', kind: 'voice', sizeBytes: 110674164, license: 'MIT', attribution: 'RVC project', installed: true, required: false },
    { id: 'gpu-pack', displayName: 'GPU acceleration (DirectML)', description: 'Runs the models on your graphics card instead of the CPU.', kind: 'pack', sizeBytes: 214751266, license: 'MIT + DirectML license', attribution: 'Microsoft', installed: true, required: false }] } }));
  onMessage(JSON.stringify({ type: 'state', payload: { running: true, label: 'Nova · DirectML · 40 kHz', voiceName: 'Nova', accel: 'DirectML', latencyMs: 210 } }));
  onMessage(JSON.stringify({ type: 'meter', payload: { input: 0.48, output: 0.61 } }));
  onMessage(JSON.stringify({ type: 'pace', payload: { status: 'KeepingUp', load: 0.31, lagMs: 20, passMs: 49, blockMs: 160, latencyMs: 230, underruns: 0 } }));
  onMessage(JSON.stringify({ type: 'docs', payload: { version: '1.0.0', machine: 'AMD Ryzen 7 9800X3D · 16 threads · 64 GB RAM · GPU: NVIDIA GeForce RTX 5080 (16 GB) · Windows 11 Pro · tier GPU (DirectML)', dataDir: 'C:\\Users\\you\\AppData\\Roaming\\Morphonic',
    readme: '# Morphonic\n\nReal-time RVC voice changer. (Demo preview text.)', license: 'MIT License. (Demo preview text.)', notice: 'Third-party notices. (Demo preview text.)' } }));
}
