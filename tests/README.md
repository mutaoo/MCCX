# MCCX 测试脚本

本目录是 MCCX 的自动化测试套件。**迁移前这些脚本散落在 `%TEMP%\opencode\`**
（随时可能被清理，导致"回归网"消失）；现已入库，并把写死的绝对路径改成相对脚本自身的路径。

## 目录结构

| 目录 | 内容 |
| --- | --- |
| `lib\` | 公共库：`uia.ps1`（UI Automation 助手）、`autolib.ps1`（启动/定位/关闭程序、断言、RCON、截图） |
| `suites\` | 完整套件：`run6.ps1`（回归编排）、`v_ui7.ps1`（UI 需求验收）、`shot_readme.ps1`（README 8 图重拍）、`dump_accounts.ps1`（账号库解密核对）、`ver_check.ps1`（标题栏取证）、`run_p.ps1`（单用例运行器） |
| `cases\` | 具体用例（`p_*` / `v_ui*` / `d_*` / `t*_`），由 `run_p.ps1` 或 `run6.ps1` 调用 |
| `artifacts\` | **输出目录（已 gitignore）**：用例日志 `*_out.txt`、临时截图、临时账号库暂存 |
| `tools\` | `Migrate-TestScripts.ps1`：本次迁移用的可重复脚本（dry-run 优先） |

## 运行前提

1. **先构建**：脚本默认指向
   `MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MCCX.exe`
   （`autolib.ps1` 的 `$script:AppExe`，由 `$script:RepoRoot` 推导）。
   ```powershell
   dotnet build "...\MCCX\MCCX.slnx" -c Debug
   ```
2. **PowerShell 5.1 执行**，脚本含中文且多为 **UTF-8 with BOM**（迁移时按原编码保留）；
   用 `-ExecutionPolicy Bypass -File` 运行。部分脚本（如 `dump_accounts.ps1`，DPAPI 解密钥）
   **必须用 `pwsh.exe`**。
3. **用例会操作真实账号面板**：涉及改账号的用例（如 `p_port`）必须开头记基线、`finally` 还原
   （这是长期硬规则，见 `other\注意事项.md`）。
4. **不要连 `127.0.0.1:25565`**：那是用户自己的 MC 客户端。本地测试服在
   `%TEMP%\opencode\mcserver\1.21.11`（默认游戏口 25599 / RCON 25575，密码 `mccxtest`），
   以该目录的 `server.properties` 实际值为准。
   **用例里取服的路径是 `tests\artifacts\mcserver\1.21.11`**（`artifacts\` 已 gitignore），
   它是指向上面那个目录的**目录联接（junction）**，本机没有就建一条：
   `New-Item -ItemType Junction -Path tests\artifacts\mcserver\1.21.11 -Target <上面的绝对路径>`
   （缺了它 `p_multi` / `p_filter` / `g_view` 会报"本地测试服缺失"）。
5. **不要抢前台焦点**：截图优先 `PrintWindow`；弹层打开期间禁用 `Set-Foreground`。

## 常用命令

```powershell
# 单个用例（输出落 tests\artifacts\<名字>_out.txt）
pwsh -ExecutionPolicy Bypass -File tests\suites\run_p.ps1 -Name p_ui

# 九套回归（编排 p_* / v_ui2，输出 SUMMARY x FAILURES=n）
pwsh -ExecutionPolicy Bypass -File tests\suites\run6.ps1

# 2026-10-03 四项修复的 GUI 冒烟：「视角调整」下拉（6 个方向按钮；恢复开关已于 2026-10-04 移除）/
# 重连默认 0（无限）/ 账号库可解密 / 日志区与状态行。只看不改，结束会收干净进程
pwsh -ExecutionPolicy Bypass -File tests\cases\g_fixes.ps1

# 2026-10-03 「视角调整 + 视角移动」端到端（2026-10-04 新需求重做）：起本地测试服 → 点方向按钮
# → RCON 读服务端 Rotation 核对（东 270 / 抬头 pitch -90 / 未连接有提示 / 进服视角 4 秒不被自动
# 改变 / 重连沿用退出时的朝向且无恢复干预日志 / 开行走点向北后保持北 + 位移在走）
# 专用账号 TestView 测完自删（实测：SUMMARY g_view FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_view.ps1

# 2026-10-03 「弓箭蓄力」端到端：给弓+箭 → 右键间隔长按按住 2s 松开 → RCON 查箭实体 + damage
# （证明补发了 RELEASE_USE_ITEM status 5、按住期间蓄力没被打断）；长按模式关右键开关也补发释放包。
# 专用账号 TestBow 测完自删（实测：SUMMARY g_bow FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_bow.ps1

