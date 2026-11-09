const $ = id => document.getElementById(id);
const say = text => $('status').textContent = text;
const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const genres = ['Blues', 'Relax' ,'Country', 'Dance', 'Elektronisch', 'Folk', 'Funk', 'Hip-hop', 'Jazz', 'Klassiek', 'Latin', 'Metal', 'Pop', 'Reggae', 'Rock', 'Soul', 'Wereldmuziek'];

function genreValues(value) {
  return value && value !== 'onbekend' ? value.split(',').map(item => item.trim()).filter(Boolean) : [];
}

function renderGenreOptions(container, selected = []) {
  const values = [...genres, ...selected.filter(value => !genres.includes(value))];
  container.replaceChildren(...values.map(value => {
    const label = document.createElement('label');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.value = value;
    checkbox.checked = selected.includes(value);
    label.append(checkbox, ' ' + genreLabel(value));
    return label;
  }));
}

function selectedGenres(container) {
  return [...container.querySelectorAll('input:checked')].map(input => input.value).join(', ');
}

function updateUploadGenreSummary() {
  $('uploadGenreSummary').textContent = genreListLabel(selectedGenres($('uploadGenreOptions'))) || t('noGenreChosen');
}

// The token is kept in localStorage and cached in this script's context.
let cachedApiToken;
function getApiToken() {
  if (cachedApiToken === undefined) cachedApiToken = localStorage.getItem('apiToken') || '';
  return cachedApiToken;
}
function setApiToken(token) {
  cachedApiToken = token;
  localStorage.setItem('apiToken', token);
}

// On a 401 we ask once and retry.
async function api(path, options = {}) {
  const send = () => {
    const token = getApiToken();
    return fetch(path, { ...options, headers: { ...options.headers, ...(token ? { 'X-Api-Token': token } : {}) } });
  };

  const res = await send();
  if (res.status !== 401) return res;

  const token = prompt(t('accessPrompt'));
  if (!token) return res;
  setApiToken(token);
  return send();
}

function speakerParam(extra = '') {
  const name = $('speaker').value;
  const params = new URLSearchParams(extra);
  if (name) params.set('speaker', name);
  const query = params.toString();
  return query ? '?' + query : '';
}

async function post(path, extra = '') {
  say(t('busy'));
  try {
    const res = await api(path + speakerParam(extra), { method: 'POST' });
    const data = await res.json();
    say(res.ok ? (data.tracks !== undefined ? t('trackCount', { n: data.tracks }) : t('ok')) : data.error);
  } catch (err) {
    say(t('error') + err.message);
  }
  refreshState();
}

function setActive(state) {
  const map = { PLAYING: 'btnStart', PAUSED_PLAYBACK: 'btnPause', STOPPED: 'btnStop' };
  const activeId = map[state] ?? 'btnStop';
  for (const id of ['btnStart', 'btnPause', 'btnStop']) $(id).classList.toggle('active', id === activeId);
}

