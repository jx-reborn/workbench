# Báo Cáo Kiểm Toán Toàn Diện: Cơ Chế Ngũ Hành, Công Thức Sát Thương & Nhịp Tick `18 Hz` (`C++ Binary` vs `JXReborn.ZoneEngine`)

- **Phạm vi kiểm toán**: Đối chiếu nguyên bản mã máy C++ đã dịch ngược trong `legacy-server/discovery/` (`jx_linux_y` ELF x86) với cài đặt C# 14 hiện hành tại `server/src/JXReborn.ZoneEngine/` và `server/src/JXReborn.Primitives/`.
- **Bộ test kiểm toán đi kèm**: `server/tests/JXReborn.ZoneEngine.Tests/Combat/FiveElementsAndFrameBreakpointAuditTests.cs` (`11` bài test neo trực tiếp vào hằng số C++ & dữ liệu thật từ `SharedGameAssetsFixture.Default`, trong đó **4 bài khóa chặt các cơ chế đã khớp 100% (`GREEN`)** và **7 bài tự động phát hiện sai lệch nhị phân (`RED`)** làm đề bài sửa lỗi).

---

## Tổng Quan Kết Quả Đối Chiếu

| Nhóm Cơ Chế | Tổng Hạng Mục | Khớp `100%` | Đang Lệch / Chưa Cài Đặt | Mức Độ Rủi Ro Cao Nhất |
| :--- | :---: | :---: | :---: | :---: |
| **1. Ngũ Hành (Tương Sinh, Tương Khắc, Kháng Tính)** | 5 | 2 | 3 | **CRITICAL** (`GAP-01`, `GAP-03`) |
| **2. Công Thức Sát Thương & Hiệu Ứng Đòn Đánh** | 8 | 6 | 2 | **MEDIUM** (`GAP-06`, `GAP-07`) |
| **3. Nhịp Tick `18 Hz` (`55.5ms`), Đạn `KMissle`, Buff & PK** | 8 | 4 | 4 | **CRITICAL / HIGH** (`GAP-02`, `GAP-04`, `GAP-05`) |
| **Tổng cộng** | **21** | **12** | **9 (8 cụm GAP)** | **3 Critical, 2 High, 3 Medium** |

---

## Phần 1 — Ngũ Hành (`Kim – Mộc – Thủy – Hỏa – Thổ`)

### 1.1. Bảng Tương Sinh – Tương Khắc & Sát Thương Ngũ Hành (`g_FiveElement`)
- **Trạng thái**: **Khớp `100%` trên luồng `ICombatEntityState` chính** (nhưng **Đang lệch** trên overload rút gọn dự phòng).
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `legacy-server/discovery/globals_free/KGlobals_P1_MathSeriesAndMissle.cpp:259-280` (`g_InitSeries @ 0x0808b420`, `g_InternalIsAccrueConquer @ 0x0808c320`):
      - `g_nAccrueSeries`: `Kim(0) -> Thủy(2) -> Mộc(1) -> Hỏa(3) -> Thổ(4) -> Kim(0)`.
      - `g_nConquerSeries`: `Kim(0) -> Mộc(1) -> Thổ(4) -> Thủy(2) -> Hỏa(3) -> Kim(0)`.
    - `legacy-server/discovery/combat/receive_damage/KNpc_ReceiveDamage_SeriesConquerAndHitCheck.cpp:13-39` (`KNpc::ReceiveDamage @ 0x080a1dd0`):
      - Chỉ khi hệ kỹ năng khắc hệ mục tiêu (`g_InternalIsAccrueConquer(g_nConquerSeries, nSeries, this->m_Series) != 0`), độ dịch kháng mới âm: `delta = SeriesDamageP - target.m_CurrentSeriesResist`, trừ phạt chênh cấp PvP `(targetLevel - casterLevel - 10) * nSeriesConquerLevelParam` khi chênh `> 10` cấp; nếu `delta > 0` thì `outSeriesConquerRes = -delta`, ngược lại bằng `0`.
      - Khi bị khắc ngược (`Fire` đánh `Water`), nhánh `else` gán `outSeriesConquerRes = 0` (**không tăng kháng cho mục tiêu** trên kênh nhận sát thương).
  - **C# `ZoneEngine`**:
    - `server/src/JXReborn.Primitives/Combat/FiveElementsSeriesMatrix.cs:11-98`: Khớp tuyệt đối bảng `AccrueTable`, `ConquerTable` và hệ số `120% / 80% / 100%`.
    - `server/src/JXReborn.ZoneEngine/Combat/CombatDamageCalculator.Payloads.cs:479` & `CombatDamageCalculator.Resolution.cs:165-190`: Khớp tuyệt đối công thức `SeriesDamagePercent = skillSeriesDmgP + combatAttacker.SeriesEnhancePercent`, phạt chênh cấp `> 10` giữa `Player/Partner`, và `seriesResDelta = 0` khi khắc ngược.
    - **Lưu ý phụ**: Overload rút gọn `CombatDamageCalculator.ResolveHitAndApplyDamage(uint attackerNpcId, ...)` tại `CombatDamageCalculator.Resolution.cs:985-988, 1077` vẫn còn gán nhầm `seriesResDelta = +payload.SeriesDamagePercent` khi khắc ngược và kẹp `Math.Max(0, resistPercent + seriesResDelta)` làm mất kháng âm.

