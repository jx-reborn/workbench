# Tổng Báo Cáo Càn Quét 5 Mặt Trận & Lộ Trình Hội Tụ Theo Đợt (`jx-reborn == jx-linux`)

Báo cáo tổng hợp kết quả chiến dịch khảo sát toàn diện `100%` Ground Truth (`legacy-server/bin/` + `legacy-server/discovery/`) do đội hình **5 Subagents chuyên trách** thực thi song song trên toàn bộ không gian bản đồ, cơ chế chiến đấu `18 Hz`, Ngũ Hành và Thập Đại Phái.

---

## 1. Danh Mục Hồ Sơ & Bộ Kiểm Toán Đã Bàn Giao

| Mũi | Báo cáo chi tiết | Bộ Kịch bản / Test Kiểm toán (`RED` + `GREEN`) | Kết quả Thực chạy |
| :---: | :--- | :--- | :--- |
| **1** | [01_towns_and_cities_gap_forecast.md](./01_towns_and_cities_gap_forecast.md) | [020_giang-tan-thon.scenario.json](../../qa/JXReborn.WireParityProxy/Scenarios/Maps/020_giang-tan-thon.scenario.json) + [walkthrough](../../qa/JXReborn.WireParityProxy/Scenarios/Maps/020_giang-tan-thon.walkthrough.md) | **`[PASS]`** `audit --strict` (`235` steps, `35/35` DialogNPCs, `12/12` Objects, `8/8` Traps, `3/3` ScriptedMonsters) |
| **2** | [02_world_maps_sects_and_instances_gap_forecast.md](./02_world_maps_sects_and_instances_gap_forecast.md) | Kiểm kê `952` map `.wor` (`669` active trong `s3relay/settings/worldset.txt`) | Phát hiện sổ cái cũ mới phủ `137/669` map active; phân hoạch MECE `16` Thôn/Thành + `88` Phái + `109` Luyện công + `739` Phó bản/Sự kiện |
| **3** | [03_combat_18hz_and_five_elements_gap_forecast.md](./03_combat_18hz_and_five_elements_gap_forecast.md) | [FiveElementsAndFrameBreakpointAuditTests.cs](../../tests/JXReborn.Workbench.ParityTests/Combat/FiveElementsAndFrameBreakpointAuditTests.cs) | Đã chạy trên `pack-tieudaotu` thật — khóa cứng công thức C++ và bắt trọn **8 lỗ hổng `RED` cơ chế lõi** |
| **4** | [04_sects_metal_wood_water_gap_forecast.md](./04_sects_metal_wood_water_gap_forecast.md) | [MetalWoodWaterSectSkillsAuditTests.cs](../../tests/JXReborn.Workbench.ParityTests/Skills/MetalWoodWaterSectSkillsAuditTests.cs) | Đã chạy trên `pack-tieudaotu` thật — kiểm toán **6 Phái Kim–Mộc–Thủy** (Thiếu Lâm, Thiên Vương, Đường Môn, Ngũ Độc, Nga Mi, Thúy Yên) |
| **5** | [05_sects_fire_earth_and_general_skills_gap_forecast.md](./05_sects_fire_earth_and_general_skills_gap_forecast.md) | [FireEarthSectSkillsAndSynergyAuditTests.cs](../../tests/JXReborn.Workbench.ParityTests/Skills/FireEarthSectSkillsAndSynergyAuditTests.cs) | Đã chạy trên `pack-tieudaotu` thật — kiểm toán **4 Phái Hỏa–Thổ** (Cái Bang, Thiên Nhẫn, Võ Đang, Côn Lôn) + Cộng hưởng & Khinh công |

> **Tổng hợp bộ Test Kiểm toán `L2` mới trong `JXReborn.Workbench.ParityTests`**: **`41` test cases** chạy trực tiếp trên dữ liệu thật của `pack-tieudaotu` (`9s`), trong đó **`14` bài `PASS` (`GREEN` — chứng minh parity)** và **`27` bài `FAIL` (`RED` — khóa chính xác từng lỗ hổng công thức/hành vi làm đề bài sửa lỗi không thể ăn gian)**.

---

## 2. Bức Tranh Định Lượng Toàn Thế Giới

