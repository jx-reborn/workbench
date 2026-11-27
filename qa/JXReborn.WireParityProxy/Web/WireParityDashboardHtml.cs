namespace JXReborn.WireParityProxy.Web;

/// <summary>
/// Lightweight self-contained live dashboard HTML for <c>JXReborn.WireParityProxy</c>.
/// </summary>
public static class WireParityDashboardHtml
{
    public const string Page = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>JXReborn WireParityProxy Dashboard</title>
          <style>
            body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0; margin: 20px; }
            h1 { margin: 0 0 12px 0; font-size: 20px; }
            .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px; margin-bottom: 16px; }
            .card { background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 12px; }
            .label { font-size: 12px; color: #94a3b8; }
            .val { font-size: 18px; font-weight: 700; margin-top: 4px; }
            .toolbar { display: flex; gap: 10px; margin-bottom: 16px; }
            button { background: #2563eb; color: #fff; border: 0; border-radius: 6px; padding: 8px 14px; cursor: pointer; font-weight: 600; }
            button:hover { background: #1d4ed8; }
            table { width: 100%; border-collapse: collapse; font-size: 13px; margin-bottom: 20px; }
            th, td { border: 1px solid #334155; padding: 6px 8px; text-align: left; }
            th { background: #1e293b; }
            .crit { color: #f87171; font-weight: 700; }
            .warn { color: #fbbf24; font-weight: 700; }
            .ok { color: #4ade80; font-weight: 700; }
          </style>
        </head>
        <body>
          <h1>JXReborn.WireParityProxy &mdash; Single-Client Dual-Backend L7 Shadow Proxy</h1>
          <div class="grid" id="summary"></div>
          <div class="toolbar">
            <button onclick="reconcileSession('auto')" style="background:#16a34a;">&#9889; Đồng bộ 2 Server (Shadow &rarr; Primary)</button>
            <button onclick="reconcileSession('ba-lang')" style="background:#d97706;">&#127968; Đưa cả 2 về Ba Lăng (53)</button>
            <button onclick="exportReport()">Export Report (HTML/MD/JSONL)</button>
            <button onclick="resetDrift()">Reset Drift Flag</button>
            <span id="statusMsg" style="align-self:center;color:#94a3b8;font-size:13px;"></span>
          </div>
          <h2>Findings</h2>
          <table>
            <thead><tr><th>ID</th><th>Severity</th><th>Category</th><th>Step</th><th>Field</th><th>Primary</th><th>Shadow</th></tr></thead>
            <tbody id="findings"></tbody>
          </table>
          <h2>Action Windows</h2>
          <table>
            <thead><tr><th>Step</th><th>Action</th><th>C2S</th><th>Primary S2C</th><th>Shadow S2C</th><th>Drift</th><th>Export</th></tr></thead>
            <tbody id="steps"></tbody>
          </table>
          <script>
            async function refresh() {
              const res = await fetch('/api/status');
              const data = await res.json();
              document.getElementById('summary').innerHTML = `
                <div class="card"><div class="label">Bishop Proxy</div><div class="val">:${data.bishopPort} (${data.bishopActive} active)</div></div>
                <div class="card"><div class="label">GameServer Proxy</div><div class="val">:${data.gameServerPort} (${data.gameServerActive} active)</div></div>
                <div class="card"><div class="label">Primary / Shadow</div><div class="val">${data.primaryBackend} / ${data.shadowBackend}</div></div>
                <div class="card"><div class="label">Traffic (C2S / P-S2C / S-S2C)</div><div class="val">${data.c2sFrames} / ${data.primaryS2cFrames} / ${data.shadowS2cFrames}</div></div>
                <div class="card"><div class="label">Findings / Proto Violations</div><div class="val">${data.findingsCount} / ${data.protocolViolations}</div></div>
                <div class="card"><div class="label">State Drift</div><div class="val ${data.isDrifted ? 'crit' : 'ok'}">${data.driftSummary}</div></div>
              `;
              const fRes = await fetch('/api/findings');
              const findings = await fRes.json();
              document.getElementById('findings').innerHTML = findings.slice(-50).reverse().map(f =>
                `<tr><td>${f.findingId}</td><td class="${f.severity === 'Critical' ? 'crit' : 'warn'}">${f.severity}</td><td>${f.category}</td><td>${f.stepIndex}</td><td>${f.fieldPath}</td><td>${f.legacyValue}</td><td>${f.rebornValue}</td></tr>`
              ).join('');
              const sRes = await fetch('/api/steps');
              const steps = await sRes.json();
              document.getElementById('steps').innerHTML = steps.slice(-50).reverse().map(s =>
                `<tr><td>${s.stepIndex}</td><td>${s.actionLabel}</td><td>0x${s.c2sOpcode.toString(16).toUpperCase().padStart(2,'0')}</td><td>${s.primaryS2cOpcodes.join(', ')}</td><td>${s.shadowS2cOpcodes.join(', ')}</td><td>${s.drift.summary}</td><td><button onclick="exportCapture(${s.stepIndex})">L3A+</button></td></tr>`
              ).join('');
            }
            async function exportReport() {
              const r = await fetch('/api/report/export', { method: 'POST' });
              const d = await r.json();
              document.getElementById('statusMsg').textContent = d.path ? 'Exported: ' + d.path : (d.error || 'Done');
            }
            async function exportCapture(step) {
              const r = await fetch('/api/capture/export/' + step, { method: 'POST' });
              const d = await r.json();
              document.getElementById('statusMsg').textContent = d.path ? 'Saved L3A+ capture: ' + d.path : (d.error || 'Done');
            }
            async function resetDrift() {
              await fetch('/api/drift/reset', { method: 'POST' });
              refresh();
            }
            async function reconcileSession(mode) {
              document.getElementById('statusMsg').textContent = mode === 'ba-lang'
                ? 'Đang đưa cả 2 server về Ba Lăng Huyện (Map 53)...'
                : 'Đang đồng bộ Shadow -> Primary (tự động fallback Ba Lăng nếu cần)...';
              try {
                const r = await fetch('/api/reconcile', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ mode })
                });
                const d = await r.json();
                document.getElementById('statusMsg').textContent = d.message || (d.succeeded ? 'Đồng bộ thành công.' : 'Đồng bộ thất bại.');
              } catch (e) {
                document.getElementById('statusMsg').textContent = 'Lỗi kết nối khi đồng bộ: ' + e.message;
              }
              refresh();
            }
            refresh();
            setInterval(refresh, 1000);
          </script>
        </body>
        </html>
        """;
}