# 2026-10-04 「自动补充」端到端：手持格掉空 → 自动从背包第 12 格补同款（RCON 回读服务端核对）
# A 开关关着不补（负向对照）/ B 打开后补上 + 背包来源被取走 + 补货日志 / C 无同款不乱补并提示；
# D 重连认领/清退回归（修重复挂载 bug）：开着断开重连不挂第二个且认领实例照常补货 /
#   断线期间关掉再重连不补货（恢复的旧实例已被卸掉，没有僵尸挂载）。
# 专用账号 TestRefill 测完自删（实测：SUMMARY g_refill FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_refill.ps1

# 2026-10-04 「自动行走」端到端：打开开关 → RCON 读服务端 Pos 核对位移
# A 默认关 / B 位移 >= 1 格（日志有开启行 + Bot 就绪行）/ C 关掉后位移停住（取消在途寻路）。
# 专用账号 TestWalk 测完自删（实测：SUMMARY g_walk FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_walk.ps1

# 2026-10-04 「服务器信息过滤」端到端：展开行尾"服务器信息过滤"下拉面板切模式 + RCON tellraw 造服务器消息，核对日志看得见/看不见
# A 默认不过滤都显示 / B 全屏蔽：玩家格式与系统消息都不显示、但 /help 内部命令输出（Info 频道）照常
#   （证明只拦 Chat 一个频道）/ C 只屏蔽玩家消息：挡 <名字> 放系统 / D 只显示指定前缀：[mfpre] 放行其余全挡，
#   D5/D6 多前缀列表（逗号分隔命中任一）/ F 只屏蔽指定前缀：[mfblk] 挡其余放行、F5 多前缀 /
# E 恢复不过滤 + 前缀框保留。专用账号 TestMsgFilter 测完自删
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_msgfilter.ps1

# 2026-10-04 「取消密码框后无限重连」修复端到端：测试服自带 mccx_dialog 数据包，RCON 下发登录对话框
# A 没取消过时踢下线照常自动重连（负向对照）/ B 点弹窗「取消」后再踢下线不再重连 + 状态停在未连接 /
# C 点服务器动作 cancel 同样压制。会自建数据包并在需要时重启测试服；专用账号 TestDialog 测完自删
# （实测：SUMMARY g_dialog FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_dialog.ps1

# 2026-10-03 「常用命令」按钮冒烟：下拉 / 手动添加 / 列表 / ✕ 删除（测试数据自清）
# 实测通过：SUMMARY g_new FAILURES=0
pwsh -ExecutionPolicy Bypass -File tests\cases\g_new.ps1

# 2026-10-03 暗色模式冒烟：开关在位 / 黑底亮度 / ui-settings.json 落盘 / 重启保持 /
# 暗色下弹窗也是深色 / 收尾拨回浅色并删测试设置文件（实测通过：SUMMARY g_theme FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_theme.ps1

# UI 需求验收
pwsh -ExecutionPolicy Bypass -File tests\suites\v_ui7.ps1

# README 8 图重拍（三处账号库备份/移除/还原 + MD5 校验 + 敏感词断言）
pwsh -ExecutionPolicy Bypass -File tests\suites\shot_readme.ps1

# 标题栏取证（不抢前台）
pwsh -ExecutionPolicy Bypass -File tests\suites\ver_check.ps1
```

## 迁移时做的路径改写

`tools\Migrate-TestScripts.ps1` 把下列写死路径换成了相对表达式（脚本放在 `tests\<组>\` 下）：

| 原写法 | 现在 |
| --- | --- |
| `%TEMP%\opencode\autolib.ps1` / `uia.ps1` | `"$(Join-Path $PSScriptRoot '..\lib')\..."` |
| `%TEMP%\opencode\<输出>` | `"$(Join-Path $PSScriptRoot '..\artifacts')\..."` |
| `...\MCCX\MCCX.App\bin\Debug\...\win-x64` | `"$($script:RepoRoot + '\MCCX.App\bin\Debug\...\win-x64')"` |
| `...\MCCX\docs\images` | `"$($script:RepoRoot + '\docs\images')"` |
| `...\MCCX\accounts-stash` | `"$($script:RepoRoot + '\accounts-stash')"` |

并统一在 dot-source autolib 之后补一行：
`$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)`。

## 已知遗留

- `dump_accounts.ps1` 末尾还会读一个**旧的账号库备份**（原在 `%TEMP%\opencode\accounts-backup\`）。
  该备份没有入库（内含真实账号信息，硬规则禁止入库），所以这一行现在指向
  `tests\artifacts\accounts-backup\accounts.dat`：**没有备份文件时会打印 `missing`，属正常**，
  不影响 `current` 那一段的输出。
- 用例脚本里仍有针对 200% 缩放、窗口尺寸的取值假设（见 `other\MCCX开发文档.md` §8.1）。
- 本套件是 PowerShell + UIA 驱动的端到端测试，**没有接进 `MCCX.SmokeTest`**（那是控制台冒烟测试）。
  想进 CI 需要无界面环境或专用桌面会话，另议。