1. **Không gian Bản đồ (`952` bản đồ `.wor` hợp lệ / `669` active trên `worldset.txt`)**:
   - **9 Thôn Trấn** (`20, 53, 54, 99, 100, 101, 121, 153, 174`): `311` `DialogNpc` (`93.5%` đã resolve, chỉ còn **`20` NPC `MISSING`** chia đều 2–7 NPC/thôn; `100%` `MapObject` `68/68`, `100%` `Trap` `70/70`, `100%` `ScriptedMonster` `26/26`). Trong đó `53`, `54`, `101` đã khóa `zero_drift_locked`.
   - **7 Đại Thành Thị** (`1, 11, 37, 78, 80, 162, 176`): `651` `DialogNpc` (`76.8%` đã resolve, còn **`151` NPC `MISSING`**; `108/108` `MapObject`, `59/59` `Trap`). Chỉ cần **5 module dùng chung** (`Vệ binh Thành môn`, `Sứ giả Liên Đấu`, `Tiếp Đầu Công Thành`, `Nha Dịch rửa PK`, `Dịch quan`) là đóng ngay **`~95/151` NPC** trên cả 7 thành!
   - **10 Đại Môn Phái + Hoa Sơn + Hoành Sơn (`88` map `.wor`)**: `117/150` `DialogNpc` (`78%`), `140/143` `MapObject` (`97.9%`), `230/232` `Trap` (`99.1%`), `149/155` `ScriptedMonster` (`96.1%`), `10/10` rương phái hoạt động. Tuy nhiên **`0/10` Chưởng Môn** đang hoạt động (`null` no-op).
   - **109 Map Luyện Công / Sơn Động / Mê Cung (`1x -> 9x`)**: `387/387` `Trap` (`100%`), `109/109` bảng `droprate` (`100%`, `36,702` quái thường), `300/302` `ScriptedMonster` (`99.3%`), nhưng còn kẹt 3 NPC chức năng sinh tử (`Lao đầu` Đại Lao `208`, `Vô danh Tăng` tẩy kỹ năng `55`, `Đạo Nhân Què` tẩy tiềm năng `140`) và thiếu spawn thật cho `160` Boss Sát Thủ + `22+` Đại Hoàng Kim.
   - **Chiến Trường & Phó Bản Đúng Giờ**: Tống Kim & Vượt Ải đạt `~15%` (mới có Controller RAM cô lập, chưa nối `MapZoneWorker`/NPC báo danh/spawn quái thật); Phong Lăng Độ, Liên Đấu, Công Thành Chiến, Viêm Đế, Thiên Trì ở mức `0% – 10%`.

---

## 3. Top 12 Lỗ Hổng Hệ Thống "Một Nhát Dao Giải Quyết Hàng Loạt" (High-Leverage Bugs)

### Khối A — Công thức Engine C++, Ngũ Hành & Kỹ Năng Thập Đại Phái (`server/` + `LegacyImporter`)
1. **`[P0 - 6 Phái 9x bị gãy tầng 2]` `SkillEffectiveParams.Resolve` bỏ qua `attrId = 267..270` (`skill_startevent`, `skill_flyevent`, `skill_collideevent`, `skill_vanishedevent`)**:
   - Làm **Thúy Yên `336 Băng Tung Vô Ảnh`** mất sen băng nổ tầng 2 (`382`), **Cái Bang `357 Phi Long Tại Thiên`** mất nổ `389 Long Chiến Ư Dã`, **Côn Lôn `372 Ngạo Tuyết Tiêu Phong`** mất lốc xoáy `373`, và **Võ Đang `368 Nhân Kiếm Hợp Nhất`** nổ nhầm ra lốc xoáy `373` của Côn Lôn ở cấp `1` thay vì kiếm khí `162` ở cấp `16+`!
2. **`[P0 - Giày tốc chạy 0% & Lệch gộp Âm/Dương]` `PlayerInventoryGrid.EquipmentStats.cs` thiếu `case 111` (`fastwalkrun_p`)**:
   - Giày có dòng `+40% tốc độ di chuyển` hiện cộng **0%** tốc chạy/đi bộ; đồng thời `PlayerZoneState` chưa gộp chung bể Âm/Dương giữa Trang bị và Kỹ năng trước khi lấy `Math.Max`.
