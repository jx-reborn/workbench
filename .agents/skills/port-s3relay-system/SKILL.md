---
name: port-s3relay-system
description: >-
  Audit, specify, and port cross-process s3relay systems (scheduled world tasks, Golden Bosses,
  Phong Lang Do hourly departures, Guild/Tong hierarchy & workshops, CityWar/SevenCity taxation,
  Martial League rankings, RelayLadder, ShareDB/gb_taskfuncs, and cross-zone chat channels) from
  legacy-server/bin/gateway/s3relay/ and legacy-server/discovery/s3relay/ into JXReborn Control
  Plane and ZoneEngine with deterministic clock-driven tests. Use whenever working on s3relay,
  scheduled events, guilds/tongs, ladders, or cross-zone state.
---

# Port & Kiểm Chứng Hệ Thống Liên Tiến Trình `s3relay` (`port-s3relay-system`)

Trong `jx-linux`, `jx_linux_y` không đứng một mình: toàn bộ "nhịp đập thế giới" (Boss Hoàng Kim, thuyền Phong Lăng Độ, Tống Kim mở báo danh theo giờ, Hạt Huy Hoàng, Bang hội `KTong`, Liên đấu, Công thành chiến, Bảng xếp hạng `ShowLadder`, và biến toàn cục `gb_GetTask`/`ShareDB`) đều do **`s3relay_y`** điều phối qua giao thức nội bộ sang các GameServer.

Skill này chuẩn hóa cách giải mã, ánh xạ sang **Distributed Control Plane + 18-Hz ZoneEngine** của `JXReborn` (theo Hiến pháp Mục I.2), và kiểm chứng tua nhanh lịch trình mà không phải chờ đồng hồ thời gian thực.

---

## 1. Nguồn Ground Truth Bắt Buộc Kiểm Tra (Zero Guessing)

Trước khi thiết kế bất kỳ tính năng liên server nào, bắt buộc đọc đủ **3 lớp nguồn gốc**:
1. **Binary C++ của `s3relay_y` & Relay Handlers trên `jx_linux_y`**:
   - `legacy-server/discovery/s3relay/` (`CoreHostP2_TaskCentreAndCommands.cpp`, `DbRelationLadder_ShareDbAndLadder.cpp`, `TongP1_*`, `League_*`, `WarBattleDungeon_*`, `ChatChannel_*`).
   - `legacy-server/discovery/relay_protocols/` (`KBattle_ProcessRelayBattleProtocol.cpp`, `KKillerTask_ProcessRelayKillerProtocol.cpp`, `KLadder_ProcessLadderProtocol.cpp`, `KServerCore_RelayForwarders.cpp`).
2. **Cấu hình Lịch trình & Kênh (`gateway/s3relay/relaysetting/` & `settings/`)**:
   - Lịch cron sự kiện: `legacy-server/bin/gateway/s3relay/relaysetting/task/tasklist.ini` và các file `.lua` con trong `relaysetting/task/` (`TaskShedule`, `TaskInterval`, `TaskTime`, `TaskCountLimit`).
   - Chiến trường & Công thành: `relaysetting/battle/`, `relaysetting/citywar.ini`, `relaysetting/tongset.ini`.
3. **Cặp Script đối xứng `s3relay` <-> `server1`**:
   - Phía `s3relay`: `gateway/s3relay/script/` (`gb_taskfuncs.lua`, `gb_modulefuncs.lua`, `startmissions.lua`, `tong/`, `leaguematch/`, `xephang/`) gọi `GlobalExecute("dwf \\script\\...\\foo.lua bar(...)")`.
   - Phía `server1`: Hàm đích được `GlobalExecute` kích hoạt, cộng với `RemoteExecute` gửi kết quả ngược về `s3relay`.

> **Bất biến Zero-Sim**: Luôn đối chiếu danh sách loại trừ trong `workbench/qa/scripts/pack_legacy_profiles.py` để bỏ qua `gateway/s3relay/script/simcity.lua` và các bot giả lập không thuộc gameplay thật.

