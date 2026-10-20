# 校园网助手

双击 `app/SzuConnect.exe` 后在 Windows 通知区域后台运行。右键托盘图标可立即检查、暂停或恢复监控、设置账号、查看日志、退出；双击图标打开账号设置。

首次使用请先按照下文构建程序，再在账号设置中输入自己的校园网账号和密码。仓库不包含任何个人账号、密码或预配置凭据。

- 通过深圳大学 SRun HTTPS 接口认证，不运行 Chrome、Selenium 或 Python。
- 正常默认每 5 分钟检测一次，设置中可按整分钟调整（1 至 60 分钟）；失败后按 15、30、60、120、240、300 秒退避重试。明确的账号密码错误等待 15 分钟。
- 检测两个外网地址的预期内容。已认证但外网不可用时不重复提交登录。
- 凭据以 Windows DPAPI 当前用户加密保存。账号和检测间隔保存在 `%LOCALAPPDATA%\SzuConnect\settings.json`，日志及最近状态在同一目录。日志最多约 2 MB。
- 设置中的“登录 Windows 后自动启动”使用当前用户启动项，无需管理员权限。默认不启用。
- 使用 Windows 命名互斥量保证当前用户只有一个后台实例。

## 构建与验证

需要 Windows 内置 .NET Framework 4.8，无需安装 NuGet 包。运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

自动检查协议编码向量、JSONP、登录状态、HTTP 错误、取消和退避策略。测试使用合成账号和模拟 HTTP，不提交真实凭据。

配置完成并退出后台后，可运行 `powershell -NoProfile -File .\tests\Lifecycle.ps1`，检查单实例、运行中拒绝导入、正常退出和即时重启。此检查会短暂启动后台程序并按当前网络状态检测；不会主动注销网络。

```powershell
# 后台启动
Start-Process .\app\SzuConnect.exe
# 正常退出
Start-Process .\app\SzuConnect.exe -ArgumentList '--stop' -Wait
# 关闭开机启动
Start-Process .\app\SzuConnect.exe -ArgumentList '--disable-autostart' -Wait
```

`--check` 执行一次完整检测（断网时可能认证），通过退出码 0/2 表示联网/未联网，结果写入 `status.json`；后台正在运行时返回 3，跳过执行。`--settings` 或后台运行时再次双击 exe 会打开账号设置。退出仅停止监控，不注销校园网。删除应用前可先关闭启动项并退出程序。

程序只适用于当前教学/办公区 SRun 接入，不包含宿舍区 ePortal 协议；遇到验证码、校园认证方式变化或 VPN 路由接管，需要人工处理。

## 协议参考

认证字段和 xEncode 行为依据当前学校认证页及其 JavaScript：

- https://net.szu.edu.cn/srun_portal_pc?ac_id=12&theme=proyx
- https://net.szu.edu.cn/static/themes/pro/js/Portal.js

`ackness/szu-autoconnect` 用于参考“先检测、断线再认证”的流程，其旧 DrCOM 表单未被用于本程序：https://github.com/ackness/szu-autoconnect

个人配置只保存在当前 Windows 用户的本地数据目录，密码不经过命令行参数或日志。旧 Python 脚本 `net.py`、个人配置、日志、备份、IDE 文件和编译产物均不提交到仓库。测试代码中的账号及密码是合成测试数据，不可用于真实登录。

`--import-credentials` 仅允许后台已退出时执行，运行中返回 4，避免磁盘配置与正在使用的账号不一致。运行时改账号请使用托盘“账号设置”。`--stop` 最多等待 20 秒完成退出，超时返回 4。