function formatTime(totalSeconds) {
  const s = Math.max(0, Math.round(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const mm = String(m).padStart(h > 0 ? 2 : 1, '0');
  const ss = String(sec).padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

let playback = null; // { title, elapsed, duration, playing } — elapsed keeps ticking locally between polls

function renderPlayback() {
  $('nowPlaying').textContent = playback?.schedulePaused
    ? t('schedulePaused')
    : playback?.waiting
    ? t('waitingForSchedule')
    : playback?.title
      ? `${playback.title} — ${formatTime(playback.elapsed ?? 0)}${playback.duration == null ? '' : ' / ' + formatTime(playback.duration)}`
      : '';
}

async function refreshState() {
  try {
    const res = await api('/api/state' + speakerParam());
    if (!res.ok) return;
    const data = await res.json();
    setActive(data.schedulePaused ? 'PAUSED_PLAYBACK' : data.state);
    playback = data.schedulePaused
      ? { schedulePaused: true }
      : data.waitingForSchedule
      ? { waiting: true }
      : data.title
      ? { title: data.title, elapsed: data.elapsed ?? 0, duration: data.duration, playing: data.state === 'PLAYING' }
      : null;
    renderPlayback();
  } catch { /* speaker unreachable, leave the buttons as they are */ }
}

async function load() {
  const speakers = await (await api('/api/speakers')).json();
  $('speaker').innerHTML = speakers.map(s => `<option value="${esc(s.name)}">${esc(s.name)}</option>`).join('')
    || `<option value="">${t('noSpeakerFound')}</option>`;

  await loadTracks();
  refreshState();
}

const crossIcon = '<svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true"><path d="M1 1 13 13M13 1 1 13" stroke="currentColor" stroke-width="2" fill="none"/></svg>';
const pageSize = 10;
let allTracks = [];
let currentPage = 0;

function matchingTracks() {
  const name = $('searchName').value.trim().toLocaleLowerCase();
  const genre = $('searchGenre').value.trim().toLocaleLowerCase();
  return allTracks.filter(track =>
    track.title.toLocaleLowerCase().includes(name)
    && (track.genre.toLocaleLowerCase().includes(genre) || genreListLabel(track.genre).toLocaleLowerCase().includes(genre)));
}

async function loadTracks() {
  const res = await api('/api/tracks');
  if (!res.ok) {
    const data = await res.json();
    throw new Error(data.error || t('loadFailed'));
  }
  allTracks = await res.json();
  renderTracks();
}

function renderTracks() {
  const tracks = matchingTracks();
  const pages = Math.max(1, Math.ceil(tracks.length / pageSize));
  currentPage = Math.min(currentPage, pages - 1);
  $('selectionInfo').textContent = tracks.length ? t('selectionInfo', { n: tracks.length }) : t('noTracksFound');
  $('pageInfo').textContent = t('pageInfo', { page: currentPage + 1, total: pages });
  $('pagePrevious').disabled = currentPage === 0;
  $('pageNext').disabled = currentPage >= pages - 1;
  const list = $('tracks');
  list.replaceChildren();
  for (const track of tracks.slice(currentPage * pageSize, (currentPage + 1) * pageSize)) {
    const row = document.createElement('tr');
    const title = document.createElement('td');
    title.className = 'title';
    title.textContent = track.title;
    const genreCell = document.createElement('td');
    const genre = document.createElement('button');
    genre.className = 'genre';
    genre.title = t('editGenre');
    genre.textContent = genreListLabel(track.genre);
    genre.onclick = () => editGenre(track);
    const del = document.createElement('button');
    del.className = 'delete';
    del.title = t('remove');
    del.setAttribute('aria-label', t('removeTrack', { title: track.title }));
    del.innerHTML = crossIcon;
    del.onclick = () => removeTrack(track);
    const actions = document.createElement('td');
    genreCell.append(genre);
    actions.append(del);
    row.append(title, genreCell, actions);
    list.append(row);
  }
}

async function startPlayback() {
  const mode = $('playMode').value;
  if (mode !== 'selection') {
    await post('/api/play', new URLSearchParams({ mode, shuffle: $('shuffle').checked }).toString());
    return;
  }
  const paths = matchingTracks().map(track => track.path);
  if (!paths.length) { say(t('noTracksFound')); return; }
  say(t('busy'));
  try {
    const res = await api('/api/play/selection' + speakerParam(new URLSearchParams({ shuffle: $('shuffle').checked }).toString()), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paths })
    });
    const data = await res.json();
    say(res.ok ? t('trackCount', { n: data.tracks }) : data.error);
  } catch (err) {
    say(t('error') + err.message);
  }
  refreshState();
}

async function editGenre(track) {
  $('genreTrackTitle').textContent = track.title;
  renderGenreOptions($('editGenreOptions'), genreValues(track.genre));
  $('genreDialog').dataset.trackPath = track.path;
  $('genreDialog').showModal();
}

async function saveGenre(path) {
  const value = selectedGenres($('editGenreOptions'));
  say(t('busy'));
  try {
    const res = await api('/api/tracks/genre', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path, genre: value })
    });
    if (!res.ok) { say((await res.json()).error); return; }
    say(t('genreSaved'));
  } catch (err) {
    say(t('error') + err.message);
    return;
  }
  await loadTracks();
}

async function refreshAfterChange() {
  await loadTracks();
  refreshState();
}

async function removeTrack(track) {
  if (!confirm(t('confirmDelete'))) return;

  const isCurrent = playback?.title === track.title;
  if (isCurrent) await post('/api/stop');

  say(t('busy'));
  try {
    const path = track.path.split('/').map(encodeURIComponent).join('/');
    const res = await api('/api/tracks/' + path, { method: 'DELETE' });
    const data = await res.json();
    say(res.ok ? t('deleted') : data.error);
    if (!res.ok) return;
  } catch (err) {
    say(t('error') + err.message);
    return;
  }
  await refreshAfterChange();
}

async function uploadFiles() {
  const input = $('uploadFiles');
  const files = [...input.files].filter(isUploadFile);
  if (files.length === 0) { say(t('noAudioSelected')); return; }

  const body = new FormData();
  for (const file of files) body.append('files', file);
  const invoice = $('invoiceFile').files[0];
  if (invoice) body.append('invoice', invoice);
  const genre = selectedGenres($('uploadGenreOptions'));
  if (genre) body.append('genre', genre);
  for (const file of files) {
    const license = document.querySelector(`[data-license-for="${CSS.escape(file.name)}"]`)?.files[0];
    if (license) body.append('license:' + file.name, license);
  }
  input.value = '';
  $('invoiceFile').value = '';
  renderGenreOptions($('uploadGenreOptions'));
  updateUploadGenreSummary();
  $('licenseFiles').replaceChildren();

  say(t('uploading'));
  setUploading(true);
  const poll = setInterval(pollConversion, 500);
  try {
    const { ok, data } = await sendUpload(body);
    if (!ok) { say(data.error); return; }

    let msg = t('uploadResult', { saved: data.saved.length, skipped: data.skipped.length });
    if (data.flagged.length > 0)
      msg += t('flaggedCopyright') + data.flagged.join('; ');
    if (data.failed.length > 0)
      msg += t('conversionFailed') + data.failed.join('; ');
    say(msg);
  } catch (err) {
    say(t('error') + err.message);
    return;
  } finally {
    clearInterval(poll);
    setUploading(false);
  }
  await refreshAfterChange();
}