3. **`[P0 - Dòng ẩn Ngũ Hành Trang Bị]` `GetEquipEnhance` sai điều kiện tương sinh hệ nhân vật & tắt sạch ô `10..14`**:
   - Nhân vật hệ Thổ cầm Vũ khí hệ Kim (khi chưa mặc Áo/Nhẫn hệ Thổ) đang bị tắt dòng ẩn 1; các ô `Ngựa, Mặt nạ, Phi phong, Ấn, Trang sức` (`10..14`) bị tắt cả 3 dòng ẩn thay vì mở đủ `3/3` như `jx_linux_y`.
4. **`[P0 - Nga Mi hụt 44.4% hồi máu & Dư Aura giả]`**:
   - `LifeReplenishFlat` / `ManaReplenishFlat` (`93 Từ Hàng Phổ Độ`, `89 Mộng Điệp`, `332 Phổ Độ Chúng Sinh`) đang tick mỗi **`18` frames** thay vì **`10` frames** (`KNpc::ProcessState`); `PartyAuraTickSystem` tự cộng thêm `+10% / +3% allres_p` giả không có trong binary gốc; và Võ Đang `157 Tọa Vọng Vô Ngã` chưa áp `+99% manashield_p` lên bản thân khi thi triển trong Zone.
5. **`[P0 - Vũ khí bắt buộc `EquipLimit`, `DoHurt`, Băng chậm, Độc sát & Chính xác/Né tránh cơ sở]`**:
   - Tay không vẫn tung được chiêu bắt buộc cầm Bổng/Thương/Kiếm/Đao (`CheckLimited`); `DoHurt` chưa chuyển `Doing = Hurt` và chưa chặn tung chiêu; Băng sát dùng sàn cứng `Math.Max(3, ...)` làm quái `WalkSpeed = 3` bị chậm `0%` thay vì bỏ frame lẻ `50%`; Độc không tắt khi chủ nhân chết; Né tránh cơ sở từ Thân pháp đang cao gấp 8 lần C++ (`(Dex*2 + Lv*2)` thay vì `Dex/4`); rớt 16 mã `MagicAttrib` trong `EntityCombatModifiers`.
6. **`[P0 - Khinh công `210` & Bùa `Ma Âm Phệ Phách` `715` & Điều kiện cộng điểm bùa 9x]`**:
   - `HandleJumpPlayerAsync` cho nhảy xa `3,008 mps` xuyên tường không giới hạn `480 mps` / thể lực; `RandMoveCount` của `Ma Âm Phệ Phách` đang là biến chết (không chạy loạn, không khóa tung chiêu); cộng điểm bùa `9x` (`391`, `394`, `390`, `351`) chưa kiểm tra `LevelUpScript`.
7. **`[P0 - Mất 3s Bất Tử khi qua cổng Trap sang Map Chiến đấu]`**:
   - `GeneratedPortalTrapScript` gọi `await TeleportPlayerAsync` (`ArrivalProtectTicks = 0`) trước `SetProtectTime(54)`, làm Zone đích xóa trắng khiên 3 giây ngay khi vừa đặt chân sang bản đồ quái.

### Khối B — Shared Wrappers, Thôn Trấn, Thành Thị & Môn Phái (`pack-tieudaotu/`)
8. **`[P0 - Bến Thuyền 8 Map đi nhầm Thiên Vương Đảo]`**: `FerryBoatmanDialogScript` chưa tra bảng `wharf.txt` + `wharfprice.txt` nên lái nhầm toàn bộ bến thuyền của Giang Tân, Long Môn và 6 thành thị sang Động Đình Hồ -> Thiên Vương Đảo.
9. **`[P0 - Lệch UI=12 vs UI=0 & Shop ID trên 7 Thành / 6 Thôn]`**:
   - `TownBlacksmithDialogScript` ở 7 thành phát `Say (UI=0)` thay vì `Describe (UI=12)` + thiếu khảm đồ xanh; `GuildGuideDialogScript` phát `Say` thay vì `Describe`; `HorseMerchantDialogScript` gán cứng `Sale(46)`; `TownGrocerDialogScript` thiếu nút `"Mua Kim thạch"` (`Sale 165`) & `"Mua thiệp chúc Tết"` (`Sale 148`) làm lệch `optionIndex`; `MapSignboardObjectScript` gán cứng thông tin Tương Dương.
