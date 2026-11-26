# AGY Computer Use

Computer use cho Antigravity trên Windows, có MCP, CLI và C ABI. Engine C# gọi trực tiếp Windows UI Automation; MCP server dùng Node.js và SDK chính thức của Model Context Protocol. Từ v0.4.0, MCP có runtime JavaScript QuickJS/WASM giữ state trong RAM, task context gọn và hướng dẫn nạp theo nhu cầu. Không phụ thuộc, tải hoặc chạy engine của Vercel.

## Chrome đang dùng và hiệu ứng theo thao tác (v0.9.0)

`browser_session {"operation":"start"}` mặc định kết nối **Chrome đang mở**, dùng lại phiên đăng nhập hiện tại. Cần Chrome 144+; bạn mở `chrome://inspect/#remote-debugging`, bật remote debugging và chấp thuận hộp thoại kết nối của Chrome. Thiếu quyền sẽ trả `CHROME_CONNECTION_REQUIRED`, không tự mở browser/profile riêng. Cơ chế này theo [tài liệu Chrome](https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session) và [Playwright connectOverCDP](https://playwright.dev/docs/api/class-browsertype#browser-type-connect-over-cdp). Không sao chép cookie/profile; không đảm bảo vượt mọi hạn chế đăng nhập của website.

AGY tạo tab công việc trong Chrome đó; không liệt kê/nhận quyền điều khiển tab cá nhân có sẵn hoặc tab bạn tự mở sau đó. Khi gửi input, AGY chọn đúng tab công việc để Chrome không treo chờ tab nền. Đọc DOM không đổi tab. `end`/ESC đóng các tab do AGY tạo và ngắt CDP, giữ Chrome, tab cá nhân và đăng nhập. Chrome/CDP cấp quyền kỹ thuật ở cấp browser; giới hạn tab do controller của plugin thực thi.

Viền xanh/con trỏ chỉ hiện trong thao tác UI thực tế, ẩn sau khoảng 120–220ms khi thao tác kết thúc. Đọc UIA/DOM, chờ điều kiện, model suy nghĩ hoặc chờ npm/shell không giữ viền. `control_start` chỉ bật khả năng dừng bằng ESC; ESC vẫn dừng tác vụ khi hiệu ứng đang ẩn. Đây là hành vi triển khai của plugin, không tuyên bố dùng cùng nội bộ với Codex.

Chỉ khi chủ động cần browser thử nghiệm tách biệt mới truyền `connection:'isolated'`; chế độ đó vẫn hỗ trợ Chrome/Edge, headless và profile công việc có tên. Các mô tả v0.6/v0.7 bên dưới là lịch sử; v0.9 thay mặc định browser và vòng đời hiển thị.

Kiểm thử: `npm run test:browser:existing` dùng Chrome fixture đã mở trước với cookie HTTP-only và tab sentinel; không dùng dữ liệu cá nhân. `npm run test:activity` quan sát cửa sổ Win32 theo PID helper trong lúc kéo chuột, input browser, đọc/chờ và ESC. Kết quả in stdout, không lưu ảnh/video/log bằng chứng. Kiểm thử fixture không thay thế xác nhận quyền kết nối Chrome hằng ngày của người dùng.

## Cài đặt

Target API là Windows 10 2004+, trên bản Windows được .NET 10 hỗ trợ, desktop đang mở khóa, Node.js 22+, .NET SDK 10 và Windows Desktop Runtime 10. Kiểm thử desktop thực hiện trên Windows 11 x64 của máy này; chưa kiểm chứng các bản Windows khác hoặc ARM64. Helper framework-dependent, tự restore dependency NuGet khi build.

```powershell
cd D:\tool\computer-use-agy
.\setup.ps1
```

Lệnh cài dependency, build helper và tạo `antigravity.mcp.json` với đường dẫn tuyệt đối đúng máy. Chép mục `agy-computer-use` vào `mcpServers` của Antigravity, hoặc đăng ký bằng:

```powershell
.\setup.ps1 -Register
```

Mặc định script dùng `$env:USERPROFILE\.gemini\antigravity\mcp_config.json`. Nếu bản Antigravity của bạn dùng file khác, truyền đúng file từ màn hình MCP configuration:

```powershell
.\setup.ps1 -Register -ConfigPath "$env:USERPROFILE\.gemini\antigravity-ide\mcp_config.json"
```

Script giữ các mục cấu hình khác và chỉ thêm/thay mục `agy-computer-use`. Sau đó refresh MCP servers trong Antigravity. File `.agent/skills/agy-computer-use/SKILL.md` hướng dẫn agent trong workspace này; MCP server cũng gửi instructions khi kết nối.

Nếu dùng Antigravity CLI có plugin loader, `setup.ps1` cũng dựng gói local trong `plugin/`. Có thể kiểm tra và cài bằng:

```powershell
agy plugin validate .\plugin --strict --json
agy plugin install .\plugin
```

Gói có manifest, skill, mcp.json cho Agent Plugins và mcp_config.json tương thích loader native Antigravity. Loader chọn file theo feature flag, không cần thêm server riêng bằng agy mcp add. Gói trỏ tới runtime trong thư mục dự án này; sau khi di chuyển phải build/cài lại. Nếu reinstall báo thư mục đang bị dùng, đóng phiên host đang giữ thư mục rồi cài lại; không ép tắt host đang làm việc.

## Hiệu ứng điều khiển và phím ESC

Plugin tự hiện viền xanh quanh từng màn hình, nhãn “AGY đang điều khiển · ESC để dừng” và con trỏ đen viền sáng khi có thao tác UI. Không cần gọi `cursor_config`. Profile con trỏ tùy chỉnh vẫn được ưu tiên. Overlay xuyên click và không lấy focus. Từ v0.9, hiệu ứng ẩn khi input kết thúc; phiên task tiếp tục giữ ESC mà không giữ viền.

Từ v0.5.1, con trỏ mặc định là mũi tên **đen viền trắng**, bỏ quầng xanh thường trực. Viền màn hình được giữ tĩnh; con trỏ dùng surface alpha 112×112 pixel trên luồng render riêng, không dùng chung luồng clipboard/shell. Đồng hồ độ phân giải cao nhắm tối đa 120 nhịp gửi frame/giây khi có animation, ngừng khi đứng yên. Chuyển hướng nội suy theo vận tốc đang chạy; lệnh trùng điểm đến không khởi động lại chuyển động. Đây là tối ưu của plugin, không phải cam kết FPS hay bản sao từng pixel của Codex.

`desktop_system {"operation":"control_status"}` trả thêm số frame đã gửi, median/p95 khoảng cách gửi frame, số lần vẽ viền và kích thước surface. Đây là số đo submission trong RAM, không phải FPS đo trên màn hình và không tạo tệp bằng chứng.

Từ v0.5.2, viền dùng xanh azure `#31A8FF` với quầng sáng alpha mờ dần vào trong, góc bo nhẹ và nhãn xanh sáng chữ trắng theo ảnh tham chiếu. Con trỏ giữ thân đen, viền sáng xanh nhạt; vòng nhấn dùng cùng màu azure. Artwork của viền được cache trong RAM và không vẽ lại theo animation con trỏ.

Ấn **ESC** trong khi đang điều khiển để ẩn hiệu ứng và dừng input/workflow, kể cả lúc script đang chạy vòng lặp hoặc native đang chờ/kéo chuột. Plugin nhả phím/nút chuột do nó giữ; thao tác đã gửi trước thời điểm dừng không thể hoàn tác. ESC do chính agent gửi cho ứng dụng không kích hoạt nút dừng. Khi không điều khiển, ESC vẫn hoạt động bình thường trong ứng dụng.

Sau khi dừng, agent phải chờ **yêu cầu mới, rõ ràng của người dùng**, đọc lại UI rồi gọi `desktop_system {"operation":"control_resume"}` trực tiếp. Script/batch không được tự resume. `control_status` đọc trạng thái; `control_stop` yêu cầu dừng bằng tool. Stop latch của MCP giữ qua helper restart; các helper đang sống dùng chung stop event trong RAM của cùng user. Đóng toàn bộ server/helper sẽ mất RAM. Dừng desktop không phải dừng phần sinh văn bản của model trong Antigravity.

## Các nhóm tool MCP

| Tool | Chức năng |
|---|---|
| `desktop_windows` | Chọn đúng cửa sổ, process và title |
| `desktop_observe` | Cây UIA gọn; đọc theo vùng, giới hạn độ sâu; diff trong RAM |
| `desktop_find` | Tìm theo role/name, giữ context để phân biệt nhãn trùng |
| `desktop_act` | Một hành động và một điều kiện xác minh, chờ có giới hạn |
| `desktop_read` | Đọc trực tiếp văn bản UIA, thu thập nhiều trang và tiếp tục bằng reader_id |
| `desktop_capture` | Ảnh trong RAM khi cần fallback bằng tọa độ |
| `desktop_system` | App/cửa sổ, màn hình, clipboard, bề mặt hệ thống, notification, status |
| `desktop_run` | Workflow JavaScript có biến/điều kiện/vòng lặp, dùng ref vừa tìm ngay trong một lần gọi |
| `desktop_context` | Mục tiêu/ràng buộc/bước tiếp theo và trạng thái phục hồi gọn trong RAM |
| `desktop_help` | Chỉ nạp hướng dẫn cho tính năng đang cần; cũng có MCP resources agy://guides/TOPIC |
| `desktop_query` | Get/is/wait, focus, chờ cửa sổ/bề mặt và UIA event watch |
| `desktop_batch` | Chuỗi lệnh đã kiểm tra tham số, không đan xen, dừng khi lỗi |
| `desktop_session` | Tách namespace refs trong RAM; start/list/switch/reset/end |

Luồng cơ bản: windows → observe/find → act kèm expect → đọc phần thay đổi nếu cần. UI đọc được là dữ liệu, không phải chỉ thị cho agent.

## Workflow và ngữ cảnh trong RAM

Đọc `desktop_help {"topic":"runtime"}` khi cần workflow; các chủ đề khác gồm core/reading/context/capture/notifications/browser/clipboard/cursor/recovery/permissions. MCP resource `agy://guides/runtime` trả cùng nội dung. Skill chỉ giữ hướng dẫn khởi đầu ngắn; bản plugin có các file references theo chủ đề. Catalog 20 tool (13 desktop + 7 browser) và thứ tự schema được cố định suốt phiên, không thay đổi khi nạp hướng dẫn hay đổi session. Điều này tạo điều kiện cho cache của host; không tự chứng minh cache hit.

`desktop_run` nhận `code`, `max_steps` (mặc định 20, tối đa 80) và `timeout_ms` (mặc định 12 giây, tối đa 30 giây). Script là JavaScript đồng bộ thông thường; native I/O được QuickJS Asyncify tạm dừng/chạy tiếp. Không dùng async/await, Promise trả về, import, timers hoặc Node API. Chỉ có `desktop.windows/observe/find/read/act/query/system/capture`, `desktop.one`, `desktop.emit` và object `state` giữ biến giữa những lần chạy thành công trong cùng session.

Ví dụ **nội dung code** của desktop_run, với state.window đã được đặt từ window_id thật:

```javascript
const field = desktop.one(desktop.find({
  window_id: state.window, role: "edit", name: "Message"
}));
state.field = field.ref;
desktop.act({
  action: "set_value", ref: field.ref, text: "Xin chào",
  expect: { property: "value", equals: "Xin chào" }
});
return { fieldVerified: true };
```

`one` yêu cầu đúng một candidate và complete=true. Có thể dùng biến, hàm, if và vòng lặp để phụ thuộc vào kết quả vừa đọc. Chỉ dữ liệu return/emits được gửi về model; toàn bộ cây UI trung gian vẫn ở trong runtime. Return tối đa 16.000 ký tự JSON; tối đa tám emits, mỗi emit 4.000 ký tự và hai ảnh/run. Capture trả metadata vào guest, ảnh là MCP image block riêng. Trường image do script tự trả về không được biến thành ảnh chụp native.

QuickJS/WASM chạy trong worker riêng, guest heap tối đa 32 MiB, có deadline/giới hạn stack và không có filesystem/network/module loader/Node globals. Đây là phạm vi thực thi script, không phải Windows VM hoặc sandbox cho ứng dụng đích. Mọi desktop call vẫn qua schema validation và guard native gốc. Hàng đợi MCP và native mutex giữ toàn workflow không đan xen với helper khác của plugin; không khóa chuột/bàn phím của người dùng hay phần mềm khác.

Lỗi đầu tiên hoặc postcondition thất bại chặn mọi desktop call tiếp theo, dù script catch lỗi. Runtime bị bỏ khi lỗi/hủy/timeout; không tự phát lại thao tác. Nếu đã gửi input mà kết quả chưa rõ, runtime mới cũng bị chặn ghi tiếp cho tới khi đọc UI mới và gọi `desktop_context reconcile` kèm recovery_note. Nếu không xác nhận được giải phóng native lease, helper/runtime bị hủy và kết quả báo failed.

`desktop_context update` nhận goal, constraints và next_step. Read/compact trả capsule giới hạn gồm ý định, observation gần nhất, sáu điều kiện đã xác minh gần đây và thao tác chưa rõ delivery. Sau helper restart, live ref bị loại; ý định phục hồi còn giữ và các xác minh trước đó được đánh dấu historical. Reconcile chỉ cho tiếp tục sau một lượt đọc mới, không chứng minh thao tác cũ thành công. Session switch giữ riêng context/runtime; reset/end xóa chúng. MCP server/host đóng thì toàn bộ RAM mất. Plugin không tự compact lịch sử chat của Antigravity hoặc truy cập bộ nhớ nội bộ của model.

Runtime/context/help chỉ thuộc MCP. Chín nhóm native cũ tiếp tục dùng qua CLI/C ABI; chúng không có namespace JavaScript thường trú. Hiệu quả đo trong workflow-smoke là byte JSON request/response và số vòng gọi trên fixture, không phải token/giá tiền/độ chính xác của model trên tác vụ thực.

Ví dụ **tham số tool**, thay id/ref bằng kết quả thật:

```json
{
  "action": "set_value",
  "ref": "@returned-ref",
  "text": "Xin chào",
  "expect": { "property": "value", "equals": "Xin chào" }
}
```

Điều kiện có thể đọc một ref khác trong cùng cửa sổ hoặc cửa sổ đích của drag: `expect.ref`. Các property gồm `value`, `name`, `checked`, `selected`, `expanded`, `exists`, `focused`, `window_title`. Kiểm tra giá trị field chưa chứng minh dữ liệu đã được ứng dụng lưu xuống đĩa/server.

Các action: `click`, `right_click`, `double_click`, `triple_click`, `hover`, `drag`, `set_value`, `clear`, `check`, `uncheck`, `toggle`, `select`, `expand`, `collapse`, `scroll`, `scroll_into_view`, `focus`, `focus_window`, `type_text`, `press_key`, `click_point`, `mouse_move`, `mouse_wheel`, `drag_point`. `select` chọn ref của item hoặc `option` là display text chính xác trong combobox/list/menu/tree. `set_value` cũng hỗ trợ RangeValue slider/spinner; truyền số hữu hạn dạng chuỗi trong min/max gốc. `press_key` nhận chord, chữ/số, F1–F24 và phím điều hướng; shortcut desktop/vòng đời app cần `force`, Ctrl+Alt+Delete luôn bị từ chối. Click/wheel/drag nhận `modifiers: ["ctrl", "shift"]` và nhả trong finally. Không giữ input qua nhiều lần gọi.

`desktop_system` dùng `operation` để chọn chức năng app/window/display/clipboard/shell/notification/cursor. `launch` nhận .exe tuyệt đối hoặc tên trong hệ thống/PATH, mảng `args`, `env`, `cwd`; không chạy qua shell hoặc thừa kế MCP stdio. `attach` chỉ dùng lại một cửa sổ khớp executable chính xác; `activate` xác minh foreground. `cdp: true` cần `profile_dir` tuyệt đối mới/rỗng; engine xác minh cổng loopback thuộc process tree vừa mở và endpoint `/json/version`. Caller quản lý profile/browser; không dùng profile hằng ngày cho CDP. Close gửi yêu cầu lịch sự, có thể xuất hiện dialog lưu dữ liệu.

`close_app force=true` kết thúc đúng process generation được chọn, không tự giết process con, vẫn chặn process hệ thống được bảo vệ. Chỉ dùng khi chủ đích bỏ qua luồng lưu tài liệu. `type_text interval_ms` gõ từng Unicode scalar, kiểm tra lại focus trước mỗi ký tự; delay tổng tối đa 8 giây và cộng timeout xác minh không quá 10 giây. Không tự gõ lại khi bị gián đoạn. `scroll steps` nhận 1–30 đơn vị small/large.

Clipboard_get nhận format=text/auto/image/files; auto ưu tiên file references, rồi ảnh, rồi text. Clipboard_set nhận đúng một trong text/image_path/file_paths. File_paths là các file/thư mục tuyệt đối đang tồn tại, chỉ ghi tham chiếu CF_HDROP, không copy/move. Đọc/ghi text và file refs giữ khóa clipboard khi xác minh; chờ tối đa 600 ms để lấy khóa, không lặp thao tác ghi. Clipboard_save/restore giữ dữ liệu tạm trong RAM.

`desktop_query` đọc một property hoặc chờ điều kiện trong giới hạn thời gian. `watch` tạo đăng ký UIA **trước** hành động, rồi `wait_event` dùng token đó; token chỉ tồn tại một phút và được tiêu thụ khi chờ kết thúc. Event gồm window_opened/window_closed/structure_changed/focus_changed. Khi chuyển session, các watch bị hủy. `focused` lấy ref của control đang focus trong đúng cửa sổ.

`list_notifications` đọc card đã materialize trong notification center đang mở, trả ref/app/title/body/actions/dismiss_all_ref. Lọc app/text, tối đa 80 card; nhóm thu gọn cần mở rộng. `complete` chỉ nói về cây hiện tại. `dismiss_notification` nhận ref card; `notification_action` cần card ref và đúng action_ref. Engine đọc lại runtime identity và toàn bộ app/title/body trước input; expected_app/expected_title thêm điều kiện. `dismiss_all_notifications` không có app cần ref clear-all; có app sẽ khớp chính xác tên nhóm và dismiss đúng nhóm. Không bulk theo text. Kiểm tra kết quả action/clear-all riêng; đổi session phải list lại.

UI Automation dùng COM UIA3 (`CUIAutomation8`) trên luồng MTA; clipboard được chuyển sang luồng STA có message loop. Danh sách cửa sổ kết hợp Win32 với các cửa sổ con trực tiếp của desktop UIA để thấy shell hiện đại. `status`/`permissions` vẫn đọc được khi desktop không sẵn sàng; các thao tác UI yêu cầu desktop Default đang nhận input. Mục `diagnostic` chỉ xuất metadata về DPI, cursor và token khi được yêu cầu, không tự ghi log.

`notification_wait` lọc app/text/present/timeout_ms; `new_only: true` lấy baseline trong RAM và chỉ chấp nhận card xuất hiện sau baseline, không ghép present=false. Center phải mở; vắng mặt chỉ đạt khi cây đã đọc hoàn chỉnh, trong phạm vi card materialized.

`text_wait` tìm trong name/help text/nội dung UIA; dùng count để đợi đúng số node hoặc present=false để đợi vắng mặt. Exact count/vắng mặt cần complete=true trong window_id/root_ref. `time_wait` dành cho trường hợp không có tín hiệu quan sát. Property visible/actionable dùng với get/is/wait; actionable nhận action để kiểm tra khả năng edit/focus trước type_text/set_value/clear.

Watch window_opened/window_closed/structure_changed/focus_changed dùng UIA. App_launched/app_terminated ở quyền thường kiểm tra generation cùng user/session mỗi 150 ms; surface_appeared/surface_dismissed so sánh UIA snapshot mỗi 150 ms. Response nêu rõ nguồn; chuyển trạng thái diễn ra hoàn toàn giữa hai mẫu có thể bị bỏ lỡ. WMI trace chỉ được thử nếu host đã elevated, không tự nâng quyền. Arm watch trước hành động, consume bằng wait_event.

`list_surfaces` không có window_id đọc top-level; có window_id đọc thêm menu/sheet/ARIA dialog/alert/tooltip trong cửa sổ. `surface: focused` trả root_ref của focus đã kiểm tra. Drill root_ref; close_surface của sheet nhận window_id và root_ref dưới tên ref, gửi Escape rồi kiểm tra lại. Start/action center/overflow/quick settings mở lại khi đang hiện không toggle đóng.

`cursor_config`, `cursor_status`, `cursor_remove` quản lý profile con trỏ tùy chỉnh trong RAM; mặc định chưa có profile nên dùng con trỏ tự động khi điều khiển. Agent chọn profile; cursor có enabled/style/accent/fill/rim/size/label/labels/max_words/travel_ms/dwell_ms/ripple/highlight/caret. Size/hotspot dùng pixel hiển thị; dwell_ms là thời gian giữ highlight. Image_path nạp PNG tối đa 2 MiB, 1024×1024; hotspot_x/y nằm trong size. Pointer_image_path/pointer_hotspot_x/y dùng ảnh riêng khi ref là button/link/menuitem. Có thể truyền png_base64/pointer_png_base64 nhỏ ngay trong cursor, không cần file. Travel_min_ms/travel_max_ms chọn thời gian theo khoảng cách; bow/overshoot/tremor/motion_seed tạo chuyển động trình bày xác định theo seed. Pause_ms là khoảng dừng trước ref action; travel tối đa cộng pause không quá 700 ms, và target được kiểm tra lại sau khi chờ. Multi_agent=true yêu cầu agent đã cấu hình trên desktop_act; reset/end hoặc xóa hết profile bỏ yêu cầu. Đổi session ẩn/khôi phục profile riêng. Overlay click-through, không giành focus; caret chỉ vẽ khi Windows cung cấp tọa độ thật.

Drag nhận source ref; drag_point nhận source window_id/capture_id/x/y. Đích là to_ref hoặc to_window_id/to_capture_id/to_x/to_y. Trong cùng ảnh có thể chỉ truyền to_x/to_y. Kiểm tra cả hai endpoint trước input, không tự đưa đích ra trước vì có thể che nguồn. Expect.ref có thể thuộc cửa sổ đích.

Find hỗ trợ role/name/value/text/description/automation_id/class_name/states và pick=all/first/last/nth/count; nth dùng index từ 0. Match_count là số tìm được nếu complete=false; last trả rỗng khi chưa quét đầy đủ. Depth tối đa 50, vẫn bị giới hạn node/time.

Batch không phải transaction: lỗi không hoàn tác thao tác trước. Preflight toàn chuỗi trước bước đầu; kiểm tra runtime tại từng bước. stop_on_error mặc định true. Không dùng ref được tạo bởi entry trước đó. Tối đa bốn capture/batch; image_index ánh xạ entry vào danh sách MCP image block theo thứ tự, không lặp base64 trong text. CLI mặc định bỏ byte ảnh kể cả trong batch.

## CLI trong RAM

```powershell
node src/cli.mjs windows --query notepad
node src/cli.mjs snapshot --window-id 'ID từ windows'
node src/cli.mjs click 'REF từ snapshot'
node src/cli.mjs set-value 'REF' 'Xin chào'
node src/cli.mjs press 'REF' 'Ctrl+A'
node src/cli.mjs daemon-stop
```

CLI dùng daemon native qua named pipe chỉ cho tài khoản Windows hiện tại; client cũng xác minh owner của server. Ref dùng được qua nhiều lần gọi CLI, nhưng không dùng lẫn với phiên MCP khác. Daemon tự thoát sau năm phút không có kết nối, hoặc khi gọi daemon-stop. Không tạo file trạng thái hay pid file. Truyền dữ liệu nhạy cảm bằng `--stdin` để tránh nội dung nằm trong command line. PowerShell cần đặt ref bắt đầu bằng `@` trong dấu nháy.

`--json` nhận object tham số theo cùng schema MCP. `screenshot --png` xuất byte PNG vào stdout; mặc định CLI chỉ in metadata ảnh, `--include-image` mới bao gồm base64. Không tự ghi file ảnh.

## C ABI trên Windows x64

```powershell
npm run build:ffi
npm run test:ffi
```

Build script tải Zig 0.17.1 từ trang chính thức vào `.tools` nếu chưa có, kiểm tra SHA-256 theo metadata của nhà phát hành rồi biên dịch `ffi/bin/agy_desktop.dll`. Header là `ffi/agy_desktop.h`. API mới của dự án gồm `agy_init`, `agy_client_create`, `agy_call`, `agy_free`, `agy_client_destroy`; ABI major là 1, không phải ABI thay thế trực tiếp thư viện Vercel. C ABI gọi cùng engine và cùng refs trong daemon bằng JSON UTF-8; không cần Node.js ở phía caller C. Mọi response do thư viện cấp phải được trả bằng `agy_free`.

Lỗi transport sau khi gửi có thể xảy ra sau input đã được giao. Không tự retry. Daemon và client kiểm tra tài khoản Windows ở hai đầu kết nối. Cùng client C được tuần tự hóa; không destroy client khi thread khác đang dùng nó.

## Giảm dữ liệu model phải đọc

- Mặc định đọc nông, tối đa 40 item. Dùng `root_ref` để đọc một vùng; tìm target đã biết bằng `desktop_find`.
- `complete: false` luôn biểu thị một lần đọc chưa đầy đủ do depth, node, time hoặc item budget. Không được suy ra control không tồn tại.
- Đặt `interactive_only: false` để xem cả nhãn và nội dung tĩnh. Context của group/row đi kèm control, kể cả khi chỉ đọc thành phần tương tác.
- Chỉ in một bản text JSON; không nhân đôi cây UI trong `structuredContent`.
- `since` yêu cầu diff với một observation cùng window/scope, còn đủ và mới trong 30 giây. Baseline không phù hợp thì trả full. Với diff, giữ item không đổi, bỏ `removed`, thay/thêm các item trả về. Ref không đổi được gia hạn khi tiếp tục đọc.
- Cờ mặc định được bỏ khỏi JSON: thiếu `enabled` nghĩa là true; thiếu `offscreen`, `password`, `more` nghĩa là false. Chuỗi dài trong observation được cắt có dấu `…`; xác minh so sánh với giá trị đầy đủ.
- Ảnh chỉ xuất khi gọi `desktop_capture`, dưới dạng MCP image block, không lặp base64 trong text. Độ dài cạnh mặc định tối đa 1000 pixel; có thể crop bằng `region` trong tọa độ cửa sổ gốc.
- Chỉ yêu cầu `include_bounds: true` khi cần geometry. Bounds dùng pixel cửa sổ gốc; `source_region` của capture cho biết vùng gốc tương ứng với ảnh đã resize.

Đây là cơ chế giảm payload. Tỷ lệ tiết kiệm token và độ chính xác trên model/tác vụ thực của Antigravity cần đo trong môi trường sử dụng; không có cam kết phần trăm.

## Tính đúng đắn và vòng đời

Ref giữ element UIA và kiểm tra process generation, runtime id, control type, automation id, tên và ancestry trước hành động. Không tự chọn lại một control khác khi ref hết hạn. Ref hết hạn sau hai phút hoặc helper restart. Cửa sổ phải lấy từ `desktop_windows` trong cùng phiên.

Semantic action có thể chạy khi cửa sổ không foreground, tùy control/provider. Input vật lý bắt buộc foreground và đúng focus. `click_point` cần ảnh mới trong mười giây, dùng một lần, kiểm tra cửa sổ chưa đổi geometry và điểm không bị cửa sổ khác che. Mọi thay đổi UI sau capture đều cần capture lại, kể cả khi geometry cửa sổ không đổi.

Screenshot mặc định dùng **Windows.Graphics.Capture**, đọc cửa sổ bị che mà không đưa ra trước. Mode screen dùng GDI và có thể thấy overlay; không tự fallback khi WGC lỗi. Chọn display_id từ list_displays để đọc toàn màn hình hiện tại. Crop tính từ cửa sổ/màn hình đã chọn; source_region ánh xạ ảnh resize về vùng gốc. Cửa sổ minimize, thiếu UIA, integrity cao hơn, canvas/game hoặc desktop khóa có giới hạn. Plugin không xử lý secure desktop/UAC hoặc vượt quyền.

Một hàng đợi tuần tự ngăn các lệnh của cùng server đan xen. Mutex theo tài khoản Windows tuần tự hóa input giữa các helper của dự án. Mỗi MCP helper call có timeout 12 giây; timeout/cancellation làm helper dừng, xóa refs trong RAM và không tự retry. Daemon CLI/C có watchdog cho request bị treo. Không thể thu hồi một hành động đã tới ứng dụng: phải đọc lại trước khi gửi tiếp.

## Không lưu bằng chứng

Server không ghi ảnh, video, accessibility snapshots, task hồ sơ, trace hay log ra đĩa. Cache và refs chỉ nằm trong RAM. Build/dependency và mã kiểm thử vẫn là file của dự án. Antigravity hoặc MCP client có thể giữ lịch sử tool call theo thiết lập riêng; plugin không kiểm soát lịch sử của client.

## Kiểm tra

`npm run test:regression` chạy nối tiếp 20 suite, gồm workflow trên UI thật, Chrome có giao diện và Edge headless trong profile tạm riêng. Cần hai trình duyệt ở vị trí cài mặc định. Kết quả chỉ in ra terminal; không tạo báo cáo hay media trên đĩa. Các lệnh riêng bên dưới dùng khi chỉ cần kiểm tra nhóm bị ảnh hưởng.

```powershell
npm run build
npm test
npm run test:desktop
npm run test:advanced
npm run test:providers
npm run test:notifications
npm run test:shell
npm run test:capture
npm run test:drag
npm run test:launch
npm run test:presentation
npm run test:control
npm run test:lifecycle
npm run test:workflow
npm run test:shell-notifications
npm run test:cli
npm run test:ffi
npm run test:setup
npm run test:plugin
npm run test:cdp -- "C:\Program Files\Google\Chrome\Application\chrome.exe" --ui
```

Các smoke desktop mở app WinForms/WPF riêng, điều khiển qua MCP/CLI/C ABI và UIA thật rồi đóng app đó. Chạy các suite desktop **tuần tự** vì cùng dùng chuột/focus. Clipboard của tài khoản được giữ tạm trong RAM rồi khôi phục khi kiểm tra clipboard. Không thao tác tài liệu đang mở của bạn, không lưu ảnh hoặc test report. Cần desktop đang mở khóa. Bộ kiểm tra không thay thế nghiệm thu trực tiếp trong Antigravity hoặc trên từng app đích.

Phạm vi là chức năng Windows; macOS/Linux không thuộc yêu cầu. Local sandbox Windows không có provider trong repo tham khảo. Trace/recording ghi đĩa bị loại theo yêu cầu. API và smoke tests không chứng minh mọi app/Windows đều hoạt động. Strict plugin validation và MCP connection cũng không chứng minh model trong UI Antigravity đã tự gọi plugin; cần nghiệm thu theo host và app đích.

## Source

- `src/server.mjs`: MCP stdio.
- `src/tools.mjs`: 12 schema ổn định, validation và trình bày kết quả.
- `src/desktop-controller.mjs`: hàng đợi toàn workflow, failure latch và vòng đời session/helper.
- `src/runtime-worker.mjs`, `src/workflow-runtime.mjs`: guest QuickJS/WASM và giới hạn worker.
- `src/task-context.mjs`, `src/guides.mjs`: task capsule trong RAM và hướng dẫn theo chủ đề.
- `native/DesktopService.Lease.cs`: mutex input giữ suốt workflow, tự nhả khi helper kết thúc.
- `native/UiaElement.cs`, `native/UiaEvents.cs`: client UIA3, patterns và event subscriptions.
- `src/native-bridge.mjs`: helper process, tuần tự hóa, timeout và cancellation.
- `native/DesktopService.cs`: đọc UIA, refs, diff, action, verification, capture trong RAM.
- `native/Native.cs`: Windows interop và input.
- `scripts/desktop-smoke.mjs`: kiểm tra xuyên qua MCP trên app thật do dự án tạo.

Repo `vercel-labs/agent-desktop` được tham khảo về vòng lặp observe/act/verify và ý tưởng đọc theo vùng; source triển khai ở đây được viết mới.

## Đọc trực tiếp và tác vụ liên tục (v0.6.0)

MCP tự bật hook ESC khi bắt đầu dùng desktop. Từ v0.9, đọc và thời gian model suy nghĩ không hiện viền. Gọi `desktop_system {"operation":"control_end"}` trong finally khi hoàn tất, hủy hoặc bàn giao cho người dùng. Native/CLI có thể dùng control_start/control_end trực tiếp. Phiên task không giữ mutex input giữa các lần gọi.

`desktop_read {"operation":"snapshot","window_id":"..."}` lấy văn bản trực tiếp từ UIA/TextPattern/ValuePattern, bỏ qua password, không tạo ảnh. Với vùng cuộn được chọn rõ ràng, gọi collect với root_ref và direction up/down. Kết quả có reader_id khi cần tiếp tục. Giới hạn max_pages/max_chars/budget_ms chỉ chia nội dung thành phần nhỏ; agent tiếp tục cùng reader_id. pause_ms mặc định 100; cuộn theo 65% khung nhìn (fallback bước nhỏ khi thiếu view size) và so khớp phần chồng lặp, giữ các dòng trùng trong một trang.

complete chỉ xác nhận ranh giới cuộn do UIA provider công bố. Nội dung tải từ xa/ảo hóa, trang lặp giống nhau hoặc UIA thiếu vẫn cần kiểm tra mốc đầu/cuối và fallback DOM/ảnh có mục tiêu. Đọc lên trả các trang theo thứ tự đi ngược, dòng bên trong trang theo thứ tự xuôi. Reader hết hạn sau hai phút; end/reset/switch xóa nội dung RAM. MCP không thể ép model của Antigravity tiếp tục khi host/provider đã kết thúc lượt.

Chạy `npm run test:reading` để kiểm tra trên cửa sổ WPF thử nghiệm. AGY_NATIVE_EXECUTABLE cho phép chọn helper build thử; AGY_DESKTOP_TEST_SCOPE (GUID N) cô lập mutex/stop event trong test. Không đặt các biến này trong cấu hình plugin sử dụng hằng ngày.

## Browser theo tab riêng (v0.7.0)

Cùng plugin có bảy nhóm tool MCP: browser_session, browser_tabs, browser_read, browser_act, browser_wait, browser_capture và browser_dialog. Phần browser dùng playwright-core 1.65.1. v0.9 mặc định kết nối Chrome đang dùng; mode `connection:'isolated'` giữ cách chạy bằng pipe/profile riêng trước đây. Native desktop CLI/FFI không chạy browser; từ v0.8 desktop_run hỗ trợ browser.* trong browser scope.

Luồng chuẩn:

```js
browser_session({operation:'start', browser:'chrome'});
const tab = browser_tabs({operation:'new', url:'https://example.com'});
browser_read({operation:'snapshot', tab_id:tab.tab_id});
// Chọn ref hoặc một locator khớp duy nhất, rồi browser_act và browser_wait.
// Gọi browser_session({operation:'end'}) trong finally khi hoàn tất.
```

Đoạn trên minh họa các lần gọi MCP, không phải API JavaScript được đưa vào desktop_run. Mỗi thao tác tab đã có đều bắt buộc tab_id. Điều khiển DOM/chuột/phím của Playwright gắn vào page, không dùng SendInput toàn màn hình, không có fallback tab hiện tại. Cùng phiên MCP này chặn các tool desktop khi browser scope còn mở, trừ status/control/help/context; không chặn chương trình, phiên MCP hay plugin khác. Agent không được dùng tool desktop khác để vượt scope. Tab popup bị cách ly cho đến khi gọi adopt rõ ràng.

browser_read trả chữ DOM và refs có kiểm tra identity; ref hết hạn sau 60 giây hoặc navigation/thay element. Password được loại khỏi DOM read/input. Iframe dùng frame_id riêng, có hỗ trợ cross-origin và open shadow DOM. Danh sách frame tối đa 80; snapshot tối đa 100 control, duyệt tối đa 300 control; các cờ complete báo giới hạn. Nội dung canvas/closed shadow DOM hoặc DOM thiếu cần ảnh trong đúng tab. Snapshot một frame không chứng minh đã đọc toàn bộ trang hay lịch sử tải từ xa.

browser_act hỗ trợ click, double_click, hover, fill, type, press, check/uncheck, select_option theo nhãn, scroll page/element, drag cùng tab, click_point và mouse_move. Các target trùng bị từ chối. Capture là một PNG viewport trong RAM, không có tùy chọn path; token 10 giây, dùng một lần, ràng buộc tab, navigation, scroll và bộ đếm thay đổi DOM chính. Bộ đếm không phát hiện đầy đủ canvas repaint hoặc thay đổi trong iframe/shadow root. Ảnh giữ cách che ký tự password thông thường của trình duyệt; không phải bộ lọc mọi dữ liệu nhạy cảm. Luôn quan sát mới và xác minh sau input.

Dialog JS cần inspect/accept/dismiss rõ ràng; không tự accept. Timeout/cancel không lặp input. Delivery không rõ buộc đọc/capture mới rồi reconcile với recovery_note. Cancel đang chạy đóng đúng tab đó để ngăn input giao muộn. ESC/control_stop hoặc mất helper ESC dừng tác vụ và giữ scope khóa; existing mode chỉ đóng tab công việc và ngắt kết nối, isolated mode đóng browser riêng. Không hoàn tác tác động đã gửi. Chế độ existing dùng đăng nhập sẵn có; chế độ isolated tạm không giữ đăng nhập sau khi đóng (profile công việc có tên thì giữ). Không hỗ trợ upload/download file, tự nhập password hay arbitrary JavaScript/evaluate qua MCP.

Không ghi screenshot, video, trace, HAR hay báo cáo test ra đĩa. Trình duyệt vẫn tạo cache/profile tạm như dữ liệu vận hành thông thường; Playwright dọn profile của nó khi đóng bình thường. Đây là cơ chế định tuyến tab cho agent, không phải sandbox an ninh cho mọi hành vi JavaScript của website. Việc model Antigravity tự chọn đúng tool và tiếp tục lượt thuộc host; một lần gọi tool thành công không chứng minh agent host tự vận hành đúng.

Các kiểm tra có thể chạy lại (chỉ dùng trang HTTP và cửa sổ thử nghiệm riêng):

```powershell
npm test
npm run test:browser
npm run test:browser:visible
npm run test:browser:edge
node scripts/browser-smoke.mjs --edge --visible
npm run test:browser:lifecycle
node scripts/browser-lifecycle-smoke.mjs --edge
node scripts/browser-escape-smoke.mjs
```

Các bài browser kiểm tra form HTTP nhận dữ liệu từ đúng tab, tab còn lại không đổi, iframe khác origin, shadow DOM, drag/drop, popup/dialog, pixel PNG, stale refs/capture, navigation, cancel/timeout/disconnect/stop và dọn profile. Kết quả từng lần chạy chỉ in ra stdout; không lưu bằng chứng. Tài liệu API thư viện: [BrowserType.launch](https://playwright.dev/docs/api/class-browsertype#browser-type-launch), [Page.screenshot](https://playwright.dev/docs/api/class-page#page-screenshot).
