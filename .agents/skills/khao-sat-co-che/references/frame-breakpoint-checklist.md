# Danh Mục Kiểm Tra Cảm Giác Chơi 18-Hz & Công Thức C++ (`Frame Breakpoint Checklist`)

Tài liệu tham chiếu cho skill `khao-sat-co-che` nhằm đảm bảo không bỏ sót các chi tiết vi mô tạo nên "chất" đặc trưng của `jx-linux` (18 ticks/giây = `55.55ms/tick`).

---

## 1. Nhịp Khung Hình 18-Hz (`Tick & Action Cadence`)

- **Tốc độ đánh & Xuất chiêu (`AttackSpeed` / `CastSpeed`)**:
  - Kiểm tra chính xác công thức tính số frame chờ giữa 2 đòn đánh (`nWaitTime` / `m_nNextActionFrame`) theo vũ khí và phần trăm tốc đánh nội/ngoại công.
  - Test tại các cặp giá trị sát ranh giới làm tròn (VD: tốc đánh làm thời gian hồi giảm từ `13 frames` xuống `12 frames`) bằng `ZoneSimulationHarness.StepFramesAsync(1)` đếm đúng frame phát gói `0x57` / `0x87` / sinh `KMissle`.
- **Quỹ đạo & Va chạm của Đạn (`KMissle`)**:
  - Frame khởi tạo (`PrePareFly` vs bay ngay), tốc độ dịch chuyển mỗi tick theo hướng lượng giác bảng tra cố định (`g_nSin` / `g_nCos` fixed-point của Kingsoft, không dùng `Math.Sin` dấu phẩy động gây lệch 1–2 mps ở tầm xa!), bán kính va chạm, số lần xuyên thấu / nổ tầng 2 (`OnMissleEvent`), và cờ `DoVanish` im lặng.
- **Di chuyển, Chạy bộ & Khinh công (`DoWalk` / `DoRun` / `DoJump`)**:
  - Tốc độ `WalkSpeed` / `RunSpeed` nhân với hệ số bùa tăng/giảm tốc (như Băng sát làm chậm, Phong Sương Toái Ảnh, Thảo Thượng Phi), tiêu hao Thể lực (`Stamina`) mỗi nhịp chạy, và khóa hành động trong suốt số frame bay trên không khi Khinh công.

---

## 2. Toán Học Chiến Đấu, Ngũ Hành & Trạng Thái (`Combat & AttribModify`)

- **5 nhánh Sát thương & Kháng tính**:
  - Vật lý (`Physics`), Băng (`Cold` + thời gian làm chậm), Hỏa (`Fire`), Lôi (`Light`), Độc (`Poison` — sát thương rút máu theo chu kỳ `nInterval` frames và cộng dồn thời gian độc!).
  - Thứ tự áp dụng: sát thương cơ bản vũ khí → phần trăm vật lý (`add_physics_damage_p`) → điểm vật lý (`add_physics_damage_v`) → tương khắc Ngũ hành → trừ kháng mục tiêu (kẹp biên âm/dương theo đúng `KNpc::ReceiveDamage`) → suy giảm PK (`m_nPKDamageRate` khi đánh người chơi khác!).
- **Giật hình (`DoHurt` Recoil) & Bất tử tạm thời (`ProtectTime`)**:
  - Xác suất và thời gian khựng đòn `DoHurt` (so với thuộc tính Thời gian phục hồi `fasthitrecover_v`), miễn giật hình khi cưỡi ngựa hoặc có khiên đặc thù.
  - `ProtectTime` khi vừa chuyển map / hồi sinh phải thực sự **chặn sát thương = 0 và không dính hiệu ứng khống chế**, đồng thời tự hủy ngay khi người chơi chủ động tung chiêu tấn công.
- **Bất biến Cộng / Trừ Thuộc tính (`KNpcAttribModify` Symmetry)**:
  - Mọi thuộc tính trang bị, vòng sáng tổ đội (`Aura`), và thuốc lắc/bùa chú khi mặc vào rồi tháo ra (hoặc hết hạn frame) phải trả chỉ số nhân vật về **chính xác 100% giá trị ban đầu** (`0` rò rỉ cộng dồn vĩnh viễn).

---

## 3. Hành Trang, Tương Tác 2 Người & Ranh Giới An Toàn

- **Lưới Túi đồ & Chuyển chỗ 3 bước (`ExchangeItemHand`)**:
  - Kích thước lưới `EquipRoom` (`6x6` hoặc `6x10` theo cấu hình), tự động tìm ô trống đầu tiên theo đúng chiều quét dòng/cột của `KItemList::FindFreeCell` để gói `S2C` đặt vật phẩm trùng khít ô với `game.exe`.
- **Tổ đội, Giao dịch & Bày bán (`Multi-Actor Atomicity`)**:
  - Chia điểm kinh nghiệm trong tầm AOI theo khoảng cách và số thành viên; đổi vòng sáng đội trưởng cập nhật tức thời lên đồng đội.
  - Khóa 2 pha khi giao dịch (`Lock -> Confirm`): bất kỳ thay đổi vật phẩm/tiền nào của đối phương phải lập tức mở khóa xác nhận, và ngắt kết nối giữa chừng không bao giờ làm mất hoặc nhân đôi vật phẩm.
- **PK & Cừu sát (`Camp & PK State Machine`)**:
  - Đếm ngược `3 phút` (`3 * 60 * 18 = 3240 frames`) đổi trạng thái Đồ sát ngoài thành; đếm ngược `200 frames` (~11 giây) chờ Cừu sát trước khi chuyển sang đánh được nhau; tự động xóa Cừu sát khi một trong hai bước qua cổng thành vào vùng `FightMode == 0`.
