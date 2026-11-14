# 参与贡献

欢迎提交问题、文档改进和代码修改。开始较大改动前，建议先通过 Issue 说明使用场景和方案。

## 报告问题

请提供 Windows 版本、TwinSync 版本、操作步骤、预期结果和实际结果。同步问题请说明同步方向、设备是否在线，以及错误提示。

截图和日志请去掉私有文件内容、配对码、设备令牌、私钥和个人目录信息。安全漏洞请按 [安全报告说明](SECURITY.md) 处理。

## 本地开发

需要 64 位 Windows 10/11 和 .NET Framework 4.7.2 或更高版本。不需要 Node.js、npm 或在线恢复依赖。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

代码位于 `native/` 和 `public/`，测试位于 `test/`，构建与测试入口位于 `scripts/`。现有测试使用独立临时目录；请勿将真实同步目录作为测试数据。

## 提交修改

- 每个 Pull Request 聚焦一个问题，说明改动原因、行为变化与验证结果。
- 同步、删除、覆盖、权限和版本恢复的改动应包含相应的回归验证。
- 不提交 `data/`、`local-data/`、`dist/`、临时测试目录或本机身份数据。
- 使用现有代码和界面风格；引入依赖时说明必要性，并保留第三方许可。

提交的项目代码按仓库 [MIT 许可证](LICENSE) 提供；第三方资源适用各自的许可证。
