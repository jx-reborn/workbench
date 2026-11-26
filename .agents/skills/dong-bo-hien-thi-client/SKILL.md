---
name: dong-bo-hien-thi-client
description: >-
  Diagnose and verify game.exe client-side protocol parsing, UI modal expectations, entity
  appearance sync (NpcRes armor/weapon/horse masks), item container grid mapping, chat color/item
  links, and packet ordering quirks using legacy-client/discovery/ and rea MCP. Use whenever
  game.exe crashes, ignores a packet, renders wrong UI/appearance/animations, or exhibits
  subtle visual/input feel differences against jx-linux.
---

# Đồng Bộ Hiển Thị & Hành Vi Ngầm Của `game.exe` (`dong-bo-hien-thi-client`)

Rất nhiều lỗi khiến người chơi cảm thấy *"server này giả, không mượt như `jx-linux`"* **không nằm ở công thức tính máu trên server**, mà nằm ở **cách binary `game.exe` (`CoreClient.dll` / `S3Client`) giải mã gói tin S2C và cập nhật RAM giao diện**:
- Sai 1 byte độ dài trong bảng `ProtocolSizeTable` của `game.exe` → lệch toàn bộ luồng giải mã TCP phía sau và văng game ngay lập tức!
- Gửi `s2c_syncitem` (`0x5F`) hoặc `c2s_playereatitem` (`0x59`) nhầm quy ước tọa độ ô hành trang vs chỉ số mảng `Item[]` → bấm chuột phải không ăn máu hoặc kẹt vật phẩm trên con trỏ chuột (`ItemExchangeFinish` `0x8E` bị thiếu)!
- Gửi thiếu cờ ngoại trang (`SyncNpcFeature` `0xAD` / `ItemMask` `0x9C` / `HelmType`, `ArmorType`, `WeaponType`, `HorseType`) → nhân vật tàng hình vũ khí hoặc trượt băng thay vì cưỡi ngựa!

Skill này nối trực tiếp kho giải mã **`legacy-client/discovery/`** vào đường ống kiểm chứng của `workbench` và `server`.

---

## 1. Bản Đồ Tra Cứu Nhanh Trong `legacy-client/discovery/`

