# Aida — Trí tuệ nhân tạo trò chuyện trên dữ liệu của riêng bạn

Aida là ứng dụng chat AI tự lưu trữ (self-hosted) với giao diện tiếng Việt. Nó trả lời câu
hỏi trên các tài liệu bạn tải lên (tìm kiếm tăng cường — RAG trên PostgreSQL + pgvector),
có thể giao việc nhiều bước cho một nhóm nhỏ agent, và áp dụng hạn mức chi tiêu hằng ngày
theo từng người dùng. Ứng dụng chạy dưới dạng Blazor trên .NET 10 trong stack
`docker-compose.yaml` (podman/docker) cùng PostgreSQL và Redis.

## Tính năng

- **Chat** — hội thoại streaming trên các model của OpenRouter (`Models:ChatModels` trong
  `appsettings.json`), danh sách hội thoại bên cạnh, tái tạo / lái lại / hủy câu trả lời,
  nhãn model và biểu tượng nhà cung cấp.
- **Tài liệu & RAG** — tải lên `.pdf`, `.md`, `.docx`, `.xlsx` (nhiều file, mỗi file tối đa
  5 MB), kèm ảnh. Tài liệu được cắt khúc, nhúng vector và tìm kiếm bằng pgvector; câu trả lời
  có trích dẫn về đúng đoạn nguồn. Mỗi người dùng chỉ thấy tài liệu của chính mình.
- **OCR** — PDF quét được đọc ngay tại chỗ bằng Tesseract (`vie+eng`, đã cài sẵn trong image),
  bị giới hạn bởi `Ocr:MaxPages`, `Ocr:OcrTimeoutSeconds`, `Ocr:MinLettersPerPage`.
- **Công cụ cho agent** — `read_document`, `save_document` (xuất câu trả lời ra `.docx` hoặc
  `.pdf`), `web_search` (Tavily, chỉ khi set `WebSearch__ApiKey`), `ask_user`, `tool_search`,
  cùng các công cụ của crew: `parallel_research` và `draft_document` (model và mức thinking
  của Director / Researcher / Writer cấu hình riêng cho từng người trong Settings).
- **Đăng nhập** — Google và tài khoản email/password cục bộ. OAuth Google **bắt buộc ở
  Production**: app từ chối khởi động nếu thiếu. Email nằm trong `Admin:Emails` sẽ tự nhận
  vai trò `Admin` khi đăng nhập Google; môi trường Development có sẵn `/dev-login`.
- **Panel admin** — `/admin` (vai trò Admin): số yêu cầu, token, chi phí theo ngày UTC,
  theo model và theo người dùng, chỉ rõ các lượt chưa có giá.
- **Ngân sách dùng & giới hạn tốc độ** — `Budget:DailyUsd` chặn chi tiêu mỗi người theo ngày
  UTC (`0` = tắt, `Budget:WarningPercent` cảnh báo trước khi chạm hạn); `RateLimit:ChatPerMinute`
  giới hạn lượt chat (`0` = tắt).
- **Sao lưu** — `scripts/backup.sh`: `pg_dump` + lưu trữ cấu hình lên S3 tương thích
  (tự nhận diện `mc` > `aws` > `rclone`), cron tùy chọn, cảnh báo lỗi qua webhook.
- **Tracing** — xuất trace OpenTelemetry tùy chọn (ASP.NET Core, EF Core, HttpClient và các
  span gọi model `Aida.Llm`) khi set `OpenTelemetry__Endpoint`.

## Công nghệ

| Thành phần | Lựa chọn |
|---|---|
| Runtime | .NET 10, Blazor Server (interactive server components) |
| Dữ liệu | EF Core + Npgsql + pgvector, PostgreSQL 17 (`pgvector/pgvector:pg17`) |
| Cache / trạng thái | Redis 7 (key DataProtection, cache phân tán, rate limit dùng chung) |
| LLM | OpenRouter (chat, nhúng vector, bảng giá model trực tiếp) |
| OCR | Tesseract 5 với model `vie` + `eng` (cài trong Dockerfile) |
| File | QuestPDF (PDF), OpenXML (DOCX) |
| Gói chạy | `docker-compose.yaml` / `Dockerfile`, engine mặc định `podman` |
| Kiểm thử | xUnit trong `tests/aida.Tests` |

## Bắt đầu nhanh

```sh
cp .env.example .env
# sửa .env — tối thiểu đặt POSTGRES_PASSWORD (và giữ ConnectionStrings__Postgres khớp
# khi chạy `dotnet run` cục bộ; compose tự dựng lại từ POSTGRES_*)

# compose tham chiếu cả hai ở chế độ external
podman network create edge-network
podman volume create aida-postgresql

podman compose up -d --build
```

