# Khảo Sát & Dự Báo Lỗ Hổng Parity Toàn Diện (Báo Cáo 02): Trụ Sở Môn Phái, Bản Đồ Luyện Công/Động Huyệt & Hệ Thống Chiến Trường – Phó Bản

> **Phạm vi kiểm toán**: Toàn bộ thế giới bản đồ hoạt động ngoài 16 Thôn trấn / Thành thị chính (`1, 11, 20, 37, 53, 54, 78, 80, 99, 100, 101, 121, 153, 162, 174, 176`), bao gồm:
> 1. Đối chiếu danh mục bản đồ giữa `legacy-server` (`worldset.ini`, `s3relay/settings/worldset.txt`, `maplist.ini`, `maps.pak`), `pack-tieudaotu` (`data/maps/*.map.json`), `server` (`MapCatalog`), và `workbench` (`parity_master_ledger.json`, `MINIMAL_TEST_MAPS`).
> 2. **Cụm A**: 10 Đại Môn Phái + Hoa Sơn (`2, 212, 333`) + Hoành Sơn (`56, 57, 58`) — tổng cộng **88 bản đồ `.wor` hợp lệ** (+ 13 bản đồ nội thất khuyết `.wor` gốc).
> 3. **Cụm B**: Toàn bộ **109 bản đồ Luyện công, Sơn mạch, Động huyệt & Mê cung (`1x -> 9x`)**, hệ thống `Trap` chuyển bản đồ (`SetProtectTime`), quái nhiệm vụ `OnDeath`, **160 Boss Sát Thủ** (`Nhiếp Thí Trần`), **Boss Hoàng Kim** (`s3relay`), và bảng rơi đồ `droprate`.
> 4. **Cụm C**: Toàn bộ **739 bản đồ Chiến trường, Phó bản & Sự kiện Đúng Giờ** (`Tống Kim`, `Vượt Ải`, `Phong Lăng Độ`, `Liên Đấu WLLS`, `Viêm Đế Bảo Tàng`, `Thiên Trì Mật Cảnh`, `Công Thành Chiến & Thất Thành Đại Chiến`, `Tỷ Võ Lôi Đài`, `Tín Sứ`, `Phong Hỏa Liên Thành`, `Đào Hoa Đảo / Tẩy Tủy Đảo`, `Lãnh Địa Bang Hội`).

---

## 1. Đối Chiếu Tổng Thể Danh Mục Bản Đồ (`Worldset` vs `Maps.pak` vs `Parity Ledger`)

### 1.1. Bảng Số Liệu Đối Chiếu Xuyên Hệ Thống (100% Ground Truth)

