'use strict';

const vscode = acquireVsCodeApi();
const q = id => document.getElementById(id);
let actionPending = false;
let busyActive = false;
let busyStarted = 0;
let timer = null;
let lastRun = null;

function fmtTime(seconds) {
  const minutes = Math.floor(seconds / 60).toString().padStart(2, '0');
  const remainder = Math.floor(seconds % 60).toString().padStart(2, '0');
  return `${minutes}:${remainder}`;
}

function updateElapsed() {
  if (busyActive) q('elapsed').textContent = `Elapsed ${fmtTime((Date.now() - busyStarted) / 1000)}`;
}

function setBusy(active, label) {
  busyActive = active === true;
  if (busyActive) {
    busyStarted = Date.now();
    q('busyLabel').textContent = label || 'Working...';
    q('busyBox').classList.remove('hidden');
    if (timer) clearInterval(timer);
    timer = setInterval(updateElapsed, 1000);
    updateElapsed();
  } else {
    q('busyBox').classList.add('hidden');
    if (timer) clearInterval(timer);
    timer = null;
  }
  renderButtons(lastRun);
}

function button(text, handler, allowBusy = false) {
  const element = document.createElement('button');
  element.textContent = text;
  element.disabled = busyActive && !allowBusy;
  element.addEventListener('click', handler);
  return element;
}

function send(type, extra = {}) {
  if (actionPending && type !== 'cancel') return;
  if (type !== 'cancel') actionPending = true;
  setBusy(true, type === 'cancel' ? 'Cancel requested...' : 'Request accepted...');
  vscode.postMessage(Object.assign({ type }, extra));
}

function activityText(run) {
  const lines = [];
  const history = Array.isArray(run.recent_history) ? run.recent_history : [];
  history.forEach((item, index) => {
    const marker = item.task_status === 'succeeded' && item.verification_accepted === true ? 'OK' : item.task_status === 'failed' ? 'FAILED' : '-';
    let line = `${index + 1}. ${marker} ${item.command_name || 'action'} - ${item.task_status || 'unknown'}`;
    if (item.verification_accepted === true) line += ' - verified';
    else if (item.verification_accepted === false) line += ' - verification rejected';
    lines.push(line);
  });
  if (run.pending_action?.status === 'pending_review') lines.push(`Waiting review: ${run.pending_action.command_name} - risk ${run.pending_action.risk}`);
  if (run.status === 'ready') lines.push('Local AI may choose the next step.');
  if (run.status === 'succeeded') lines.push(`Completed: ${run.completion_summary || 'goal completed'}`);
  if (run.status === 'failed') lines.push(`Failed: ${run.error || 'unknown error'}`);
  if (run.status === 'cancelled') lines.push('Run cancelled.');
  return lines.length ? lines.join('\n') : 'Waiting for first action...';
}

function renderButtons(run) {
  const actions = q('actions');
  actions.textContent = '';
  if (!run) return;
  if (run.status === 'awaiting_plan_review') {
    actions.append(button('Approve plan', () => send('plan', { approved: true })), button('Reject plan', () => send('plan', { approved: false })));
  } else if (run.status === 'ready') {
    actions.append(button('Continue', () => send('advance')));
  } else if (run.status === 'awaiting_action_review') {
    actions.append(button('Approve action', () => send('action', { approved: true })), button('Reject action', () => send('action', { approved: false })));
  } else if (run.status === 'awaiting_command_approval') {
    actions.append(button('Approve sensitive command', () => send('commandApproval', { approved: true })), button('Reject sensitive command', () => send('commandApproval', { approved: false })));
  }
  if (!['succeeded', 'failed', 'cancelled', 'limit_reached'].includes(run.status)) actions.append(button('Cancel', () => send('cancel'), true));
}

q('start').addEventListener('click', () => {
  if (actionPending) return;
  actionPending = true;
  setBusy(true, 'Sending request to local AI...');
  vscode.postMessage({ type: 'create', goal: q('goal').value, autoSafe: q('autoSafe').checked });
});
q('refresh').addEventListener('click', () => {
  q('connection').textContent = 'Refreshing local connection...';
  q('connection').className = 'muted';
  vscode.postMessage({ type: 'refresh' });
});

window.addEventListener('message', event => {
  const message = event.data || {};
  if (message.type === 'busy') {
    setBusy(message.active === true, message.label || 'Working...');
    if (!message.active) actionPending = false;
    return;
  }
  if (message.type === 'notice') { q('notice').textContent = message.message || ''; return; }
  if (message.type === 'error') {
    actionPending = false;
    setBusy(false, '');
    q('error').textContent = message.message || 'Unknown error';
    return;
  }
  if (message.type === 'connection') {
    q('connection').textContent = message.message || 'API unavailable';
    q('connection').className = 'warn';
    return;
  }
  q('error').textContent = '';
  if (message.type === 'status') {
    const configured = message.provider?.configured === true;
    q('connection').textContent = configured
      ? `API ${message.health?.status || '?'} - ${message.health?.version || ''} - provider ${message.provider.provider_name || 'configured'}`
      : `Provider unavailable - API ${message.health?.status || '?'} - ${message.health?.version || ''}`;
    q('connection').className = configured ? 'ok' : 'warn';
    return;
  }
  if (message.type !== 'run' || !message.run) return;
  const run = message.run;
  lastRun = run;
  q('runBox').classList.remove('hidden');
  q('runStatus').textContent = `${run.status} - actions ${run.action_count}/${run.maximum_actions} - provider calls ${run.provider_calls}/${run.maximum_provider_calls}`;
  const percent = run.maximum_actions ? Math.min(100, Math.round((run.action_count / run.maximum_actions) * 100)) : 0;
  q('progressFill').style.width = `${percent}%`;
  q('activity').textContent = activityText(run);
  const info = { plan_summary: run.plan_summary, plan_steps: run.plan_steps, pending_action: run.pending_action, last_verification_accepted: run.last_verification_accepted, last_verification_reason: run.last_verification_reason, completion_summary: run.completion_summary, error: run.error, recent_history: run.recent_history };
  q('details').textContent = JSON.stringify(info, null, 2);
  if (run.status === 'failed') q('error').textContent = `Run failed: ${run.error || 'unknown error'}`;
  renderButtons(run);
});

vscode.postMessage({ type: 'ready' });