App lắng nghe tại `http://127.0.0.1:8080` (Redis tại `127.0.0.1:6379`).

Lần chạy đầu tiên:

- **Production từ chối khởi động nếu thiếu Google OAuth**: phải set
  `Authentication__Google__ClientId` và `Authentication__Google__ClientSecret`, nếu không
  app ném lỗi khi boot (`Missing Google OAuth configuration…`). Tương tự với
  `ConnectionStrings__Postgres`.
- **Quyền admin**: đặt `Admin__Emails__0=<email Google của bạn>` trong `.env` — địa chỉ đó
  sẽ nhận vai trò `Admin` khi đăng nhập Google và mở được `/admin`. Giá trị không bí mật cũng
  có thể ghi vào mảng `Admin:Emails` trong `appsettings.json` (file này đã được commit).
- **Migration**: compose tự chạy khi khởi động (`Database__AutoMigrate=true`).
- **Chạy Development không cần Docker**: tạo DB cục bộ, khai báo `.env`, rồi `dotnet run` và
  mở `/dev-login` để có ngay user dev quyền admin.

## Cấu hình

Tất cả khóa nằm trong `.env` (bắt đầu từ `.env.example`):

| Khóa | Mục đích |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` khi chạy cục bộ, `Production` sau proxy |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | database do container postgres tạo; compose dựng chuỗi kết nối của app từ các khóa này |
| `ConnectionStrings__Postgres` | chuỗi kết nối dùng cho `dotnet run` cục bộ (compose ghi đè bằng `POSTGRES_*`) |
| `OpenRouter__ApiKey` | gọi LLM, nhúng vector và tải bảng giá trực tiếp |
| `Budget__DailyUsd` | hạn chi của mỗi người theo ngày UTC, tính bằng USD; `0` = tắt |
| `Budget__WarningPercent` | phần trăm hạn mức bắt đầu cảnh báo (mặc định `80`; ngoài khoảng 1–100 sẽ quay về 80) |
| `WebSearch__ApiKey` | key Tavily tùy chọn — bật công cụ `web_search` cho agent; bỏ trống là biến mất công cụ |
| `Authentication__Google__ClientId`, `Authentication__Google__ClientSecret` | đăng nhập Google; bắt buộc ở Production (thiếu là app không khởi động được) |
| `S3_ENDPOINT`, `S3_REGION`, `S3_ACCESS_KEY_ID`, `S3_SECRET_ACCESS_KEY` | đích tải backup (`S3_BUCKET`/`S3_PREFIX`, mặc định `s3://aidagent/backups/`) |
| `BACKUP_WEBHOOK_URL` | URL Discord/Slack/Telegram tùy chọn — chỉ dùng để báo lỗi backup |

Các khóa khác thường đặt khi triển khai (qua biến môi trường hoặc `appsettings.json`):

| Khóa | Mục đích |
|---|---|
| `Admin__Emails__0`, `Admin__Emails__1`, … | email được cấp vai trò `Admin` khi đăng nhập Google |
| `RateLimit__ChatPerMinute` | lượt chat mỗi phút mỗi người (mặc định `20`, `0` = tắt) |
| `Database__AutoMigrate` | chạy EF migration khi khởi động (compose đặt `true`) |
| `Redis__Enabled`, `Redis__ConnectionString` | compose đặt `true` + `redis:6379`; `dotnet run` cục bộ mặc định `false` và tự chuyển về cache/DataProtection trong bộ nhớ |
| `OpenTelemetry__Endpoint` | endpoint OTLP nhận trace; không set = không trace |
| `Ocr__Enabled`, `Ocr__Language`, `Ocr__MinLettersPerPage`, `Ocr__MaxPages`, `Ocr__OcrTimeoutSeconds` | giới hạn OCR (mặc định: bật, `vie+eng`, `20`, `50`, `120`) |
| `OpenRouter__BaseUrl`, `Models:*`, `Crew:*` | chọn model và cấu hình crew mặc định trong `appsettings.json` (đã commit, không bí mật) |
| `BACKUP_KEEP_DAYS`, `BACKUP_TZ`, `BACKUP_TRACE_FILE` | thời gian giữ backup (mặc định 30 ngày), múi giờ cron (mặc định `Asia/Ho_Chi_Minh`), file trace |

Giá model **không** cấu hình ở đâu: được lấy trực tiếp từ danh mục OpenRouter khi khởi động
và làm mới mỗi 6 giờ. Model không có trong danh mục hiện là "chưa có giá" trong `/admin`;
nếu làm mới thất bại sẽ ghi log cảnh báo và thử lại ở chu kỳ sau.

## Kiến trúc