### 1.2. Chuỗi Kích Hoạt Dòng Ẩn Trang Bị Ngũ Hành (`KItemList::GetEquipEnhance`)
- **Trạng thái**: **ĐANG LỆCH NGHIÊM TRỌNG (`GAP-01` — CRITICAL)**
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**: `legacy-server/discovery/Items/KItemList_EquipAndAttrib.cpp:17-29, 629-655` (`ms_ActivedEquip`, `KItemList::GetEquipEnhance @ 0x08078f20`):
    - Bảng `ms_ActivedEquip[0..9][0..1]` ánh xạ đúng 10 vị trí trang bị chính:
      - `Head(0) <- Body(1), Amulet(6)`
      - `Body(1) <- Ring2(8), Belt(2)`
      - `Belt(2) <- Pendant(9), Cuff(5)`
      - `Weapon(3) <- Amulet(6), Body(1)`
      - `Foot(4) <- Weapon(3), Head(0)`
      - `Cuff(5) <- Foot(4), Ring1(7)`
      - `Amulet(6) <- Belt(2), Ring2(8)`
      - `Ring1(7) <- Weapon(3), Head(0)`
      - `Ring2(8) <- Cuff(5), Pendant(9)`
      - `Pendant(9) <- Foot(4), Ring1(7)`
    - Các ô mở rộng `nPlace` thuộc `10..14` (`Horse`, `Mask`, `Mantle`, `Signet`, `Shipin`): `if (((uint)(nPlace - 10) < 5)) return 3;` — **luôn kích hoạt trọn vẹn cả 3 dòng ẩn `(1, 3, 5)` vô điều kiện!**
    - Với các ô `0..9`: biến đếm `nCount` (`0..3`) cộng **độc lập** từ **3 nguồn tương sinh**:
      1. `g_InternalIsAccrueConquer(g_nAccrueSeries, Player[m_PlayerIdx].m_Series, equipSeries)` (`+1` nếu hệ nhân vật sinh hệ món đồ).
      2. `+1` nếu món ở `ms_ActivedEquip[nPlace][0]` hợp lệ và sinh hệ món đồ.
      3. `+1` nếu món ở `ms_ActivedEquip[nPlace][1]` hợp lệ và sinh hệ món đồ.
      Mỗi đơn vị của `nCount` mở khóa lần lượt dòng ẩn `1`, `3`, `5`.
  - **C# `ZoneEngine`**: `server/src/JXReborn.ZoneEngine/Items/PlayerInventoryGrid.cs:31-43` & `PlayerInventoryGrid.EquipmentStats.cs:201-233`:
    - Bảng `ActivatingPartsByEquipSlot` khớp 100% với `ms_ActivedEquip[0..9]`.
    - **Sai lệch 1 (Ô `10..14` mất sạch 3 dòng ẩn)**: Dòng `201` viết `else if (partIdx < ActivatingPartsByEquipSlot.Length)` (`partIdx < 10`), bỏ rơi hoàn toàn `Horse(10)`, `Mask(11)`, `Mantle(12)`, `Signet(13)`, `Shipin(14)` khiến `ActiveHiddenSlots[0..2]` luôn bằng `false`!
    - **Sai lệch 2 (Điều kiện hệ nhân vật bị nhốt trong `activeCount >= 2`)**: Dòng `219-232` chỉ tính `activeCount` từ 2 trang bị lân cận rồi đặt `if (playerGeneratesTarget)` nằm **bên trong** khối `if (activeCount >= 2)`. Hậu quả: nhân vật hệ Thổ cầm kiếm hệ Kim (chưa mặc áo/dây chuyền hệ Thổ) đáng lẽ mở được **1 dòng ẩn** (`nCount = 1`), hoặc mặc thêm 1 áo hệ Thổ đáng lẽ mở được **2 dòng ẩn** (`nCount = 2`), nhưng `ZoneEngine` chỉ mở tương ứng **0 dòng** và **1 dòng**!