| Nguồn cấu hình / Hệ thống | Đường dẫn Ground Truth | Tổng dòng khai báo | ID Duy nhất (Unique) | Có `.wor` hợp lệ trong `maps.pak` | Khuyết `.wor` gốc | Thuộc 16 Thôn/Thành (Subagent 1) | Còn lại thuộc phạm vi Báo cáo 02 (`.wor` hợp lệ) |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **GameServer `worldset.ini`** (chỉ cụm GS3+GS4) | [worldset.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/maps/worldset.ini) | 139 (`Count=139`) | **136** *(trùng `57, 78, 209`)* | 132 | **4** (`88, 89, 107, 108`) | 4 (`53, 78, 80, 101`) | **128** *(+4 khuyết `.wor`)* |
| **Sổ cái QA `parity_master_ledger.json`** | [parity_master_ledger.json](file:///d:/Projects/jx-reborn/workbench/qa/parity_master_ledger.json) | 137 | **137** *(= `worldset.ini` + Map `54`)* | 133 | **4** (`88, 89, 107, 108`) | 5 (`53, 54, 78, 80, 101`) | **128** *(tất cả `132` mục đều `pending`)* |
| **S3Relay Single-World `worldset.txt`** (`GAMESVR_SET_1=1`) | [worldset.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/settings/worldset.txt) | 669 | **669** | **669** | **0** | 16 (đủ `16/16`) | **653 bản đồ active thực tế** |
| **Danh mục gốc `maplist.ini` & `maps.pak`** | [maplist.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/maplist.ini) | 994 | **994** | **952** | **42** | 16 (đủ `16/16`) | **936 bản đồ có `.wor`** |
| **Content Pack `pack-tieudaotu` & `MapCatalog` Runtime** | [MapCatalog.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.GameAssets/WorldMaps/MapCatalog.cs#L301-L320) | 952 `*.map.json` | **952** | **952** | 0 | 16 | **936 `MapZoneWorker` đang chạy** |
| **Cấu hình Test Nhanh `MINIMAL_TEST_MAPS`** | [pack_legacy_profiles.py](file:///d:/Projects/jx-reborn/workbench/qa/scripts/pack_legacy_profiles.py#L22-L58) | 32 | **32** | **32** | 0 | 5 (`53, 54, 78, 80, 101`) | **27 bản đồ có mặt trong test profile** |

### 1.2. Phát Hiện Kiến Trúc Nghiêm Trọng Về "Điểm Mù Bản Đồ" (`Ledger Blind Spot`)

1. **Nguyên nhân `server1/maps/worldset.ini` chỉ có 136 bản đồ**:
   - Đối chiếu với [worldset0.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/settings/worldset0.txt) (cấu hình chia 8 tiến trình GameServer `GAMESVR_SET_8`), tệp [worldset.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/maps/worldset.ini) hiện tại chỉ là lát cắt gộp của **2 tiến trình GS3 + GS4** (chứa Ba Lăng, Tương Dương, Dương Châu, Long Tuyền, Thiếu Lâm, Cái Bang, Võ Đang), bỏ sót hoàn toàn 6 cụm GameServer còn lại!
   - Ngược lại, cấu hình thế giới đầy đủ cho 1 cụm máy chủ hoạt động nằm ở [worldset.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/settings/worldset.txt) với **669 bản đồ active** (`0` bản đồ lỗi `.wor`).
2. **`parity_master_ledger.json` đang mù `525` bản đồ active thực tế (và `808` bản đồ `.wor`)**:
   - Do [parity_master_ledger.json](file:///d:/Projects/jx-reborn/workbench/qa/parity_master_ledger.json) được khởi tạo từ `server1/maps/worldset.ini` (+ Map `54`), sổ cái QA hiện **bỏ sót hoàn toàn**:
     - **7/10 Đại Môn Phái + Hoa Sơn**: Nga Mi (`13`), Đường Môn (`25`), Thiên Nhẫn (`45, 49`), Thiên Vương (`59`), Côn Lôn (`131`), Thúy Yên (`154`), Ngũ Độc (`183`), Hoa Sơn (`2, 212, 333`).
     - **11/16 Thôn trấn & Thành thị lớn**: Phượng Tường (`1`), Thành Đô (`11`), Giang Tân (`20`), Biện Kinh (`37`), Vĩnh Lạc (`99`), Chu Tiên (`100`), Long Môn (`121`), Thạch Cổ (`153`), Đại Lý (`162`), Long Tuyền Phủ/Tuyền Châu (`174`), Lâm An (`176`).
     - **77/109 Bản đồ Luyện công & Động huyệt** (Cụm B), **32/40 Bản đồ Boss Sát Thủ**, cùng toàn bộ **Liên Đấu (`396..415, 539..559`)**, **Viêm Đế (`851..862`)**, **Thiên Trì (`934..974`)**.
   - Đồng thời, sổ cái lại chứa **4 ID bản đồ rỗng không tồn tại `.wor` trong `maps.pak`**: `88, 89` (Võ Đang Nhã thất 1–2) và `107, 108` (Thiếu Lâm Đệ tử phòng 1–2).
3. **Phân hoạch MECE 100% cho toàn bộ `952` bản đồ `.wor` hợp lệ**:
   - `16` Thôn trấn & Thành thị (Subagent 1) + `88` Bản đồ Môn phái (Cụm A) + `109` Bản đồ Luyện công / Động huyệt / Mê cung (Cụm B) + `739` Bản đồ Chiến trường / Phó bản / Sự kiện / Bang hội (Cụm C) = **952 bản đồ** (`0` trùng lặp, `0` bỏ sót).
   - Lưu ý vận hành: `JXReborn.Host` hiện nạp đồng loạt **cả 952 tệp `*.map.json`** vào `952` vòng lặp 18-Hz `MapZoneWorker` ngay khi khởi động ([ZoneEngineHostedService.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/ZoneEngineHostedService.cs)), gây tốn bộ nhớ cho ~283 bản đồ sự kiện/bang hội dự phòng không nằm trong `s3relay/settings/worldset.txt` (`669` map).

---

## 2. Cụm A — Kiểm Toán 10 Trụ Sở Đại Môn Phái, Hoa Sơn & Hoành Sơn (`88` Bản Đồ Hợp Lệ)

### 2.1. Tổng Hợp Toàn Cụm Theo Từng Môn Phái (Trụ Sở + Nội Điện + Động Thử Luyện)

> **Ghi chú kỹ thuật về `autoexec` NPC**: Trong tổng số `157` thực thể được `audit_map_scenario.py` xếp vào nhóm `DialogNpc` của Cụm A, có **6 thực thể sinh từ `autoexec.lua`** (`Không Tịch Đại Sư` Map 103, `Hàn Ngư Ông` Map 66, `3 Hà Hoa Đạo` Map 131, `Lư Thiện Tượng` Map 136) thực chất là **quái nhiệm vụ Hoàng Kim/Chính tuyến (`isFight=1`)** đã được xuất đúng `entityKind: "Monster"` trong [global-spawns.npcs.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/npcs/global-spawns.npcs.json) và đã có script `OnDeath` trong [GeneratedMonsterDeathScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Npcs/Monsters/GeneratedMonsterDeathScript.cs). Ngoài ra Map `52` dính 1 dòng rác `\script\vlus\npctanthu.lua` bị comment dở trong `autoexec.lua`. Do đó, độ phủ `DialogNpc` thực tế trên `Region_S` là **`117 / 150` (`78.0%`)**.

| Hệ phái / Cụm bản đồ | Số map `.wor` hợp lệ / Khai báo | Map khuyết `.wor` gốc | Có trong `Ledger` | Có trong `MINIMAL` | `DialogNpc` (Đã phủ / Tổng) | `MapObject` có script | `Trap` nối map / PK | `ScriptedMonster` (`OnDeath`) | Tổng quái thường |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Thiếu Lâm Phái** (`103..114`) | **10 / 12** | `107, 108` | 10 | 3 (`103,106,111`) | 10 / 13 *(1 autoexec)* | 36 / 36 | 27 / 27 | 14 / 14 | 1,641 |
| **Thiên Vương Bang** (`59..69`) | **9 / 11** | `63, 64` | 0 | 1 (`59`) | 23 / 25 *(1 autoexec)* | **9 / 11** | 17 / 17 | **12 / 13** | 1,069 |
| **Đường Môn** (`25..36, 197*`) | **13 / 13** | — | 0 | 0 | 12 / 15 | 12 / 12 | 28 / 28 | 27 / 27 | 950 |
| **Ngũ Độc Giáo** (`183..190, 196*`) | **9 / 11** | `191, 192` | 0 | 0 | 8 / 12 | 3 / 3 | 29 / 29 | 14 / 14 | 1,117 |
| **Nga Mi Phái** (`13..18`) | **6 / 6** | — | 0 | 0 | 11 / 15 | 6 / 6 | 16 / 16 | 13 / 13 | 597 |
| **Thúy Yên Môn** (`154..161`) | **5 / 8** | `156, 157, 159` | 0 | 0 | 9 / 12 | 21 / 21 | 14 / 14 | 9 / 9 | 1,653 |
| **Cái Bang** (`115..120`) | **6 / 6** | — | 6 | 2 (`115,116`) | 6 / 10 | 14 / 14 | 17 / 17 | 5 / 5 | 795 |
| **Thiên Nhẫn Giáo** (`45..52`) | **9 / 9** | — | 1 (`49`?) | 0 | 9 / 13 *(1 rác vlus)* | 8 / 8 | **20 / 21** | 12 / 12 | 581 |
| **Võ Đang Phái** (`81..90*`) | **7 / 9** | `88, 89` | 7 | 3 (`81,82,84`) | 8 / 13 | **9 / 10** | 17 / 17 | 15 / 15 | 1,334 |
| **Côn Lôn Phái** (`131..139`) | **8 / 10** | `133, 134` | 0 | 0 | 13 / 18 *(3 autoexec)* | 11 / 11 | 19 / 19 | 13 / 13 | 1,051 |
| **Hoa Sơn & Hoa Sơn Phái** (`2, 212, 333`) | **3 / 3** | — | 0 | 0 | 4 / 6 | 9 / 9 | **20 / 21** | **11 / 16** | 693 |
| **Hoành Sơn Phái** (`56, 57, 58`) | **3 / 3** | — | 1 (`56`) | 2 (`56,57`) | 4 / 5 | 2 / 2 | 6 / 6 | 4 / 4 | 181 |
| **TỔNG CỤM A (88 MAP)** | **88 / 101** | **13 map** | **25 / 88** | **11 / 88** | **117 / 157** (`74.5%`) | **140 / 143** (`97.9%`) | **230 / 232** (`99.1%`) | **149 / 155** (`96.1%`) | **11,662** |

### 2.2. Bảng Kiểm Kê Chi Tiết `audit_map_scenario.py inventory` Cho 20 Bản Đồ Trụ Sở & Thử Luyện Trọng Tâm

| Map ID | Tên bản đồ | Thư mục gốc Legacy | Trong `Ledger` | Trong `MINIMAL` | `DialogNpc` (Phủ/Tổng) | `MapObject` (Phủ/Tổng) | `Trap` (Phủ/Tổng) | `ScriptedMonster` (Phủ/Tổng) | Quái thường (Dải cấp) | Thực thể bị rớt Script (`null` no-op) |
| :---: | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **`2`** | Hoa Sơn Cảnh Kỹ Trường | `西北南区\华山` | Không | Không | **3 / 5** | 2 / 2 | 4 / 4 | **11 / 16** | 693 (`10–90`) | `Kim Sơn Chưởng Môn` (`kingofkingsoft.lua`), `Hoa Sơn Lão Tẩu` (`HSBattleManager.lua`), 5 `Kim binh` (`战斗_金兵.lua`) |
| **`333`** | Hoa Sơn Phái | `西北南区\华山派\华山派` | Không | Không | 1 / 1 | 7 / 7 | 16 / 16 | 0 / 0 | 0 | *(Đủ 100% thực thể hiện có)* |
| **`212`** | Hoa Sơn Tuyệt Đỉnh | `西北南区\华山绝顶` | Không | Không | 0 / 0 | 0 / 0 | **0 / 1** | 0 / 0 | 0 | 1 `Trap` `customScript` rỗng |
| **`103`** | **Thiếu Lâm Phái** | `中原北区\少林派\少林派` | Có | **Có** | **7 / 9** | 35 / 35 | 16 / 16 | 3 / 3 | 1,002 (`11–38`) | **`Trừng Mạc` bán vũ khí (`Shop 69`)** (`少林卖武器.lua`); *(Không Tịch Đại Sư là quái autoexec đã có OnDeath)* |
| **`109`** | Phương Trượng Thiền Phòng | `中原北区\少林派\方丈禅室` | Có | Không | **0 / 1** | 0 / 0 | 1 / 1 | 0 / 0 | 0 | **Chưởng môn `Phương Trượng Huyền Nhân`** (`路人_方丈玄因.lua`) |
| **`110`** | La Hán Đường | `中原北区\少林派\罗汉堂` | Có | Không | 1 / 1 | 0 / 0 | 2 / 2 | 0 / 0 | 0 | `Huyền Nan` đã phủ (`GeneratedNpcDialogScript`) |
| **`111`** | Mộc Nhân Hạng | `中原北区\少林派\木人巷` | Có | **Có** | 0 / 0 | 0 / 0 | 1 / 1 | 3 / 3 | 160 (`11–20`) | `157` Mộc nhân + `3` Cơ quan Nhân (`战斗_sll10_敌人01.lua`) đủ |
| **`113`** | Mật Thất Thiếu Lâm | `中原北区\少林派\少林密室` | Có | Không | 0 / 0 | 1 / 1 | 1 / 1 | 4 / 4 | 226 (`40–50`) | Đủ `OnDeath` & `Obj` |
| **`114`** | 108 La Hán Trận | `中原北区\少林派\一百零八罗汉阵` | Có | Không | 0 / 0 | 0 / 0 | 1 / 1 | 4 / 4 | 253 (`63–70`) | Đủ `OnDeath` & `Trap` |
| **`59`** | **Thiên Vương Đảo** | `两湖区\天王帮\天王帮` | Không | **Có** | **20 / 20** | 7 / 7 | 10 / 10 | 0 / 0 | 362 (`10–20`) | *(Trụ sở ngoài đủ 20/20, nhưng Chưởng môn Dương Anh nằm ở Tẩm Cung Map `62` bị `0/1`!)* |
| **`25`** | **Đường Môn** | `西南北区\唐门\唐门` | Không | Không | **8 / 8** | 10 / 10 | 14 / 14 | 18 / 18 | 891 (`1–30`) | *(Trụ sở ngoài đủ, nhưng `Đường Hạc` Map `31`, `Đường Cừu` Map `33`, `Đường Bất Nhiễm` Map `36` đều `0/1`!)* |
| **`183`** | **Ngũ Độc Giáo** | `江南区\五毒教\五毒` | Không | Không | **4 / 5** | 3 / 3 | 20 / 20 | 3 / 3 | 556 (`11–50`) | `Đệ tử Ngũ Độc giáo` nhập môn/30x (`路人_五毒弟子1.lua`); *(Chưởng môn Map `189`, Trưởng lão Map `185, 188` đều `0/1`)* |
| **`13`** | **Nga Mi Phái** | `西南北区\峨嵋派\峨嵋派` | Không | Không | **9 / 11** | 6 / 6 | 11 / 11 | 10 / 10 | 548 (`10–40`) | `Mục Vân Từ` (`emnpc09.lua`), `Hà Linh Phiêu` (`emnpc04.lua`); *(Chưởng môn Thanh Hiểu Map `17` & Diệu Như Map `15` đều `0/1`)* |
| **`154`** | **Thúy Yên Môn** | `西南南区\翠烟门\翠烟` | Không | Không | **7 / 8** | 21 / 21 | 10 / 10 | 7 / 7 | 1,333 (`10–39`) | **`Nhược Lan` bán vũ khí (`Shop 66`)** (`翠烟卖武器.lua`); *(Chưởng môn Doãn Hàm Yên Map `161` & Vô Danh Lão Giả Map `158` đều `0/1`)* |
| **`115`** | **Cái Bang** | `中原南区\丐帮\丐帮` | Có | **Có** | **6 / 10** | 1 / 1 | 6 / 6 | 1 / 1 | 257 (`21–29`) | **Chưởng môn `Hà Nhân Ngã`**, `Từ Tam Trượng` (10x/30x), `Đệ tử Cái Bang` (20x), `Ngụy Liễu Ông` (40x) |
| **`45`** | **Thiên Nhẫn Giáo (Ngoài)** | `中原北区\天忍教\天忍教室外` | Không | Không | **2 / 3** | 5 / 5 | 2 / 2 | 0 / 0 | 121 (`10–20`) | `Thiên Nhẫn Giáo đồ` nhập môn (`trnpc01.lua`) |
| **`49`** | **Thiên Nhẫn Giáo Tầng 3** | `中原北区\天忍教\天忍教室内3` | Không | Không | **7 / 9** | 2 / 2 | 3 / 3 | 0 / 0 | 0 | **Giáo chủ `Hoàn Nhan Hồng Liệt`** (`trnpc09.lua`), **Tả Hộ Pháp `Đoan Mộc Duệ`** (`trnpc08.lua`) |
| **`81`** | **Võ Đang Phái** | `中原南区\武当派\武当派` | Có | **Có** | **6 / 8** | 9 / 9 | 12 / 12 | 0 / 0 | 676 (`10–20`) | `Diệp Tiếp Mĩ` (`wdnpc03.lua`), `Chu Vân Tuyền` 90x (`wdnpc06.lua`) |
| **`82`** | Mê Cung Đáy Giếng | `中原南区\武当派\井底迷宫` | Có | **Có** | 0 / 0 | **0 / 1** | 0 / 0 | 6 / 6 | 438 (`10–20`) | **Cổng thoát `MapObject` `điểm đưa` (`dataId=39`)** trỏ vào `\trap\井底迷宫to武当派.lua`! |
| **`84`** | Tử Tiêu Đại Điện | `中原南区\武当派\紫霄大殿` | Có | **Có** | **0 / 1** | 0 / 0 | 1 / 1 | 0 / 0 | 0 | **Chưởng môn `Đạo Nhất Chân Nhân`** (`wdnpc02.lua`) |
| **`131`** | **Côn Lôn Phái** | `西北北区\昆仑派\昆仑派` | Không | Không | **11 / 14** *(11/11 Region_S)* | 4 / 4 | 11 / 11 | 0 / 0 | 560 (`10–20`) | *(Trụ sở ngoài đủ 11/11 `Region_S`, nhưng **Chưởng môn `Tuyền Cơ Tử`** ở Chính Điện Map `138` bị `0/1`!)* |

### 2.3. Phân Tích Chuyên Sâu Các Lỗ Hổng Nghiêm Trọng Của Cụm A

1. **Lỗ hổng A1 (P0 — Chặn đứng tiến trình môn phái): `0 / 10` Chưởng Môn Thập Đại Phái + Độc Cô Kiếm hoạt động!**
   - Toàn bộ 10 vị Chưởng môn Thập Đại Phái (`Phương Trượng Huyền Nhân` Map 109, `Dương Anh` Map 62, `Đường Cừu` Map 33, `Hắc Diện Lang Quân` Map 189, `Thanh Hiểu Sư Thái` Map 17, `Doãn Hàm Yên` Map 161, `Hà Nhân Ngã` Map 115, `Hoàn Nhan Hồng Liệt` Map 49, `Đạo Nhất Chân Nhân` Map 84, `Tuyền Cơ Tử` Map 138) + `Độc Cô Kiếm` (Map 57) đều sử dụng chuỗi `Include` phức tạp (nhiệm vụ Xuất sư 60x, Trùng phản sư môn `Task 39/40`, Đái nghệ đầu sư `daiyitoushi`, nhiệm vụ kỹ năng 90x/150x) nên bị bộ chuyển dịch AST bỏ qua, trả về `null` khi click!
   - **Lỗi gán nhầm `[LegacySource]` nghiêm trọng**: Tệp [SectMasterDialogScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Npcs/Sects/SectMasterDialogScript.cs#L9-L11) (`INpcDialogScript`) hiện đang bị gắn thuộc tính `[LegacySource(@"\script\江南区\雁荡山\羊角洞迷宫\npc\战斗_wul60_雁荡派掌门.lua")]` — vốn là script **`OnDeath` của con quái nhiệm vụ 60x Ngũ Độc** ở Dương Giác Động (`Map 196`), khiến:
     - Cả 10 Chưởng môn thật không ai gọi vào `SectMasterDialogScript`.
     - Ngược lại, con quái `Chưởng môn Nhạn Đãng phái` trên Map `196` bị thiếu `IMonsterDeathScript`!
2. **Lỗ hổng A2 (P1 — Đứt gãy chuỗi nhiệm vụ phái `10x -> 60x/90x` & 2 Shop Vũ Khí Phái)**:
   - **Rương chứa đồ (`OpenBox`) & Bán thuốc/trang bị**: `10/10` môn phái đều hoạt động tốt rương chứa đồ (`GeneratedStorageBoxObjectScript`) và `28/30` đệ tử bán thuốc/trang bị/vũ khí hoạt động. Riêng **2 NPC bán Vũ khí phái bị câm (`null`)**: `Trừng Mạc` (`Map 103`, `Shop 69`, dính nhiệm vụ `Task 131`) và `Nhược Lan` (`Map 154`, `Shop 66`).
   - **19 Trưởng lão / Đệ tử nhiệm vụ môn phái bị câm (`null`)**: Làm đứt gãy hoàn toàn chuỗi thăng cấp kỹ năng `10x–60x` và `90x` tại **8/10 môn phái** (Đường Môn `31, 36`; Ngũ Độc `183, 185, 188`; Nga Mi `13, 15`; Thúy Yên `158`; Cái Bang `115`; Thiên Nhẫn `45, 49`; Võ Đang `81, 85, 86`). Chỉ riêng Thiếu Lâm (trừ Chưởng môn & Trừng Mạc), Thiên Vương (trừ Chưởng môn) và Côn Lôn (trừ Chưởng môn) có đủ các Trưởng lão 10x–50x nhờ `GeneratedNpcDialogScript`.
3. **Lỗ hổng A3 (P1 — Kẹt nhân vật trong Mê cung thử luyện do `MapObject` cổng dịch chuyển trỏ vào `\trap\*.lua`)**:
   - Tại **Mê cung Đáy Giếng Võ Đang (`Map 82`)** và **Đáy Động Đình Hồ tầng 1–2 Thiên Vương (`Map 66, 67`)**, lối thoát không phải ô `Trap` dưới đất mà là vật thể tương tác `MapObject` **`điểm đưa` (`DataID=39`)** có `scriptPath` trỏ vào tệp `\trap\井底迷宫to武当派.lua` và `\trap\洞庭湖底1to洞庭湖底2.lua`.
   - Do transpiler đăng ký các tệp `\trap\*.lua` thành `ITrapScript` ([GeneratedPortalTrapScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Traps/GeneratedPortalTrapScript.cs)) thay vì `IObjInteractionScript`, khi người chơi click vào cổng `điểm đưa`, `TieuDaoTuScriptCodeResolver.ResolveObjScript` trả về `null` khiến người chơi **bị nhốt cứng trong đáy giếng / đáy hồ** nếu không có Thổ Địa Phù!
4. **Lỗ hổng A4 (Độ phủ `MINIMAL_TEST_MAPS`)**: Chỉ có `11/88` bản đồ phái nằm trong `MINIMAL_TEST_MAPS`, bỏ trống hoàn toàn **7 đại môn phái** (`Đường Môn, Ngũ Độc, Nga Mi, Thúy Yên, Thiên Nhẫn, Côn Lôn, Hoa Sơn`) và thiếu cả `La Hán Đường (110)`, `Mật thất (113)`, `108 La Hán Trận (114)`.

---

## 3. Cụm B — Bản Đồ Luyện Công, Sơn Mạch, Động Huyệt & Mê Cung (`1x -> 9x`, `109` Bản Đồ)

### 3.1. Phân Nhóm MECE Toàn Bộ `109` Bản Đồ Luyện Công / Động Huyệt Theo Dải Cấp Quái

| Dải cấp / Phân nhóm | Số map | Danh sách Map ID đầy đủ | Trong `Ledger` | Trong `MINIMAL` | `DialogNpc` (Phủ/Tổng) | `MapObject` (Phủ/Tổng) | `Trap` nối map | `ScriptedMonster` (`OnDeath`) | Tổng quái thường | Lỗi thiếu `droprate` |
| :--- | :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Khu Đặc Thù / Lao Ngục / Quá Cảnh** *(0 quái thường)* | **6** | `175, 177, 197, 208, 332, 343` *(Tây Sơn Tự, Thiên Tầm Tháp, Đại Lao, Thiên Long Tự...)* | 1 (`208`) | **0** | 1 / 12 *(10 autoexec Tàng Bảo Khách)* | 3 / 3 | 14 / 14 | 0 / 0 | 0 | **0** |
| **`1x` (`Lv 1–19`)** — Ngoại ô sơ cấp | **2** | `140` (Dược Vương Cốc), `195` (Nhạn Đãng Sơn) | 0 | **0** | 0 / 2 | 3 / 3 | 14 / 14 | 24 / 24 | 777 | **0** |
| **`2x` (`Lv 20–29`)** — Sơn mạch & Động 20 | **8** | `3, 7, 19, 43, 70, 71, 73, 179` *(Kiếm Các, Tần Lăng T1, Vũ Lăng Sơn, Phục Ngưu...)* | 0 | 1 (`70`) | 3 / 4 | 32 / 32 | 36 / 36 | 37 / 37 | 3,128 | **0** |
| **`3x` (`Lv 30–39`)** — Sơn mạch & Động 30 | **12** | `4, 8, 22, 74, 77, 90, 92, 141, 145, 146, 170, 193` *(Kim Quang, Bạch Thủy, Miêu Lĩnh, Phục Ngưu Đông, Thục Cương, Tiên Cúc...)* | 4 | 3 (`74,90,92`) | 0 / 4 *(4 autoexec)* | 29 / 29 | 63 / 63 | 76 / 76 | 4,339 | **0** |
| **`4x` (`Lv 40–49`)** — Động huyệt & Mê cung 40 | **13** | `5, 6, 21, 23, 41, 91, 122, 167, 168, 171, 172, 173, 180` *(Kinh Hoàng, Tỏa Vân, Thanh Thành, Thần Tiên, Kê Quán, Hoàng Hà Nguyên Đầu, Điểm Thương...)* | 2 | 1 (`122`) | 5 / 8 *(3 autoexec)* | 20 / 20 | 69 / 69 | **46 / 47** | 4,254 | **0** |
| **`5x` (`Lv 50–59`)** — Động sâu & Thiết Tháp 50 | **13** | `12, 24, 38, 39, 40, 42, 125, 147, 164, 165, 178, 182, 194` *(Địa Đạo Ác Bá, Hưởng Thủy, Thiết Tháp T1–T3, Thiên Tâm, Lưu Tiên, Thiên Tầm T1–T2, Nghiệt Long, Ngọc Hoa...)* | 2 | **0** | 1 / 3 *(2 autoexec)* | **3 / 7** | 30 / 30 | 34 / 34 | 2,573 | **0** |
| **`6x` (`Lv 60–69`)** — Mê cung 60 | **10** | `9, 79, 126, 127, 128, 148, 166, 196, 341, 342` *(Trường Giang Nguyên Đầu, Mật Đạo Tương Dương, Lưu Tiên T2–T4, Tuyết Báo T4, Thiên Tầm T3, Dương Giác, Mạc Bắc Thảo Nguyên, Vi Sơn Đảo)* | 6 | 2 (`79,341`) | 10 / 11 *(1 autoexec)* | 6 / 6 | 39 / 39 | **22 / 23** | 2,680 | **0** |
| **`7x` (`Lv 70–79`)** — Động cao cấp 70 | **7** | `72, 129, 142, 149, 163, 233, 319` *(Đại Từ, Lưu Tiên T5, Dược Vương T2, Tuyết Báo T5, Ác Bá Gia Trang, Đào Hoa Đảo Mật Đạo, Lâm Du Quan)* | 4 | **0** | 0 / 0 | 2 / 2 | 21 / 21 | 5 / 5 | 1,883 | **0** |
| **`8x` (`Lv 80–89`)** — Cao nguyên & Bờ Bắc 80 | **13** | `10, 76, 94, 123, 143, 150, 151, 169, 181, 206, 224, 320, 336` *(Nhạn Thạch, Sơn Bảo, Linh Cốc, Lão Hổ, Dược Vương T3, Tuyết Báo T6–T7, Lưỡng Thủy, Tần Lăng T2, Sa Mạc Địa Biểu, Chân Núi Trường Bạch, Phong Lăng Độ Bờ Bắc)* | 6 | 1 (`336`) | 0 / 7 *(1 autoexec + 6 Thuyền phu PLD)* | 6 / 6 | 52 / 52 | 0 / 0 | 4,876 | **0** |
| **`9x` (`Lv 90–95+`)** — Thánh địa Luyện công & Săn Boss | **25** | `55, 75, 93, 124, 130, 144, 152, 198, 199, 200, 201, 202, 203, 204, 205, 207, 225, 226, 227, 230, 231, 232, 321, 322, 340` *(Đào Hoa Nguyên, Khoả Lang, Tiến Cúc, Cán Viên, Lưu Tiên T6, Dược Vương T4, Tuyết Báo T8, Thanh Khê, Vũ Lăng, Cổ Dương, Băng Hà, Phù Dung, Xích Thủy, Thiên Nhẫn Mật Đạo, Điểm Thương Sơn, Sa Mạc Mê Cung 1–3, Trường Bạch Nam/Bắc, Mạc Cao Quật)* | 8 | 3 (`199,200,205`) | 8 / 9 | 12 / 12 | 49 / 49 | 56 / 56 | 12,192 | **0** |
| **TỔNG CỤM B (`109` MAP)** | **109** | — | **33 / 109** | **11 / 109** | **28 / 60** | **116 / 120** | **387 / 387 (`100%`)** | **300 / 302 (`99.3%`)** | **36,702** | **0 (`100%` hợp lệ)** |

### 3.2. Kiểm Toán Cơ Chế `Trap` Chuyển Bản Đồ (`NewWorld` + `SetPos` + `SetFightState` + `SetProtectTime`)

- **Độ phủ nối dây Trap**: Đạt **`387 / 387` (`100%`)** trên toàn bộ 109 bản đồ Cụm B (thông qua [GeneratedPortalTrapScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Traps/GeneratedPortalTrapScript.cs), [GeneratedGeneralTrapScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Traps/GeneratedGeneralTrapScript.cs) và bảng `portals` trong `*.map.json`).
- **LỖI ENGINE NGHIÊM TRỌNG B1 (P0 — Mất hoàn toàn 3 giây Bất Tử `SetProtectTime(54)` khi qua cổng liên bản đồ bằng Script)**:
  - Trong Ground Truth Legacy (`872` tệp `trap/*.lua`), các cổng xuất thành / sang bản đồ chiến đấu đều gọi bộ 4 hàm: `NewWorld(mapId, x, y)` + `SetFightState(1)` + `SetProtectTime(18*3)` (`54` ticks = 3 giây) + `AddSkillState(963, 1, 0, 18*3)`.
  - Trong [GeneratedPortalTrapScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Traps/GeneratedPortalTrapScript.cs#L15-L31), trình tự sinh mã hiện tại là:
    1. Gọi `await context.TeleportPlayerAsync(entry.DestMapId, ...)` -> đi qua [ZoneScriptTrapContext.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Scripting/ZoneScriptTrapContext.cs#L24-L45) -> gọi `ZoneClusterRouter.TryTransferPlayerAcrossMapsAsync(..., arrivalProtectTicks: 0)` (mặc định `0`!).
    2. Tại Zone đích, `MapZoneWorker.HandleAttachTransferredPlayer` ([MapZoneWorker.Session.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/MapZoneWorker.Session.cs#L230-L241)) nhận `cmd.ArrivalProtectTicks == 0` nên gán cứng **`state.SpawnProtectUntilTick = 0`** và bỏ qua `ApplyTeleportProtectSkillState`!
    3. Sau khi `await TeleportPlayerAsync` trả về, dòng `context.SetProtectTime(54)` ở `GeneratedPortalTrapScript` mới chạy nhưng lúc đó **chỉ ghi vào `PlayerZoneState` cũ đã bị detach ở Zone nguồn**, hoàn toàn vô tác dụng ở Zone đích!
  - **Hệ quả**: Mọi dịch chuyển qua `GeneratedPortalTrapScript`, `GeneratedGeneralTrapScript`, `Xa Phu`, `Thuyền Phu` ra vùng PK/quái dày đều có **`0` tick bảo vệ**, khiến nhân vật có thể bị quái hoặc người chơi đồ sát đánh chết ngay trong lúc màn hình đang nạp bản đồ! (Chỉ nhánh fallback đọc tĩnh `portal.DestinationMapId` trong [MapZoneWorker.Movement.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/MapZoneWorker.Movement.cs#L430-L438) truyền `arrivalProtectTicks: 54`, nhưng các trap có script đều đi vào nhánh script trước!).

### 3.3. Kiểm Toán Rơi Đồ (`Droprate`), Quái Nhiệm Vụ `OnDeath` & NPC Chức Năng Hiếm Ngoại Ô

1. **Bảng rơi đồ `droprate` (`100%` Đạt chuẩn)**: Đối chiếu toàn bộ `36,702` điểm spawn quái thường trên 109 bản đồ Cụm B với [droprates.loot.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/loot/droprates.loot.json) (`lootTables`): **`0` mã quái bị đứt gãy `lootCode`**!
2. **Quái nhiệm vụ `OnDeath`**:
   - Trên `Region_S`: Đạt **`300 / 302` (`99.3%`)** thông qua `GeneratedMonsterDeathScript` và `TieuDaoTuGlobalNpcDeathScript`. Chỉ sót **2 con**: `'Đá luyện công'` (`Map 41` Phục Ngưu Sơn Tây, `战斗_试功碑.lua`) và `'Chưởng môn Nhạn Đãng phái'` (`Map 196` Dương Giác Động, do bị gán nhầm sang `SectMasterDialogScript` như đã nêu ở Mục 2.3).
   - Trên `autoexec.lua`: Toàn bộ **338 nhóm quái nhiệm vụ Hoàng Kim Chính tuyến / Phụ tuyến** (`enemy_*.lua`, `战斗_*.lua`) đã được xuất đúng `entityKind: "Monster"` trong [global-spawns.npcs.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/npcs/global-spawns.npcs.json) và nạp vào `autoExecSpawns` ([GameAssetsSnapshotLoader.NpcsAndPlayers.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.GameAssets/Snapshots/GameAssetsSnapshotLoader.NpcsAndPlayers.cs#L216-L308)).
3. **3 NPC Chức Năng Cốt Lõi Ngoại Ô / Lao Ngục Đang Bị Câm (`null`)**:
   - **Tẩy tủy Kỹ năng miễn phí / Thủy Tinh**: `'Vô danh Tăng'` tại Đào Hoa Nguyên (`Map 55`, `\script\两湖区\桃花源\npc\桃花源_洗点的叛僧.lua`) -> `null`.
   - **Tẩy tủy Tiềm năng bằng Tử Thủy Tinh**: `'Đạo Nhân Què'` tại Dược Vương Cốc (`Map 140`, `\script\西北北区\药王谷\药王谷\npc\洗潜能的道士.lua`) -> `null`.
   - **Rửa PK & Thả tù Đại Lao (`Timer 9`)**: `'Lao đầu'` tại Đại Lao (`Map 208`, `\script\global\洗pk的牢头.lua`) -> `null` (người chơi bị tống giam vào Map `208` sẽ **không bao giờ ra tù được**!).

### 3.4. Hệ Thống Boss Sát Thủ (`Nhiếp Thí Trần` — `160` Boss Trên `40` Bản Đồ)

- **Ground Truth Legacy** (`script/task/tollgate/killer/` — 6 tệp Lua & `settings/task/tollgate/killer/killer.txt`):
  - Gồm **8 bậc cấp độ (`Lv 20, 30, 40, 50, 60, 70, 80, 90`) x 20 Boss/bậc = `160` Boss Sát Thủ**, phân bổ trên **40 bản đồ động huyệt/sơn mạch** (`5` bản đồ mỗi bậc, tối đa 8 nhiệm vụ/ngày `Task 1192`, `Task 1082` lưu ID Boss `1..160`).
  - Khi hạ gục đúng Boss (`kill_level.lua`), người chơi (hoặc đồng đội đứng cùng tổ đội trên bản đồ) hoàn thành nhiệm vụ, nhận **Sát Thủ Giản** `(6, 1, 400, level)` (dùng ghép Ngũ Hành hợp thành **Lệnh Bài Vượt Ải**), xác suất rớt **Sát Thủ Bí Bảo** và trang bị xanh dòng cao.
- **Hiện trạng JX Reborn (`P0` Gap — Stub rỗng không chơi được)**:
  1. [NieShiChenDialogScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Npcs/Tasks/NieShiChenDialogScript.cs) mới chỉ là bản rút gọn 81 dòng (`1/6` tệp Lua): ép gán cứng 1 ID cố định mỗi bậc (`1, 21, 41, 61, 81, 101, 121, 141`) thay vì cho chọn danh sách 20 Boss mỗi cấp, và **thiếu hoàn toàn các dòng hội thoại báo danh Vượt Ải / Ghép Sát Thủ Giản**.
  2. **Không hề spawn bất kỳ Boss Sát Thủ nào trên thế giới**: Tệp `killbosshead.lua` / tọa độ spawn 160 Boss chưa được port; tệp [killer-boss-settings.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/events/killer-boss-settings.json) lại xuất nhầm từ `settings/task/killer/killer.ini` + `adventure.txt` (truy nã PK cũ) thay vì bảng `160` Boss Sát Thủ!
  3. **Không có `OnDeath` trả thưởng**: Chưa port `kill_level.lua`, không rơi `Sát Thủ Giản`, làm đứt luôn nguồn nguyên liệu vào phó bản Vượt Ải.
  4. Độ phủ kiểm thử: Chỉ có **`8 / 40`** bản đồ Boss Sát Thủ trong `parity_master_ledger.json` và **`1 / 40`** (`Map 56`) trong `MINIMAL_TEST_MAPS`.

### 3.5. Hệ Thống Boss Hoàng Kim (`s3relay` Scheduled World Bosses)

- **Ground Truth Legacy** ([tasklist.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/relaysetting/task/tasklist.ini)):
  - **Đại Hoàng Kim Môn Phái (`Task_21, 22, 23` — `08:00`, `19:30`, `22:00`)**: [makegoldboss_1930.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/relaysetting/task/makegoldboss_1930.lua) gọi `boss_make_one_only` từ bảng tỉ lệ xác suất và tệp tọa độ ngẫu nhiên `.txt` cho hơn **22 Đại Boss Hoàng Kim** (`Cổ Bách, Huyền Giác, Đường Bất Nhiễm, Bạch Doanh Doanh, Thanh Tuyệt Sư Thái, Chung Linh Tú, Hà Nhân Ngã, Đoan Mộc Duệ, Đạo Thanh Chân Nhân, Tuyền Cơ Tử, Vương Tá, Huyền Nan, Lam Y Ngọc, Mạnh Thương Lương...`) trải khắp ~20 động huyệt 9x & 7 thành thị, kèm thông báo kênh thế giới `AddGlobalCountNews`.
  - **Tiểu Hoàng Kim (`Task_30, 31, 32` — `04:00`, `12:30`, `20:00`, `23:00`)**: [small_goldboss_2000.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/relaysetting/task/small_goldboss_2000.lua) gọi 10 Tiểu Boss (`Diệu Như, Liễu Thanh Thanh, Trương Tông Chính...`).
  - **Đại Boss Độc Cô Thiên Phong (`Task_25` — `19:45`)**: [make_new_big_boss.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/gateway/s3relay/relaysetting/task/make_new_big_boss.lua).
- **Hiện trạng JX Reborn (`P1` Gap — Placeholder cứng)**:
  - [WorldBossScheduledSpawnerTickSystem.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Systems/WorldBossScheduledSpawnerTickSystem.cs) và [BossTemplateCatalog.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.GameAssets/Catalogs/BossTemplateCatalog.cs) hiện đang **hardcode 11 Boss tên tiếng Anh giả lập (`"World Boss 1" .. "World Boss 11"`)** tại duy nhất **1 tọa độ cố định `(50304, 103168)`** trên 6 bản đồ (`321, 322, 336, 340, 341, 342`), hoàn toàn không nối với [golden-boss-schedules.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/events/golden-boss-schedules.json) (vốn cũng mới chỉ chứa chỉ số `goldboss_adjust` chứ chưa xuất danh sách lịch `s3relay` & bảng tọa độ `.txt`) và chưa có Tiểu Hoàng Kim hay Độc Cô Thiên Phong.

---

## 4. Cụm C — Chiến Trường, Phó Bản & Sự Kiện Đúng Giờ (`Missions & s3relay` — `739` Bản Đồ)

### 4.1. Ma Trận Đối Chiếu Toàn Diện 12 Hệ Thống Chiến Trường, Phó Bản & Sự Kiện Định Kỳ

| Hệ thống Chiến trường / Phó bản | Nguồn Ground Truth (`server1` + `s3relay`) | Số Map `.wor` (Trong `Ledger` / `MINIMAL`) | Tệp Lua Đã Port (`[LegacySource]`) | Hiện trạng Engine (`JXReborn.ZoneEngine`) & Content Pack (`pack-tieudaotu`) | Mức độ Hoàn thiện Thực Tế |
| :--- | :--- | :---: | :---: | :--- | :---: |
| **1. Chiến Trường Tống Kim** (`Song-Jin`) | `script/battles/` (6 phương thức: Cừu Sát, Bảo Vệ Nguyên Soái, Đoạt Cờ, Hộ Lương, Đấu Tướng, Quốc Chiến) + `s3relay` `battle_1/2.lua` | **52 map** (`17` / `3`: `324,327,345`) | **`0 / 161`** *(+1 trap rời `SongJinSignupTrapScript`)* | Có [SongJinMissionController.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Missions/SongJin/SongJinMissionController.cs) (`Spec 027`) tính giờ & điểm trong RAM, nhưng **KHÔNG nối với `MapZoneWorker.Combat`** (`HandlePlayerDeath`/`HandleNpcDeath`), **0 NPC `Mộ Binh Quan` / `Quân Nhu Quan`** ở Báo danh `323–325`, **0 đợt lính/Boss/Nguyên Soái** (`0/3,188` `ScriptedMonster`), thiếu 5/6 chế độ chơi! | **~15%** *(Chỉ có khung Controller cô lập)* |
| **2. Vượt Ải — Thách Thức Thời Gian** (`Challenge of Time`) | `missions/challengeoftime/` (`464..495`: Sơ cấp `464–479`, Cao cấp `480–495`, 28 ải + Mật phòng ải 29 `<18p`) + `s3relay` `challengeoftime.lua` | **32 map** (`3` / `1`: `468`) | **`0 / 18`** | Có [VuotAiMissionController.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Missions/VuotAi/VuotAiMissionController.cs) & [VuotAiWaveCatalog.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Missions/VuotAi/VuotAiWaveCatalog.cs) (`Spec 028`), nhưng **KHÔNG spawn quái thật lên `MapZoneWorker`**, dùng `TemplateId = 400 + (i % 10)` giả định, thưởng sai nhịp (mỗi ải chẵn thay vì ải 15/28/Mật phòng 29), và `Nhiếp Thí Trần` **chưa có menu báo danh Vượt Ải**! | **~15%** *(Chỉ có khung Controller cô lập)* |
| **3. Phong Lăng Độ** (`Fengling Ferry`) | `missions/fengling_ferry/` (Thuyền `337, 338, 339` xuất bến mỗi giờ từ Bờ Nam -> Bờ Bắc `336`, `25p` hành trình, 3 đợt Boss Thủy Tặc `3p/9p/16p`) + `s3relay` `fengling_ferry.lua` | **3 map thuyền** + `336` (`3` / `0`) | **`0 / 11`** | **0% Mission Controller**, **6/6 Thuyền Phu bến Phong Lăng Độ (`Map 336`) bị `null`**, chưa trừ `Lệnh Bài Phong Lăng Độ (4,489)` / `Lệnh Bài Thủy Tặc`, chưa khóa Thổ Địa Phù trên thuyền, chưa gọi Thủy Tặc Đại Đầu Lĩnh. | **0%** |
| **4. Võ Lâm Liên Đấu** (`WLLS / LeagueMatch`) | `missions/leaguematch/` (8 thể thức theo tháng: Đơn đấu, Môn phái, Song đấu, Tam đấu, Sư đồ..., Chuẩn bị `396..415` + Đấu trường `539..559`) + `s3relay` `wlls_*.lua` (`LEAGUE` engine) | **41 map** (`0` / `0`) | **`4 / 46`** *(chỉ có Rương, Thuốc & 2 túi thưởng)* | **0% `IMissionController` & `LEAGUE` Control Plane**; NPC `Sứ Giả Liên Đấu` (`officer.lua` từ `wlls_autoexec.lua`) bị `null`, chưa có lập chiến đội, ghép cặp tính điểm thắng/hòa/sát thương, hay Shop Điểm Vinh Dự (`Task 2500/2501`). | **< 5%** |
| **5. Viêm Đế Bảo Tàng** (`Yandi Baozang`) | `missions/yandibaozang/` (`851..862`, 3 phe `A/B/C` x 10 ải Boss vượt chướng ngại tranh Boss cuối `Lương Mi Nhi`, vé `Anh Hùng Thiếp (6,1,1604)`) + `s3relay` `yandibaozang.lua` | **12 map** (`0` / `0`) | **`4 / 24`** *(chỉ có Thuốc & 3 trap cửa vào)* | NPC `Bình Bình Cô Nương` mới chỉ có hội thoại tĩnh cơ bản; **0% Mission Controller 3 nhánh 10 ải**, chưa spawn Boss ải hay cơ chế mở cửa sang ải trung tâm. | **< 5%** |
| **6. Thiên Trì Mật Cảnh** (`Tianchi Mijing`) | `missions/tianchimijing/` (`934..974`: Tầng 1–3 mê cung ngẫu nhiên, Tầng 4 Đại Boss `Hoàn Nhan Nghiêm`) | **41 map** (`0` / `0`) | **`0 / 46`** | **0% Mission Controller & NPC dẫn đường** (`0 / 24` `DialogNpc` được phủ). | **0%** |
| **7. Công Thành Chiến & Thất Thành Đại Chiến** (`CityWar & SevenCity`) | `missions/citywar_arena/`, `citywar_city/`, `citywar_global/` (`221..223` Chiến trường, `213..220` Lôi đài bang) & `missions/sevencity/` (`926..932` Thất Thành tối Thứ 6) + `s3relay` `citywar_*.lua`, `sevencity_*.lua` | **23 map** (`3` / `0`) | **`7 / 61`** *(chỉ có Bảng cáo thị, Thuốc, Rương, Ám đạo & 1 túi thưởng)* | Có xuất cấu hình tĩnh [citywar-settings.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/events/citywar-settings.json) và packet nhập số thuế [CityWarExtendPacketHandler.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Handlers/CityWarExtendPacketHandler.cs) (`0x82`), nhưng **0% Siege Mission Controller** (`Long Trụ`, `Thành Môn`, `Đầu Thạch Xa`, Lôi đài tranh Khiêu Chiến Lệnh, thu thuế thành thị động & Thất Thành Đại Chiến). | **~5%** |
| **8. Tỷ Võ Lôi Đài** (`BW Arena`) | `missions/bw/` (`209, 210, 211` Công Bình Tử Lôi Đài: `2p` báo danh + `10p` quyết đấu 1v1 / Tổ đội đặt cược + Khán giả `Camp 0`) | **11 map** (`1` / `0`) | **`1 / 10`** | [ArenaSparringMasterDialogScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/scripts/Npcs/Cities/ArenaSparringMasterDialogScript.cs) hiện bị giản lược thành gọi `StartExerciseSpar` (cừu sát luyện tập tại chỗ ngoài thành) thay vì mở phòng Lôi Đài `Map 209..211`, thiếu `Công Bình Tử`, đặt cược ngân lượng và chế độ Khán giả! | **~10%** *(Sai cơ chế bản đồ Lôi Đài)* |
| **9. Phong Hỏa Liên Thành** (`CityDefence`) | `missions/citydefence/` & `newcitydefence/` (`516..523`, thủ thành Kim/Tống cuối tuần) + `s3relay` `citydefence.lua` | **8 map** (`1` / `0`) | **`8 / 95`** *(chỉ có Rương, Quân Y, Xa phu ra & Trap)* | **0% Wave Defense Mission Controller** (chưa sinh quân công thành / Nguyên Soái thủ thành). | **< 5%** |
| **10. Nhiệm Vụ Tín Sứ** (`Messenger Tollgate`) | `script/task/tollgate/messenger/` (`387..395`: Phong Chi Kỵ, Sơn Thần Miếu, Thiên Bảo Khố) | **9 map** (`5` / `0`) | **`3 / 60`** *(chỉ có 3 trap)* | **`0 / 18` `DialogNpc` được phủ**, chưa có logic Dịch Quan giao nhiệm vụ Tín Sứ hay cơ quan mật thất. | **< 5%** |
| **11. Đào Hoa Đảo (Ủy Thác) & Tẩy Tủy Đảo (7x11 Động)** | `missions/clearskill/` & khu Đào Hoa Đảo (`235..318`: Luyện skill mộc nhân / Cọc gỗ thử kỹ năng sau tẩy điểm) | **84 map** (`24` / `0`) | Một phần qua `GeneratedNpcDialogScript` (`84 / 98` NPC) | Có NPC hội thoại tĩnh và `322 / 350` Trap, nhưng **thiếu logic `clearskill` Mission quản lý lưu trạng thái tẩy tủy miễn phí thử nghiệm** (ra đảo mới chốt điểm thật hoặc hoàn tác). | **~35%** |
| **12. Lãnh Địa Bang Hội & Sự Kiện `s3relay` Định Kỳ Khác** | Bang hội động (`584..604`+, `107` map) + `s3relay/relaysetting/task/tasklist.ini` (`18` cron task đang bật: `Quả Huy Hoàng` 12:00 / `Đêm Huy Hoàng` 19:30, `Hoa Đăng`, `Bảng Xếp Hạng Ladder`) | **155+ map** (`11` / `0`) | **`0%`** cho Bang địa & Quả Huy Hoàng / Hoa Đăng | `0 / 188` NPC Phường Tác Bang Hội được phủ; chưa có bộ lập lịch `s3relay` Control Plane cho **Quả Huy Hoàng (`great_seed.lua`)**, **Đoán Hoa Đăng (`lantern.lua`)**, và **RelayLadder (`ladderload.lua`)**. | **0%** |

---

## 5. Xếp Hạng Lỗ Hổng Ưu Tiên (`P0 -> P3`) & Dự Báo Lộ Trình Đóng Gap

### Bảng Xếp Hạng Ưu Tiên Kỹ Thuật

| Hạng | Mã Gap | Hạng mục Lỗ hổng Parity | Tác động Gameplay trực tiếp | Giải pháp Kiến trúc & Kiểm chứng TDD (`Zero Docker / Fast-Forward`) |
| :---: | :---: | :--- | :--- | :--- |
| **`P0`** | **GAP-B1** | **Mất 3 giây bất tử `SetProtectTime(54)` khi qua cổng liên bản đồ bằng Script** | Chết nhân vật oan ức ngay khi vừa bước qua cổng thành/động PK | Truyền `pendingProtectTicks` (hoặc cho phép truyền `arrivalProtectTicks` vào `TeleportPlayerAsync` / lưu trên session transfer) để `MapZoneWorker.HandleAttachTransferredPlayer` áp dụng đúng `54` ticks + `Skill 963`; khóa bằng `L2` multi-zone transfer test. |
| **`P0`** | **GAP-A1** | **`0 / 10` Chưởng Môn Thập Đại Phái + Độc Cô Kiếm hoạt động & gán nhầm `SectMasterDialogScript`** | Tắc nghẽn Xuất sư, Trùng phản sư môn, nhận nhiệm vụ 60x/90x/150x tại mọi môn phái | Gỡ `[LegacySource]` quái Map `196` khỏi `SectMasterDialogScript` (trả về `IMonsterDeathScript`), port đầy đủ 10 Chưởng môn + Độc Cô Kiếm + 2 NPC bán vũ khí Thiếu Lâm (`Shop 69`) & Thúy Yên (`Shop 66`). |
| **`P0`** | **GAP-A2/A3** | **19 Trưởng lão nhiệm vụ phái `10x–60x/90x` + 3 Cổng thoát `MapObject` (`Map 66, 67, 82`) + 3 NPC Tẩy điểm/Ra tù (`Map 55, 140, 208`)** | Đứt chuỗi học võ công 8 phái; kẹt cứng ở Mê cung Đáy Giếng / Đáy Động Đình Hồ / Đại Lao | Bổ sung adapter `IObjInteractionScript` cho `MapObject` trỏ tới `\trap\*.lua`; port 19 Trưởng lão phái và 3 NPC `Vô danh Tăng`, `Đạo Nhân Què`, `Lao đầu`; khóa bằng `audit_map_scenario.py verify`. |
| **`P0`** | **GAP-LEDGER** | **`parity_master_ledger.json` mù `525` bản đồ active của `s3relay` & chứa 4 map rỗng (`88,89,107,108`)** | Báo cáo tiến độ QA sai lệch hoàn toàn, bỏ lọt 7 đại môn phái & toàn bộ phó bản | Đồng bộ `parity_master_ledger.json` theo `gateway/s3relay/settings/worldset.txt` (`669` map hợp lệ), loại bỏ 4 ID khuyết `.wor`, và mở rộng `MINIMAL_TEST_MAPS` phủ đủ 10 phái + mỗi dải `1x–9x`. |
| **`P1`** | **GAP-C1/C2** | **Tống Kim (`SongJin`) & Vượt Ải (`VuotAi`) mới chỉ có khung RAM rời rạc, chưa nối `MapZoneWorker`** | Hai tính năng trụ cột bậc nhất của Võ Lâm Truyền Kỳ chưa thể vào chơi thực tế | Nối `MissionZoneTickSystem` vào `MapZoneWorker` (`SpawnNpc`, `HandleNpcDeath`, `HandlePlayerDeath`), port `Mộ Binh Quan` / `Quân Nhu Quan` (`323–325`), menu Vượt Ải tại `Nhiếp Thí Trần`, sửa lại bảng quái thật 28+1 ải; kiểm chứng bằng `18-Hz Time-Warp` (`AdvanceTicks`). |
| **`P1`** | **GAP-B2/B3** | **160 Boss Sát Thủ (`Nhiếp Thí Trần`) chưa spawn/rớt Sát Thủ Giản & Boss Hoàng Kim `s3relay` đang hardcode giả lập** | Mất hoàn toàn vòng lặp săn Boss cá nhân/bang hội và nguồn ghép Lệnh Bài Vượt Ải | Xuất đúng `settings/task/tollgate/killer/killer.txt` (`160` Boss, `40` map), port `killbosshead.lua` + `kill_level.lua`; thay `BossTemplateCatalog` hardcode bằng dữ liệu thật từ `makegoldboss_*`, `small_goldboss_*`, `make_new_big_boss.lua`. |
| **`P2`** | **GAP-C3/C4/C8** | **Phong Lăng Độ (`337–339`), Võ Lâm Liên Đấu (`WLLS`), Tỷ Võ Lôi Đài (`209–211`)** | Thiếu chuỗi hoạt động PvP định kỳ theo giờ/tháng và đấu trường đặt cược công bằng | Xây dựng `FengLingDoMissionController` (25p + 3 đợt Thủy Tặc), `WllsMissionController` + `ControlPlane League Service`, và chuyển `ArenaSparringMasterDialogScript` sang mở Mission thật trên `Map 209–211`. |
| **`P3`** | **GAP-C5/C6/C7** | **Công Thành Chiến / Thất Thành, Viêm Đế Bảo Tàng, Thiên Trì Mật Cảnh, Quả Huy Hoàng & Bang Địa** | Các hoạt động Endgame bang hội & phụ bản mở rộng chưa vận hành | Port tuần tự theo đặc tả `port-s3relay-system` & `khao-sat-chien-truong-pho-ban` sau khi khóa xong Tống Kim, Vượt Ải, Phong Lăng Độ và Boss Hoàng Kim. |