`Program.cs` dựng một ứng dụng Blazor Server duy nhất:

1. `Env.Load()` đọc `.env`, rồi đăng ký identity + EF Core kèm kiểm tra **fail-fast** với
   chuỗi kết nối và (ngoài Development) OAuth Google.
2. Khi `Redis:Enabled`, app lưu key DataProtection (đăng nhập sống sót qua lần redeploy),
   cache phân tán và rate limit chat dùng chung trong Redis; nếu không thì tự chuyển sang
   bản tương ứng trong bộ nhớ.
3. Pipeline middleware: forwarded headers → exception handler + HSTS + redirect HTTPS
   (ngoài Development) → authentication/authorization → antiforgery → static files →
   `MapRazorComponents`, endpoint đăng nhập (`/account/*`, `/dev-login`), endpoint tài liệu
   (`GET /documents/{name}`), tự migrate, rồi chạy.
4. Service nền: làm mới giá OpenRouter (6 giờ); các service theo scope xử lý nạp tài liệu,
   tìm kiếm ngữ nghĩa, ghi nhận sử dụng và ngân sách hằng ngày.

`docker-compose.yaml` chạy ba service — `app` (build từ Dockerfile, đã có Tesseract),
`postgres` và `redis`, đều có healthcheck — cùng mạng `edge-network` và volume
`aida-postgresql` ở chế độ external.

## Phát triển

```sh
dotnet run                          # http://localhost:5172 (hoặc https://localhost:7141)
dotnet test                         # test unit; test tích hợp OCR tự bỏ qua khi thiếu AIDA_OCR_IT=1
```

Công cụ cho OCR (cần container engine, `CONTAINER_ENGINE` mặc định `podman`):

```sh
./scripts/ocr-it.sh                 # chạy OCR IT trong container Linux (image SDK + tesseract vie/eng)
./scripts/smoke-ocr.sh [--keep]      # build image app, kiểm tra tesseract/vie/alias bên trong
./scripts/link-tesseract-libs.sh DIR # alias soname của distro cạnh assembly test (đúng việc CI làm)
```

Lưu ý khi chạy cục bộ: `ConnectionStrings__Postgres` trong `.env` phải khớp Postgres đang chạy
— sai mật khẩu sẽ báo `28P01 password authentication failed` từ `dotnet ef`. Với DB cục bộ,
chạy `dotnet ef database update` một lần.

## API

Tài liệu OpenAPI dạng máy đọc được được phục vụ tại `/openapi/v1.json` cho client muốn gọi
trực tiếp các endpoint HTTP.

## Checklist triển khai Production

- [ ] Đã điền `.env` — xem bảng cấu hình ở trên; không bao giờ commit file này.
- [ ] Đã có thông tin OAuth Google: app **từ chối khởi động** ở Production nếu thiếu
      `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret`.
- [ ] Đã đặt `Admin__Emails__0` là email Google của admin.
- [ ] Đằng sau proxy: forwarded headers được tin từ **mọi địa chỉ** (thiết lập một proxy
      duy nhất) để callback OAuth Google nhận được `https`. Nếu expose app thẳng ra internet,
      xem lại `ForwardedHeadersOptions` trong `Program.cs`.
- [ ] `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] Có Redis (compose đã cung cấp) để key DataProtection và bộ đếm rate limit sống sót
      qua các lần redeploy.
- [ ] Sao lưu:

  ```sh
  ./scripts/backup.sh --dry-run      # kiểm chứng pg_dump + archive, không upload
  ./scripts/backup.sh                # lần upload thật đầu tiên
  ./scripts/backup.sh --install-cron # hằng ngày 02:00 (BACKUP_TZ, mặc định Asia/Ho_Chi_Minh)
  ```

  Nội dung: `chatbot_<dd-MM-yyyy_HH-mm-ss>.dump.gz` + lưu trữ cấu hình (`.env`,
  `appsettings*`, file compose, Dockerfile, `docs/`, `prompts/`) tới `s3://aidagent/backups/`;
  dọn sau 30 ngày (`BACKUP_KEEP_DAYS`). Mỗi lần chạy ghi các dòng giai đoạn `[run=<id>]` vào
  `/var/log/aida-backup.log` và một đối tượng JSON vào
  `/var/log/aida-backup-runs.jsonl` (`BACKUP_TRACE_FILE`). Lỗi sẽ báo tới
  `BACKUP_WEBHOOK_URL` (tự nhận diện Discord/Slack/Telegram) kèm cùng `run=` id.
- [ ] Tùy chọn: đặt `OpenTelemetry__Endpoint` (ví dụ `http://<otlp-collector>:4318/v1/traces`)
      để xuất trace, gồm các span gọi model `Aida.Llm` lồng nhau.