### 1.3. Kháng Tính, Kháng Âm, Trần Kháng `75% -> 95%` & Xuyên Kháng (`CalcCurRes`, `ModifyAllMaxRes`)
- **Trạng thái**:
  - Toán học sát thương (`CalcCurRes`, `ModifyAllMaxRes`, kháng âm, xuyên kháng): **Khớp `100%`**.
  - Tổng hợp thuộc tính từ kỹ năng bị động / bùa chú (`EntityCombatModifiers`): **ĐANG LỆCH NGHIÊM TRỌNG (`GAP-03` — CRITICAL)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `legacy-server/discovery/combat/damage/KNpc_CalcCurRes.cpp:8-18` (`KNpc::CalcCurRes @ 0x08090320`):
      - Nếu `res <= maxRes`: giữ nguyên (kể cả `res < 0` để khuếch đại sát thương qua `(raw * (100 - effRes)) / 100`).
      - Nếu `res > maxRes`: giảm biên lợi nhuận `(((res - maxRes) * (95 - maxRes)) / 400) + maxRes`, kẹp cứng trần tuyệt đối tại `95` (`0x5f`).
    - `legacy-server/discovery/combat/damage/KNpc_ModifyAllMaxRes.cpp:8-15` (`KNpc::ModifyAllMaxRes @ 0x08091b90`):
      - Khi bị hệ khắc chế (`seriesResDelta < 0`), trần kháng tối đa của nạn nhân bị kéo tụt theo:
        `maxResDelta = ((((FiveElementsEnhance - defender.FiveElementsResist) * 100) / (defender.Level * 8 + 200) + 100) * seriesResDelta) / 700`.
    - `legacy-server/discovery/attrib_modify/KNpcAttribModify_Dispatch.cpp:178-179, 219-226, 242, 244-247, 255`: Toàn bộ các thuộc tính Ngũ Hành nâng cao và xuyên kháng đều đi qua `g_MagicModify` cho cả trang bị lẫn kỹ năng/bùa chú.
  - **C# `ZoneEngine`**:
    - `CombatDamageCalculator.Resolution.cs:58-66, 186-189, 261-268`: Công thức `CalcCurRes`, kháng âm, `ModifyAllMaxRes` (`/ 700`), `anti_*res_p` và `anti_maxres_p` khớp chính xác 100%.
    - **Sai lệch (`GAP-03`)**: `server/src/JXReborn.ZoneEngine/Combat/EntityCombatModifiers.cs:551-1029` (`AddAttribute` dùng cho mọi kỹ năng bị động, vòng sáng và bùa chú qua `SkillStateEffectApplier.RecalculateEntitySkillModifiers`) **bỏ sót 16 mã thuộc tính** vốn đã khai báo field trên `EntityCombatModifiers`:
      - `177` (`seriesres_p`), `178` (`seriesenhance_p`), `193` (`poisondamagereturn_v`), `219` (`anti_hitrecover`), `220` (`anti_stuntimereduce_p`), `221` (`anti_poisonres_p`), `222` (`anti_fireres_p`), `223` (`anti_lightingres_p`), `224` (`anti_physicsres_p`), `225` (`anti_coldres_p`), `242` (`anti_maxres_p`), `244` (`magicdamage_p`), `245` (`fasthitrecover_yan_v`), `246` (`five_elements_enhance_v`), `247` (`five_elements_resist_v`), `255` (`add_boss_damage`).

---

## Phần 2 — Công Thức Sát Thương & Hiệu Ứng Đòn Đánh

### 2.1. Ngoại Công Vật Lý & Nội Công Ngũ Hành (`GetWeaponDamage`, `CalcPhysicsAttribDamage`, `GetEngergyDamage`)
- **Trạng thái**: **Khớp `100%`**
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `combat/damage/combat.damage.weapon_damage.json` (`0x08077d70`): Tay không `Strength / 5 + 1`; cầm vũ khí `((base + flat28_29) * (100 + enhance40)) / 100` cộng `Strength / 5` (cận chiến) hoặc `Dexterity / 5` (tầm xa) cộng `m_CurrentAddPhysicsDamage` (`addphysicsdamage_v` `121`).
    - `combat/damage/combat.damage.physics_attrib_damage.json` (`0x080925f0`) & `combat/player/KPlayer_GetWeaponEnhance.cpp` (`0x080c3bb0`): Nhân `%` vật lý kỹ năng (`physicsenhance_p` `65`), cộng hỗ trợ kỹ năng (`addskilldamage1..6` + `skill_enhance`) nhân trên `d1`, cộng `%` thông thạo vũ khí (`addphysicsdamage_p` `126`, ánh xạ `WeaponParticular 0..5, 10->6, 6=all, 7=range, 8=melee, 9=hand+dagger`) nhân trên sát thương nền `base`, sau cùng cộng sát thương điểm kỹ năng (`damage_v` `58`) và `addphysicsmagic_v` (`170`).
    - `combat/player/KPlayer_GetEngergyDamage.cpp` (`0x080c3c40`) & `combat/damage/combat.damage.{cold,fire,light,poison}_attrib_damage.json`: `GetEngergyDamage` chỉ trả về `EffectiveEnergy` khi `m_Kind == 1` (`Player`), trả `0` cho `NPC`; cộng điểm nội lực vào các nhánh nội công (`addcoldmagic_v`, `addfiremagic_v`, `addlightingmagic_v`, `addpoisonmagic_v` hoặc sát thương nguyên tố gốc của kỹ năng nội công `!IsPhysical`).
  - **C# `ZoneEngine`**: `CombatDamageCalculator.Payloads.cs:43-432` & `EntityCombatModifiers.cs:587-755` khớp 100% phép chia nguyên từng bước.