---

## 2. Ánh Xạ Kiến Trúc Clean-Room Sang `JXReborn`

Tuyệt đối không nhúng tiến trình phụ hay dùng biến `static` cục bộ trong 1 `MapZoneWorker` (vì sẽ vỡ khi tách nhiều Zone shard):

| Cơ chế trên `s3relay_y` <-> `jx_linux_y` | Đối ứng chuẩn trong `JXReborn` (`server/` + `pack-tieudaotu/`) |
| :--- | :--- |
| `TaskCentre` (`relaysetting/task/*.lua` + `GlobalExecute`) | Dịch vụ lập lịch trên Control Plane phát lệnh có kiểm soát thời gian (`ISystemClock` / `TimeProvider` có thể tua nhanh trong test) tới các Zone đích. |
| `gb_GetTask` / `gb_SetTask` / `ShareDB` | `IGlobalScriptStateService` (lưu trữ xuyên Zone trên Control Plane, đồng bộ đọc nhanh không chặn luồng 18-Hz). |
| `KLadder` (`RelayLadder` <-> `0x97` `s2c_showladder` / `0x96` `s2c_ladderresult`) | Dịch vụ xếp hạng Control Plane + gói tin `ShowLadderS2CPacket` (`7 + 4*N`B) & `LadderResultS2CPacket` (`466`B top 10). |
| `KTong` (`0xFD` `c2s_TongExtend`, `0xA5`/`0xC3` `tongexmsg`, `0x98` `s2c_tongcreate`) | Aggregate Bang hội trên Control Plane (giao dịch quỹ/cống hiến nguyên tử chống nhân đôi tiền) + đồng bộ trạng thái danh hiệu/phe xuống `PlayerZoneState`. |
| `ChatChannel` (`0xFA` `QueryChannelID`, `0xFB` `PlayerCommunity`) | Định tuyến kênh liên bản đồ (Thế giới, Thành thị, Môn phái, Bang hội, Mật) kèm khấu trừ Nội lực/Ngân lượng và kiểm tra mặt nạ chặn kênh. |

---

## 3. Quy Trình Kiểm Chứng 3 Cổng Cho Hệ Thống `s3relay`

1. **Cổng L1 — Khớp Khung Byte Gói Tin Client (`JXReborn.Protocol.Tests`)**:
   - Mọi gói S2C phát về `game.exe` từ sự kiện Relay (`0x96`, `0x97`, `0x98`, `0xAC`, `0xC3`, `0xFA`, `0xFB`) phải đối chiếu kích thước và từng offset với `legacy-client/discovery/protocol/` (`ProtocolP1_TongAndObserver.cpp`, `ProtocolP1_ChatLinkAndEmbed.cpp`).
2. **Cổng L2 — Tua Đồng Hồ Giả Lập Lịch Trình & Xuyên Bản Đồ (`FakeTimeProvider` + Multi-Zone Harness)**:
   - Cấm chờ giờ thực (VD: chờ đến `20:00` mới nổ Boss Hoàng Kim). Bắt buộc bơm đồng hồ giả lập nhảy thẳng qua biên mốc giờ (`19:59:59 -> 20:00:00`), xác nhận:
     - Đúng Boss/Sự kiện sinh ra trên đúng `MapId` và tọa độ `(MpsX, MpsY)` đọc từ bảng gốc.
     - Bản tin hệ thống toàn máy chủ (`AddGlobalNews` / `Msg2SubWorld`) đến được người chơi đang đứng ở **một bản đồ khác** (chứng minh truyền thông xuyên Zone thật, tuân thủ Quy tắc 4 trong `ground-truth-testing.md`).
3. **Cổng L3A+ — Multi-Actor Offline Wire Capture (`2 VirtualJxClient`)**:
   - Với Bang hội, Kênh chat, Liên đấu, Tổ đội liên map: thu hoặc dựng kịch bản 2 nhân vật `P1` & `P2` kiểm chứng sự kiện của `P1` đồng bộ tức thời sang màn hình `P2`.
