'use strict';

// Mapping UI. Talks to the /api endpoints in Api/MappingApi.cs. The CRM has no
// lookup by id, so each rule stores the linked name next to its id and the
// table shows that; the link picker searches the CRM as you type.

const $ = (id) => document.getElementById(id);

const KIND_LABELS = { task: 'Task', project: 'Project', relation: 'Relation' };

const state = {
  rules: [],
  tasks: [],
  links: [],          // current search results for the dialog's link kind
  linkKind: 'task',
  selectedLink: null, // { kind, id, name } chosen in the dialog
  editingId: null,
  searchSeq: 0,       // drops search answers that arrive out of order
  // The days-back box is seeded from the server config once; the 15 s status
  // poll must not overwrite what the user typed.
  daysSeeded: false,
};

// ---- helpers ----------------------------------------------------------------

async function api(path, options) {
  const resp = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (resp.status === 204) return null;

  const body = await resp.text();
  let data = null;
  try {
    data = body ? JSON.parse(body) : null;
  } catch {
    data = null;
  }
  if (!resp.ok) {
    const err = new Error(data?.detail || data?.title || `${resp.status} ${resp.statusText}`);
    err.status = resp.status;
    err.data = data;
    throw err;
  }
  return data;
}

let bannerTimer;
function banner(message, isError = false) {
  const el = $('banner');
  el.textContent = message;
  el.classList.toggle('error', isError);
  el.hidden = false;
  clearTimeout(bannerTimer);
  bannerTimer = setTimeout(() => { el.hidden = true; }, 5000);
}

function formatTime(value) {
  if (!value) return 'never';
  const d = new Date(value);
  return `${d.toLocaleString()} (${relative(d)})`;
}

function relative(date) {
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

function days(n) {
  return n === 1 ? '1 day' : `${n} days`;
}

function today() {
  return new Date().toISOString().slice(0, 10);
}

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

// Mirrors TimeEntryMapper.Map: {task} and {date}, defaulting to the task name.
function renderTemplate(template, taskName) {
  return (template || '{task}')
    .replaceAll('{task}', taskName || 'Task')
    .replaceAll('{date}', today());
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  })[c]);
}

// ---- status -----------------------------------------------------------------

async function loadStatus() {
  let status;
  try {
    status = await api('/api/status');
  } catch (err) {
    $('status-fields').innerHTML = `<dt>Status</dt><dd class="error-text">${escapeHtml(err.message)}</dd>`;
    return;
  }

  const summary = status.lastSummary;
  const rows = [
    ['Last run', formatTime(status.lastRunAt)],
    ['Last success', formatTime(status.lastSuccessAt)],
    ['Last result', summary
      ? `created ${summary.created}, updated ${summary.updated}, recreated ${summary.recreated}, skipped ${summary.skipped}, errors ${summary.errors}`
      : '—'],
    ['Schedule', `every ${status.config.intervalMinutes} min, ${days(status.config.lookbackDays)} lookback` +
      (status.config.dryRun ? ' — DRY RUN, nothing is written to the CRM' : '')],
  ];
  if (status.lastError) rows.push(['Last error', status.lastError]);

  $('status-fields').innerHTML = rows
    .map(([label, value]) => {
      const cls = label === 'Last error' ? ' class="error-text"' : '';
      return `<dt>${escapeHtml(label)}</dt><dd${cls}>${escapeHtml(value)}</dd>`;
    })
    .join('');

  $('unmapped-behaviour').textContent = status.config.skipUnmapped
    ? 'skipped'
    : 'reported as an error';

  const daysInput = $('sync-days');
  daysInput.max = status.config.maxManualLookbackDays;
  $('scheduled-lookback').textContent = days(status.config.lookbackDays);
  if (!state.daysSeeded) {
    daysInput.value = status.config.lookbackDays;
    state.daysSeeded = true;
  }

  const button = $('sync-now');
  button.disabled = status.isRunning;
  button.textContent = status.isRunning ? 'Syncing…' : 'Sync now';
}

async function syncNow() {
  const input = $('sync-days');
  const lookback = Number(input.value);
  if (!Number.isInteger(lookback) || lookback < 1) {
    banner('Days back must be a whole number of at least 1.', true);
    return;
  }

  const button = $('sync-now');
  button.disabled = true;
  button.textContent = 'Syncing…';
  try {
    const result = await api(`/sync?lookbackDays=${lookback}`, { method: 'POST' });
    banner(`Sync done over ${days(result.lookbackDays)} — created ${result.created}, ` +
           `updated ${result.updated}, recreated ${result.recreated}, ` +
           `skipped ${result.skipped}, errors ${result.errors}.`);
  } catch (err) {
    if (err.status === 409) banner('A sync pass is already running.', true);
    else if (err.status === 401) banner('POST /sync needs the X-Trigger-Token header.', true);
    else if (err.status === 400) banner(err.data?.message || err.message, true);
    else banner(`Sync failed: ${err.message}`, true);
  } finally {
    await loadStatus();
  }
}