### 2.2. Chí Mạng, Bỏ Qua Né Tránh & Chính Xác vs Né Tránh (`CheckHitTarget`, `SetNpcAttackRating`, `SetNpcDefence`)
- **Trạng thái**:
  - Hàm xác suất trúng đòn (`CheckHitTarget`), `ignoredefense_p` & `deadlystrikeenhance_p`: **Khớp `100%`**.
  - Công thức Chính xác / Né tránh cơ sở từ Thân Pháp (`Dexterity`) của nhân vật: **ĐANG LỆCH (`GAP-07` — MEDIUM)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `combat/hit_check/KNpc_CheckHitTarget.cpp:9-33` (`KNpc::CheckHitTarget @ 0x080951b0`):
      - `effDef = ((100 - nIngore) * nDf) / 100`; `denom = effDef + nAR`; `pct = (nAR * 100) / denom`, kẹp biên `[5, 95]` (`nAR < 0 -> 0`, `nDf < 0 -> 95`, `denom == 0 -> 50`).
    - `combat/receive_damage/KNpc_ReceiveDamage_SeriesConquerAndHitCheck.cpp:54-60`: Chí mạng (`deadlystrike`) chỉ nhân `2` duy nhất kênh vật lý (`damage_physics`), tuyệt đối không nhân kênh Băng/Hỏa/Lôi/Độc/Phép.
    - `Player/KPlayer_P2_AttributesAndStats.cpp:180-195` (`KPlayer::SetNpcAttackRating @ 0x080c3860`, `KPlayer::SetNpcDefence @ 0x080c3820`) & `attrib_modify/KNpcAttribModify_DamageAndAttack.cpp:74-88`:
      - Chính xác nền nhân vật: `m_AttackRating = m_nCurDexterity * 4 - 28`; `attackrating_p` chỉ nhân trên `m_AttackRating` nền, không nhân chồng lên `attackrating_v` cộng điểm!
      - Né tránh nền nhân vật: `m_Defend = m_nCurDexterity / 4`.
  - **C# `ZoneEngine`**:
    - `CombatDamageCalculator.Resolution.cs:17-51, 198-199`: `ComputeHitPercent` và `DeadlyStrike` khớp 100%.
    - **Sai lệch (`GAP-07`)**: `server/src/JXReborn.ZoneEngine/PlayerSessions/PlayerZoneState.cs:436-441`:
      - `EffectiveAttackRating` đang tính `(((EffectiveDexterity * 4) + (Level * 5) + BonusAttackRating + ...) * (100 + AttackRatingPercent)) / 100` — dư `Level * 5 + 28` và nhân `%` chồng lên cả điểm cộng phẳng `BonusAttackRating`!
      - `EffectiveDefense` đang tính `(EffectiveDexterity * 2) + (Level * 2) + BonusDefense` — cao gấp **8 lần** hệ số Thân Pháp của C++ (`* 2` thay vì `/ 4`) và cộng thêm `Level * 2` không tồn tại trong binary!

### 2.3. Độc Sát Cộng Dồn & Nhịp Nhảy Tick Độc (`MixPoisonDamage`, `AddPoisonState`, `ProcessState`)
- **Trạng thái**:
  - Công thức trộn trung bình trọng số (`MixPoisonDamage` & `AddPoisonState`): **Khớp `100%`**.
  - Nhịp nhảy tick độc theo frame & hủy độc khi chủ nhân chết: **ĐANG LỆCH (`GAP-06` — MEDIUM)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `combat/damage/KNpcAttribModify_MixPoisonDamage.cpp:8-34` (`0x080b07f0`) & `combat/state/KNpc_AddPoisonState.cpp:10-49` (`0x08091c10`):
      - `t_new = (t2 * d2 * c1 + t1 * d1 * c2) / (c1 * d2 + c2 * d1)` (trên `AddPoisonState` dùng số thực làm tròn gần nhất, kẹp trần `600,000` cho Player và `123,456` cho NPC);
      - `d_new = (((c1 + c2) * d2 / c2) + ((c1 + c2) * d1 / c1)) / 2`; `c_new = (c1 + c2) / 2`.
    - `combat/state/KNpc_ProcessState_AilmentsAndFeatureTimer.cpp:22-46` (`KNpc::ProcessState @ 0x0809f6f0`):
      - **Quy tắc 1 (Hủy độc khi nguồn gây độc chết/rời map)**: Nếu `Npc[nLauncherIdx].m_Index == 0 || Npc[nLauncherIdx].m_CurrentLife < 1` (hoặc khác `dwID`), gán ngay `m_PoisonState.nTime = 0` và dừng sát thương độc!
      - **Quy tắc 2 (Pha chia hết của `nTime`)**: Mỗi tick giảm `iVar7 = m_PoisonState.nTime - 1`, sau đó kiểm tra `if (iVar7 % m_PoisonState.nInterval == 0)` thì gọi `CalcDamage(..., damage_poison, ...)` (kể cả tick cuối cùng khi `iVar7 == 0`!).
  - **C# `ZoneEngine`**:
    - `CombatDamageCalculator.cs:209-233` & `EntityStatusEffectState.cs:70-126`: Khớp 100% công thức trộn độc và trần `600,000 / 123,456`.
    - **Sai lệch (`GAP-06`)**: `server/src/JXReborn.ZoneEngine/WorldZones/MapZoneWorker.StatusEffects.cs:110-119, 173-182`:
      - Không kiểm tra trạng thái sống/hiện diện của `LastPoisonAttackerNpcId` — kẻ địch chết rồi độc vẫn tiếp tục rút máu nạn nhân đến chết!
      - Đếm xuôi `PoisonTicksSinceLastHit++ >= PoisonIntervalFrames` thay vì kiểm tra `(PoisonRemainingFrames) % PoisonIntervalFrames == 0` sau khi trừ `1`: ví dụ `PoisonRemainingFrames = 15, Interval = 10` trong C++ nhảy sát thương **2 lần** (khi còn `10` frame và khi về `0` frame), nhưng C# chỉ nhảy **1 lần** (ở frame thứ 10)!
      - Thuộc tính `poison2decmana_p` (`135`) đã lưu vào `Poison2DecManaPercent` nhưng chưa chuyển hóa sát thương độc sang trừ Mana trong `ResolvePoisonTickDamage` (`CombatDamageCalculator.Resolution.cs:610-670`).

