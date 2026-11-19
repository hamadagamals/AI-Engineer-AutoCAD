'use strict';

const vscode = require('vscode');
const http = require('http');

class LocalApi {
  constructor() {
    const port = Number.parseInt(process.env.AI_ENGINEER_API_PORT || '8765', 10);
    if (!Number.isInteger(port) || port < 1024 || port > 65535) {
      throw new Error('AI_ENGINEER_API_PORT is invalid.');
    }
    this.port = port;
  }

  token() {
    const value = process.env.AI_ENGINEER_API_TOKEN || '';
    if (value.length < 32 || /\s/.test(value)) {
      throw new Error('AI_ENGINEER_API_TOKEN is not configured in the VS Code environment.');
    }
    return value;
  }

  request(method, path, body, authenticated = true, timeoutMs = 300000) {
    const payload = body === undefined ? null : Buffer.from(JSON.stringify(body), 'utf8');
    if (payload && payload.length > 1024 * 1024) {
      return Promise.reject(new Error('Local API request exceeds 1 MiB.'));
    }
    const headers = { 'Accept': 'application/json' };
    if (authenticated) headers['Authorization'] = `Bearer ${this.token()}`;
    if (payload) {
      headers['Content-Type'] = 'application/json';
      headers['Content-Length'] = String(payload.length);
    }
    return new Promise((resolve, reject) => {
      const request = http.request({
        host: '127.0.0.1',
        port: this.port,
        method,
        path,
        headers,
        timeout: timeoutMs
      }, response => {
        const chunks = [];
        let bytes = 0;
        response.on('data', chunk => {
          bytes += chunk.length;
          if (bytes > 1024 * 1024) {
            request.destroy(new Error('Local API response exceeds 1 MiB.'));
            return;
          }
          chunks.push(chunk);
        });
        response.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          let value = {};
          try { value = text ? JSON.parse(text) : {}; }
          catch (_) { return reject(new Error('Local API returned invalid JSON.')); }
          if ((response.statusCode || 500) < 200 || (response.statusCode || 500) >= 300) {
            const detail = typeof value.detail === 'string' ? value.detail : `HTTP ${response.statusCode}`;
            const error = new Error(detail);
            error.statusCode = response.statusCode;
            return reject(error);
          }
          resolve(value);
        });
      });
      request.on('timeout', () => request.destroy(new Error('Local API request timed out.')));
      request.on('error', reject);
      if (payload) request.write(payload);
      request.end();
    });
  }
}

class OperatorViewProvider {
  constructor(extensionUri) {
    this.extensionUri = extensionUri;
    this.api = new LocalApi();
    this.view = null;
    this.run = null;
    this.autoSafe = true;
    this.busy = false;
  }

  resolveWebviewView(webviewView) {
    this.view = webviewView;
    webviewView.webview.options = { enableScripts: true };
    webviewView.webview.onDidReceiveMessage(message => this.handle(message));
    webviewView.webview.html = this.html(webviewView.webview);
  }

  setBusy(active, label = '') {
    this.busy = active === true;
    this.post({ type: 'busy', active: this.busy, label });
  }

  busyLabelFor(message) {
    if (!message || typeof message.type !== 'string') return 'Working…';
    if (message.type === 'create') return 'Local AI is creating a reviewed plan…';
    if (message.type === 'plan') return message.approved === true ? 'Plan approved. Local AI is starting…' : 'Rejecting plan…';
    if (message.type === 'advance') return 'Local AI is analyzing the next step…';
    if (message.type === 'action') {
      const name = this.run?.pending_action?.command_name || 'local command';
      return message.approved === true ? `Executing and verifying ${name}…` : `Rejecting ${name}…`;
    }
    if (message.type === 'commandApproval') return message.approved === true ? 'Executing approved sensitive command…' : 'Rejecting sensitive command…';
    if (message.type === 'cancel') return 'Cancelling current run…';
    if (message.type === 'refresh') return 'Refreshing local status…';
    return 'Working…';
  }

