# 📘 Sổ tay quy chuẩn thương hiệu chính thức: Adam Dùng AI

Sổ tay hướng dẫn chi tiết nhận diện thương hiệu số (Brand Design System) chính thức của **Adam Dùng AI** với tone màu Vàng Hổ Phách & Nền Đen Than (Gold & Matte Charcoal).

---

## 1. Bản sắc thương hiệu & Logo

- **Tên kênh / thương hiệu**: **Adam Dùng AI**
- **Logo chính thức**: File ảnh `assets/logos/adam-logo.png`
  - Hình minh họa chân dung Adam đeo kính đen, mặc vest đen sơ mi trắng, nhìn nghiêng kiên định trên nền vuông màu vàng hổ phách (`#E59A2E` / `#D89B2B`).
  - Khi hiển thị trên video/ấn phẩm: Bo góc lớn `45px`, viền vàng dày `5px solid #D89B2B`, kèm bóng đổ `boxShadow: 0 30px 90px #0008`.
- **Khẩu hiệu cốt lõi**: *Học Có Chọn Lọc · Làm Có Mục Tiêu*

---

## 2. Bảng mã màu chuẩn (Tone Vàng Hổ Phách)

```
[ Matte Charcoal: #111111 ]  --> Nền chính video & canvas
[ Warm Amber Gold: #D89B2B ] --> Màu chủ đạo: Viền, Lưới, Từ khóa chính, Nút số, Footbar
[ Warm Cream: #F8F4E8 ]      --> Văn bản thân bài, giải thích, trích dẫn
[ Pure White: #FFFFFF ]      --> Tiêu đề lớn, số liệu
[ Tech Aqua: #2FD0C8 ]       --> Điểm nhấn công nghệ phụ
[ Card Charcoal: #181818 ]   --> Nền các thẻ Card nổi bật
```

### Chi tiết bảng màu:

| Tên màu | Mã HEX | RGB | Mục đích sử dụng |
| :--- | :--- | :--- | :--- |
| **Matte Charcoal** | `#111111` | `17, 17, 17` | Nền canvas toàn bộ khung hình video |
| **Warm Amber Gold** | `#D89B2B` | `216, 155, 43` | **Chủ đạo**: Logo border, Eyebrow, Text Highlight, Footbar, Grid |
| **Amber Warm Alt** | `#E59A2E` | `229, 154, 46` | Màu nền khối vuông trong Logo chân dung |
| **Warm Cream** | `#F8F4E8` | `248, 244, 232` | Màu chữ giải thích, thân bài, trích dẫn |
| **Pure White** | `#FFFFFF` | `255, 255, 255` | Chữ tiêu đề chính (kết hợp đan xen với từ khóa Gold) |
| **Card Charcoal** | `#181818` | `24, 24, 24` | Bề mặt hộp card nổi bật |
| **Gold Hard Shadow**| `#D89B2B33`| `rgba(216, 155, 43, 0.2)` | Bóng đổ cứng tạo độ sâu cho card |

---

## 3. Hệ thống Typography

1. **Headline Hook (`72–108px`)**: `Segoe UI Black` (`seguibl.ttf`) / `Arial Bold` / `Be Vietnam Pro ExtraBold`
   - Chữ in hoa, khoảng cách dòng sít sao ($0.96$).
   - Kỹ thuật phối màu: Dòng đầu trắng, dòng sau hoặc từ khóa nhấn mạnh màu Vàng Gold.
2. **Eyebrow Header (`24–28px`)**: `Segoe UI Bold` (`segoeuib.ttf`)
   - Chữ in hoa màu Vàng Gold `#D89B2B`, khoảng cách ký tự rộng (`letter-spacing: 0.18em`).
3. **Nội dung thân bài (`36–44px`)**: `Segoe UI Bold` / `Arial`
   - Màu Kem Ấm `#F8F4E8`, độ dày vừa phải, dễ đọc trên màn hình di động.
4. **Footer Wordmark (`22px`)**: `Segoe UI Black`
   - Chữ `ADAM DÙNG AI` màu Vàng Gold đặt ở góc phải trên thanh footer.

---

## 4. Bóc tách 6 Linh kiện UI giao diện chuẩn

### 1. Gold Grid Overlay
- Lưới vàng mảnh đan vuông `90x90 px` với `opacity: 0.16` tạo chiều sâu công nghệ tinh tế trên nền đen than `#111111`.

### 2. Gold Footer Bar
- Thanh kẻ ngang màu vàng `#D89B2B` dày `5px` nằm ở đáy màn hình (cách mép dưới `80px`).
- Góc phải phía trên thanh kẻ có chữ `ADAM DÙNG AI` in hoa màu vàng gold.

### 3. Eyebrow Tagline
- Nằm trên đầu mỗi phân đoạn nội dung (ví dụ: `SAI LẦM CỦA NGƯỜI MỚI`, `BƯỚC ĐẦU TIÊN`, `TẬP TRUNG ĐÚNG HƯỚNG`).

### 4. Hard-Shadow Frame Card
- Khung hộp bo viền vàng `4px solid #D89B2B`, nền đen `#181818`, bóng đổ khối cứng màu vàng `box-shadow: 24px 24px 0 #D89B2B33`.

### 5. Numbered Steps (Quy trình 3 bước)
- Vòng tròn số thứ tự màu vàng (`background: #D89B2B`, chữ đen than `#111111`), nối tiếp bằng mũi tên vàng `↓`.

### 6. Outro Brand Identity
- Logo chân dung Adam bo góc `45px`, viền vàng dày `5px solid #D89B2B`, bóng đổ sâu `0 30px 90px #0008`.
- Eyebrow: `ADAM DÙNG AI`
- Headline: `HỌC CÓ CHỌN LỌC. LÀM CÓ MỤC TIÊU.`
- Body: `Theo dõi để học AI dễ hiểu, thực chiến và có kiểm chứng.`

---

## 5. Âm thanh & Chuyển cảnh

- **Giọng đọc**: `vi-VN-NamMinhNeural` (+10% tốc độ, đanh thép, chắc chắn).
- **Chuyển cảnh Signature**: Gold Wipe (thanh quét ngang/dọc màu vàng `#D89B2B`).
- **Nhạc nền**: Electronic Tech / Editorial Pulse (118 BPM, ducking 0.16 khi có giọng đọc).