### 2.4. Phản Đòn, Hút Máu/Nội Lực, Tọa Vọng Vô Ngã (`manashield_p`) & Chuyển Thương Thành Nội (`damage2addmana_p`)
- **Trạng thái**: **Khớp `100%`**
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `combat/damage/combat.damage.shields_and_return_damage.json` & `combat/receive_damage/KNpc_ReceiveDamage_ElementalDamagePipeline.cpp` (`KNpc::CalcDamage @ 0x0809e790`):
      - Thứ tự chuẩn: `StaticMagicShield` -> `DynamicMagicShield` (kể cả kênh `damage_magic`) -> Kháng tính (có bảo vệ tràn số `20,000,000`) -> **Phản đòn cận chiến / tầm xa / độc tính TRƯỚC khi nhân hệ số PvP `m_nPKDamageRate = 20%`** (và giảm theo `ReturnResP` của kẻ tấn công) -> PvP `20%` -> `SorbDamage` (`/ 1000`, trần `500`) -> `ManaShield` -> Trừ máu -> `Damage2AddMana`.
      - **Đặc tính nhị phân của `ManaShield` (Tọa Vọng Vô Ngã — Võ Đang Khí)**: `manaAbsorb = (m_CurrentManaShield * dmg) / 100`. Nếu `CurrentMana >= manaAbsorb` thì trừ `manaAbsorb` nội lực và trừ phần máu còn lại `dmg - manaAbsorb`; nhưng nếu **`CurrentMana < manaAbsorb` (`CurrentMana > 0`)**, nội lực sập thẳng về `0` và **sát thương máu của kênh đó bị triệt tiêu về `0`** (cú đánh làm vỡ khiên nội lực không xuyên lủng vào máu!).
    - `combat/receive_damage/KNpc_ReceiveDamage_LeechKnockBackFatallyStun.cpp:10-42`: Hút sinh lực (`steallife_p`), hút nội lực (`stealmana_p`), hút thể lực (`stealstamina_p`) chỉ tính trên lượng máu thực tế mất bởi **kênh Vật lý** (`physHpLost`), không hút trên sát thương nguyên tố!
  - **C# `ZoneEngine`**: `CombatDamageCalculator.Resolution.cs:491-523, 730-865` tái hiện chính xác 100% toàn bộ thứ tự pipeline và các trường hợp biên trên!

---

## Phần 3 — Nhịp Tick `18 Hz` (`55.5ms`) & Máy Trạng Thái

### 3.1. Breakpoint Tốc Chạy, Tốc Đánh Ngoại Công & Tốc Xuất Chiêu Nội Công (`18 Hz`)
- **Trạng thái**:
  - Công thức khung hình tốc đánh / tốc xuất chiêu & `60%` windup: **Khớp `100%`**.
  - Dòng tốc độ chạy trang bị (`fastwalkrun_p` `111`) & quy tắc gộp Âm/Dương (`Yin/Yan`): **ĐANG LỆCH NGHIÊM TRỌNG (`GAP-02` — CRITICAL)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**:
    - `CharacterActions/SkillActions.cs:20-58` (`DoOrdinSkill`, `OnSkill`) & `AttackMagicActions.cs:25-70`:
      - Tổng số khung hình động tác: `nTotalFrame = (baseFrame * 100) / (100 + speed)` (`basevalue.ini`: `AttackFrame = 18`, `CastFrame = 18`); điểm phát chiêu tại `IsReachFrame(60)` = `(60 * nTotalFrame) / 100`; riêng `CharAnimId == 14` bỏ qua windup (`0` frame).
      - Bảng bậc khung hình chuẩn tại `baseFrame = 18`:
        - `+0%` -> `18f` (`1000ms`), `+20%` -> `15f` (`833ms`), `+29%` -> `13f` (`722ms`), `+50%` -> `12f` (`666ms`), `+64%` -> `10f` (`555ms`), `+80%` -> `10f`, `+101%` -> `8f` (`444ms`), `+126%` -> `7f` (`388ms`).
    - `attrib_modify/KNpcAttribModify_Dispatch.cpp:117, 236-238` & `KNpcAttribModify_StatesAndSpeed.cpp:8-55`:
      - `case 0x6f (111)`: `FastWalkRunP` cộng `%` tốc độ chạy/đi bộ cho cả giày ma thuật lẫn bùa chú (`walkSpeed = (baseWalk * (100 + max(totalYin, totalYan))) / 100`).
      - Tốc đánh / tốc xuất chiêu / tốc chạy chia 2 bể toàn cục trên `KNpc`: bể Âm (`Equip.Yin + Skill.Yin`) và bể Dương (`Equip.Yan + Skill.Yan`), sau đó lấy `max(totalYin, totalYan)`.
  - **C# `ZoneEngine`**:
    - `MapZoneWorker.Combat.cs:318-343`: Công thức `totalAnimFrames`, `castWindupFrames`, `actionFrames` và `CharAnimId == 14` khớp 100%.
    - **Sai lệch 1 (`GAP-02a` — Giày mất 100% dòng tốc chạy)**: Trong `server/src/JXReborn.ZoneEngine/Items/PlayerInventoryGrid.EquipmentStats.cs:586-1037`, hàm `ApplyMagicAttributeToPlayer` (`switch (ma.AttributeId)`) **không có `case 111` (`fastwalkrun_p`)**, cũng thiếu luôn `case 238` (`fastwalkrun_yan_p`), `case 239` (`attackspeed_yan_v`), `case 240` (`castspeed_yan_v`), `case 88` (`lifereplenish_v`), `case 92` (`manareplenish_v`), `case 96` (`staminareplenish_v`)! Mọi đôi giày xanh / hoàng kim có dòng Tốc độ di chuyển (`AttributeId = 111`) khi mang vào người đều cộng **0%** tốc chạy!
    - **Sai lệch 2 (`GAP-02b` — Gộp tách rời Yin/Yan giữa Đồ và Chiêu)**: `PlayerZoneState.cs:605-612, 652-674` tính `Equip.AttackSpeed + max(Skill.Yin, Skill.Yan)` thay vì `max(Equip.Yin + Skill.Yin, Equip.Yan + Skill.Yan)`.

