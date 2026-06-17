# android / template

工程脚手架模板 —— `export` 时由 appbuilder（`AndroidWorkload`）渲染进用户工程，
包住已 bed 的 per-ABI `libz42_*.so` + `app.zpkg`。

预期内容（parked，待深加工填充）：
- `AndroidManifest.xml.tmpl` —— 由 `[platform.android]`（app_id / version_code / version_name / 权限）渲染
- `build.gradle.kts.tmpl` / `settings.gradle.kts.tmpl` —— gradle 工程骨架
- `gradle.properties.tmpl` —— min_sdk / target_sdk

> 与 `platform/`（原 facade，编成 .so / AAR 的原生绑定源）区分。
