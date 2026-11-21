# Hướng dẫn vận hành AI Agent với `JXReborn.WireParityProxy`

Tài liệu này lưu lại quy trình đầy đủ để kết nối AI Agent (Antigravity / Cursor / Claude) với `JXReborn.WireParityProxy` theo mô hình **Cách A: Proxy Daemon + Thin MCP Bridge** — đảm bảo nâng cấp/restart Proxy hoặc `reborn-server` thoải mái mà **không làm rớt kết nối MCP**.

---

## 1. Kiến trúc Cách A (2 tiến trình tách rời)

```text
[AI Agent] <--stdio MCP--> [Thin MCP Bridge (--mcp --proxy-url http://127.0.0.1:5100)]
                                  │ HTTP (/api/mcp)
                                  ▼
[Proxy Daemon (--standalone --reuse-legacy --keep-containers)]
   ├── legacy-linux (Docker/wslc, sống dai, ~3-5s reconnect)
   ├── reborn-server (JXReborn.Host cô lập, SQLite T=0)
   ├── 1x game.exe + driver
   └── Dashboard :5100
```

- **Ưu điểm**:
  - `initialize` / `ping` của MCP trả lời cục bộ tức thì (< 5 ms), không bao giờ timeout.
  - Bạn (hoặc Agent) có thể tắt/bật/nâng cấp `Proxy Daemon` hoặc sửa code `reborn-server` ở ngoài mà kết nối MCP của AI Agent không bị đứt.
  - Dễ nhìn log trực tiếp ở terminal và Dashboard `http://127.0.0.1:5100`.

---

## 2. Cài đặt & Chạy từng bước

### Bước 0 — Build bản mới nhất (chạy 1 lần sau khi sửa code Proxy)

Nếu đang mở `JXReborn.WireParityProxy` ở cửa sổ terminal khác thì bấm **`Ctrl+C`** tắt trước để tránh khóa file `.dll`, sau đó chạy:

```powershell
dotnet build d:\Projects\jx-reborn\workbench\qa\JXReborn.WireParityProxy\JXReborn.WireParityProxy.csproj
```

### Bước 1 — Terminal riêng: Chạy **Proxy Daemon**

Mở 1 terminal riêng và giữ nó chạy:

```powershell
dotnet run --project d:\Projects\jx-reborn\workbench\qa\JXReborn.WireParityProxy --no-build -- --standalone --reuse-legacy --keep-containers
```

- Thêm `--headless` nếu muốn ẩn cửa sổ `game.exe` và không tự mở trình duyệt.
- Bỏ `--headless` nếu muốn quan sát trực tiếp cửa sổ `game.exe` di chuyển/đối thoại theo lệnh của AI.

Chờ terminal hiển thị đủ các dòng sau:

```text
[WireParityProxy] Both backend servers are ready.
[WireParityProxy] Bishop L7  : 127.0.0.1:16622
[WireParityProxy] GS L7      : 127.0.0.1:16668
[WireParityProxy] Single game.exe + driver attached.
[WireParityProxy] Dashboard  : http://127.0.0.1:5100
```

### Bước 2 — Khai báo **Thin MCP Bridge** trong `mcp_config.json`

Không cần chạy tay lệnh này ở terminal — dán vào cấu hình MCP của IDE/Agent rồi bấm **Reload / Refresh MCP Servers**:

```json
{
  "mcpServers": {
    "jx-wire-parity": {
      "command": "dotnet",
      "args": [
        "d:\\Projects\\jx-reborn\\workbench\\artifacts\\mcp-bridge\\JXReborn.WireParityProxy.dll",
        "--mcp",
        "--proxy-url",
        "http://127.0.0.1:5100"
      ]
    }
  }
}
```

---

## 3. Danh sách 8 MCP Tools

| Tool | Công dụng |
| :--- | :--- |
| `get_proxy_status` | Kiểm tra trạng thái 2 server, cổng `:16622`/`:16668`, `driverConnected`, số frame, `findingsCount`, `isDrifted`. |
| `get_world_state` | Lấy `WorldDomSnapshot` (Player, NPCs, Dialog, Inventory, Skills) từ RAM `game.exe` hoặc wire state. |
| `perform_action` | Điều khiển `game.exe`: `login`, `move_to`, `interact_npc`, `select_dialog`, `confirm_talk`, `use_item`, `switch_pk`, `send_chat`. |
| `get_proxy_findings` | Đọc `ParityFinding` từ 3 tầng (Protocol Gate, Action-Window Wire Diff, Semantic State Diff). Lọc được theo `severity`, `since_step`. |
| `get_action_steps` | Lấy timeline từng `WireStepReport` đã đóng. |
| `export_l3a_capture` | Xuất 1 `step_index` thành `*.capture.json` đã ký SHA-256 để replay offline L3A+ trong CI. |
| `export_report` | Xuất `findings.jsonl`, `report.md`, `report.html` (+ server logs). |
| `reset_drift` | Reset cờ `StateDriftDetector` sau khi đưa nhân vật về trạng thái chuẩn. |