### 3.2. Giật Hình Thọ Thương (`DoHurt` Frame Lock & `fasthitrecover_v`)
- **Trạng thái**:
  - Xác suất giật hình & công thức số khung hình `fasthitrecover_v`: **Khớp `100%`**.
  - Máy trạng thái khóa tư thế `Doing = Hurt` & ngắt di chuyển / xuất chiêu: **ĐANG LỆCH CAO (`GAP-04` — HIGH)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**: `combat/state/KNpc_DoHurt.cpp:10-44` (`KNpc::DoHurt @ 0x08099270`) & `CharacterActions/HurtActions.cs:20-92`:
    - Khi lật đồng xu `GetRandomNumber(0, 99) > 49` (50% cơ hội) và vượt qua `IgnoreNegativeState`, `DoHurt` tính `nTotalFrame = max(1, (((100 - max(HitRecover_Yin, HitRecover_Yan)) + AntiHitRecover) * m_HurtFrame) / 100)`, **chuyển ngay `this->m_eDoing = do_hurt`**, khóa toàn bộ lệnh di chuyển / tấn công / xuất chiêu mới cho tới khi `OnHurt` đếm hết `nTotalFrame` và trả về `DoStand`.
  - **C# `ZoneEngine`**:
    - `CombatDamageCalculator.Resolution.cs:71-106`: Công thức xác suất `> 49`, `IgnoreNegativeState`, và `hurtFrames` khớp 100%.
    - **Sai lệch (`GAP-04`)**: `TryTriggerDoHurt` chỉ ghi `defender.StatusEffects.HurtRecoveryRemainingFrames = hurtFrames` mà **không chuyển `defender.Doing = EntityDoingPosture.Hurt`**, đồng thời cả `MapZoneWorker.Combat.cs:44-55` (`HandleCastSkill`) lẫn `MapZoneWorker.Movement.cs` đều **không kiểm tra `HurtRecoveryRemainingFrames > 0` hay `Doing == EntityDoingPosture.Hurt`**, khiến nhân vật đang bị đấm giật hình liên hoàn vẫn vừa chạy vừa tung chiêu bình thường!

### 3.3. Làm Chậm Băng Sát (`FreezeState` & `freezetimereduce_p`) và Choáng (`stun_p`, `stuntimereduce_p`, `fatallystrikeres_p`)
- **Trạng thái**:
  - Thời lượng băng sát, `freezetimereduce_p` (trần `77%`), choáng `stun_p`, `stuntimereduce_p - anti_stuntimereduce_p` (`< 75` vs `/ 400`), và Đòn chí tử (`fatallystrikeres_p` trên `curLife / 4`): **Khớp `100%`**.
  - Nhịp làm chậm khi trúng Băng sát (`IsFrozen`): **ĐANG LỆCH CAO (`GAP-05` — HIGH)**.
- **Đối chiếu nguồn**:
  - **C++ Ground Truth**: `combat/state/KNpc_ProcessState_AilmentsAndFeatureTimer.cpp:48-62` (`KNpc::ProcessState @ 0x0809f6f0`):
    - C++ **tuyệt đối không chia đôi chỉ số `WalkSpeed` / `RunSpeed` kèm sàn cứng**! Thay vào đó, khi `m_FreezeState.nTime > 0`, cứ mỗi khung hình lẻ `(uVar6 & 1) != 0`, `ProcessState` trả về `1` để **bỏ qua toàn bộ bước tiến khung hình hành động (di chuyển, đánh thường, xuất chiêu)** của thực thể ở tick đó -> làm chậm chính xác **`50%` trên mọi tốc độ gốc** và làm chậm cả tốc độ đánh/xuất chiêu!
  - **C# `ZoneEngine`**: `PlayerZoneState.cs:656, 673` & `NpcZoneState.cs:170, 185`:
    - Đang tính `StatusEffects.IsFrozen ? Math.Max(2, scaled / 2) : scaled` (đi bộ Player), `Math.Max(5, scaled / 2)` (chạy Player), `Math.Max(3, scaled / 2)` (đi bộ NPC), `Math.Max(6, scaled / 2)` (chạy NPC), và **không làm chậm tốc đánh / xuất chiêu**.
    - Hậu quả trực tiếp: Quái vật phổ thông có `WalkSpeed = 3` (hoặc `RunSpeed = 6`) khi bị Băng sát đóng băng có `Math.Max(3, 3 / 2) = 3` -> **tốc độ di chuyển giữ nguyên `100%` (làm chậm `0%`)**! Nhân vật chạy bộ cơ bản `RunSpeed = 10` bị đóng băng rồi giảm tốc thêm cũng chạm sàn cứng `5`.

