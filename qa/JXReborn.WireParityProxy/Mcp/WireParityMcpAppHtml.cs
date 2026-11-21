namespace JXReborn.WireParityProxy.Mcp;

/// <summary>
/// Self-contained MCP App HTML (<c>ui://wire-parity/dashboard.html</c>) rendered inside Antigravity's
/// side pane / sidebar / inline frame, communicating with the MCP host via JSON-RPC over <c>postMessage</c>.
/// </summary>
public static class WireParityMcpAppHtml
{
    public const string ResourceUri = "ui://wire-parity/dashboard.html";
    public const string MimeType = "text/html;profile=mcp-app";

    public const string Page = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>JXReborn WireParityProxy MCP App</title>
          <style>
            :root { color-scheme: dark; }
            body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0; margin: 14px; font-size: 13px; }
            h1 { margin: 0 0 10px 0; font-size: 16px; }
            .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; margin-bottom: 12px; }
            .card { background: #1e293b; border: 1px solid #334155; border-radius: 6px; padding: 8px 10px; }
            .label { font-size: 11px; color: #94a3b8; }
            .val { font-size: 14px; font-weight: 700; margin-top: 2px; }
            .toolbar { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; align-items: center; }
            button { background: #2563eb; color: #fff; border: 0; border-radius: 5px; padding: 6px 10px; cursor: pointer; font-size: 12px; font-weight: 600; }
            button:hover { background: #1d4ed8; }
            table { width: 100%; border-collapse: collapse; font-size: 12px; margin-bottom: 14px; }
            th, td { border: 1px solid #334155; padding: 5px 7px; text-align: left; }
            th { background: #1e293b; }
            .crit { color: #f87171; font-weight: 700; }
            .warn { color: #fbbf24; font-weight: 700; }
            .ok { color: #4ade80; font-weight: 700; }
          </style>
        </head>
        <body>
          <h1>WireParityProxy &mdash; Live Differential Monitor</h1>
          <div class="grid" id="summary"></div>
          <div class="toolbar">
            <button onclick="refreshAll()">Refresh</button>
            <button onclick="callAndNotice('export_report', {})">Export Report</button>
            <button onclick="callAndNotice('reset_drift', {})">Reset Drift</button>
            <span id="msg" style="color:#94a3b8;font-size:12px;"></span>
          </div>
          <h3>Findings</h3>
          <table>
            <thead><tr><th>ID</th><th>Severity</th><th>Step</th><th>Field</th><th>Primary</th><th>Shadow</th></tr></thead>
            <tbody id="findings"></tbody>
          </table>
          <h3>Action Windows</h3>
          <table>
            <thead><tr><th>Step</th><th>Action</th><th>C2S</th><th>Primary S2C</th><th>Shadow S2C</th><th>L3A+</th></tr></thead>
            <tbody id="steps"></tbody>
          </table>
          <script>
            let nextId = 1;
            const pending = new Map();

            window.addEventListener('message', (event) => {
              const data = event.data;
              if (!data || typeof data !== 'object') return;
              if (data.id !== undefined && pending.has(data.id)) {
                const { resolve, reject } = pending.get(data.id);
                pending.delete(data.id);
                if (data.error) reject(new Error(data.error.message || 'MCP error'));
                else resolve(data.result);
                return;
              }
              if (data.method === 'ui/notifications/tool-result' && data.params?.structuredContent) {
                renderFromStructured(data.params.structuredContent);
              }
            });

            function rpc(method, params) {
              const id = nextId++;
              return new Promise((resolve, reject) => {
                pending.set(id, { resolve, reject });
                window.parent.postMessage({ jsonrpc: '2.0', id, method, params }, '*');
              });
            }

            async function callServerTool(name, args) {
              const res = await rpc('tools/call', { name, arguments: args || {} });
              const txt = res?.content?.[0]?.text;
              return txt ? JSON.parse(txt) : res;
            }

            function renderStatus(s) {
              if (!s) return;
              document.getElementById('summary').innerHTML = `
                <div class="card"><div class="label">Bishop / GS</div><div class="val">:${s.bishopPort || '-'} / :${s.gameServerPort || '-'}</div></div>
                <div class="card"><div class="label">Primary / Shadow</div><div class="val">${s.primaryBackend} / ${s.shadowBackend}</div></div>
                <div class="card"><div class="label">Traffic (C2S/P/S)</div><div class="val">${s.c2sFrames}/${s.primaryS2cFrames}/${s.shadowS2cFrames}</div></div>
                <div class="card"><div class="label">Findings / Proto</div><div class="val">${s.findingsCount} / ${s.protocolViolations}</div></div>
                <div class="card"><div class="label">Drift</div><div class="val ${s.isDrifted ? 'crit' : 'ok'}">${s.driftSummary}</div></div>
              `;
            }

            function renderFindings(findings) {
              document.getElementById('findings').innerHTML = (findings || []).slice(-30).reverse().map(f =>
                `<tr><td>${f.findingId}</td><td class="${f.severity === 'Critical' ? 'crit' : 'warn'}">${f.severity}</td><td>${f.stepIndex}</td><td>${f.fieldPath}</td><td>${f.legacyValue}</td><td>${f.rebornValue}</td></tr>`
              ).join('');
            }

            function renderSteps(steps) {
              document.getElementById('steps').innerHTML = (steps || []).slice(-30).reverse().map(s =>
                `<tr><td>${s.stepIndex}</td><td>${s.actionLabel}</td><td>0x${(s.c2sOpcode || 0).toString(16).toUpperCase().padStart(2,'0')}</td><td>${(s.primaryS2cOpcodes || []).join(', ')}</td><td>${(s.shadowS2cOpcodes || []).join(', ')}</td><td><button onclick="callAndNotice('export_l3a_capture', {step_index: ${s.stepIndex}})">L3A+</button></td></tr>`
              ).join('');
            }

            function renderFromStructured(sc) {
              if (sc.status) renderStatus(sc.status);
              if (sc.findings) renderFindings(sc.findings);
              if (sc.steps) renderSteps(sc.steps);
            }

            async function refreshAll() {
              try {
                const [status, findings, steps] = await Promise.all([
                  callServerTool('get_proxy_status', {}),
                  callServerTool('get_proxy_findings', {}),
                  callServerTool('get_action_steps', {})
                ]);
                renderStatus(status);
                renderFindings(findings);
                renderSteps(steps);
              } catch (e) {
                document.getElementById('msg').textContent = e.message;
              }
            }

            async function callAndNotice(tool, args) {
              try {
                const out = await callServerTool(tool, args);
                document.getElementById('msg').textContent = JSON.stringify(out);
                await refreshAll();
              } catch (e) {
                document.getElementById('msg').textContent = e.message;
              }
            }

            (async () => {
              try {
                await rpc('ui/initialize', {
                  appInfo: { name: 'WireParityProxy', version: '1.0.0' },
                  appCapabilities: {},
                  protocolVersion: '2026-01-26'
                });
                window.parent.postMessage({ jsonrpc: '2.0', method: 'ui/notifications/initialized', params: {} }, '*');
                await refreshAll();
              } catch (e) {
                document.getElementById('msg').textContent = 'Init: ' + e.message;
              }
            })();
          </script>
        </body>
        </html>
        """;
}