// ---- rules table ------------------------------------------------------------

function taskLabel(rule) {
  if (rule.taskId) {
    const known = state.tasks.find((t) => t.id === rule.taskId);
    if (known) return { text: known.name, unresolved: false };
    return { text: rule.taskName || rule.taskId, unresolved: true };
  }
  return { text: rule.taskName, unresolved: false };
}

function linkLabel(rule) {
  if (!rule.linkKind || !rule.linkId) return { text: '—', unresolved: true };
  return { text: `${KIND_LABELS[rule.linkKind]}: ${rule.linkName || rule.linkId}`, unresolved: false };
}

function cell(row, value) {
  const td = document.createElement('td');
  td.textContent = value.text;
  if (value.unresolved) {
    td.classList.add('unresolved');
    td.title = 'Not found in the current list — it may have been removed or renamed.';
  }
  row.appendChild(td);
}

function renderRules() {
  const body = $('rules-body');
  body.replaceChildren();

  for (const rule of state.rules) {
    const row = document.createElement('tr');
    cell(row, taskLabel(rule));
    cell(row, linkLabel(rule));
    cell(row, { text: rule.billable === null ? 'CRM default' : (rule.billable ? 'yes' : 'no') });
    cell(row, { text: rule.descriptionTemplate || '{task}' });

    const actions = document.createElement('td');
    actions.className = 'actions-cell';

    const edit = document.createElement('button');
    edit.type = 'button';
    edit.className = 'link';
    edit.textContent = 'Edit';
    edit.addEventListener('click', () => openDialog(rule));

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'link danger';
    remove.textContent = 'Delete';
    remove.addEventListener('click', () => deleteRule(rule));

    actions.append(edit, remove);
    row.appendChild(actions);
    body.appendChild(row);
  }

  $('rules-empty').hidden = state.rules.length > 0;
  $('rules-table').hidden = state.rules.length === 0;
}

async function loadRules() {
  state.rules = await api('/api/rules');
  renderRules();
}

async function deleteRule(rule) {
  const label = taskLabel(rule).text;
  if (!confirm(`Delete the mapping rule for "${label}"?`)) return;
  try {
    await api(`/api/rules/${rule.id}`, { method: 'DELETE' });
    banner(`Deleted the rule for "${label}".`);
    await loadRules();
  } catch (err) {
    banner(`Could not delete the rule: ${err.message}`, true);
  }
}

// ---- TimeFlip task picker ---------------------------------------------------

async function loadTasks() {
  try {
    state.tasks = await api('/api/timeflip/tasks');
  } catch (err) {
    banner(`Could not load TimeFlip tasks: ${err.message}`, true);
  }
}

function refreshTaskSelect(selectedId) {
  const select = $('task-select');
  select.replaceChildren();
  select.appendChild(new Option(state.tasks.length ? 'Select a task…' : 'No tasks loaded', ''));
  for (const task of state.tasks) select.appendChild(new Option(task.name, task.id));
  select.value = selectedId || '';
}

// ---- CRM link picker --------------------------------------------------------

function currentKind() {
  return document.querySelector('input[name="link-kind"]:checked')?.value || 'task';
}

function renderLinkSelect() {
  const select = $('link-select');
  select.replaceChildren();
  select.appendChild(new Option('(choose)', ''));

  // Keep the current selection visible even when the search doesn't return it.
  const selected = state.selectedLink?.kind === state.linkKind ? state.selectedLink : null;
  if (selected && !state.links.some((l) => l.id === selected.id)) {
    select.appendChild(new Option(selected.name || selected.id, selected.id));
  }
  for (const link of state.links) select.appendChild(new Option(link.name, link.id));
  select.value = selected?.id || '';
}

async function searchLinks(refresh = false) {
  const kind = currentKind();
  const search = $('link-search').value.trim();
  const seq = ++state.searchSeq;

  const params = new URLSearchParams();
  if (search) params.set('search', search);
  if (refresh) params.set('refresh', 'true');

  $('link-status').textContent = 'Searching…';
  try {
    const links = await api(`/api/crm/links/${kind}?${params}`);
    if (seq !== state.searchSeq) return;
    state.linkKind = kind;
    state.links = links;
    $('link-status').textContent = links.length === 0
      ? 'Nothing found.'
      : links.length >= 100 ? 'Showing the first 100 — type to narrow.' : `${links.length} found.`;
  } catch (err) {
    if (seq !== state.searchSeq) return;
    state.linkKind = kind;
    state.links = [];
    $('link-status').textContent = `Could not search the CRM: ${err.message}`;
  }
  renderLinkSelect();
}

const searchLinksDebounced = debounce(() => searchLinks(), 300);

function onKindChange() {
  const kind = currentKind();
  $('link-label').textContent = `CRM ${KIND_LABELS[kind].toLowerCase()}`;
  $('link-search').value = '';
  state.links = [];
  state.linkKind = kind;
  renderLinkSelect();
  searchLinks();
}