### 3.4. Đạn Bay `KMissle` (`Fly`, `Collide`, `Vanish`, Xuyên Thấu, Bẫy Mặt Đất, `SubMissle`)
- **Trạng thái**: **Khớp `100%`** trên luồng mô phỏng chính (`MissileSimulationTickSystem`).
- **Đối chiếu nguồn**:
  - `traps/KMissle_PrePareFlyAndOnFly.cpp`, `KMissle_StepFlyAndNearestCollision.cpp`, `KMissle_TrapLifecycleAndCollision.cpp`, `KSkill_CreateMissleAndEvents.cpp` vs `MissileSimulationTickSystem.cs`, `.Collision.cs`, `.TickAndSpawns.cs`, `.Trajectory.cs`:
    - Khớp công thức lưới tròn `CastZone` (`dx^2 + dy^2 <= (N^2)/4`, gốc không lệch `1/2` ô), chu kỳ gây sát thương lặp `DamageInterval + 1` (so sánh `<=`), bẫy đứng yên `MISSLE_MMK_Stand` tái va chạm, `AutoExplode` kích nổ `OnCollide` khi hết đời dù chạm `0` mục tiêu, đạn xuyên thấu (`IsPierce`) vs nổ đơn mục tiêu, sinh đạn con `EventSkills` (`OnFly`, `OnCollide`, `OnVanish`), và thu hồi im lặng `DoVanish(0)` (không nổ `OnVanish`) khi chủ nhân rời bản đồ hoặc đổi Phe / trạng thái PK.

### 3.5. Cộng Dồn / Gỡ Bỏ Bùa Chú `KNpcAttribModify` & Máy Trạng Thái PK (`Peace`/`Fight`/`Slaughter`/`Enmity`/`Exercise`)
- **Trạng thái**:
  - Không trôi chỉ số khi hết hạn bùa chú / chết & chu kỳ PK cốt lõi: **Khớp `100%`**.
  - Các hệ số sửa đổi trị PK nâng cao & khóa bảo hiểm PK: **Chưa nối đầy đủ (`GAP-08` — MEDIUM)**.
