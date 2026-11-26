---
name: khao-sat-chien-truong-pho-ban
description: >-
  Survey, verify against legacy-server/bin/server1/script/missions/ & battles/, and lock
  multi-phase timed instances and battlegrounds (Song-Jin / Tong Kim, Vuot Ai / Challenge
  of Time, Phong Lang Do Ferry, Martial League / Lien Dau, Viem De, Thien Tri, CityWar)
  using 18-Hz fast-forward tick simulation (MissionZoneTickSystem + IMissionController)
  without waiting real-world hours. Use when implementing or testing missions, battlegrounds,
  arenas, or timed wave dungeons.
---

# Khảo Sát & Tua Nhanh Chiến Trường / Phó Bản (`khao-sat-chien-truong-pho-ban`)

Các chiến trường và phó bản lớn của `jx-linux` (`server1/script/missions/`, `server1/script/battles/`, `Specs 027–028` & `Specs 039–041`) là những máy trạng thái đa pha chạy kéo dài **30–60 phút** với hàng chục biến `MissionV[1..100]`, `MissionS[1..10]`, bộ đếm `StartMissionTimer`, phân nhóm `Group 1 (Tống)` vs `Group 2 (Kim)`, điểm tích lũy cá nhân (`Task 747`...), và rớt bảng xếp hạng trực tiếp (`0xB0` / `0xA1`).

Không bao giờ kiểm thử các hệ thống này bằng cách treo `WireParityProxy` chờ 60 phút thật! Skill này quy định cách trích xuất đầy đủ máy trạng thái Mission và kiểm chứng **chính xác từng mốc tick 18-Hz bằng kỹ thuật Tua Nhanh Khung Hình (`Time-Warp Frame Stepping`)**.

---

## 1. Danh Mục Trích Xuất Bắt Buộc Từ `legacy-server/bin/` (Tier 2 Deep Trace)

Khi khảo sát bất kỳ thư mục `missions/<name>/` hoặc `battles/<name>/` nào qua `legacy-explore`, bắt buộc lập bảng đầy đủ **6 thành phần** sau trước khi viết code:

1. **Bảng Ánh Xạ `MissionV` & `TaskID` Cá Nhân**:
   - Liệt kê từng hằng số `MS_STATE` (`MissionV[1]`: `0=Đóng, 1=Báo danh, 2=Đang đánh, 3=Kết thúc`), điểm hai phe, số quái còn lại của đợt (`wave`), và mọi `TaskID` lưu điểm/lượt đi trong ngày của người chơi (VD: `VuotAiRemainingAttempts = 1550`, `SongJinBattleScore = 747`).
2. **Biểu Đồ Nhịp `MissionTimer` (Tính Chính Xác Ra Số Frame `18 Hz`)**:
   - Trong Lua gốc: `StartMissionTimer(missionId, timerId, intervalTicks)` — lưu ý trong engine `jx_linux_y`, tham số `interval` của `StartMissionTimer` tính trực tiếp bằng **số game frames (18 frames = 1 giây)**! Bắt buộc kiểm tra hằng số `TIMER_1 = 20 * 18` hay `60 * 18` trong file `head.lua` của mission đó.
3. **Luật Vào / Ra, Rớt Mạng & Bảo Toàn Trạng Thái (`Join / Leave / Disconnect / Death`)**:
   - Điều kiện báo danh (cấp độ, phí ngân lượng, vật phẩm lệnh bài như `Sát Thủ Giản` cho Vượt Ải, giới hạn số người mỗi phòng/phe).
   - Trạng thái áp đặt khi vào phòng: `SetFightState`, `SetPunish(0)` (chết không rớt kinh nghiệm/tiền!), `SetPKFlag`, `SetTmpCamp`, khóa hay mở `TownPortal` (`Thổ Địa Phù`), điểm hồi sinh nội bộ doanh trại.
   - Bắt buộc khôi phục sạch `Camp`, `FightState`, `Punish` khi rời phó bản hoặc ngắt kết nối đột ngột!
4. **Cấu Trúc Đợt Quái / Boss & Công Thức Tính Điểm (`Waves, NPCs & Scoring`)**:
   - Đọc bảng `.txt` danh sách quái từng ải / từng cấp chiến trường (Sơ cấp `40–59`, Trung cấp `60–79`, Cao cấp `80+`), điều kiện gọi Nguyên Soái / Boss ải cuối, công thức cộng điểm hạ NPC vs hạ người chơi liên trảm.
5. **Bẫy Nội Khu (`Battlefield Camp Traps`)**:
   - Các bẫy ngăn người chơi lao khỏi hậu doanh trong giai đoạn Báo danh (`MS_STATE == 1`) hoặc bước từ hậu doanh (`FightMode = 0`) ra tiền tuyến (`FightMode = 1` + cấp `ProtectTime` chống bị camp cửa!).
6. **Gói Tin Bảng Điểm & Thông Báo Đặc Thù**:
   - Tống Kim: `s2c_battlefieldstatus` (`0xB0`), `s2c_battlefieldscoreboard` (`0xA1`).
   - Vượt Ải: `s2c_dungeonwaveclear` (`0x8C`).

---

## 2. Phương Pháp Kiểm Chứng Tua Nhanh Đồng Hồ 18-Hz (`Time-Warp Simulation Gate`)

Thực thi kiểm thử trên `ZoneSimulationHarness` (`IMissionController` + `MissionZoneTickSystem`) với tối thiểu **2 nhân vật mô phỏng** (hoặc 2 phe đối kháng):

1. **Bước 1 — Chặn Ngoài Biên & Báo Danh**:
   - Kiểm chứng nhân vật thiếu cấp / thiếu lệnh bài / khác phe bị từ chối.
   - Đưa nhân vật vào hậu doanh lúc `State == Signup`: xác nhận `FightMode == 0` và bước lên bẫy ranh giới bị đẩy lùi về hậu doanh (chưa cho ra chiến trường).
2. **Bước 2 — Tua Nhanh Qua Giai Đoạn Báo Danh (`StepFramesAsync(signupFrames)`)**:
   - Nhảy chính xác số frame báo danh (VD: `10 * 60 * 18 = 10,800 frames` — trên `ZoneSimulationHarness` chạy trong `< 150ms`!).
   - Kiểm chứng trường hợp **không đủ số người tối thiểu** (hủy trận, hoàn trả/đưa về điểm báo danh) VÀ trường hợp **đủ người khai chiến** (`State -> Active`).
3. **Bước 3 — Mô Phỏng Giao Tranh, Tử Vong Không Phạt & Chuyển Đợt (`Wave / Boss Progression`)**:
   - Cho nhân vật bước qua bẫy ra tiền tuyến (`FightMode == 1`, có `ProtectTime`), tiêu diệt hết quái Đợt `k` → kiểm chứng tự động sinh Đợt `k + 1` ở đúng frame quy định.
   - Cho nhân vật bị hạ gục trong phó bản → xác nhận **không mất EXP/ngân lượng** (`SetPunish(0)`) và hồi sinh đúng tọa độ hậu doanh (hoặc bị loại khỏi phòng với Vượt Ải).
4. **Bước 4 — Tua Nhanh Tới Mốc Kết Thúc (`StepFramesAsync(remainingBattleFrames)`)**:
   - Xác nhận tổng kết thưởng EXP/Điểm tích lũy chính xác theo công thức chia nguyên gốc, đẩy toàn bộ người chơi về bản đồ báo danh với `FightMode == 0` và `TmpCamp` đã trả về phe gốc.