  async handle(message) {
    let ownsBusy = false;
    try {
      if (!message || typeof message.type !== 'string') return;
      if (message.type === 'ready') return this.refreshStatus();
      if (message.type === 'refresh') return this.refreshStatus();
      this.setBusy(true, this.busyLabelFor(message));
      ownsBusy = true;

      if (message.type === 'create') {
        const goal = String(message.goal || '').trim();
        if (!goal || goal.length > 16000) throw new Error('Enter a bounded goal first.');
        this.autoSafe = message.autoSafe === true;
        this.run = await this.api.request('POST', '/v1/developer/runs', {
          goal,
          maximum_actions: 10
        });
        this.renderRun();
        return;
      }
      if (!this.run || !this.run.run_id) throw new Error('No active developer run.');
      const id = encodeURIComponent(this.run.run_id);
      if (message.type === 'plan') {
        this.run = await this.api.request('POST', `/v1/developer/runs/${id}/plan-review`, { approved: message.approved === true });
      } else if (message.type === 'advance') {
        this.run = await this.api.request('POST', `/v1/developer/runs/${id}/advance`, {});
      } else if (message.type === 'action') {
        this.run = await this.api.request('POST', `/v1/developer/runs/${id}/action-review`, { approved: message.approved === true });
      } else if (message.type === 'commandApproval') {
        const action = this.run.pending_action;
        if (!action || !action.approval_id) throw new Error('No command approval is pending.');
        await this.api.request('POST', `/v1/approvals/${encodeURIComponent(action.approval_id)}/decision`, { approved: message.approved === true });
        this.run = await this.api.request('POST', `/v1/developer/runs/${id}/advance`, {});
      } else if (message.type === 'cancel') {
        this.run = await this.api.request('POST', `/v1/developer/runs/${id}/cancel`, {});
        this.renderRun();
        return;
      }
      await this.autoContinueSafe();
      this.renderRun();
    } catch (error) {
      if (this.run && this.run.run_id) {
        try {
          this.run = await this.api.request('GET', `/v1/developer/runs/${encodeURIComponent(this.run.run_id)}`);
          this.renderRun();
        } catch (_) {}
      }
      this.post({ type: 'error', message: error instanceof Error ? error.message : String(error) });
    } finally {
      if (ownsBusy) this.setBusy(false, '');
    }
  }

  async refreshStatus() {
    this.setBusy(true, 'Refreshing local status…');
    try {
      let health;
      try {
        health = await this.api.request('GET', '/health', undefined, false, 5000);
      } catch (error) {
        const reason = error instanceof Error ? error.message : String(error);
        this.post({ type: 'connection', state: 'api_unavailable', message: `API unavailable: ${reason}` });
        return;
      }
      let provider;
      try {
        provider = await this.api.request('GET', '/v1/developer/status', undefined, true, 10000);
      } catch (error) {
        const reason = error instanceof Error ? error.message : String(error);
        const authenticationFailure = error?.statusCode === 401 || reason.includes('AI_ENGINEER_API_TOKEN');
        this.post({
          type: 'connection',
          state: authenticationFailure ? 'authentication_failed' : 'api_unavailable',
          message: `${authenticationFailure ? 'Authentication failed' : 'API unavailable'}: ${reason}`
        });
        return;
      }
      this.post({ type: 'status', health, provider });
      if (this.run && this.run.run_id) {
        this.run = await this.api.request('GET', `/v1/developer/runs/${encodeURIComponent(this.run.run_id)}`);
        this.renderRun();
      }
    } catch (error) {
      this.post({ type: 'error', message: error instanceof Error ? error.message : String(error) });
    } finally {
      this.setBusy(false, '');
    }
  }

  async autoContinueSafe() {
    if (!this.autoSafe) return;
    // One approved plan can finish a fully LOW/MODERATE local workflow. HIGH/DESTRUCTIVE
    // commands still stop at the durable approval gates.
    for (let count = 0; count < 24; count += 1) {
      if (!this.run) return;
      if (this.run.status === 'awaiting_action_review' && this.run.pending_action) {
        const risk = String(this.run.pending_action.risk || '').toLowerCase();
        if (risk !== 'low' && risk !== 'moderate') return;
        const name = this.run.pending_action.command_name || 'local command';
        this.setBusy(true, `Executing and verifying ${name}…`);
        this.run = await this.api.request('POST', `/v1/developer/runs/${encodeURIComponent(this.run.run_id)}/action-review`, { approved: true });
        this.renderRun();
        continue;
      }
      if (this.run.status === 'ready') {
        this.setBusy(true, `Local AI is analyzing step ${this.run.action_count + 1}…`);
        this.run = await this.api.request('POST', `/v1/developer/runs/${encodeURIComponent(this.run.run_id)}/advance`, {});
        this.renderRun();
        continue;
      }
      return;
    }
    this.post({ type: 'notice', message: 'Automatic safe-step burst limit reached. Review the current state before continuing.' });
  }

  renderRun() {
    this.post({ type: 'run', run: this.run });
  }

