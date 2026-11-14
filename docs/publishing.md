# 首次发布到 GitHub

本指南面向项目维护者。推荐先上传源码，再通过 Releases 分发安装程序。本文的版本号 `v1.0.0-beta.1` 是首次公开测试版本的示例，可按实际发布计划调整。

## 1. 准备发布源码

使用本次重新生成的 `dist/TwinSync-source.zip`，解压到一个新的空目录，例如桌面的 `TwinSync-public`。后续 Git 操作均在这个新目录进行。

不要直接上传开发工作区的整个外层文件夹。源码包已经按明确清单筛选，适合作为首次公开仓库的起点。

解压后，目录根部应该直接包含：

```text
README.md
LICENSE
THIRD_PARTY_NOTICES.md
CONTRIBUTING.md
SECURITY.md
.gitignore
.gitattributes
native/
public/
scripts/
test/
docs/
```

源码包中包含 `.gitignore` 和 `.gitattributes`，以点开头的文件也要保留。确认 `native/lib/Newtonsoft.Json.dll` 和其许可文件存在，这是离线构建需要的依赖。

以下内容不应进入仓库：

- 实际运行的 `data/`，其中可能包含设备身份和密钥。
- 本机的 `local-data/`、个人文件、同步目录或历史版本库。
- `.test-tmp/`、`.build/` 和开发工具的本地状态。
- 密钥、证书私钥、环境变量文件或访问令牌。
- `dist/` 生成物；安装 EXE 放到 Releases 的附件中。

GitHub 的本地项目上传指南同样要求不要提交密码或 API 密钥等敏感信息。参见 [官方上传指南](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github?platform=windows)。

## 2. 检查许可证和署名

当前准备的项目许可证是 MIT，版权行暂用 `TwinSync contributors`。可以把 `LICENSE` 中的署名改为自己的姓名、GitHub 用户名或实际版权持有者名称。

MIT 允许使用、修改和分发，包括商业使用，要求保留版权及许可声明。发布前应确认该授权方式符合自己的意图。参见 [MIT 许可证说明](https://choosealicense.com/licenses/mit/)。

项目许可证不替换第三方组件的许可。保留 `THIRD_PARTY_NOTICES.md`、JSON 库的 MIT 许可及字体的 OFL 文件。

## 3. 在 GitHub 创建仓库

1. 登录 GitHub。
2. 点击右上角 **+ → New repository**。
3. 仓库名填写 `TwinSync`。
4. Description 可以填写：

   ```text
   Windows file synchronization with a native C# core, local web UI, encrypted device pairing and version history.
   ```

5. 选择 **Public**。
6. 不勾选初始化 README、`.gitignore` 或 License；本地已经有这些文件。
7. 点击 **Create repository**，保留新仓库页面。

为已有本地项目创建空仓库可以避免首次推送时产生不同的初始提交。操作依据见 [GitHub 官方说明](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github?platform=windows)。

## 4. 初始化本地 Git 仓库

在解压后的源码目录打开 PowerShell：在资源管理器地址栏输入 `powershell` 并回车。

先确认当前目录和 Git：

```powershell
Get-Location
Get-ChildItem -Force
git --version
```

当前目录应直接包含 `README.md`、`native`、`public` 和 `scripts`。

执行以下命令，把署名和邮箱替换成自己的信息：

```powershell
git init -b main
git config user.name "你的 GitHub 用户名"
git config user.email "你的提交邮箱"
```

这些 `git config` 命令仅配置当前仓库。想隐藏个人邮箱，可以在 GitHub 的 **Settings → Emails** 中复制自己的 noreply 提交邮箱，不要直接照抄他人的邮箱。

加入文件并检查：

```powershell
git add .
git status --short
git diff --cached --stat
git ls-files
```

文件列表应为源码、资源、依赖、文档与脚本，不应出现前文列出的运行数据。确认后提交：

```powershell
git commit -m "Initial public release"
```

## 5. 推送源码

把下面地址中的 `YOUR_USERNAME` 替换为自己的 GitHub 用户名；如果仓库名不是 `TwinSync`，也一并修改：

```powershell
git remote add origin https://github.com/YOUR_USERNAME/TwinSync.git
git remote -v
git push -u origin main
```

首次 HTTPS 推送可能打开 Git 的登录流程，按提示登录自己的 GitHub 账号。如果要求命令行凭据，不要把访问令牌写入远程地址、脚本或 README；按 GitHub 支持的凭据方式认证。参见 [GitHub 身份验证](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/about-authentication-to-github)。

推送成功后刷新仓库页面，应能看到完整源码，以及自动展示的 README。源码 ZIP 中的文件需要解压提交；仅把 ZIP 上传为仓库文件无法方便地浏览和维护代码。

## 6. 验证发布所用版本

在准备发布的源码根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-setup.ps1 -InstallerPackage dist/TwinSyncSetup.exe
```

生成的安装包和源码包位于 `dist/`。发布附件应来自将要打标签的同一份代码；如果修改了代码或 README，先重新构建并提交改动，再发布。

不要把真实同步文件夹用于测试，也不要把安装后生成的 `data/` 打进附件。

## 7. 创建第一个 Release

1. 在仓库首页打开 **Releases**，点击 **Draft a new release**。
2. **Choose a tag** 输入 `v1.0.0-beta.1`，选择创建新标签。
3. **Target** 选择 `main`。
4. 标题填写 `TwinSync v1.0.0-beta.1`。
5. 说明可复制 [发布说明草稿](release-notes.md)。
6. 在附件区域上传刚刚构建的两个文件：

   ```text
   dist/TwinSyncSetup.exe
   dist/TwinSync-source.zip
   ```

7. 作为公开测试版本，勾选 **This is a pre-release**。
8. 检查标签目标、说明和附件，再点击 **Publish release**。准备过程中可以先保存为草稿。

GitHub 也会提供标签对应的自动源码归档；安装用户应下载手动上传的 EXE。发布入口、标签、附件和预发布选项见 [GitHub Release 官方说明](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)。

## 8. 整理仓库首页

- 在 **About** 填写简短介绍。
- Topics 可使用 `windows`、`file-sync`、`csharp`、`dotnet-framework`、`desktop-app`。
- 保留 Issues，便于收集问题。
- 如果准备接收私密安全报告，在仓库 Security 设置中开启相关入口；报告方式已写入根目录的 `SECURITY.md`。
- 添加界面截图时使用测试设备和测试文件，隐藏个人路径、IP、指纹、配对码及真实文件名。

## 9. 后续更新

在同一个源码目录修改代码后：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-setup.ps1 -InstallerPackage dist/TwinSyncSetup.exe
git add .
git diff --cached --stat
git commit -m "Describe this update"
git push
```

然后创建新的 Release，例如 `v1.0.0-beta.2`，上传本次生成的两个附件，并写清变化、已知问题和升级注意事项。不要重复使用旧标签来代表另一份代码。
