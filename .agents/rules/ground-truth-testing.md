---
trigger: always_on
---
# Quy Định Bắt Buộc Về Kiểm Thử: Mọi Test Phải Có Ground Truth (NON-NEGOTIABLE)

1. **Nói đến Test là Phải Có Ground Truth**: Bất kỳ khi nào tạo mới, sửa đổi, review hoặc chạy kiểm chứng test (Unit, Integration, Exporter, Transpiler, Compliance, Wire, Gameplay), mọi kỳ vọng (`Assert`) bắt buộc phải neo trực tiếp vào Ground Truth đã xác minh:
   - Với Server / Content Pack / Workbench: đối chiếu trực tiếp từ file gốc `legacy-server/bin/` (`server1/script`, `gateway/s3relay/script`, `server1/settings`, `.pak`/`.wor`), bản decompile đã xác minh trong `legacy-server/discovery/` (hoặc kiểm chứng binary qua MCP `rea`), hoặc gói tin thực tế (`*.capture.json` / `WireParityProxy`).
   - Với Client: đối chiếu trực tiếp từ `legacy-client/discovery/`, tài nguyên `.pak`/`.spr`/`.wor`/`.ini` gốc của Legacy Client, hoặc đặc tả wire chuẩn `JXR-Net v1` / `jxreborn-client.pack`.
2. **Cấm Test Giả Tạo Chỉ Dùng Chuỗi/Số Tự Bịa (No Synthetic-Only Toy Tests)**: Cấm viết test chỉ truyền vài chuỗi/con số tự nghĩ ra (`"Xin chào"`, `"Trang 1"`, ID giả, công thức tự đoán) vào hàm helper rồi tuyên bố tính năng đạt chuẩn. Mọi test cho bộ giải mã, exporter, analyzer hoặc script bắt buộc phải kiểm chứng trên dữ liệu thật, đường dẫn thật, byte thật hoặc trường hợp thực tế trích từ Ground Truth.
3. **Cấm Vacuous Pass & Tautology Rỗng**:
   - Mọi test duyệt danh sách (`foreach` qua attributes, manifest, catalog entries, files) bắt buộc phải `Assert.NotEmpty(...)` (hoặc kiểm tra số lượng thực tế kỳ vọng `> 0`) trước vòng lặp; vòng lặp 0 phần tử không bao giờ được phép báo PASS.
   - Mọi kiểm tra truy vết nguồn (`[LegacySource(path, sha256)]`, `sourceSha256`) phải đối chiếu đường dẫn tồn tại thật và khớp băm SHA-256 của file thật trên đĩa Ground Truth (khi nguồn có mặt), không được chỉ kiểm tra regex định dạng.
4. **Cấm Test Cắt Khúc Bỏ Qua Hành Vi Runtime Thực Tế**:
   - Không được chỉ test hàm parse/export tách rời mà bỏ quên consumer runtime đọc dữ liệu đó (ví dụ: xuất hội thoại `Talk` nhiều trang ra JSON thì phải test cả script runtime phát đủ số trang theo đúng thứ tự).
   - Không được test "hiệu ứng vỏ" thay cho cơ chế thật (ví dụ: `SetProtectTime` phải kiểm chứng miễn sát thương thật trong combat chứ không chỉ kiểm tra gói tin hiệu ứng hình ảnh; `GlobalValue` phải kiểm chứng chia sẻ xuyên suốt giữa nhiều map chứ không cô lập trong 1 map).
5. **Cấm Code Mới Không Test & Cấm Default Method Rỗng `{}` Che Giấu Chưa Implement**:
   - Mọi API, Exporter, Transpiler, Command Handler mới thêm vào `src/` hoặc `tools/` bắt buộc đi kèm Ground-Truth test.
   - Cấm đặt thân hàm mặc định rỗng `{}` nuốt lệnh im lặng cho các API gameplay/UI chưa nối wire/logic thật.
6. **Fail-First TDD & Bất Biến Kỳ Vọng Test**:
   - Trước khi sửa lỗi hoặc đóng lỗ hổng parity, bắt buộc viết test neo theo Ground Truth và quan sát nó **FAIL trước**, sau đó mới sửa code sản xuất để PASS.
   - Tuyệt đối không sửa ngược `Assert` để ép test xanh, trừ khi đưa ra bằng chứng Ground Truth chứng minh kỳ vọng cũ bị sai.
7. **Quy Chuẩn Đặt Tên & Cấu Trúc Test `.NET / xUnit`**:
   - Tuân thủ `1 type / file`, `sealed` concrete test class, đặt trong thư mục con số nhiều theo domain (không đặt ở thư mục gốc của `.Tests.csproj`).
   - Tên hàm `[Fact]` / `[Theory]` bắt buộc theo mẫu `<Subject>_<Condition>_Should<ExpectedOutcome>` và không chứa các từ khóa tiến trình bị cấm (`Spec\d+`, `Phase\d+`, `Adversarial`, `Challenger`, `Empirical`, `RegressionTests`, `DomainTests`).