let uploadSent = false;

function setUploadProgress(label, fraction) {
  const btn = $('btnUpload');
  btn.textContent = label;
  btn.classList.toggle('indeterminate', fraction === null);
  btn.style.setProperty('--progress', fraction === null ? '0%' : Math.round(fraction * 100) + '%');
}

function setUploading(on) {
  document.body.classList.toggle('uploading', on);
  $('btnUpload').disabled = on;
  uploadSent = false;
  if (on) setUploadProgress(t('uploading'), 0);
  else {
    $('btnUpload').classList.remove('indeterminate');
    $('btnUpload').textContent = t('upload');
  }
}

// After the bytes are sent the server converts each file; its progress comes from the status endpoint.
async function pollConversion() {
  if (!uploadSent) return;
  try {
    const s = await (await api('/api/tracks/upload-status')).json();
    if (!s.busy || s.total === 0) return;
    const fraction = (s.current + s.fraction) / s.total;
    setUploadProgress(`${t('converting', { current: Math.min(s.current + 1, s.total), total: s.total })} (${Math.round(fraction * 100)}%)`, fraction);
  } catch { /* try again on the next tick */ }
}

function sendUpload(body) {
  const attempt = () => new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/tracks');
    const token = getApiToken();
    if (token) xhr.setRequestHeader('X-Api-Token', token);
    xhr.upload.onprogress = e => {
      if (e.lengthComputable) setUploadProgress(`${t('uploading')} ${Math.round(e.loaded / e.total * 100)}%`, e.loaded / e.total);
    };
    xhr.upload.onload = () => { uploadSent = true; setUploadProgress(t('converting', { current: 1, total: '…' }), null); };
    xhr.onerror = () => reject(new Error(t('error')));
    xhr.onload = () => {
      let data = {};
      try { data = JSON.parse(xhr.responseText); } catch { /* non-JSON error body */ }
      resolve({ ok: xhr.status >= 200 && xhr.status < 300, status: xhr.status, data });
    };
    xhr.send(body);
  });

  return attempt().then(result => {
    if (result.status !== 401) return result;
    const token = prompt(t('accessPrompt'));
    if (!token) return result;
    setApiToken(token);
    return attempt();
  });
}
window.addEventListener('beforeunload', e => {
  if (document.body.classList.contains('uploading')) e.preventDefault();
});

renderGenreOptions($('uploadGenreOptions'));
function isUploadFile(file) {
  return /\.(mp3|m4a|flac|wav|ogg|mp4|wmv)$/i.test(file.name);
}
$('uploadGenreOptions').onchange = updateUploadGenreSummary;
$('btnUpload').onclick = async () => {
  try {
    const status = await (await api('/api/tracks/upload-status')).json();
    if (status.busy) { say(t('uploadBusy')); return; }
  } catch { /* the server still rejects concurrent uploads */ }
  $('uploadDialog').showModal();
};
$('uploadFiles').onchange = e => {
  const licenses = $('licenseFiles');
  licenses.replaceChildren();
  for (const file of e.target.files) {
    if (!isUploadFile(file)) continue;
    const label = document.createElement('label');
    label.textContent = t('licenseFor', { name: file.name });
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'application/pdf,image/*,.txt';
    input.dataset.licenseFor = file.name;
    label.append(input);
    licenses.append(label);
  }
};
$('uploadDialog').addEventListener('close', e => {
  if (e.target.returnValue === 'upload') uploadFiles();
  else {
    $('uploadFiles').value = '';
    $('invoiceFile').value = '';
    renderGenreOptions($('uploadGenreOptions'));
    updateUploadGenreSummary();
    $('licenseFiles').replaceChildren();
  }
});
$('genreDialog').addEventListener('close', e => {
  if (e.target.returnValue === 'save') saveGenre(e.target.dataset.trackPath);
});
$('btnStart').onclick = startPlayback;
for (const id of ['searchName', 'searchGenre']) $(id).oninput = () => { currentPage = 0; renderTracks(); };
$('pagePrevious').onclick = () => { currentPage--; renderTracks(); };
$('pageNext').onclick = () => { currentPage++; renderTracks(); };
$('volume').onchange = e => post('/api/volume', 'level=' + e.target.value);
$('speaker').onchange = refreshState;
load().catch(err => say(t('error') + err.message));
setInterval(refreshState, 10000);
setInterval(() => {
  if (!playback?.playing) return;
  playback.elapsed = playback.duration == null ? playback.elapsed + 1 : Math.min(playback.elapsed + 1, playback.duration);
  renderPlayback();
}, 1000);

api('/api/version').then(r => r.text()).then(v => { document.getElementById('appVersion').textContent = 'v' + v; }).catch(() => {});
