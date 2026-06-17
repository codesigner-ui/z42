# desktop / template

工程脚手架模板 —— 由 appbuilder（`DesktopWorkload`）渲染。desktop 复用宿主 runtime，
产物是 apphost 布局（裸 exe 或 macOS `.app`）。

预期内容（parked，待深加工填充）：
- `Info.plist.tmpl` —— macOS `.app/Contents/Info.plist`（bundle_id 等）
- apphost 布局说明 / `.app` 骨架

> desktop 无 `platform/`（不含原生 runtime 绑定，复用宿主）。
