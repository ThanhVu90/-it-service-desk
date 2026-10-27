# Học và bảo vệ project DeskOps khi phỏng vấn

## Mục tiêu

Bạn cần tự chạy, sửa, kiểm thử và giải thích được project. Đây là dự án cá nhân có hỗ trợ AI; không ghi thành kinh nghiệm làm việc tại doanh nghiệp. Chỉ thêm vào CV những phần bạn đã hiểu và thực hành.

## Buổi 1 — Chạy và quan sát

Đọc README, chạy demo, đăng nhập lần lượt bốn tài khoản. Viết lại quyền của mỗi vai trò bằng lời của bạn. Tạo một ticket của Alex rồi thử truy cập nó bằng tài khoản Linh. Giải thích tại sao trả 404 trong trường hợp này giúp hạn chế lộ sự tồn tại của dữ liệu.

Kết quả: tự demo được ba phút mà không cần đọc từng bước.

## Buổi 2 — C# và mô hình dữ liệu

Đọc `Domain/Models.cs`: class, record, enum, nullable, property và exception. Phân biệt dữ liệu của ticket với quy tắc đổi trạng thái. Thêm một bài test cho việc nhân viên không được tự nhận xử lý ticket. Hiểu `async/await` và vì sao truy vấn cơ sở dữ liệu không nên bị chặn bằng `.Result`.

Kết quả: giải thích được `Actor`, `Rules.Text` và `Rules.Transition`.

## Buổi 3 — HTTP và quyền truy cập

Đọc `Program.cs`, lần theo GET /api/tickets và POST /api/tickets. Phân biệt 400, 401, 403, 404 và 409. Dùng Developer Tools để xem cookie, JSON và X-CSRF-TOKEN. Giải thích tại sao ẩn nút trên giao diện không đủ để bảo vệ API.

Bài tập: thêm bộ lọc mức ưu tiên nhưng vẫn giữ điều kiện chỉ xem ticket của chính mình.

## Buổi 4 — SQL và giao dịch

Đọc `DeskDb` và `DeskService`. Vẽ quan hệ User–Ticket–Assignment–Asset. Giải thích khóa ngoại, unique index, chỉ mục có điều kiện và transaction. Chạy riêng test `Failed_audit_insert_rolls_back_ticket_creation` rồi đọc phần trigger cố tình gây lỗi.

Kết quả: trả lời được "Nếu lưu ticket thành công nhưng ghi lịch sử lỗi thì sao?" bằng bằng chứng từ test.

## Buổi 5 — Xung đột đồng thời

Mở hai tab cùng một ticket. Nhận xử lý từ tab thứ nhất, rồi thao tác từ bản dữ liệu cũ trong tab thứ hai. Đọc trường Version và cách EF tạo điều kiện cập nhật. Chạy test concurrent handover.

Kết quả: giải thích được vì sao kiểm tra trước khi lưu chưa đủ, và unique index bảo vệ điều gì.

## Buổi 6 — Tự thêm tính năng

Thêm chức năng mở lại ticket đã Resolved, yêu cầu lý do và chỉ requester/admin được thực hiện. Trước khi code, viết bảng chuyển trạng thái mới và các ca bị từ chối. Sau đó sửa domain, API, UI và test. Không chỉ sửa nút giao diện.

Kết quả: một commit do bạn tự giải thích được từ đầu đến cuối.

## Buổi 7 — Review và trình bày

Tự trả lời:

1. Vì sao chọn modular monolith? Khi nào mới cần tách dịch vụ?
2. Vì sao chọn SQLite cho demo? Muốn chạy PostgreSQL thật phải kiểm thử thêm gì?
3. Khi nào trả 409? Có nên tự động ghi đè khi xảy ra xung đột không?
4. Hạn xử lý cuối tuần được tính thế nào? Có phải SLA giờ hành chính không?
5. Audit hiện tại bảo vệ được gì và không bảo vệ được gì?
6. Trước khi dùng cho công ty thật còn thiếu những gì?

## Cách ghi CV sau khi đã nắm vững

**DeskOps — dự án cá nhân C#/ASP.NET Core**

- Xây dựng ứng dụng quản lý yêu cầu hỗ trợ và bàn giao thiết bị, phân quyền Employee/Technician/Admin.
- Áp dụng optimistic concurrency, ràng buộc dữ liệu và giao dịch để xử lý cập nhật đồng thời và đảm bảo lịch sử thay đổi nhất quán.
- Viết/chạy bộ kiểm thử HTTP tích hợp trên SQLite; đính kèm repository và báo cáo kiểm thử.

Chỉnh câu chữ theo phần bạn thực sự làm, hiểu và sửa được. Không tự nhận đã triển khai production hoặc cải thiện năng suất bao nhiêu phần trăm khi chưa đo.