- **Đối chiếu nguồn**:
  - **Đã khớp `100%`**:
    - `SkillStateEffectApplier.RecalculateEntitySkillModifiers` (`SkillStateEffectApplier.cs:170-239`) & `MapZoneWorker.StatusEffects.cs`: Tính lại từ đầu trên tập hiệu ứng còn hiệu lực mỗi khi thêm/ghi đè/hết hạn/chết, đảm bảo **0% trôi chỉ số (zero stat drift)** và không phát gói `0x87` rỗng khi hết hạn tự nhiên (`KNpc_ProcessState_StateSkillExpiry.cpp`).
    - `PkStateZoneTickSystem.cs`, `PvpDeathPenaltyCalculator.cs`, `MapZoneWorker.Pk.cs`: Khớp thời gian hồi chuyển về Luyện Công `3,240` frame (`3 phút`) ở map chiến đấu, miễn hồi ngay lập tức ở khu an toàn (`FightMode == 0`), khóa đổi trạng thái khi âm kinh nghiệm `<= -80%`, Cừu Sát `200` frame khởi động -> `10,800` frame (`10 phút`) chiến đấu, Tỷ Thí (`Exercise`) không phạt chết, và công thức rớt kinh nghiệm / bạc (`< 100,000` vs `>= 100,000`, trần cấp `> 129`, rớt `1/2` bạc xuống đất).
  - **Còn thiếu (`GAP-08`)**:
    1. `combat/death/KNpc_DeathCalcPKValue.cpp:77-79` & `KNpc_KilledByNpc.cpp:75-80`: Chưa nối thuộc tính tăng/giảm điểm PK nhận vào (`m_nPkPunishEnhance`, `m_nPkPunishWeaken` — mã thuộc tính `248`/`249`) và xác suất miễn tăng PK (`m_dwNotAddPkValueRate` / `SetNotAddPKValue`). (Lưu ý thú vị trong binary gốc dòng `57`: biểu thức kiểm tra giết cấp thấp `< 51` trong `jx_linux_y` bị biên dịch nhầm thành `Killer.Level * 3 <= Killer.Level * 2` luôn sai với cấp dương, trong khi C# `PvpDeathPenaltyCalculator.cs:67` đang cài theo ý đồ thiết kế `killer.Level * 2 >= victim.Level * 3`).
    2. `Player/KPlayer_P4_PK.cpp` (`KPlayerPK::CheckLockPK @ 0x080dabb0`): Khi `PKValue >= 3` mà nhân vật đang bật khóa bảo hiểm trang bị, C++ cưỡng bức chuyển về `Peace` và đóng Cừu Sát — chưa nối vào `PkStateZoneTickSystem`.

---

## Bảng Tổng Hợp Đề Bài Sửa Lỗi Ưu Tiên (Actionable Fix Roadmap)

| Mã GAP | Mức Độ | Vị Trí File C# Cần Sửa (Ở Pha Fix Tiếp Theo) | Tóm Tắt Chuẩn Hóa Theo C++ Binary | Bài Test Kiểm Toán Tương Ứng (`FiveElementsAndFrameBreakpointAuditTests.cs`) |
| :---: | :---: | :--- | :--- | :--- |
| **GAP-01** | **CRITICAL** | `PlayerInventoryGrid.EquipmentStats.cs:201-233` | Cho `partIdx` thuộc `10..14` bật đủ `ActiveHiddenSlots[0..2] = true`; cộng độc lập `playerGeneratesTarget + aGeneratesTarget + bGeneratesTarget` (`0..3`) để bật `0..3` dòng ẩn. | `FiveElementsEquipmentActivation_WhenPlayerSeriesGeneratesEquipOrHorseEquipped_ShouldMatchGetEquipEnhanceCount` |
| **GAP-02** | **CRITICAL** | `PlayerInventoryGrid.EquipmentStats.cs:586-1037`, `PlayerZoneState.cs:605-612, 652-674` | Bổ sung `case 111` (`fastwalkrun_p`), `238..240` (`*_yan`), `88/92/96` (`*replenish_v`) vào `ApplyMagicAttributeToPlayer`; gộp chung bể Âm/Dương giữa Trang bị + Kỹ năng trước khi lấy `Math.Max`. | `EquipmentMagicAttributes_WhenBootsCarryFastWalkRunPercentAffix_ShouldIncreasePlayerWalkAndRunSpeed` |
| **GAP-03** | **CRITICAL** | `EntityCombatModifiers.cs:551-1029` | Bổ sung đủ 16 `case` thuộc tính (`177, 178, 193, 219..225, 242, 244..247, 255`) vào `EntityCombatModifiers.AddAttribute`. | `SkillStateModifiers_WhenBuffCarriesSeriesEnhanceAndAntiResistAttributes_ShouldAggregateIntoCombatState` |
| **GAP-04** | **HIGH** | `CombatDamageCalculator.Resolution.cs:103-105`, `MapZoneWorker.{Combat,Movement,StatusEffects}.cs` | Khi `TryTriggerDoHurt` thành công, chuyển `Doing = EntityDoingPosture.Hurt`, hủy windup/di chuyển đang dở, chặn `HandleCastSkill` & bước di chuyển cho tới khi `HurtRecoveryRemainingFrames == 0` thì trả về `Stand`. | `DoHurtRecoil_WhenTriggeredDuringCombat_ShouldSetHurtPostureAndBlockCastDuringRecoveryFrames` |
| **GAP-05** | **HIGH** | `PlayerZoneState.cs:656, 673`, `NpcZoneState.cs:170, 185`, `MapZoneWorker.{Movement,Combat}.cs` | Thay phép chia đôi kèm sàn cứng `Math.Max(3, WalkSpeed/2)` bằng cơ chế bỏ qua nhịp hành động ở frame lẻ `(FreezeRemainingFrames & 1) != 0` đúng như `KNpc::ProcessState`. | `ColdFreezeSlow_OnStandardWalkSpeedMonster_ShouldReduceMovementProgressByHalfWithoutArtificialFloor` |
| **GAP-06** | **MEDIUM** | `MapZoneWorker.StatusEffects.cs:110-119, 173-182`, `CombatDamageCalculator.Resolution.cs:610-670` | Hủy `PoisonRemainingFrames = 0` ngay khi `LastPoisonAttackerNpcId` chết/rời map; kích hoạt sát thương độc khi `(--PoisonRemainingFrames) % PoisonIntervalFrames == 0`; nối `Poison2DecManaPercent`. | `MixPoisonDamageAndTickCadence_WhenCasterDiesOrDurationDividesInterval_ShouldMatchProcessStateBehavior` |
| **GAP-07** | **MEDIUM** | `PlayerZoneState.cs:436-441`, `CombatDamageCalculator.Payloads.cs:437` | Đưa Chính xác nền về `Dexterity * 4 - 28` (nhân `%` trên base trước khi cộng điểm phẳng) và Né tránh nền về `Dexterity / 4` theo `SetNpcAttackRating` / `SetNpcDefence`. | `PlayerBaseAttackRatingAndDefense_FromDexterityWithoutEquipment_ShouldMatchSetNpcAttackRatingAndDefence` |
| **GAP-08** | **MEDIUM** | `PvpDeathPenaltyCalculator.cs`, `PkStateZoneTickSystem.cs`, `CombatDamageCalculator.Resolution.cs:962-1080` | Nối `PkPunishEnhance/Weaken`, `NotAddPkValueRate`, `CheckLockPK` (`PK >= 3`), và đồng bộ overload `ResolveHitAndApplyDamage` dự phòng với đường chính. | Ghi nhận trong báo cáo để xử lý cùng đợt dọn dẹp hệ thống PK. |