Trước khi suy đoán tại sao `game.exe` hiển thị sai hoặc không phản hồi một gói tin, tra cứu ngay các file giải mã đã xác minh tại `d:\Projects\jx-reborn\legacy-client\discovery\`:

| Nhóm hiện tượng trên `game.exe` | File Ground Truth cần đọc trong `legacy-client/discovery/` |
| :--- | :--- |
| **Kích thước gói tin S2C/C2S & Hàm xử lý chính (`ProcessNetMsg`)** | `protocol/ProtocolP2_ProtocolProcessHandlers.cpp`, `protocol/ProtocolP1_OperationRequest.cpp` |
| **Đồng bộ NPC, Người chơi, Quái & Gói Script `0x63`/`0xCF`** | `protocol/ProtocolP2_ScriptProtocolAndEntitySync.cpp`, `npc/NpcPartner_SyncAndPlayer.cpp` |
| **Túi đồ `6x6`/`6x10`, Mặc/Tháo đồ 3 bước & Mở khóa chuột (`0x8E`)** | `protocol/ProtocolP2_ContainerMapsAndItemSync.cpp`, `protocol/ProtocolP2_ItemEquipAndExchange.cpp`, `protocol/ProtocolP2_ItemSyncAudioAndSerializer.cpp` |
| **Ngoại trang Vũ khí / Áo / Ngựa / Mặt nạ (`NpcRes`)** | `item/ItemEquipP1_NpcResAndBobo.cpp`, `item/ItemEquipP1_KItemCore.cpp` |
| **Bày bán, Cửa hàng, Sửa đồ & Giá tiền** | `protocol/ProtocolP2_StallAndItemList.cpp`, `core/CoreEconCityWar_GoodsAndBuySell.cpp` |
| **Chat tiếng Việt `TCVN3`, Thẻ màu `[0x02,R,G,B]` & Đính kèm Item** | `protocol/ProtocolP1_ChatLinkAndEmbed.cpp`, `protocol/ProtocolP1_StringTableAndRainbow.cpp` |
| **Bang hội (`KTong`), Công thành & Quan sát** | `protocol/ProtocolP1_TongAndObserver.cpp`, `core/CoreTongP1_ProtocolDispatchAndUiRelay.cpp` |
| **Đăng nhập `Bishop` -> `Goddess` -> Chuyển bản đồ `0x21`** | `network/NetLoginCb_NetAgentAndLogin.cpp`, `network/NetLoginCb_ClientCallback.cpp` |

*(Nếu hàm client cụ thể chưa có trong `legacy-client/discovery/`, dùng skill `/discover-legacy-client` hoặc MCP `rea` mở binary PE của client để giải mã bổ sung).*

---

## 2. 5 Quy Luật Sống Còn Của `game.exe` Phải Luôn Khóa Test

1. **Bắt buộc Khớp Tuyệt Đối `GameExeProtocolVerifier` (`L1` & `ProtocolSafetyGate`)**:
   - Mọi gói S2C có độ dài cố định phải khớp đúng từng byte với `m_nProtocolSize[opcode]` của client; mọi gói độ dài biến thiên phải ghi đúng `ushort length` (hoặc `lengthMinusOne` tùy opcode như `0x91`/`0x92`/`0x97` — tuyệt đối tra `ProtocolP2_ProtocolProcessHandlers.cpp`, cấm đoán `+0` hay `-1`!).
2. **Bắt buộc Gửi Gói Mở Khóa Giao Diện Sau Mọi Thao Tác Túi Đồ**:
   - Khi `game.exe` nhấc/đặt hoặc chia tách vật phẩm, client tự khóa thao tác chuột cho tới khi nhận được gói xác nhận hoàn tất (`s2c_itemexchangefinish` `0x8E` hoặc chuỗi cập nhật vị trí tương ứng). Ngay cả khi server **từ chối** thao tác, vẫn phải gửi gói đồng bộ trả vật phẩm về chỗ cũ + mở khóa, nếu không túi đồ của người chơi sẽ bị đơ cứng cho tới khi thoát game!
3. **Thứ Tự Gói Tin Khi Đăng Nhập & Chuyển Map (`World Entry Handshake`)**:
   - Tuân thủ nghiêm ngặt trình tự: `s2c_syncworld (0x49)` → `s2c_synccurplayer (0x44)` → `s2c_synccurplayerskill (0x45)` → đồng bộ danh sách `s2c_syncitem (0x5F)` & `s2c_syncmoney (0x61)` → `s2c_syncend (0x43)` (chờ nhận `c2s_syncend` mới bắt đầu bơm AOI `3x3` và nhịp `0x46`!).
4. **Đồng Bộ Nhịp Tim `0x46` (`s2c_synccurplayernormal`) ở 18 Hz**:
   - Client `game.exe` tăng biến đếm nhịp nội suy `m_nServerLoop` dựa trên gói `0x46`; nếu ngừng phát hoặc phát thưa gói `0x46`, chuyển động bước chân và hồi chiêu trên màn hình người chơi sẽ khựng giật dù ping mạng bằng `0ms`!
5. **Khóa Kiểm Chứng Bằng `L1` Golden Wire + `L3A+` Capture + `L3B` Headless RAM**:
   - Sau khi xác minh cấu trúc byte từ `legacy-client/discovery/`, viết Golden Wire test tại `JXReborn.Protocol.Tests` (`L1`), kiểm tra replay trên `WireCaptures` (`L3A+`), và nếu liên quan tới cửa sổ UI phức tạp của client thì chạy smoke test `L3B` (`JXReborn.ClientDriver.Tests` đọc trực tiếp `WorldDomSnapshot` từ RAM `game.exe`).