  post(value) {
    if (this.view) this.view.webview.postMessage(value);
  }

  html(webview) {
    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'operator.js'));
    const csp = `default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src ${webview.cspSource};`;
    return `<!doctype html><html><head><meta charset="UTF-8"><meta http-equiv="Content-Security-Policy" content="${csp}"><meta name="viewport" content="width=device-width,initial-scale=1"><style>
      body{font-family:var(--vscode-font-family);color:var(--vscode-foreground);padding:10px} textarea{box-sizing:border-box;width:100%;min-height:92px;background:var(--vscode-input-background);color:var(--vscode-input-foreground);border:1px solid var(--vscode-input-border);padding:8px} button{margin:4px 4px 4px 0;padding:6px 9px} button:disabled{opacity:.55} pre{white-space:pre-wrap;word-break:break-word;border:1px solid var(--vscode-panel-border);padding:8px;max-height:250px;overflow:auto}.muted{color:var(--vscode-descriptionForeground)}.warn{color:var(--vscode-editorWarning-foreground)}.ok{color:var(--vscode-testing-iconPassed)}.row{margin:8px 0}.hidden{display:none}h3{margin-bottom:6px}.busy{border:1px solid var(--vscode-panel-border);padding:8px;margin:8px 0}.spinner{display:inline-block;width:10px;height:10px;border:2px solid var(--vscode-panel-border);border-top-color:var(--vscode-progressBar-background);border-radius:50%;animation:spin .8s linear infinite;margin-right:7px;vertical-align:-2px}@keyframes spin{to{transform:rotate(360deg)}}.progress{height:6px;background:var(--vscode-panel-border);margin:7px 0}.progress>div{height:100%;background:var(--vscode-progressBar-background);width:0}.activity{max-height:190px}.label{font-weight:600}
    </style></head><body>
      <div id="connection" class="muted">Connecting to AI-Engineer…</div>
      <div id="busyBox" class="busy hidden"><span class="spinner"></span><span id="busyLabel">Working…</span><div class="muted" id="elapsed">Elapsed 00:00</div></div>
      <div class="row"><textarea id="goal" placeholder="اطلب: راجع المشروع، أصلح الاختبارات، افتح الملف… / Ask AI-Engineer to work on the current project"></textarea></div>
      <label><input id="autoSafe" type="checkbox" checked> Auto-approve LOW/MODERATE local steps</label>
      <div class="row"><button id="start">Start reviewed run</button><button id="refresh">Refresh</button></div>
      <div id="runBox" class="hidden"><h3>Current run</h3><div id="runStatus"></div><div class="progress"><div id="progressFill"></div></div><div class="label">Live activity</div><pre id="activity" class="activity">Waiting for activity…</pre><details><summary>Technical details</summary><pre id="details"></pre></details><div id="actions"></div></div>
      <div id="notice" class="muted"></div><div id="error" class="warn"></div>
      <script>
        const vscode=acquireVsCodeApi(); const q=id=>document.getElementById(id);
        let actionPending=false,busyActive=false,busyStarted=0,timer=null,lastRun=null;
        function fmtTime(seconds){const m=Math.floor(seconds/60).toString().padStart(2,'0');const s=Math.floor(seconds%60).toString().padStart(2,'0');return m+':'+s}
        function updateElapsed(){if(!busyActive)return;q('elapsed').textContent='Elapsed '+fmtTime((Date.now()-busyStarted)/1000)}
        function setBusy(active,label){busyActive=active===true;if(busyActive){busyStarted=Date.now();q('busyLabel').textContent=label||'Working…';q('busyBox').classList.remove('hidden');if(timer)clearInterval(timer);timer=setInterval(updateElapsed,1000);updateElapsed()}else{q('busyBox').classList.add('hidden');if(timer){clearInterval(timer);timer=null}} renderButtons(lastRun)}
        q('start').addEventListener('click',()=>{if(actionPending)return;actionPending=true;setBusy(true,'Sending request to local AI…');vscode.postMessage({type:'create',goal:q('goal').value,autoSafe:q('autoSafe').checked})});
        q('refresh').addEventListener('click',()=>{q('connection').textContent='Refreshing local connectionâ€¦';q('connection').className='muted';vscode.postMessage({type:'refresh'})});
        function button(text,handler,allowBusy=false){const b=document.createElement('button');b.textContent=text;b.disabled=busyActive&&!allowBusy;b.addEventListener('click',handler);return b}
        function send(type,extra={}){if(actionPending&&type!=='cancel')return;if(type!=='cancel')actionPending=true;setBusy(true,type==='cancel'?'Cancel requested…':'Request accepted…');vscode.postMessage(Object.assign({type},extra))}
        function activityText(r){const lines=[];const h=Array.isArray(r.recent_history)?r.recent_history:[];h.forEach((x,i)=>{const ok=x.task_status==='succeeded'&&x.verification_accepted===true?'✓':x.task_status==='failed'?'✗':'•';let line=(i+1)+'. '+ok+' '+(x.command_name||'action')+' · '+(x.task_status||'unknown');if(x.verification_accepted===true)line+=' · verified';else if(x.verification_accepted===false)line+=' · verification rejected';lines.push(line)});if(r.pending_action&&r.pending_action.status==='pending_review')lines.push('→ Waiting review: '+r.pending_action.command_name+' · risk '+r.pending_action.risk);if(r.status==='ready')lines.push('→ Local AI may choose the next step.');if(r.status==='succeeded')lines.push('✓ Completed: '+(r.completion_summary||'goal completed'));if(r.status==='failed')lines.push('✗ Failed: '+(r.error||'unknown error'));if(r.status==='cancelled')lines.push('■ Run cancelled.');return lines.length?lines.join('\n'):'Waiting for first action…'}
        function renderButtons(r){const a=q('actions');a.textContent='';if(!r)return;if(r.status==='awaiting_plan_review'){a.append(button('Approve plan',()=>send('plan',{approved:true})),button('Reject plan',()=>send('plan',{approved:false})));}else if(r.status==='ready'){a.append(button('Continue',()=>send('advance')));}else if(r.status==='awaiting_action_review'){a.append(button('Approve action',()=>send('action',{approved:true})),button('Reject action',()=>send('action',{approved:false})));}else if(r.status==='awaiting_command_approval'){a.append(button('Approve sensitive command',()=>send('commandApproval',{approved:true})),button('Reject sensitive command',()=>send('commandApproval',{approved:false})));}if(!['succeeded','failed','cancelled','limit_reached'].includes(r.status)){a.append(button('Cancel',()=>send('cancel'),true));}}
        window.addEventListener('message',event=>{const m=event.data||{};
          if(m.type==='busy'){setBusy(m.active===true,m.label||'Working…');if(!m.active)actionPending=false;return}
          if(m.type==='notice'){q('notice').textContent=m.message||'';return}
          if(m.type==='error'){actionPending=false;setBusy(false,'');q('error').textContent=m.message||'Unknown error';return}
          if(m.type==='connection'){q('connection').textContent=m.message||'API unavailable';q('connection').className='warn';return}
          q('error').textContent='';
          if(m.type==='status'){const configured=m.provider?.configured===true;q('connection').textContent=configured?'API '+(m.health?.status||'?')+' · '+(m.health?.version||'')+' · provider '+(m.provider.provider_name||'configured'):'Provider unavailable · API '+(m.health?.status||'?')+' · '+(m.health?.version||'');q('connection').className=configured?'ok':'warn';return}
          if(m.type!=='run'||!m.run)return;
          const r=m.run;lastRun=r;q('runBox').classList.remove('hidden');q('runStatus').textContent=r.status+' · actions '+r.action_count+'/'+r.maximum_actions+' · provider calls '+r.provider_calls+'/'+r.maximum_provider_calls;const pct=r.maximum_actions?Math.min(100,Math.round((r.action_count/r.maximum_actions)*100)):0;q('progressFill').style.width=pct+'%';q('activity').textContent=activityText(r);
          const info={plan_summary:r.plan_summary,plan_steps:r.plan_steps,pending_action:r.pending_action,last_verification_accepted:r.last_verification_accepted,last_verification_reason:r.last_verification_reason,completion_summary:r.completion_summary,error:r.error,recent_history:r.recent_history};q('details').textContent=JSON.stringify(info,null,2);if(r.status==='failed')q('error').textContent='Run failed: '+(r.error||'unknown error');renderButtons(r);
        });
        vscode.postMessage({type:'ready'});
      </script><script src="${scriptUri}"></script></body></html>`;
  }
}

function activate(context) {
  const provider = new OperatorViewProvider(context.extensionUri);
  context.subscriptions.push(vscode.window.registerWebviewViewProvider('aiEngineer.operatorView', provider));
  context.subscriptions.push(vscode.commands.registerCommand('aiEngineer.refreshOperator', () => provider.refreshStatus()));
}

function deactivate() {}
module.exports = { activate, deactivate };
