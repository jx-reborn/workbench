# Báo Cáo Kiểm Toán & Dự Báo Khoảng Trống (Gap Forecast) — 6 Môn Phái Cổ Điển Hệ Kim – Mộc – Thủy

> **Phạm vi kiểm toán**: Hệ Kim (**Thiếu Lâm** `0`, **Thiên Vương** `1`), Hệ Mộc (**Đường Môn** `2`, **Ngũ Độc** `3`), Hệ Thủy (**Nga Mi** `4`, **Thúy Yên** `5`).
> **Nguồn Ground Truth đối chiếu**:
> - Cấu hình kỹ năng & đạn đạo gốc: [skills.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/skills.txt), [missles.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/missles.txt), [attribconstdata.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/attribconstdata.ini).
> - Script đường cong thông số & cấp chiêu Lua: [shaolin.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/shaolin.lua), [tianwang.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/tianwang.lua), [tangmen.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/tangmen.lua), [wudu.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/wudu.lua), [emei.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/emei.lua), [cuiyan.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/cuiyan.lua), [advancedskill.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/advancedskill.lua), [skilllvlup.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/skilllvlup.lua).
> - Bản dịch ngược nhị phân `jx_linux_y`: [KSkill_CastAndMissile.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/skills/KSkill_CastAndMissile.cpp), [KSkill_AddMagicAttribute_MissleAndSetting.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/skills/KSkill_AddMagicAttribute_MissleAndSetting.cpp), [KNpcAttribModify_Dispatch.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/attrib_modify/KNpcAttribModify_Dispatch.cpp), [KNpc_ProcessState_RegenStaminaAndAuraLoop.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/state/KNpc_ProcessState_RegenStaminaAndAuraLoop.cpp), [KNpc_CastAura.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/skills/KNpc_CastAura.cpp).
> - Thực thi hiện tại trên JX Reborn: [base-and-factions.skills.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/skills/base-and-factions.skills.json), [missiles.skills.json](file:///d:/Projects/jx-reborn/pack-tieudaotu/data/skills/missiles.skills.json), [MapZoneWorker.Combat.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.Combat.cs), [MapZoneWorker.StatusEffects.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.StatusEffects.cs), [MissileSimulationTickSystem.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/MissileSimulationTickSystem.cs), [EntityCombatModifiers.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/EntityCombatModifiers.cs), [SkillEffectiveParams.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/SkillEffectiveParams.cs), [PartyAuraTickSystem.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Parties/PartyAuraTickSystem.cs), [FactionProgressionManager.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Factions/FactionProgressionManager.cs).
> - Bộ test kiểm toán đi kèm: [MetalWoodWaterSectSkillsAuditTests.cs](file:///d:/Projects/jx-reborn/server/tests/JXReborn.ZoneEngine.Tests/Skills/MetalWoodWaterSectSkillsAuditTests.cs).

---

## 1. Tóm Tắt Điều Hành & Bảng Điểm Parity Tổng Thể

| Hạng mục kiểm toán | Mức độ hoàn thiện | Trạng thái cơ chế cốt lõi | Lỗ hổng (Gap) trọng yếu phát hiện |
| :--- | :---: | :--- | :--- |
| **Dữ liệu Catalog (`skills.json` & `missiles.json`)** | **96%** | Đã xuất đầy đủ `1,854` skill và `554` missile kèm bảng tra cấp 1–30 từ Lua (`shaolin`..`cuiyan.lua`). | **GAP-P0-1**: Các skill gán sự kiện nổ phụ qua `LvlSetting = skill_collideevent / skill_vanishedevent` khi cột `CollideEvent=0` trong `skills.txt` (điển hình **Thúy Yên 90 `Băng Tung Vô Ảnh` `336` -> `382 Băng Tâm Tuyết Liên`**) bị xuất `eventSkills = null` và runtime bỏ qua hoàn toàn! |
| **Đạn đạo, Liên hoàn, Lướt, Bẫy & Tàng hình (`ZoneEngine`)** | **90%** | Đã chạy chuẩn: `meleeMany` liên hoàn 2–5 đòn (`Truy Tinh Trục Nguyệt` 5 đòn), `meleeRunAttack` lướt + choáng (`Đoạn Hồn Thích`), `meleeMovePos` dịch chuyển (`Mê Ảnh Tung`), **Bẫy Đường Môn nằm chờ & nổ khi địch dẫm vào** (`303, 343, 345, 347, 349, 351`), Bùa trừ kháng âm Ngũ Độc, Ẩn thân `HideMode` (`Ngự Tuyết Ẩn` `713`). | **GAP-P0-2**: Nhịp hồi HP/MP từ buff/hào quang (`Từ Hàng Phổ Độ` `93`, `Mộng Điệp` `89`, `Phổ Độ Chúng Sinh` `332`, `Băng Tâm Thiến Ảnh` `269`) đang cộng mỗi **`18` frames** thay vì **`10` frames** như `KNpc::ProcessState` (`jx_linux_y`), làm hụt **44.4%** lượng máu/mana hồi thực tế và bỏ qua hệ số `lifereplenish_p` của `Đảo Hư Thiên` (`708`)! |
| **Hào quang (Aura) & Tổ đội (`PartyAuraTickSystem`)** | **72%** | `CastPlayerActiveAura` đã phát xung mỗi `10` frames đúng chu kỳ nhị phân. | **GAP-P0-3**: [PartyAuraTickSystem.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Parties/PartyAuraTickSystem.cs#L233-L286) tiêm thêm buff giả `+10% / +3% allres_p` đè lên **mọi** hào quang đội trưởng (`Lưu Thủy`, `Mộng Điệp`, `La Hán Trận`, `Vô Hình Độc`), sai lệch hoàn toàn với `KNpc::ProcessState`. Ngoài ra `69 Vô Hình Độc` chưa kích `OnStartSkillId = 1731` (tăng tốc chạy) và `712 Bế Nguyệt Phất Trần` chưa giới hạn theo cấp kỹ năng hỗ trợ thấp nhất đã học. |
| **Cộng hưởng `addskilldamage1..6` & Ràng buộc nâng điểm (`LevelUpScript`)** | **80%** | `ComputeSynergyAddDamagePercent` tính đúng cộng hưởng từ `LearnedSkills` cho cả chiêu 90, 110 Thất Truyền và 150. | **GAP-P1-1**: [FactionProgressionManager.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Factions/FactionProgressionManager.cs#L218-L263) hoàn toàn chưa kiểm tra `LevelUpScript` ([skilllvlup.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/skilllvlup.lua)) cho `351 Loạn Hoàn Kích`, `390 Đoạn Cân Hủ Cốt`, `332 Phổ Độ Chúng Sinh`, `1110 Tích Lịch Loạn Hoàn Kích` (cho phép nâng thẳng từ 0 -> 20 khi 5 chiêu nền còn ở cấp 0). |
| **Thuộc tính `MagicAttrib` (`EntityCombatModifiers`)** | **78%** | Đầy đủ nhóm sát thương 5 hệ, trừ kháng âm, phản đòn cận/xa, phản bùa `returnskill_p`, kháng bùa `ignoreskill_p`, rút mana theo độc `poison2decmana_p`. | **GAP-P1-2**: Thiếu `case` trong `EntityCombatModifiers.AddAttribute` cho `193 poisondamagereturn_v`, `177 seriesres_p`, `178 seriesenhance_p`, `127 slowmissle_b`, `87/91/95` (`life_v/mana_v/stamina_v` tức thời), `153/154` (`lifepotion_v/manapotion_v`), `209..215` (`enhance_709_auto`, `enhance_708_life_p`, `enhance_93_life_v`, `enhance_711_auto`...), `219..225` (`anti_*`); và 3 cờ `ForbidAttackCount`, `FrozenActionCount`, `RandMoveCount` chưa có nơi chặn ở `MapZoneWorker`. |
| **Nhiệm vụ Môn phái & Sách Kỹ năng (`pack-tieudaotu`)** | **45%** | Đã có Đệ tử chiêu mộ 6 phái ở thôn trấn (khớp `CFG_SkillNhapMon = 1` mở sẵn skill 1x–6x), Chưởng môn Xuất sư/Trùng phản, Sách 90, Sách 120, Võ học Thất Truyền 110. | **GAP-P2-1**: Mới chuyển đổi `1 / 417` file Lua nhiệm vụ bản đồ môn phái gốc (thiếu toàn bộ chuỗi nhiệm vụ cấp 10–50, Xuất sư thực chiến Mộc Nhân Hạng / Thập Bát La Hán / Trúc Tơ Động / Cấm Địa Mê Cung, nhiệm vụ 90 cốt truyện và nhiệm vụ 120/150). |

---

## 2. Kiểm Toán Chi Tiết Từng Môn Phái (Skill ID, Đạn Đạo, Liên Hoàn, Bẫy, Bùa, Hào Quang)

### 2.1. Hệ Kim — Thiếu Lâm (`shaolin`, Faction ID = `0`, `UTask = 7`)

#### A. Bảng kiểm toán toàn bộ kỹ năng Thiếu Lâm
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Event / Số tia | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `8` | Thiếu Lâm Quyền Pháp | Bị động Quyền | `passive` (`Eqt=-1`) | — | `+addphysicsdamage_p` (`21%..165%`), `+attackratingenhance_p` (`24%..115%`), `+deadlystrikeenhance_p` (`6%..29%`) | `LOCKED` |
| `4` | Thiếu Lâm Côn Pháp | Bị động Côn | `passive` (`Eqt=2`) | — | `+addphysicsdamage_p` (`17%..155%`), `+attackratingenhance_p` (`14%..105%`), `+deadlystrikeenhance_p` (`5%..25%`) | `LOCKED` |
| `6` | Thiếu Lâm Đao Pháp | Bị động Đao | `passive` (`Eqt=1`) | — | `+addphysicsdamage_p` (`17%..145%`), `+deadlystrikeenhance_p` (`5%..30%`) | `LOCKED` |
| `14` | **Hàng Long Bất Vũ** | Quyền 1x | `projectile` / `atFirer` | `M[66]` x `1` | Xung kích vật lý cận chiến (`Eqt=-1`), `physicsenhance_p` (`32%..260%`), cộng hưởng cho 6 chiêu | `LOCKED` |
| `10` | Kim Cang Phục Ma | Côn 1x | `melee` / `meleeBlur` (`sameTime:5`) | `Child=216` x `1` -> `M[63]` | Quét côn đường thẳng `line`, `physicsenhance_p` (`18%..150%`), cộng hưởng cho 6 chiêu | `LOCKED` |
| `15` | **Bất Động Minh Vương** | Buff 2x | `projectile` / `circle` (`sameTime:0`) | `M[76]`, `OnVanish=186` | Buff bản thân & đồng đội (`Tgt=1/1/0`): `+adddefense_v` (`40..360`), `+attackratingenhance_p` (`24%..157%`) trong `2..10` phút | `LOCKED` |
| `271` | **Long Trảo Hổ Trảo** | Quyền 3x | `melee` / `meleeMany` (`sameTime:5`) | `Child=272` x `2` -> `M[113]` | Đánh **2 trảo liên hoàn** cách nhau `5` frames (`Eqt=-1`), bỏ qua né tránh `ignoredefense_p` (`10%..75%`), băng sát + choáng `stun_p` (`1%..18%`) | `LOCKED` |
| `11` | **Hoành Tảo Lục Hợp** | Côn 3x | `projectile` / `atFirer` (`sameTime:6`) | `M[86]` x `1` | Xoay côn 360° quanh thân (`Eqt=2`, dùng được trên ngựa `Horse=1`), `physicsenhance_p` (`20%..220%`) + băng sát | `LOCKED` |
| `16` | **La Hán Trận** | Hào quang 3x | `aura` / `atFirer` | `Child=202` -> `M[92]` (`circle`) | **Phản đòn cận chiến & tầm xa** cho bản thân + đồng đội (`Rng=180`): `meleedamagereturn_p` (`6%..45%`), `rangedamagereturn_p` (`6%..45%`), `adddefense_v` (`30..410`), `addphysicsdamage_p` (`12%..72%`) | `PARTIAL` *(Bị `PartyAuraTickSystem` cộng thừa `+10%/+3% allres_p`)* |
| `19` | **Ma Kha Vô Lượng** | Đao 4x | `projectile` / `line` (`order:9`) | `M[61]` x `2` | Phóng **2 luồng đao khí nối tiếp** cách nhau `9` frames (`Eqt=1`, `Horse=0`), `physicsenhance_p` (`46%..350%`) + băng sát | `LOCKED` |
| `20` | **Sư Tử Hống** | Khống chế 5x | `projectile` / `atFirer` | `M[77]`, `OnStart=22` (`734` phụ) | Gầm AoE quanh bản thân gây choáng diện rộng `stun_p` (`32%..89%`, thời gian `18..45` frames), sát thương vật lý phẳng `5..100`, giãn cách `54` frames (3.0s) | `LOCKED` |
| `273` | Như Lai Thiên Diệp | Tự buff 6x | `selfBuff` / `atFirer` | — | Tăng `lifemax_p` (`56%..170%`), `attackspeed_v` (`11..68`), `addphysicsdamage_p` (`32%..150%`), `addcolddamage_v` (`11..68`), `deadlystrikeenhance_p` (`11%..30%`) | `LOCKED` |
| `21` | **Dịch Cân Kinh** | Bị động 6x | `passive` (`Style=3`) | — | Tăng kháng tất cả `allres_p` (`3%..36%`). *(Lưu ý: `skills.txt` khai báo thêm cột `attackspeed_v` & `castspeed_v` trỏ vào `yijin_jing`, nhưng `shaolin.lua` không định nghĩa 2 khóa này nên cả `jx_linux_y` lẫn `pack-tieudaotu` đều chỉ kích `allres_p`)* | `LOCKED` |
| `318` | **Đạt Ma Độ Giang** | Quyền 90 | `melee` / `meleeMany` (`sameTime:5`) | `Child=317` x `2` -> `M[134]` | Xuất **2 chưởng Kim Phật Xuất Kích (`317`)** cách nhau `5` frames (`Eqt=-1`, `Horse=1`), `physicsenhance_p` (`62%..380%`), `ignoredefense_p` (`12%..85%`), băng sát + choáng `stun_p` (`5%..25%`) | `LOCKED` |
| `319` | **Hoành Tảo Thiên Quân** | Côn 90 | `projectile` / `atFirer` | `M[135]`, `OnCollide=382` | Quét côn AoE quanh thân (`Eqt=2`, `Horse=1`), `physicsenhance_p` (`50%..330%`) + băng sát, khi trúng địch nổ phụ `382` | `LOCKED` |
| `321` | **Vô Tướng Trảm** | Đao 90 | `projectile` / `line` (`order:9`) | `M[136]` x `2` | Phóng **2 luồng đao khí xuyên thấu** cách nhau `9` frames (`Rng=400`, `Eqt=1`, `Horse=0`), `physicsenhance_p` (`70%..390%`) + băng sát | `LOCKED` |
| `709` | **Đại Thừa Như Lai Chú** | Bị động 120 | `passive` (`Style=3`) | `autoreplyskill` -> `725` | Phản độc `poisondamagereturn_p` (`5%..81%`), **phản bùa chú `returnskill_p` (`5%..56%`)** theo danh sách `attribconstdata.ini`, tự kích `725` khi bị đánh | `PARTIAL` *(Thiếu `enhance_709_auto` Attr `209` từ trang bị)* |
| `1245`, `1255`, `1256` | Thất Truyền 110 (Quyền / Đao / Côn) | Thất Truyền | `meleeMany` / `projectile` | Đa tầng | Đã xuất vào `base-and-factions.skills.json` và nhận cộng hưởng từ skill 1x–6x; học qua [ThatTruyenDialogLibrary.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/GlobalLibraries/ThatTruyenDialogLibrary.cs) | `LOCKED` |
| `1055`, `1056`, `1057` | Đại Lực Kim Cang / Vi Đà Hiến Xử / Tam Giới Quy Thiền | Kỹ năng 150 | `meleeMany` / `atFirer` / `spread` | `1083x2` / `318x1` / `319x3` + `OnStart=1085` | Đã có đủ template và cộng hưởng trong JSON; nhiệm vụ 150 đang khóa trong `item/skillbook.lua` | `LOCKED` *(Data/Engine)* |

---

### 2.2. Hệ Kim — Thiên Vương (`tianwang`, Faction ID = `1`, `UTask = 3`)

#### A. Kiểm chứng chuyên sâu số đòn đánh liên hoàn (`meleeMany`) & Kỹ năng Thiên Vương
Trong [MapZoneWorker.Combat.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.Combat.cs#L724-L745), nhánh `meleeMany` (`MisslesForm = 12`) lập lịch chính xác `hitCount = effParams.ChildSkillCount` đòn đánh tại các frame `tick + 1 + (i * delayStep)`:
- **Nhánh Thương (`EquipLimit = 3`) — Tăng dần số nhát đâm mỗi lần xuất chiêu**:
  - `30 Hồi Phong Lạc Nhạn` (1x): **`2` đòn** (`Child=219`, `delay=10` frames -> frame `+1, +11`).
  - `35 Dương Quan Tam Điệp` (3x): **`3` đòn** (`Child=221`, `delay=8` frames -> frame `+1, +9, +17`).
  - `41 Huyết Chiến Bát Phương` (6x): **`4` đòn** (`Child=225`, `delay=4` frames -> frame `+1, +5, +9, +13`).
  - **`323 Truy Tinh Trục Nguyệt` (90)**: **`5` đòn liên hoàn mỗi lần xuất chiêu!** (`Child=327`, `ChildSkillCount=5`, `delay=3` frames -> đâm liên tiếp ở frame **`+1, +4, +7, +10, +13`**, mỗi nhát phóng `M[139]` tầm `75 mps`, dùng được cả trên ngựa `HorseLimit=1`).
  - `1060 Bá Vương Tạm Kim` (150): **`5` nhịp** x `1088` (`3` tia `spread` mỗi nhịp = tổng cộng **15 tia thương khí**).
- **Nhánh Chùy (`EquipLimit = 4`)**:
  - `29 Trảm Long Quyết` (`1` nhịp -> `405`), `31 Hành Vân Quyết` (`1` nhịp -> `406`), `324 Thừa Long Quyết` (`1` nhịp -> `407`): cận chiến đơn nhịp tốc độ cao kèm `ignoredefense_p` và `deadlystrike_p`.
  - **`325 Truy Phong Quyết` (90 — quen gọi là Truy Tâm Chùy)**: `meleeMany` `1` nhịp (`delay=4`) gọi `Child=408`; `408` có `MisslesForm = spread` (`2`) và đường cong `skill_misslenum_v = {{1,1},{20,3}}` ([tianwang.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/tianwang.lua#L519-L532)), tức cấp 1 đánh `1` tia, cấp 20 tỏa **`3` tia chùy khí** với `ignoredefense_p = 90%`, `physicsenhance_p = 380%`, `colddamage_v = 50..250`!
- **Nhánh Đao (`EquipLimit = 1`, bắt buộc xuống ngựa `HorseLimit = 0`)**:
  - `34 Kinh Lôi Trảm`, `37 Bát Phong Trảm`, `32 Vô Tâm Trảm`: `1` đòn `meleeMany` (`404 / 222 / 220`).
  - **`322 Phá Thiên Trảm` (90)**: `1` nhịp `meleeMany` gọi `Child=326`; `326` bắn **`3` tia đao khí dàn hàng ngang (`wall`, `centerExtendLine`)** qua `M[138]`.

#### B. Bảng kiểm toán đầy đủ kỹ năng Thiên Vương
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Số đòn | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `26`, `23`, `24` | Thiên Vương Chùy / Thương / Đao Pháp | Bị động Vũ khí | `passive` (`Eqt=4 / 3 / 1`) | — | Tăng vật lý ngoại công `%`, chính xác `%`, chí mạng `%` theo đúng loại binh khí | `LOCKED` |
| `29`, `31`, `324` | Trảm Long / Hành Vân / Thừa Long Quyết | Chùy 1x–6x | `melee` / `meleeMany` | `405x1`, `406x1`, `407x1` | Cận chiến Chùy (`Horse=1`), xuyên né tránh `ignoredefense_p` + chí mạng cao | `LOCKED` |
| `30`, `35`, `41` | Hồi Phong / Dương Quan / **Huyết Chiến Bát Phương** | Thương 1x–6x | `melee` / `meleeMany` | **`219x2` / `221x3` / `225x4`** | Liên hoàn **2 / 3 / 4 nhát thương** mỗi lần vung (`Horse=1`) | `LOCKED` |
| `34`, `37`, `32` | Kinh Lôi / Bát Phong / Vô Tâm Trảm | Đao 1x–6x | `melee` / `meleeMany` | `404x1`, `222x1`, `220x1` | Cận chiến Đao (`Horse=0`), tốc xuất chiêu nhanh (`delay=5..10`) | `LOCKED` |
| `33` | **Tĩnh Tâm Quyết** | Tự buff 2x | `selfBuff` / `atFirer` | — | Tăng chính xác `attackratingenhance_p` (`33%..300%`) | `LOCKED` |
| `40` | **Đoạn Hồn Thích** | Lướt khống chế 3x | `melee` / **`meleeRunAttack`** | `Child=224` x `1` | **Lướt nhanh từng bước `16 mps`** tới mục tiêu (tầm `200..520 mps`, dừng trước vật cản, chỉ phát `0x5A`, không phát nhầm `0x51/0xC5`), đâm sau `Param2 = 18..1` frames + **gây choáng `stun_p` (`16%..35%`, `5..18` frames)**, tốn Thể lực (`10..20`), giãn cách `27` frames | `LOCKED` |
| `42` | **Kim Chung Trạo** | Tự buff 4x | `selfBuff` / `atFirer` | — | Tăng `physicsres_p` (`5%..25%`), `coldres_p` (`10%..50%`), `poisonres_p` (`7%..35%`) nhưng **bị trừ Kháng Hỏa âm `fireres_p` (`-11%..-30%`)** đúng nguyên tác Kim kỵ Hỏa | `LOCKED` |
| `36` | **Thiên Vương Chiến Ý** | Bị động 6x | `passive` (`Style=3`) | — | Tăng sinh lực tối đa `lifemax_p` (`38%..212%`) và chí mạng `deadlystrikeenhance_p` (`4%..15%`) | `LOCKED` |
| `325` | **Truy Phong Quyết** *(Truy Tâm Chùy)* | Chùy 90 | `melee` / `meleeMany` (`sameTime:4`) | `408` -> `M[141]` x `1..3` (`spread`) | Tỏa **3 tia chùy băng** ở Lv20, `physicsenhance_p` (`25%..380%`), `ignoredefense_p` (`12%..90%`), băng sát `50..250` | `LOCKED` |
| `323` | **Truy Tinh Trục Nguyệt** | Thương 90 | `melee` / `meleeMany` (`sameTime:3`) | **`Child=327` x `5`** -> `M[139]` | **Đâm 5 đòn liên hoàn** (`+1, +4, +7, +10, +13` frames), `physicsenhance_p` (`45%..345%`), băng sát + chí mạng | `LOCKED` |
| `322` | **Phá Thiên Trảm** | Đao 90 | `melee` / `meleeMany` (`sameTime:5`) | `326` -> `M[138]` x `3` (`wall`) | Phóng **3 luồng đao khí song song** (`Horse=0`), `physicsenhance_p` (`55%..375%`) + băng sát | `LOCKED` |
| `708` | **Đảo Hư Thiên** | Bị động 120 | `passive` (`Style=3`) | — | `+allres_p` (`1%..10%`), `+allresmax_p` (`1%..5%`), `+lifereplenish_p` (`1%..35%`), **kháng bùa chú `+ignoreskill_p` (`5%..89%`)** | `PARTIAL` *(`lifereplenish_p` chưa nhân vào hồi máu buff 10-frame; thiếu `enhance_708_life_p` Attr `210`)* |
| `1271`, `1273`, `1275` / `1058..1060` | Thất Truyền 110 & Kỹ năng 150 | 110 / 150 | `meleeMany` | Đa tầng | Đầy đủ thông số & cộng hưởng trong JSON | `LOCKED` *(Data/Engine)* |

---

### 2.3. Hệ Mộc — Đường Môn (`tangmen`, Faction ID = `2`, `UTask = 2`)

#### A. Kiểm chứng chuyên sâu Cơ Chế Đặt Bẫy Địa Lôi Đường Môn trên `ZoneEngine`
Qua đối chiếu [MapZoneWorker.Combat.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.Combat.cs#L209-L222) và [MissileSimulationTickSystem.TickAndSpawns.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/MissileSimulationTickSystem.TickAndSpawns.cs) + [MissileSimulationTickSystem.Collision.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/MissileSimulationTickSystem.Collision.cs) với [KSkill_CastAndMissile.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/skills/KSkill_CastAndMissile.cpp):
1. **Đặt bẫy tại chân người phóng bỏ qua tầm xa chuột (`CanCastSkill` parity)**: Cả 6 bẫy Đường Môn (`347 Địa Diệm Hỏa`, `303 Độc Thích Cốt`, `343 Xuyên Tâm Thích`, `345 Hàn Băng Thích`, `349 Lôi Kích Thuật`, `351 Loạn Hoàn Kích`) đều có `SkillStyle = Projectile` (`0`), `MisslesForm = atFirer` (`7`). `HandleCastSkill` đặt `skipRangeCheck = true`, cho phép người chơi click chuột ở bất kỳ khoảng cách nào trên màn hình mà vẫn đặt bẫy ngay tại tọa độ đứng `(caster.CurrentMpsX, caster.CurrentMpsY)`.
2. **Nằm chờ trên mặt đất (`MoveKind = Stand`)**: Các đạn bẫy `M[157, 127, 153, 155, 159, 161]` có `SpeedMpsPerFrame = 0`, `LifeFrames = 540` (`30` giây ở 18 Hz cho 5 bẫy cơ bản) và `LifeFrames = 1080` (`60` giây cho bẫy 90 `351 Loạn Hoàn Kích`), `AutoExplode = false` (hết thời gian chờ mà không ai dẫm thì tự biến mất êm, **không tự nổ khống**). Nếu người đặt rời bản đồ, chết hoặc đổi trạng thái PK/Camp thì hệ thống gọi `DoVanish(0)` thu hồi toàn bộ bẫy cũ để chống exploit đặt bẫy hòa bình rồi bật đồ sát.
3. **Kích nổ khi kẻ địch dẫm vào + Tia nổ phụ**: Mỗi frame, `MissileSimulationTickSystem` quét vùng va chạm `CollideRadiusCells = 2` (`HitRadiusMps = 64`, `CanCollideFriend = false`). Ngay khi kẻ địch bước vào phạm vi `64 mps`, bẫy gây sát thương vùng `IsRangeDamage = true` (`DamageRadiusCells = 2`), tự hủy (`PierceCount = 1, CollideVanish = true`) và **kích hoạt `OnCollideSkillId` (`348 / 304 / 344 / 346 / 350 / 352`)** phóng tiếp **`4` tia nổ phụ (`randomOrder:5`)** tại tâm nạn nhân!
4. **Điểm khuyết duy nhất ở nhánh Bẫy**: Cột `LevelUpScript = \script\skill\lvlup_luanhuan_ji.lua` của `351 Loạn Hoàn Kích` (và `lvlup_pililuanhuan_ji.lua` của `1110`) yêu cầu cả 5 bẫy cơ bản phải đạt cấp `>= 5` và (khi `mlvl <= 15`) `>= mlvl + 5` mới cho cộng điểm kỹ năng tiếp theo ([skilllvlup.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/skilllvlup.lua)), nhưng `FactionProgressionManager.TryAllocateSkillPoint` chưa kiểm tra điều kiện này.

#### B. Bảng kiểm toán toàn bộ kỹ năng Đường Môn
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Event / Số tia | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `43` | Đường Môn Ám Khí | Bị động Vũ khí | `passive` (`Eqt=-2`) | — | `+addphysicsdamage_p` (`18%..155%`) cho toàn bộ ám khí | `LOCKED` |
| `45` | Tích Lịch Đơn | Ám khí 1x | `projectile` / `line` | `M[35]` x `1` | Đạn thẳng vật lý + độc sát, cộng hưởng cho 6 kỹ năng cấp cao | `LOCKED` |
| `347` | **Địa Diệm Hỏa** | Bẫy 1x | `projectile` / `atFirer` | `M[157]` (`540f`), `OnCollide=348` (`x4`) | Bẫy lửa nằm chờ 30s, nổ hỏa sát + 4 tia lửa phụ | `LOCKED` |
| `303` | **Độc Thích Cốt** | Bẫy 2x | `projectile` / `atFirer` | `M[127]` (`540f`), `OnCollide=304` (`x4`) | Bẫy độc nằm chờ 30s, nổ độc sát + 4 tia độc phụ | `LOCKED` |
| `50` | **Truy Tâm Tiễn** | Phi Đao 3x | `projectile` / `line` (`order:4`) | `M[37]` x `2` (`Eqt=101`) | Phóng **2 phi đao nối đuôi** cách nhau `4` frames (`Horse=1`), vật lý + độc sát | `LOCKED` |
| `54` | Mạn Thiên Hoa Vũ | Tụ Tiễn 3x | `projectile` / `atTarget` | `M[38]` x `1` (`Eqt=102`) | Mưa tên rơi tại mục tiêu (`Horse=1`), vật lý + độc sát | `LOCKED` |
| `47` | **Đoạt Hồn Tiêu** | Phi Tiêu 3x | `projectile` / `line` | `M[116]` x `1` (`Eqt=100`) | Tiêu thẳng tầm xa `450 mps` (`Horse=1`), vật lý + 2 dòng độc sát | `LOCKED` |
| `343` | **Xuyên Tâm Thích** | Bẫy 3x | `projectile` / `atFirer` | `M[153]` (`540f`), `OnCollide=344` (`x4`) | Bẫy chông vật lý nằm chờ 30s + nổ 4 tia phụ | `LOCKED` |
| `345` | **Hàn Băng Thích** | Bẫy 4x | `projectile` / `atFirer` | `M[155]` (`540f`), `OnCollide=346` (`x4`) | Bẫy băng nằm chờ 30s, làm chậm băng sát + nổ 4 tia phụ | `LOCKED` |
| `349` | **Lôi Kích Thuật** | Bẫy 5x | `projectile` / `atFirer` | `M[159]` (`540f`), `OnCollide=350` (`x4`) | Bẫy sét nằm chờ 30s, gây lôi sát + **choáng `stun_p`** + nổ 4 tia phụ | `LOCKED` |
| `249` | Tiểu Lý Phi Đao | Phi Đao 6x | `projectile` / `atTarget` | `M[106]` x `1` (`Eqt=101`) | Phi đao điểm huyệt trực tiếp tại mục tiêu + `skill_dohurt` giật ngửa | `LOCKED` |
| `58` | **Thiên La Địa Võng** | Tụ Tiễn 6x | `projectile` / `line` | `M[67]`, **`OnCollide=227`** (`circle` `x3`) | Tên chính trúng đích tỏa tròn **3 tiểu phi đao (`227`)** (`Eqt=102, Horse=0`) | `LOCKED` |
| `341` | Tán Hoa Tiêu | Phi Tiêu 6x | `projectile` / `spread` (`sameTime:3`) | `M[151]` x `5` (`Eqt=100`) | Phóng hình quạt **5 mũi phi tiêu** đồng loạt | `LOCKED` |
| `48` | **Tâm Nhãn** | Bị động 6x | `passive` (`Style=3`) | — | `attackspeed_v` (`11..75`), `addphysicsdamage_p`, `addpoisondamage_v`, `poisonenhance_p`, `addcolddamage_v`, `deadlystrikeenhance_p` | `LOCKED` |
| `339` | **Nhiếp Hồn Nguyệt Ảnh** | Phi Đao 90 | `projectile` / `line` (`order:5`) | `M[149]` x `1`, **`OnCollide=340`** | Phi đao xoáy (`Eqt=101, Horse=1`), khi va chạm nổ vòng **Ngân Đao Xạ Nguyệt (`340`)** | `LOCKED` |
| `302` | **Bạo Vũ Lê Hoa** | Tụ Tiễn 90 | `projectile` / `atTarget` | `M[96]` (`36f`), **`OnFly=301@30`** (`circle` **`x8`**) | Đặt tâm mưa tên tại mục tiêu (`Eqt=102, Horse=0`), ở frame thứ `30` kích nổ **Truy Tinh Trục Điện (`301`) bắn tỏa tròn 8 tia độc tiễn** | `LOCKED` |
| `342` | **Cửu Cung Phi Tinh** | Phi Tiêu 90 | `projectile` / `spread` (`centerExtendLine:2`) | `M[152]` x `5` (`Eqt=100`) | Phóng **5 phi tiêu tỏa đều từ tâm** (`Horse=1`), tốc ra chiêu nhanh (`Wait=2`) | `LOCKED` |
| `351` | **Loạn Hoàn Kích** | Bẫy 90 | `projectile` / `atFirer` | `M[161]` (`1080f`), **`OnCollide=352`** (`x4`) | Đại bẫy **Ngũ Hành (Hỏa + Độc + Vật lý + Băng + Lôi + Choáng)** nằm chờ 60s | `PARTIAL` *(Runtime bẫy chuẩn 100%, thiếu `lvlup_luanhuan_ji.lua` khi nâng điểm)* |
| `710` | **Mê Ảnh Tung** | Khinh công 120 | `melee` / **`meleeMovePos`** | — | **Dịch chuyển tức thời** tối đa `Param1 = 120..400 mps`, tự dừng trước ô vật cản, giãn cách `270..90` frames (ngựa `360..144` frames) | `LOCKED` |
| `1248`, `1251`, `1253` / `1069..1071`, `1110` | Thất Truyền 110 & Kỹ năng 150 | 110 / 150 | `spread` / `line` / `atTarget` | Đa tầng (`1110 -> OnVanish=1113 -> 352`) | Đầy đủ thông số JSON; `1110` thiếu kiểm tra `lvlup_pililuanhuan_ji.lua` | `PARTIAL` |

---

### 2.4. Hệ Mộc — Ngũ Độc (`wudu`, Faction ID = `3`, `UTask = 10`)

#### A. Kiểm chứng Hệ Thống Bùa Chú Trừ Kháng Âm & Truy Vết "Đới Giáp Môn"
1. **Cơ chế Kháng Âm khuếch đại sát thương & kéo dài độc**:
   - Trong [CombatDamageCalculator.Resolution.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/CombatDamageCalculator.Resolution.cs), `CalcCurRes` giữ nguyên giá trị kháng âm (`rawRes < 0`), nên công thức `(rawDamage * (100 - effectiveResist)) / 100` tự động **nhân đại sát thương nhận vào** (ví dụ đối thủ kháng 0% dính `Xuyên Y Phá Giáp` `-49%` nhận `149%` sát thương vật lý).
   - Riêng `73 Vạn Độc Thực Tâm` gán `poisontimereduce_p` âm (`-100%..-200%`), làm thời gian trúng độc kéo dài từ `200%` lên **`300%` (gấp 3 lần tổng nhảy độc)**!
   - Toàn bộ các bùa chú Ngũ Độc (`64, 67, 70, 72, 356, 390`) đều nằm trong bảng đối kháng [attribconstdata.ini](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/attribconstdata.ini), tương tác đúng với `returnskill_p` (Thiếu Lâm `709`) và `ignoreskill_p` (Thiên Vương `708`).
2. **Làm rõ kỹ năng "Đới Giáp Môn" (phản độc)**:
   - Kiểm chứng toàn bộ `1,435` dòng trong [skills.txt](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/settings/skills.txt) và [wudu.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/wudu.lua) xác nhận: **bản VNG / JX1 cổ điển không có kỹ năng Ngũ Độc nào tên "Đới Giáp Môn"** (tên gọi này xuất phát từ Kiếm Thế / VLTK2; ở JX1 vị trí bị động phòng hộ của Ngũ Độc là **`66 Tạp Nan Dược Kinh`** tăng kháng độc `+6%..+30%` và **`69 Vô Hình Độc`** hào quang độc). Thuộc tính phản độc phần trăm `poisondamagereturn_p` (`Attr 194`) đã hoạt động trên `709 Đại Thừa Như Lai Chú`, còn phản độc phẳng `poisondamagereturn_v` (`Attr 193`) mới nối cho trang bị, đang thiếu `case 193:` trong `EntityCombatModifiers.AddAttribute`.

#### B. Bảng kiểm toán toàn bộ kỹ năng Ngũ Độc
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Event / Số tia | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `60`, `62` | Ngũ Độc Đao / Chưởng Pháp | Bị động Vũ khí | `passive` (`Eqt=1 / -2`) | — | Đao: `+addphysicsdamage_p` + chí mạng; Chưởng: `+addpoisonmagic_v` nội công độc | `LOCKED` |
| `63` | Độc Sa Chưởng | Chưởng 1x | `projectile` / `line` | `M[30]` x `1` | Chưởng độc tầm ngắn `180 mps`, cộng hưởng cho 6 kỹ năng | `LOCKED` |
| `65` | Huyết Đao Độc Sát | Đao 1x | `projectile` / `line` | `M[31]` x `1` (`Eqt=1`) | Đao độc thẳng (`Horse=0`), vật lý + độc sát | `LOCKED` |
| `66` | **Tạp Nan Dược Kinh** | Bị động 2x | `passive` (`Style=3`) | — | Tăng kháng độc `+poisonres_p` (`6%..30%`) | `LOCKED` |
| `67` | Cửu Thiên Cuồng Lôi | Bùa 2x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **trừ Kháng Lôi âm `lightingres_p` (`-9%..-49%`)** | `LOCKED` |
| `70` | Xích Diệm Thực Thiên | Bùa 2x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **trừ Kháng Hỏa âm `fireres_p` (`-9%..-49%`)** | `LOCKED` |
| `64` | Băng Lam Huyền Tinh | Bùa 2x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **trừ Kháng Băng âm `coldres_p` (`-9%..-49%`)** | `LOCKED` |
| `68` | **U Minh Khô Lâu** | Chưởng 3x | `projectile` / `line` | `M[32]` x `1` (`Horse=1`) | Đầu lâu độc bay thẳng tầm `400 mps` | `LOCKED` |
| `384` | **Bách Độc Xuyên Tâm** | Đao 3x | `projectile` / `atFirer` (`sameTime:5`) | `M[190]` x `1` (`Horse=0`) | Vung đao tỏa độc quanh thân `180 mps` | `LOCKED` |
| `356` | **Xuyên Y Phá Giáp** | Bùa 3x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **trừ Phòng Thủ Vật Lý âm `physicsres_p` (`-9%..-49%`)** trong `900..3600` frames | `LOCKED` |
| `69` | **Vô Hình Độc** *(Vô Hình Cổ)* | Hào quang 3x | `aura` / `atFirer` | `Child=203` (độc địch), **`OnStart=1731`** (tốc chạy ta) | Xung mỗi `10f` rút độc kẻ địch quanh thân (`203`), đồng thời trong `skills.txt` khai báo `StartEvent=1:1731` tăng tốc chạy `fastwalkrun_p` cho phe ta | `GAP` *(Chưa kích `OnStart=1731` khi phát aura & bị `PartyAuraTickSystem` cộng nhầm `+10%/+3% allres_p`)* |
| `72` | **Xuyên Tâm Độc Thích** | Bùa 4x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **trừ Kháng Độc âm `poisonres_p` (`-29%..-49%`)** | `LOCKED` |
| `71` | **Thiên Can Địa Sát** | Chưởng 5x | `projectile` / `atTarget` | `M[33]` x `1` (`Horse=1`) | Cột độc nổ tại mục tiêu tầm `420 mps` | `LOCKED` |
| `74` | **Chu Cáp Thanh Minh** | Đao 5x | `projectile` / `line` (`order:9`) | `M[34]` x `2` (`Eqt=1, Horse=0`) | Phóng **2 luồng đao độc nối tiếp** cách nhau `9` frames | `LOCKED` |
| `73` | **Vạn Độc Thực Tâm** | Bùa 6x | `projectile` / `atTarget` | `M[20]` x `1` | Bùa **kéo dài thời gian trúng độc `poisontimereduce_p` (`-100%..-200%`)** -> nhân `2x..3x` tổng nhịp độc | `LOCKED` |
| `75` | **Ngũ Độc Kỳ Kinh** | Bị động 6x | `passive` (`Style=3`) | — | Tăng `addpoisondamage_v`, `addpoisonmagic_v`, **phát huy độc `poisonenhance_p` (`11%..50%`)**, chí mạng | `LOCKED` |
| `353` | **Âm Phong Thực Cốt** | Chưởng 90 | `projectile` / `atTarget` | `M[163]`, **`OnVanish=354`** (`wall`) | Cột âm phong tại mục tiêu (`Horse=1`), khi tan kích hoạt **Thiên Cương Độc Thủ (`354`)** quét ngang | `LOCKED` |
| `355` | **Huyền Âm Trảm** | Đao 90 | `projectile` / `line` (`order:6`) | `M[165]` x `2`, **`OnCollide=383`** (`wall`) | Phóng **2 lưỡi đao độc** (`Eqt=1, Horse=0`), khi chạm mục tiêu nổ tường độc **Tinh Không Phá (`383`)** | `LOCKED` |
| `390` | **Đoạn Cân Hủ Cốt** | Bùa 90 | `projectile` / `atTarget` | `M[20]` x `1` | Đại bùa **trừ Kháng Tất Cả âm `allres_p` (`-1%..-40%`)** | `PARTIAL` *(Hiệu ứng bùa chuẩn 100%, thiếu kiểm tra `lvlup_duanjin_fugu.lua` khi nâng điểm)* |
| `711` | **Hấp Tinh Yểm** | Bị động 120 | `passive` (`Style=3`) | `autoattackskill` -> **`719`** | Khi đánh trúng tự niệm **`719 Hấp Tinh Yểm_Quyết Chú`** (`poison2decmana_p` `30%..85%` chuyển sát thương độc thành đốt nội lực địch) | `PARTIAL` *(Cốt lõi chạy đúng, thiếu `enhance_711_auto` Attr `212`)* |
| `1257`, `1260` / `1066`, `1067` | Thất Truyền 110 & Kỹ năng 150 | 110 / 150 | `line` / `atTarget` | Đa tầng | Đầy đủ dữ liệu & cộng hưởng trong JSON | `LOCKED` *(Data/Engine)* |

---

### 2.5. Hệ Thủy — Nga Mi (`emei`, Faction ID = `4`, `UTask = 1`)

#### A. Kiểm chứng chuyên sâu Hào Quang (Aura), Hồi Máu Tổ Đội & `Bế Nguyệt Phất Trần` (`712`)
1. **Lỗ hổng nhịp hồi máu/mana (`18` frames vs `10` frames)**:
   - Trong nhị phân `jx_linux_y` ([KNpc_ProcessState_RegenStaminaAndAuraLoop.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/state/KNpc_ProcessState_RegenStaminaAndAuraLoop.cpp#L30-L135)), `m_CurrentLifeReplenish` và `m_CurrentManaReplenish` (từ `93 Từ Hàng Phổ Độ`, `89 Mộng Điệp`, `332 Phổ Độ Chúng Sinh`, `269 Băng Tâm Thiến Ảnh`) được cộng vào máu/mana mỗi **`10` frames (`0.556` giây)** và nhân với `m_CurrentLifeReplenishPercent / 100`.
   - Trong [MapZoneWorker.StatusEffects.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.StatusEffects.cs#L125-L138), hệ thống lại kiểm tra `(_currentFrameTick % 18) == 0` (**`1.0` giây/nhịp**) và **không nhân `LifeReplenishPercent`**, khiến lượng máu hồi mỗi giây của Nga Mi chỉ đạt `10 / 18 = 55.6%` so với bản gốc!
2. **Lỗ hổng tiêm chồng hào quang giả trong `PartyAuraTickSystem`**:
   - [PartyAuraTickSystem.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Parties/PartyAuraTickSystem.cs#L63-L67) quét đội trưởng có `ActiveAuraSkillId > 0` rồi gắn thêm một `ActiveStateSkillEffect` giả (`IsPartyAura = true`) cộng `+10% allres_p` (cùng phái) hoặc `+3% allres_p` (khác phái) cho cả đội, song song với `CastPlayerActiveAura` vốn đã bắn đạn hào quang thật (`206, 207, 208, 281, 333, 718`). Cần gỡ bỏ lớp buff giả `+10%/+3% allres_p` này để trả lại đúng thông số gốc từng hào quang.
3. **Cơ chế `712 Bế Nguyệt Phất Trần` (Skill 120 Nga Mi)**:
   - `712` là hào quang tổng hợp gọi `ChildSkillId = 718`, trên đó khai báo 5 dòng `skill_appendskill` (`emei120..emei120_4`) đính kèm cùng lúc 5 hào quang: `86 Lưu Thủy`, `89 Mộng Điệp`, `92 Phật Tâm Từ Hựu`, `282 Thanh Âm Phạn Xướng`, `332 Phổ Độ Chúng Sinh` với đường cong cấp `{{1,1},{19,19},{20,40}}`. Hiện tại `ExtractAppendedStateSkills` đã bung đủ cả 5 hào quang cho bản thân và đồng đội trong tầm `180 mps`, tuy nhiên cần kiểm chứng thêm hành vi nhị phân đối với mô tả trong `emei.lua` (*"Đẳng cấp kỹ năng này sẽ lấy theo kỹ năng hỗ trợ có cấp thấp nhất"*).

#### B. Bảng kiểm toán toàn bộ kỹ năng Nga Mi
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Event / Số tia | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `77`, `79` | Nga Mi Kiếm / Chưởng Pháp | Bị động Vũ khí | `passive` (`Eqt=0 / -2`) | — | Kiếm: `+addphysicsdamage_p` + chí mạng; Chưởng: `+addcoldmagic_v` nội công băng | `LOCKED` |
| `80` | Phiêu Tuyết Xuyên Vân | Chưởng 1x | `projectile` / `line` | `M[68]` x `1` | Tia băng thẳng tầm `240 mps`, làm chậm băng sát | `LOCKED` |
| `85` | Nhất Diệp Tri Thu | Kiếm 1x | `projectile` / `line` | `M[2]` x `1` (`Eqt=0, Horse=1`) | Kiếm khí băng thẳng | `LOCKED` |
| `89` | **Mộng Điệp** | Hào quang 2x | `aura` / `atFirer` | `Child=207` -> `M[92]` (`circle`) | Hào quang tổ đội hồi HP + MP (`lifereplenish_v`, `manareplenish_v`) bán kính `180 mps` | `PARTIAL` *(Nhịp hồi 18f thay vì 10f & dính `+10%/+3% allres_p` giả)* |
| `86` | **Lưu Thủy** | Hào quang 3x | `aura` / `atFirer` | `Child=206` -> `M[92]` (`circle`) | Hào quang tổ đội tăng tốc độ chạy `fastwalkrun_p` (`12%..56%`) | `PARTIAL` *(Dính `+10%/+3% allres_p` giả từ `PartyAuraTickSystem`)* |
| `82` | **Tứ Tượng Đồng Quy** | Chưởng 3x | `projectile` / `atTarget` | `M[101]`, **`OnStart=243`** (`circle` `x4`) | Nổ băng tại mục tiêu kèm 4 tia băng tỏa tròn (`Horse=1`) | `LOCKED` |
| `385` | Thôi Song Vọng Nguyệt | Kiếm 3x | `projectile` / `line` | `M[191]` x `1` (`Eqt=0, Horse=1`) | Kiếm khí xuyên băng tầm `360 mps` | `LOCKED` |
| `92` | **Phật Tâm Từ Hựu** | Hào quang 4x | `aura` / `atFirer` | `Child=208` -> `M[92]` (`circle`) | Hào quang tổ đội **tăng sinh lực tối đa `lifemax_p` (`24%..115%`)** | `PARTIAL` *(Dính `+10%/+3% allres_p` giả)* |
| `88` | **Bất Diệt Bất Tuyệt** | Kiếm 5x | `projectile` / `line` | `M[3]` x `1` (`Eqt=0, Horse=1`) | Luồng kiếm băng tầm `360 mps`, vật lý + băng sát + chí mạng | `LOCKED` |
| `91` | Phật Quang Phổ Chiếu | Chưởng 5x | `projectile` / `atFirer` | `M[4]` x `1` (`Horse=1`) | Sóng băng tỏa quanh thân | `LOCKED` |
| `282` | **Thanh Âm Phạn Xướng** | Hào quang 5x | `aura` / `atFirer` | `Child=281` -> `M[92]` (`circle`) | Hào quang giảm thời gian choáng / làm chậm / trúng độc + phục hồi + kháng chí mạng | `PARTIAL` *(Dính `+10%/+3% allres_p` giả)* |
| `93` | **Từ Hàng Phổ Độ** | Hồi máu 6x | `projectile` / `atTarget` | `M[5]` (`Tgt=1/1/0`, `Horse=0`) | Buff hồi HP liên tục `lifereplenish_v` (`15..290` HP/nhịp) cho bản thân & đồng đội vùng mục tiêu | `GAP` *(Đang hồi mỗi 18f thay vì 10f -> hụt 44.4% HP/s; thiếu `enhance_93_life_v` Attr `211`)* |
| `252` | **Phật Pháp Vô Biên** | Bị động 6x | `passive` (`Style=3`) | — | Tăng `attackspeed_v`, `castspeed_v`, `addcolddamage_v`, `addcoldmagic_v`, `coldenhance_p` | `LOCKED` |
| `328` | **Tam Nga Tề Tuyết** | Kiếm 90 | `projectile` / `wall` (`centerExtendLine:1`) | `M[142]` x `3`, **`OnStart=329`** | Phóng **3 luồng kiếm băng song song** (`Eqt=0, Horse=1`) + nổ cận thân `329 Ngọc Tuyền Tẩy Trần` | `LOCKED` |
| `380` | **Phong Sương Toái Ảnh** | Chưởng 90 | `projectile` / `atTarget` | `M[186]` (xuyên), **`OnStart=331`** (`circle` `4..9` tia) | Đạn băng nổ xuyên tại mục tiêu (`CollideVanish=false, AutoExplode=true`) đồng thời **phát tỏa `331 Kim Đỉnh Phật Quang` (`4..9` tia băng phụ)** | `LOCKED` |
| `332` | **Phổ Độ Chúng Sinh** | Hào quang 90 | `aura` / `atFirer` | `Child=333` -> `M[137]` (`circle`) | Đại hào quang cộng `allres_p` (`4%..28%`) + hồi HP + hồi MP cho tổ đội | `PARTIAL` *(Thiếu `lvlup_pudu_zhongsheng.lua` & nhịp hồi 18f vs 10f)* |
| `712` | **Bế Nguyệt Phất Trần** | Hào quang 120 | `aura` / `atFirer` | `Child=718` (`5x skill_appendskill`) | Gộp đồng thời 5 hào quang (`86, 89, 92, 282, 332`) lên toàn tổ đội trong bán kính `180 mps` | `PARTIAL` *(Cần đối chiếu giới hạn cấp theo kỹ năng hỗ trợ thấp nhất & bỏ `PartyAuraTickSystem` giả)* |
| `1227`, `1230` / `1061`, `1062`, `1114` | Thất Truyền 110 & Kỹ năng 150 | 110 / 150 | `atTarget` / `line` / `wall` | Đa tầng | Đầy đủ dữ liệu & cộng hưởng trong JSON | `LOCKED` *(Data/Engine)* |

---

### 2.6. Hệ Thủy — Thúy Yên (`cuiyan`, Faction ID = `5`, `UTask = 6`)

#### A. Kiểm chứng chuyên sâu Mưa Băng, Tia Băng Tỏa/Đuổi, `Hộ Thể Hàn Băng` & `Ngự Tuyết Ẩn`
1. **BUG NGHIÊM TRỌNG (P0) ở `336 Băng Tung Vô Ảnh` (Đơn Đao 90)**:
   - Theo [cuiyan.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/skill/cuiyan.lua) (`bingzong_wuying`), mỗi lần xuất chiêu phóng `1..5` kim băng hình quạt (`MisslesForm = spread`, `skill_misslenum_v = 1 -> 5`, đạn `M[146]` `ColFollowTarget = true`), và khi kim băng chạm mục tiêu phải nổ tầng sát thương thứ 2 là **`382 Băng Tâm Tuyết Liên`** (`skill_collideevent = 382`, `skill_eventskilllevel = 1..20`).
   - Do dòng `336` trong `skills.txt` để `CollideEvent = 0` và gán sự kiện qua cột động `LvlSetting6 = skill_collideevent` (`Attr 269`), bộ xuất `pack-tieudaotu` đã ghi `eventSkills = null`, còn [SkillEffectiveParams.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/SkillEffectiveParams.cs#L93-L236) lại chưa đọc các thuộc tính `267..270` (`skill_startevent / skill_flyevent / skill_collideevent / skill_vanishedevent`). Hậu quả: **`Băng Tung Vô Ảnh` hiện mất trắng 100% bông sen băng nổ phụ `382 Băng Tâm Tuyết Liên`!** (Cùng cơ chế này cũng cần kiểm tra cho các skill 150 dùng `skill_collideevent` / `skill_vanishedevent`).
2. **Làm rõ bản chất `100 Hộ Thể Hàn Băng`**:
   - Kiểm tra trực tiếp dòng `100` trong `skills.txt` và khóa `huti_hanbing` trong `cuiyan.lua` xác nhận: kỹ năng này **chỉ có 2 thuộc tính `meleedamagereturn_p` và `rangedamagereturn_p` (`5%..35%`)**, phản lại phần trăm sát thương nhận vào chứ không gán trạng thái đóng băng làm chậm kẻ đánh trong dữ liệu gốc. `ZoneEngine` đã thực thi đúng 100% công thức phản đòn này.
3. **Ẩn thân `713 Ngự Tuyết Ẩn` (Skill 120 Thúy Yên)**:
   - Đã hoàn thiện rất sát nhị phân: bật `HideMode = 1` (`Attr 200`, thời gian `5s..30s`, giãn cách `45s` dưới đất / `45s` trên ngựa), phát gói xóa thực thể khỏi AOI kẻ địch, chặn phát xung hào quang khi đang ẩn thân để không lộ vị trí, và gọi `ClearHideState` (xóa `[713, 1235, 1258, 1267]` theo `attribconstdata.ini`) **trước** khi phát gói `0x5A` ngay khi nhân vật ra chiêu hoặc lên ngựa.

#### B. Bảng kiểm toán toàn bộ kỹ năng Thúy Yên
| Skill ID | Tên kỹ năng | Phân nhóm | `SkillStyle` / `MissileForm` / `Gen` | Child / Event / Số tia | Cơ chế đặc thù & Giá trị Ground Truth (Lv1 -> Lv20) | Trạng thái JX Reborn |
| :---: | :--- | :--- | :--- | :--- | :--- | :---: |
| `95`, `97` | Thúy Yên Đao / Song Đao | Bị động Vũ khí | `passive` (`Eqt=1 / -2`) | — | Đơn đao: `+addphysicsdamage_p` + chí mạng; Song đao: `+addcoldmagic_v` nội công băng | `LOCKED` |
| `99` | Phong Hoa Tuyết Nguyệt | Đơn Đao 1x | `projectile` / `line` | `M[6]` x `1` (`Eqt=1, Horse=0`) | Đao băng đường thẳng | `LOCKED` |
| `102` | Phong Quyển Tàn Tuyết | Song Đao 1x | `projectile` / `line` | `M[7]`, **`OnStart=398`** (`order:1` `x16`) | Lốc tuyết thẳng kèm 16 tia xoáy nối đuôi (`Horse=1`) | `LOCKED` |
| `100` | **Hộ Thể Hàn Băng** | Tự buff 2x | `selfBuff` / `atFirer` | — | Phản sát thương cận chiến & tầm xa `meleedamagereturn_p` + `rangedamagereturn_p` (`5%..35%`) | `LOCKED` |
| `105` | Vũ Đả Lê Hoa | Đơn Đao 3x | `projectile` / `atTarget` (`randomOrder:10`) | `M[8]` x `4` (`Eqt=1, Horse=1`) | **4 đợt mưa băng rơi ngẫu nhiên** quanh mục tiêu | `LOCKED` |
| `113` | Phù Vân Tán Tuyết | Song Đao 3x | `projectile` / `atTarget` | `M[12]` x `1` (`Horse=1`) | Nổ băng tại điểm chỉ định | `LOCKED` |
| `269` | Băng Tâm Thiến Ảnh | Tự hồi máu 3x | `projectile` / `atTarget` | `M[111]` (`Tgt=1/0/0`) | Tự hồi sinh lực `lifereplenish_v` theo nhịp tick | `GAP` *(Đang hồi mỗi 18f thay vì 10f)* |
| `108` | **Mục Dã Lưu Tinh** | Đơn Đao 4x | `projectile` / `atTarget` | `M[9]` (`Height=320, ZSpd=-40960, DmgR=2`) | **Sao băng rơi từ trên cao xuống vùng mục tiêu** (`Eqt=1, Horse=1`), nổ AoE `IsRangeDamage=true` | `LOCKED` |
| `109` | **Tuyết Ảnh** | Tự buff 5x | `selfBuff` / `atFirer` | — | Tăng tốc đánh `attackspeed_v`, tốc niệm `castspeed_v` và tốc chạy `fastwalkrun_p` | `LOCKED` |
| `111` | **Bích Hải Triều Sinh** | Song Đao 6x | `projectile` / `atFirer` | `M[10]`, **`OnStart=112`** (`circle` **`x16`**) | Vòng triều băng tỏa tròn **16 tia quanh thân** (`Horse=1`) | `LOCKED` |
| `114` | **Băng Cốt Tuyết Tâm** | Bị động 6x | `passive` (`Style=3`) | — | 7 dòng: `addcoldmagic_v`, `addcolddamage_v`, `addphysicsmagic_v`, `coldenhance_p`, `lifemax_p`, `fasthitrecover_v`, `deadlystrikeenhance_p` | `LOCKED` |
| `336` | **Băng Tung Vô Ảnh** | Đơn Đao 90 | `projectile` / `spread` (`none:1`) | `M[146]` x `1..5`, **`skill_collideevent=382`** | Phóng **5 tia kim băng hình quạt** (`Eqt=1, Horse=1`), khi trúng phải nổ **Băng Tâm Tuyết Liên (`382`)** | **`GAP (P0)`** *(Mất tầng nổ phụ `382` do `eventSkills=null` & `SkillEffectiveParams` bỏ qua `attrId=269`!)* |
| `337` | **Băng Tâm Tiên Tử** | Song Đao 90 | `projectile` / `line` | `M[147]`, **`OnFly=338@3`** | Tia băng bay thẳng để lại chuỗi **Phong Tuyết Băng Thiên (`338`) nổ dọc đường bay mỗi 3 frames** | `LOCKED` |
| `713` | **Ngự Tuyết Ẩn** | Ẩn thân 120 | `selfBuff` / `atFirer` | — | **Tàng hình `HideMode = 1` (`5..30` giây)**, xóa hình khỏi AOI địch, tự lộ diện trước khi xuất chiêu/lên ngựa | `LOCKED` |
| `1233`, `1235` / `1063`, `1065` | Thất Truyền 110 (*Băng Tung 110 / Băng Tâm Trảm 3 Tiên*) & 150 | 110 / 150 | `line` / `spread` | Đa tầng | Đầy đủ dữ liệu & cộng hưởng trong JSON (`1235` có trong danh sách phá ẩn thân `attribconstdata.ini`) | `LOCKED` *(Data/Engine)* |

---

## 3. Kiểm Toán Công Thức Cộng Hưởng Kỹ Năng Cấp Thấp (`addskilldamage1..6`)

Trong [CombatDamageCalculator.Payloads.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/CombatDamageCalculator.Payloads.cs), `ComputeSynergyAddDamagePercent` duyệt toàn bộ `attacker.LearnedSkills` với các chiêu chủ động tấn công (`Melee | Projectile | InstantMissile`) và cộng dồn `Param2.Evaluate(learnedLevel)` khi `Param0 == targetSkillId`.
> [!NOTE]
> Trong kho nguồn `legacy-server/bin/server1/script/skill/*.lua`, các bảng cộng hưởng đã được nhà phát triển bản gốc mở rộng để kỹ năng cấp thấp đồng thời cộng hưởng cho **kỹ năng 90**, **kỹ năng 110 Thất Truyền (`12xx`)** và **kỹ năng 150 (`10xx`)**. File `base-and-factions.skills.json` của `pack-tieudaotu` đã trích xuất chính xác 100% các hệ số này:

| Môn phái | Kỹ năng đích nhận cộng hưởng | Công thức cộng hưởng % sát thương tại cấp 20 của các kỹ năng nền (trích trực tiếp từ `*.lua` & `skills.json`) |
| :--- | :--- | :--- |
| **Thiếu Lâm** | `318` Đạt Ma Độ Giang (Quyền 90) | `+150%` từ `14 Hàng Long Bất Vũ` + `+110%` từ `271 Long Trảo Hổ Trảo` = **+260%** ở Lv20 (đồng thời truyền sang `1245` & `1055`) |
| **Thiếu Lâm** | `319` Hoành Tảo Thiên Quân (Côn 90) | `+64%` từ `10 Kim Cang Phục Ma` + `+48%` từ `11 Hoành Tảo Lục Hợp` = **+112%** ở Lv20 (đồng thời truyền sang `1256` & `1056`) |
| **Thiếu Lâm** | `321` Vô Tướng Trảm (Đao 90) | `+61%` từ `10 Kim Cang Phục Ma` + `+46%` từ `19 Ma Kha Vô Lượng` = **+107%** ở Lv20 (đồng thời truyền sang `1255` & `1057`) |
| **Thiên Vương** | `323` Truy Tinh Trục Nguyệt (Thương 90) | `+60%` từ `30 Hồi Phong Lạc Nhạn` + `+65%` từ `35 Dương Quan Tam Điệp` + `+70%` từ `41 Huyết Chiến Bát Phương` = **+195%** ở Lv20 (kèm `1271` & `1060`) |
| **Thiên Vương** | `325` / `322` Truy Phong Quyết & Phá Thiên Trảm | Lần lượt nhận cộng hưởng từ bộ Chùy (`29, 31, 324`) và bộ Đao (`34, 37, 32`), kèm nhánh `1275/1059` và `1273/1058` |
| **Đường Môn** | `302` Bạo Vũ Lê Hoa (Tụ Tiễn 90) | `+35%` từ `45 Tích Lịch Đơn` + `+45%` từ `54 Mạn Thiên Hoa Vũ` + `+55%` từ `58 Thiên La Địa Võng` = **+135%** ở Lv20 (kèm `1251` & `1070`) |
| **Đường Môn** | `339` / `342` Nhiếp Hồn Nguyệt Ảnh & Cửu Cung Phi Tinh | Lần lượt nhận cộng hưởng từ nhánh Phi Đao (`45, 50, 249`) và Phi Tiêu (`45, 47, 341`), kèm nhánh `1248/1069` và `1253/1071` |
| **Ngũ Độc** | `355` Huyền Âm Trảm (Đao 90) | `+60%` từ `65 Huyết Đao Độc Sát` + `+60%` từ `74 Chu Cáp Thanh Minh` = **+120%** ở Lv20 (riêng `384 Bách Độc Xuyên Tâm` truyền sang `1257` & `1067`) |
| **Ngũ Độc** | `353` Âm Phong Thực Cốt (Chưởng 90) | Nhận cộng hưởng từ bộ Chưởng độc (`63 Độc Sa Chưởng`, `68 U Minh Khô Lâu`, `71 Thiên Can Địa Sát`), kèm `1260` & `1066` |
| **Nga Mi** | `380` Phong Sương Toái Ảnh (Chưởng 90) | `+75%` từ `80 Phiêu Tuyết Xuyên Vân` + `+80%` từ `91 Phật Quang Phổ Chiếu` = **+155%** ở Lv20 (riêng `82 Tứ Tượng Đồng Quy` truyền sang `1227` & `1062`) |
| **Nga Mi** | `328` Tam Nga Tề Tuyết (Kiếm 90) | Nhận cộng hưởng từ bộ Kiếm (`85 Nhất Diệp Tri Thu`, `385 Thôi Song Vọng Nguyệt`, `88 Bất Diệt Bất Tuyệt`), kèm `1230` & `1061` |
| **Thúy Yên** | `336` Băng Tung Vô Ảnh (Đơn Đao 90) | `+55%` từ `99 Phong Hoa Tuyết Nguyệt` + `+60%` từ `108 Mục Dã Lưu Tinh` = **+115%** ở Lv20 (riêng `105 Vũ Đả Lê Hoa` truyền sang `1233` & `1063`) |
| **Thúy Yên** | `337` Băng Tâm Tiên Tử (Song Đao 90) | Nhận cộng hưởng từ bộ Song Đao (`102 Phong Quyển Tàn Tuyết`, `113 Phù Vân Tán Tuyết`, `111 Bích Hải Triều Sinh`), kèm `1235` & `1065` |

---

## 4. Bảng Kiểm Toán Thuộc Tính `MagicAttrib` Chưa Nối Handler Hoặc Lệch Hành Vi

Đối chiếu bảng hàm con trỏ `g_MagicProcess.ProcessFunc[0..257]` trong [KNpcAttribModify_Dispatch.cpp](file:///d:/Projects/jx-reborn/legacy-server/discovery/combat/attrib_modify/KNpcAttribModify_Dispatch.cpp) và `KSkill::AddMagicAttribute` với [EntityCombatModifiers.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/EntityCombatModifiers.cs) & [SkillEffectiveParams.cs](file:///d:/Projects/jx-reborn/server/src/JXReborn.ZoneEngine/Combat/SkillEffectiveParams.cs):

| Attribute ID | Mã `MagicAttrib` | Kỹ năng / Hệ thống sử dụng | Trạng thái hiện tại trong `ZoneEngine` | Tác động & Mức ưu tiên |
| :---: | :--- | :--- | :--- | :---: |
| `267..270` | `skill_startevent`, `skill_flyevent`, `skill_collideevent`, `skill_vanishedevent` | Thúy Yên `336 Băng Tung Vô Ảnh` (`-> 382`), Đường Môn `1110`, Ngũ Độc/Đường Môn/Thúy Yên 150 | **CHƯA NỐI** trong `SkillEffectiveParams.Resolve`; nếu `skills.txt` để cột tĩnh `= 0` thì mất sự kiện nổ phụ | **P0** |
| `88` / `92` | `lifereplenish_v` / `manareplenish_v` | Nga Mi `89, 93, 332`, Thúy Yên `269` | Đã nối chỉ số, nhưng `MapZoneWorker.StatusEffects.cs:126` chạy mỗi **`18f` thay vì `10f`** và không nhân `LifeReplenishPercent` | **P0** |
| `190` | `lifereplenish_p` | Thiên Vương 120 `708 Đảo Hư Thiên` (`+1%..+35%`) | Mới nhân cho thuốc (`MedicineLifeRegenPerTick`) và ngồi thiền (`do_sit`), **chưa nhân cho `LifeReplenishFlat`** | **P0** |
| `193` | `poisondamagereturn_v` | Phản độc phẳng từ buff/kỹ năng | Có property `PoisonDamageReturnFlat` (đã nối trang bị), **thiếu `case 193:` trong `EntityCombatModifiers.AddAttribute`** | **P1** |
| `177`, `178` | `seriesres_p`, `seriesenhance_p` | Tương khắc ngũ hành từ buff/kỹ năng | Có property trên `EntityCombatModifiers` (đã nối trang bị), **thiếu `case 177, 178:` trong `AddAttribute`** | **P1** |
| `198`, `241`, `152` | `forbidattack`? / `forbidskill_v`, `frozen_action`, `randmove` | Khóa xuất chiêu / đóng băng hành động / loạn bước | Đã cộng bộ đếm `ForbidAttackCount`, `FrozenActionCount`, `RandMoveCount`, nhưng **`0` nơi kiểm tra trong `HandleCastSkill` / `HandleMovePlayer`** | **P1** |
| `127` | `slowmissle_b` | Nga Mi `84 Phong Vũ Phiêu Hương` | **CHƯA NỐI** (dù `MissileTemplate.CanSlow` đã được xuất ra JSON cho đạn bay) | **P2** |
| `209..215` | `enhance_709_auto`, `enhance_708_life_p`, `enhance_93_life_v`, `enhance_711_auto`... | Trang bị Hoàng Kim / Bạch Kim cường hóa trực tiếp skill 90/120 (`709, 708, 93, 711`) | **CHƯA NỐI** trong `EntityCombatModifiers` & `PlayerInventoryGrid.EquipmentStats` | **P1** |
| `219..225`, `242`, `244`, `245` | `anti_hitrecover`, `anti_*res_p`, `antimaxres_p`, `magicdamage_p`, `fasthitrecover_yan_v` | Thuộc tính đối kháng cao cấp | Có property trên `EntityCombatModifiers`, **thiếu `case` trong `AddAttribute` khi đi từ skill state** | **P2** |
| `87, 91, 95` / `153, 154` | `life_v, mana_v, stamina_v` / `lifepotion_v, manapotion_v` | Hồi tức thời / bình máu trạng thái kỹ năng | **CHƯA NỐI** trong nhánh hiệu ứng tức thời của skill (`KNpc_SetImmediatelySkillEffect`) | **P2** |

---

## 5. Kiểm Toán Nhiệm Vụ Môn Phái & Lộ Trình Học Kỹ Năng (`pack-tieudaotu`)

Đối chiếu [cfg_server.lua](file:///d:/Projects/jx-reborn/legacy-server/bin/server1/script/global/nobitaxd/config/cfg_server.lua) (`CFG_SkillNhapMon = 1`), `script/global/各派接引弟子/*.lua`, `script/item/skillbook.lua`, `script/task/lv120skill/` và `417` file Lua trong 6 thư mục bản đồ môn phái gốc với `pack-tieudaotu`:

| Giai đoạn tiến trình | Thiếu Lâm (`68` Lua) | Thiên Vương (`76` Lua) | Đường Môn (`102` Lua) | Ngũ Độc (`57` Lua) | Nga Mi (`63` Lua) | Thúy Yên (`51` Lua) | Đánh giá Parity trên `pack-tieudaotu` |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Đệ tử chiêu mộ ở thôn trấn & Nhập môn** | Có | Có *(kèm nhánh Tỳ Bà Hoàn Tiểu Ngư)* | Có | Có | Có | Có | **`LOCKED` theo chế độ `CFG_SkillNhapMon = 1`**: Kiểm tra đúng hệ Ngũ Hành, giới tính (Nam Thiếu Lâm, Nữ Nga Mi/Thúy Yên), cấp `>= 10`, gán `FactionId`, `Camp`, truyền tống về môn phái và trao trọn bộ skill 1x–6x ở cấp 0 qua [ClassicSectRecruiterCatalog.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Factions/ClassicSectRecruiterCatalog.cs). |
| **Nhiệm vụ Môn phái 10x – 50x & Học skill theo bậc (`CFG_SkillNhapMon = 0`)** | 1/68 file (`战斗_sll10_敌人01.lua`) | 0/76 file | 0/102 file | 0/57 file | 0/63 file | 0/51 file | **`GAP (Chưa port)`**: Toàn bộ chuỗi nhiệm vụ 10x, 20x, 30x, 40x, 50x trên bản đồ môn phái (Mộc Nhân Hạng, Thiên Vương Đảo Sơn Động, Trúc Tơ Động, Ngũ Độc Mê Cung, Thủy Từ...) chưa có trong `TieuDaoTuExactScriptPathManifest`. |
| **Xuất sư & Trùng phản sư môn (60x)** | Có hội thoại Chưởng môn | Có hội thoại Bang chủ | Có hội thoại Chưởng môn | Có hội thoại Giáo chủ | Có hội thoại Chưởng môn | Có hội thoại Chưởng môn | **`PARTIAL`**: [SectMasterDialogScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Factions/SectMasterDialogScript.cs) đã hỗ trợ Xuất sư (`Camp = 4`) ở cấp `>= 60` và Trùng phản sư môn tốn `50,000` lượng, nhưng bỏ qua phó bản khảo nghiệm Xuất sư (Thập Bát La Hán Trận, Thanh Loa Đảo, Cấm Địa Mê Cung...). |
| **Kỹ năng 90 (Sách Mật Tịch & Nhiệm vụ 90)** | 3 sách (`56, 57, 58`) | 3 sách (`37, 38, 39`) | 4 sách (`45, 27, 46, 28`) | 3 sách (`47, 48, 49`) | 3 sách (`42, 43, 59`) | 2 sách (`40, 41`) | **`PARTIAL`**: [SkillBook90ItemScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Items/SkillBook90ItemScript.cs) đã khớp chính xác 18 quyển bí kíp 90 của 6 phái (cấp `>= 80`, mở ở `Level 1` cho chiêu chiến đấu và `Level 0` cho `351/390/332`). Thiếu chuỗi nhiệm vụ cốt truyện 90 môn phái. |
| **Kỹ năng 110 Thất Truyền** | `1245, 1255, 1256` | `1271, 1273, 1275` | `1248, 1251, 1253` | `1257, 1260` | `1227, 1230` | `1233, 1235` | **`LOCKED`**: Học trực tiếp qua Đệ tử môn phái ([ThatTruyenDialogLibrary.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/GlobalLibraries/ThatTruyenDialogLibrary.cs)) khớp `thattruyen_head.lua`. |
| **Kỹ năng 120 (Bàn Nhược Ba La Mật / Đại Thành 120)** | `709` | `708` | `710` | `711` | `712` | `713` | **`PARTIAL`**: [SkillBook120ItemScript.cs](file:///d:/Projects/jx-reborn/pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Items/SkillBook120ItemScript.cs) đã khớp `lv120skillbook.lua` (`Particular = 1125`, cấp `>= 120`, lưu `Task 2425`). Chưa port chuỗi nhiệm vụ `task/lv120skill` (nếu mở nhận sách qua nhiệm vụ). |

---

## 6. Lộ Trình Khắc Phục Đề Xuất (Prioritized Forecast Roadmap)

1. **Gói P0 — Sửa ngay các sai lệch cơ chế chiến đấu & hào quang cốt lõi (Ước lượng: Nhỏ – Trung bình, tác động trực tiếp PvP/PvE)**:
   - **P0.1 (`SkillEffectiveParams` & Exporter — Sự kiện đạn động)**: Bổ sung đọc `attrId = 267..270` (`skill_startevent`, `skill_flyevent`, `skill_collideevent`, `skill_vanishedevent`) trong `SkillEffectiveParams.Resolve` và `MissileSimulationTickSystem` để khôi phục ngay bông sen băng nổ phụ **`382 Băng Tâm Tuyết Liên`** cho **Thúy Yên `336 Băng Tung Vô Ảnh`** và các chiêu 150.
   - **P0.2 (`MapZoneWorker.StatusEffects.cs` — Chu kỳ hồi máu 10-frame & `lifereplenish_p`)**: Đổi nhịp cộng `LifeReplenishFlat` / `ManaReplenishFlat` từ `% 18 == 0` về **`% 10 == 0`**, gộp cả `EquipmentCombatModifiers` và nhân với `(100 + LifeReplenishPercent) / 100` theo đúng `KNpc::ProcessState @ 0x0809f6f0` (khôi phục đúng tốc độ bơm máu của **Nga Mi `Từ Hàng Phổ Độ` `93`**, **`Mộng Điệp` `89`**, **`Phổ Độ Chúng Sinh` `332`** và tương tác với **Thiên Vương `Đảo Hư Thiên` `708`**).
   - **P0.3 (`PartyAuraTickSystem` & `69 Vô Hình Độc`)**: Xóa bỏ việc tiêm `+10%/+3% allres_p` giả trong `PartyAuraTickSystem`; bổ sung kích hoạt `EventSkills.OnStartSkillId` (`1731` tăng tốc chạy) trong `CastPlayerActiveAura` cho Ngũ Độc `69 Vô Hình Độc`.

2. **Gói P1 — Đóng các khoảng trống nâng điểm kỹ năng & `MagicAttrib` nâng cao**:
   - **P1.1 (`LevelUpScript` Gating)**: Xuất trường `levelUpScript` vào `SkillTemplate` và kiểm tra điều kiện 5 kỹ năng nền (`>= 5` và `>= mlvl + 5`) trong `FactionProgressionManager.TryAllocateSkillPoint` cho `351 Loạn Hoàn Kích`, `390 Đoạn Cân Hủ Cốt`, `332 Phổ Độ Chúng Sinh`, `1110 Tích Lịch Loạn Hoàn Kích`.
   - **P1.2 (`EntityCombatModifiers` & Action Locks)**: Bổ sung các `case` còn thiếu (`177, 178, 193, 209..215, 219..225, 242, 244, 245`) vào `EntityCombatModifiers.AddAttribute`, kiểm tra `ForbidAttackCount` / `FrozenActionCount` trong `HandleCastSkill` & `HandleMovePlayer`, và đối chiếu giới hạn cấp kỹ năng hỗ trợ thấp nhất cho Nga Mi 120 `712 Bế Nguyệt Phất Trần`.

3. **Gói P2 — Chuyển đổi Nhiệm vụ Môn phái cổ điển (`CFG_SkillNhapMon = 0` & Săn cốt truyện)**:
   - Port hệ thống NPC nội môn, rương/cơ quan và quái khảo nghiệm của 6 bản đồ môn phái (`少林派`, `天王帮`, `唐门`, `五毒教`, `峨嵋派`, `翠烟门`) cùng chuỗi nhiệm vụ cốt truyện 90 sang `pack-tieudaotu`.