---

## 4. Quy trình nâng cấp nóng (Hot-Upgrade)

1. **Khi AI Agent sửa code C# trong `server/`**:
   - Agent xuất lỗi ra `*.capture.json` (`export_l3a_capture`), sửa code trong `server/src/`, và chạy test L3A+ (`dotnet test ... --filter "FullyQualifiedName~WireCaptures"`) chỉ mất ~1 giây — **không cần tắt Proxy Daemon**.
2. **Khi muốn nạp bản `JXReborn.Host` hoặc `WireParityProxy` mới vào Proxy đang chạy**:
   - Qua terminal ở **Bước 1**, bấm `Ctrl+C` → build lại (nếu sửa Proxy) → chạy lại lệnh Bước 1.
   - Container `legacy-linux` được giữ sẵn nhờ `--reuse-legacy --keep-containers` nên lên lại chỉ trong ~3–5 giây.
   - Kết nối MCP của Agent vẫn sống nguyên.

---

## 5. Mẫu Prompt cho AI Agent

### A. Prompt kiểm tra kết nối nhanh

```text
Hãy gọi MCP tool `get_proxy_status` của `jx-wire-parity` để kiểm tra kết nối. Nếu `driverConnected: true`, hãy gọi `perform_action(action: "login", account: "gm001", role_name: "AgentQa01")` rồi gọi `get_world_state` và `get_proxy_findings` báo cáo lại cho tôi.
```

### B. Prompt `/goal` — Tự chơi, bắt bug, đóng băng L3A+ và tự sửa `server/`

```markdown
Bạn là QA & Parity Engineer tự động cho JXReborn. Hãy kết nối qua MCP server `jx-wire-parity` và thực hiện vòng lặp "Tìm sai lệch -> Đóng băng L3A+ -> Tự sửa server -> Xác minh" theo đúng quy tắc sau:

### Quy tắc bắt buộc
1. **Không đoán**: Luôn gọi `get_world_state` trước khi hành động và `get_proxy_findings` sau mỗi nhóm 1-3 thao tác.
2. **Kiểm soát Drift**: Sau mỗi thao tác, kiểm tra `get_proxy_status`. Nếu `isDrifted == true`, dừng nhánh hiện tại, gọi `export_report` và `reset_drift` trước khi thử nhánh khác.
3. **Khóa Test Chống Gian Lận (Red -> Green)**:
   - Khi phát hiện bất kỳ `ParityFinding` nào (Critical hoặc Warning), gọi ngay `export_l3a_capture(step_index)` để xuất file `*.capture.json` (đã ký SHA-256).
   - **TUYỆT ĐỐI KHÔNG** sửa file `*.capture.json` để làm test pass.
4. **Sửa đúng nguyên nhân gốc**:
   - Đọc `legacy-server` để đối chiếu logic chuẩn.
   - Chỉ sửa code C# trong `d:\Projects\jx-reborn\server\src\`.
   - Sau khi sửa, chạy `dotnet test` trên test L3A+ tương ứng để xác nhận từ **RED -> GREEN**.

### Nhiệm vụ phiên này (Scope: Ba Lăng Huyện - Map 53)
1. Gọi `get_proxy_status` xác nhận proxy sẵn sàng, sau đó gọi `perform_action(action: "login", account: "gm001", role_name: "AgentQa01")`.
2. Gọi `get_world_state` lấy danh sách NPC xung quanh (`npcs`).
3. Lần lượt di chuyển (`move_to`) và tương tác (`interact_npc`) với tối đa 5 NPC gần nhất: mở thoại, kiểm tra nhánh chọn (`select_dialog`), xác nhận thoại (`confirm_talk`), và gọi `get_proxy_findings`.
4. Với mỗi sai lệch tìm được: xuất `export_l3a_capture`, đối chiếu `legacy-server`, sửa code ở `server/`, và chạy test xác minh xanh.
5. Cuối phiên, gọi `export_report` và tổng kết: số NPC đã test, danh sách bug đã sửa, và các bug còn tồn đọng.
```