10. **`[P0 - 6 Tân Thủ Thôn còn lại chỉ thiếu đúng 20 NPC nhiệm vụ]`**: Port 20 NPC này + sửa các Shared Wrapper ở mục #8–#9 sẽ mở khóa trọn vẹn **100% (8/8 Tân Thủ Thôn + Nam Nhạc Trấn)** đạt `zero_drift_locked`!
11. **`[P0 - 10 Chưởng Môn Phái, 3 Cổng thoát `MapObject` Mê cung & 3 NPC Đại Lao / Tẩy Tủy]`**: Khôi phục 10 Chưởng môn + sửa nhầm `SectMasterDialogScript`, nối 3 cổng thoát `DataID=39` (`Map 66, 67, 82`), và port `Lao đầu` (`Map 208`), `Vô danh Tăng` (`Map 55`), `Đạo Nhân Què` (`Map 140`).
12. **`[P1 - Đồng bộ Sổ cái `parity_master_ledger.json` lên đủ `669` Map Active]`**: Cập nhật `parity_ledger.py` nạp từ `gateway/s3relay/settings/worldset.txt` thay vì chỉ lát cắt `137` map của `server1/maps/worldset.ini`.

---

## 4. Lộ Trình Sửa Lỗi & Hội Tụ Đề Xuất (6 Đợt Khép Kín `RED -> GREEN -> Zero Drift`)

- **Đợt 1 — Cơ Chế Chiến Đấu 18-Hz, Ngũ Hành & Kỹ Năng 10 Phái (`server/` + `LegacyImporter`)**:
  - Chuyển toàn bộ **`27/27` bài test `RED`** vừa tạo trong `FiveElementsAndFrameBreakpointAuditTests`, `MetalWoodWaterSectSkillsAuditTests` và `FireEarthSectSkillsAndSynergyAuditTests` sang **`41/41 GREEN`** (sửa `attr 267..270`, `case 111` tốc chạy giày, dòng ẩn Ngũ hành trang bị, nhịp `10f` hồi máu Nga Mi, xóa aura giả, `Tọa Vọng Vô Ngã`, `EquipLimit`, `DoHurt`, băng chậm frame lẻ, độc sát, AR/Né tránh Thân pháp, `Khinh Công`, `Ma Âm Phệ Phách`, `LevelUpScript`, và khiên `SetProtectTime(54)` qua cổng Trap).
- **Đợt 2 — Khóa Chuẩn Shared Wrappers & Hoàn Tất 100% Tân Thủ Thôn (`6` Thôn còn lại: `20, 99, 100, 121, 153, 174`)**:
  - Sửa dứt điểm 5 Shared Wrappers (`FerryBoatman`, `TownBlacksmith`, `TownGrocer`, `GuildGuide`, `HorseMerchant`, `CityBulletin`) + port `20` NPC nhiệm vụ thôn -> chạy `020_giang-tan-thon` và 5 thôn còn lại qua Live `WireParityProxy` `T=0` đạt `0` Critical Findings.
- **Đợt 3 — 10 Trụ Sở Đại Môn Phái + Hoa Sơn & 3 NPC Tẩy Tủy / Đại Lao**:
  - Port 10 Chưởng Môn + Độc Cô Kiếm, 2 Đệ tử bán vũ khí phái, 19 Trưởng lão nhiệm vụ phái, 3 cổng thoát `MapObject` đáy giếng/đáy hồ, và 3 NPC `Lao đầu` / `Vô danh Tăng` / `Đạo Nhân Què`.
- **Đợt 4 — 7 Đại Thành Thị (`1, 11, 37, 78, 80, 162, 176`)**:
  - Port 5 module dịch vụ thành thị dùng chung (`~95` NPC) + hoàn tất `078_tuong-duong` sang `zero_drift_locked`, sau đó cuốn chiếu 6 thành còn lại.
- **Đợt 5 — Bản Đồ Luyện Công `1x–9x`, 160 Boss Sát Thủ (`Nhiếp Thí Trần`) & Boss Hoàng Kim `s3relay`**:
  - Nối spawn động 160 Boss Sát Thủ + rơi `Sát Thủ Giản` + hợp thành Ngũ Hành và lịch trình Đại/Tiểu Hoàng Kim từ `s3relay`.
- **Đợt 6 — Chiến Trường & Phó Bản Theo Giờ (`Tống Kim`, `Vượt Ải`, `Phong Lăng Độ`, `Liên Đấu`, `Công Thành Chiến`)**:
  - Nối `SongJin` / `VuotAi` vào `MapZoneWorker` thực chiến với fast-forward clock `18 Hz` theo skill `khao-sat-chien-truong-pho-ban`.
