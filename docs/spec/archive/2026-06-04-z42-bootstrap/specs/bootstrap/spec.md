# Spec: z42-bootstrap

## ADDED Requirements
### Requirement: download prebuilt launcher into project-local .z42
#### Scenario: fresh install (nightly)
- WHEN `scripts/install-z42.sh` 首次运行,versions.toml launcher=nightly
- THEN 下载 `z42-nightly-<host-rid>.tar.gz`、SHA256 校验、解压到 `<repo>/.z42`,写 `.bootstrap-stamp`
#### Scenario: up-to-date skip
- WHEN 再次运行且 nightly 远端未变(published_at 同 stamp)
- THEN 跳过下载,打印 up-to-date
#### Scenario: nightly changed → re-download
- WHEN nightly 远端 published_at 与 stamp 不同
- THEN 重新下载覆盖 `.z42`,更新 stamp
#### Scenario: pinned version
- WHEN launcher="0.1.0" 且已装
- THEN 跳过(immutable)
#### Scenario: unsupported platform
- WHEN host RID 不在支持列表(且非 Windows)
- THEN 报错退出,提示用对应平台脚本