function onLinkSelect() {
  const id = $('link-select').value;
  if (!id) {
    state.selectedLink = null;
    return;
  }
  const found = state.links.find((l) => l.id === id);
  state.selectedLink = found
    ? { kind: state.linkKind, id: found.id, name: found.name }
    : state.selectedLink; // the kept-visible current selection
}

// ---- dialog -----------------------------------------------------------------

function setManualTask(manual) {
  $('task-manual').checked = manual;
  $('task-name').hidden = !manual;
  $('task-select').hidden = manual;
}

function updatePreview() {
  const manual = $('task-manual').checked;
  const taskName = manual
    ? $('task-name').value
    : state.tasks.find((t) => t.id === $('task-select').value)?.name;
  $('description-preview').textContent = renderTemplate($('description').value, taskName);
}

function openDialog(rule) {
  state.editingId = rule ? rule.id : null;
  $('dialog-title').textContent = rule ? 'Edit rule' : 'Add rule';
  $('form-errors').hidden = true;

  const manual = rule ? !rule.taskId : false;
  setManualTask(manual);
  $('task-name').value = rule?.taskName || '';
  refreshTaskSelect(rule?.taskId || '');

  state.selectedLink = rule?.linkKind && rule?.linkId
    ? { kind: rule.linkKind, id: rule.linkId, name: rule.linkName }
    : null;
  const kind = rule?.linkKind || 'task';
  for (const radio of document.getElementsByName('link-kind')) {
    radio.checked = radio.value === kind;
  }
  onKindChange();

  const billable = rule && rule.billable !== null ? String(rule.billable) : '';
  for (const radio of document.getElementsByName('billable')) {
    radio.checked = radio.value === billable;
  }

  $('description').value = rule?.descriptionTemplate || '';
  updatePreview();
  $('rule-dialog').showModal();
}

function readForm() {
  const manual = $('task-manual').checked;
  const taskId = manual ? null : ($('task-select').value || null);
  const selectedTask = state.tasks.find((t) => t.id === taskId);
  const billable = document.querySelector('input[name="billable"]:checked')?.value;
  const link = state.selectedLink?.kind === currentKind() && $('link-select').value
    ? state.selectedLink
    : null;

  return {
    taskId,
    // Keep the name alongside the id so the table stays readable even if the
    // task later disappears from the cube.
    taskName: manual ? ($('task-name').value.trim() || null) : (selectedTask?.name || null),
    linkKind: link?.kind || null,
    linkId: link?.id || null,
    linkName: link?.name || null,
    billable: billable === '' || billable === undefined ? null : billable === 'true',
    descriptionTemplate: $('description').value.trim() || null,
  };
}

function showErrors(messages) {
  const list = $('form-errors');
  list.replaceChildren();
  for (const message of messages) {
    const li = document.createElement('li');
    li.textContent = message;
    list.appendChild(li);
  }
  list.hidden = messages.length === 0;
}

async function saveRule() {
  const payload = readForm();
  const editing = state.editingId !== null;

  try {
    await api(editing ? `/api/rules/${state.editingId}` : '/api/rules', {
      method: editing ? 'PUT' : 'POST',
      body: JSON.stringify(payload),
    });
    $('rule-dialog').close();
    banner(editing ? 'Rule updated.' : 'Rule added. It applies from the next sync pass.');
    await loadRules();
  } catch (err) {
    if (err.status === 400 && Array.isArray(err.data?.errors)) showErrors(err.data.errors);
    else showErrors([err.message]);
  }
}

// ---- wiring -----------------------------------------------------------------

$('sync-now').addEventListener('click', syncNow);
$('add-rule').addEventListener('click', () => openDialog(null));
$('save-rule').addEventListener('click', saveRule);
$('cancel-rule').addEventListener('click', () => $('rule-dialog').close());

// method="dialog" would close the dialog on Enter without saving.
$('rule-form').addEventListener('submit', (e) => {
  e.preventDefault();
  saveRule();
});

$('task-manual').addEventListener('change', (e) => {
  setManualTask(e.target.checked);
  updatePreview();
});
$('task-select').addEventListener('change', updatePreview);
$('task-name').addEventListener('input', updatePreview);
$('description').addEventListener('input', updatePreview);

for (const radio of document.getElementsByName('link-kind')) {
  radio.addEventListener('change', onKindChange);
}
$('link-search').addEventListener('input', searchLinksDebounced);
$('link-select').addEventListener('change', onLinkSelect);

$('refresh-lists').addEventListener('click', async (e) => {
  e.preventDefault();
  await Promise.all([loadTasks(), searchLinks(true)]);
  refreshTaskSelect($('task-select').value);
  renderRules();
  banner('Reloaded TimeFlip tasks and CRM links.');
});

(async function init() {
  await loadTasks();
  await Promise.all([loadRules(), loadStatus()]);
  setInterval(loadStatus, 15000);
})();
