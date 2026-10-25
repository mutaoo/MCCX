# MCCX 测试脚本

本目录是 MCCX 的自动化测试套件。**迁移前这些脚本散落在 `%TEMP%\opencode\`**
（随时可能被清理，导致"回归网"消失）；现已入库，并把写死的绝对路径改成相对脚本自身的路径。

## 目录结构

| 目录 | 内容 |
| --- | --- |
| `lib\` | 公共库：`uia.ps1`（UI Automation 助手）、`autolib.ps1`（启动/定位/关闭程序、断言、RCON、截图） |
| `suites\` | 完整套件：`run6.ps1`（`p_*` / `v_ui2` 回归编排）、`run_g.ps1`（日志区/键盘/连接流程回归编排：`g_fixes`/`g_msgfilter`/`g_view`/`g_refill`/`g_dialog`/`g_walk`/`g_logkeys`/`g_logcn`/`g_connlock`/`g_logswitch`/`g_autocmd`/`g_loginopt`/`g_useitem`）、`v_ui7.ps1`（UI 需求验收）、`shot_readme.ps1`（README 8 图重拍）、`dump_accounts.ps1`（账号库解密核对）、`ver_check.ps1`（标题栏取证）、`run_p.ps1`（单用例运行器） |
| `cases\` | 具体用例（`p_*` / `v_ui*` / `d_*` / `g_*`），由 `run_p.ps1` 或 `run6.ps1` / `run_g.ps1` 调用 |
| `artifacts\` | **输出目录（已 gitignore）**：用例日志 `*_out.txt`、临时截图、临时账号库暂存 |
| `tools\` | `Test-TestScripts.ps1`（总自检：语法 + 编码 + 路径 + 用例/套件清点）、`Migrate-TestScripts.ps1`（2026-10-03 迁移用的可重复脚本，dry-run 优先）、`Capture-CancelRepro.ps1`（附加到运行中的窗口抓"取消密码框后无限重连"现场） |

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
   `%TEMP%\opencode\mcserver\26.1`（默认游戏口 25599 / RCON 25575，密码 `mccxtest`），
   以该目录的 `server.properties` 实际值为准。**26.1 服务端必须用 Java 25 启动**
   （Java 21 会 `UnsupportedClassVersionError`）；用例通过 `autolib.ps1` 的
   `Get-TestServerJava` 解析 Java：优先 `MCCX_TEST_JAVA` 环境变量，其次 `JAVA_HOME`，
   再扫常见 JDK 安装目录，最后回退 PATH 里的 `java`。
   **用例里取服的路径是 `tests\artifacts\mcserver\26.1`**（`artifacts\` 已 gitignore），
   它是指向上面那个目录的**目录联接（junction）**，本机没有就建一条：
   `New-Item -ItemType Junction -Path tests\artifacts\mcserver\26.1 -Target <上面的绝对路径>`
   （缺了它 `p_multi` / `p_filter` / `g_view` 会报"本地测试服缺失"）。
   还有一条**旧的 `tests\artifacts\mcserver\1.21.11` 联接暂时保留备用**（2026-10-15 决定先不删）：
   指向 `%TEMP%\opencode\mcserver\1.21.11`，端口/RCON 密码同 26.1 那套（Java 21 也能起）。
   以后要回测 1.21.11 直接用这条联接，别去重下服务端；也别忘了它一直在 artifacts 里。
5. **不要抢前台焦点**：截图优先 `PrintWindow`；弹层打开期间禁用 `Set-Foreground`。
   两个坑（2026-10-16 实测）：① 刚启动后第一张 `PrintWindow` 可能是空帧（数出来全是 0），
   先空抓一张热身再正式抓；② `PrintWindow` 抓出来的图比 UIA 的 `BoundingRectangle` 靠左
   约 5 DIP（窗口左边框宽度），按 UIA 坐标裁像素块时要留够余量或改用相对边缘定位。
6. **SendKeys 打字用例必须在英文输入法下跑**：Windows 按进程记住输入语言，MCCX 上次是中文时，
   打进去的 `/he` 会变成 IME 组合串——空格把组合串上屏成汉字、Esc 把组合串取消、
   ↑ 被 IME 拿去翻候选（Tab/Esc 用例整个失效）。
   `Start-App` 里已自动调用 `uia.ps1` 的 `Set-EnglishInputLayout`（给主窗口发
   `WM_INPUTLANGCHANGEREQUEST` 切 en-US 00000409）；`Set-Foreground` 把窗口抢到前台后
   **还会再切一次**——窗口没被激活时那条消息会被丢掉，重新激活又按进程记住的中文生效
   （2026-10-16 复现过：只靠启动时切一次，段的 Tab/Esc 断言会红）。
   新写打字用例时用 `Start-App` 起的实例即可，不要自己去改输入法。
7. **命令栏和日志搜索框只在连上服务器后才显示**（2026-10-16 需求）：未连接时「常用命令 / 输入框 /
   发送 / 清空日志」和日志搜索框整块收起（UIA 里连节点都没有）。所以：
   - 要摸这些控件的用例（`g_search` / `g_new`）得先开一个测试会话：
     `Enter-TestSession -User <专用账号>`（起本地测试服 → 记账号列表基线 → 加账号 → 选中 → 连接），
     收尾 `Exit-TestSession -Session $s`（断开 → 删账号 → 核对列表回到基线）。两个助手都在
     `autolib.ps1`，只用 UIA 点界面、不碰用户已有账号（流程同 `p_multi` / `g_walk` 里的副本）。
   - `Clear-Log` 在未连接时会打印 `[ui] 未连接：清空日志按钮已隐藏，跳过清日志` 后跳过：这类用例
     别再把"清完日志再断言"当唯一依据（`p_port` 的几处就是这种情况，断言本身仍成立）。
   - 键盘（SendKeys）用例抢不到前台时会打印 `[skip]` 跳过那几条断言（不是失败）：
     系统前台被别的进程占着时（实测 `GameInputSvc`）按键会打进别的窗口。
   - 连上之后的日志断言用 `Assert-Log`：它**先轮询最新一屏，读不到会往回翻几屏**再判失败
     （日志区是虚拟化列表，一次只实体化约 58 行，操作后瞬间灌进几十行就会把那行挤出窗口；
     详见 `other\MCCX开发文档.md` §9）。"是否连上"这种前置判断用 `Assert-Connected`（看状态条），
     不要再去匹配日志里的 `连接成功`。
8. **容器面板（服务器箱子 / 商店 GUI + 玩家背包）的 UIA 锚点**（2026-10-16）：面板本体是浮在**蒙板**
   正中的 `Border`（2026-10-16 第四轮用户要求：改成同「添加账号」的蒙板弹窗，蒙板 `ContainerMask` 盖住
   第 1 行功能区到第 3 行命令栏，账号列表与第 0 行连接参数留在蒙板外面，面板开着照样能切账号 / 点断开），
   而 `Border` 没有自动化对等体、在 UIA 树里根本看不见，不能拿它当锚点。判断面板在不在
   要用面板右上角的「关闭」按钮（`AutomationProperties.Name="关闭容器窗口"`，`Button` 有对等体）；
   蒙板会挡住**鼠标**点它下面的东西，但 UIA 的 `InvokePattern` / `ValuePattern` 不走命中测试，
   用例照常能点被蒙板盖住的按钮、往被盖住的输入框写值（所以现有用例一条都不用改）。
   格子同样是 `Button`，UIA 名字形如 `格子 0：钻石 x5` / `格子 5：空`（物品名查中文名表，查不到才退回
   MCC 的英文名，断言写成 `-match '(钻石|Diamond)'` 这种兼容写法），`InvokePattern` 点下去
   走的就是左键拿起 / 放下的真实路径。参照 `cases\g_container.ps1`。
   玩家背包面板同一套锚点：功能区按钮 `背包`（`AutomationProperties.Name="背包"`）点开 / 收起，
   面板里格子号是 MCC 窗口 0 的槽位号（5-8 装备、9-35 背包、36-44 快捷栏、45 副手；合成格 0-4 不画）。
   2026-10-18 用户要求按**原版背包界面**摆（`MccWindowGroup.Role` 决定每块的位置：顶上一行装备横排
   + 右边空一小段挂副手、再下面 3×9 背包与 1×9 快捷栏；不摆玩家人像那块、原版右侧的合成台不做），
   **没有分组小标题**了，所以用例改成按 `BoundingRectangle` 断言"每格都在、位置关系对"（A8b 四格 Y 相同 = 横排、A8c 自左而右头盔 → 靴子、A8e 副手与头盔同一行且在靴子右边）。
   参照
   `cases\g_inventory.ps1`（它用 `item replace entity hotbar.0 / inventory.0 / armor.head / weapon.offhand`
   精准塞物品，再用 `data get entity <玩家> Inventory` 回读服务端 NBT 核对点击真的过了协议）。
    服务器窗口和玩家背包是**联动**的（2026-10-16 第三轮）：窗口下面还会跟着画一块小标题 `物品栏` 的
    36 格（27 背包 + 9 快捷栏，连排 4 行）。这时格子号是**这个窗口自己的**槽位号，不是窗口 0 的号：
    9x3 箱子是 0-26、物品栏 27-62（最后 9 格是快捷栏，即窗口槽位 54-62 对应 NBT `hotbar.0`-`hotbar.8`），
    点下去发的也是这个窗口号的真实槽位，服务端认得。用 `Get-SlotElement 63` 为空可以卡边界；
    参照 `cases\g_container.ps1` 的 B4-B7 段。

9. **账号列表的读取一律用公共库里的 `Get-AccountListEl` / `Get-AccountCount`**
   （2026-10-18 收进 `autolib.ps1`：之前 11 个用例各抄一份，内容逐字相同，改一处漏一处）。
   断言"账号数"时别连着调两次（条件里一次、失败详情里再一次）——详情里那次是**失败之后**才采的，
   会报出 `count=13 before=13 却 FAIL` 这种看不懂的结果；先采进变量再断言。
   刚加 / 刚删完账号，点数用 `Wait-AccountCount -Expect <期望值>` 等 UI 结算
   （删项后 ListView 重建期间 UIA 会短暂读到旧项数，单点读一次会偶发假红，2026-10-18 实测踩过）。
   同理，"逐行"类用例不要写死本机账号名（老版本的 `v_ui2` 写死 `Player` / `TestBob`，本机没有就永远红）：
   按列表顺序取实际存在的账号行，跳过"按服务器分组"的组头（组头没有叉号）。

10. **别断言 MCC 输出的英文原文**（2026-10-19 起 MCC 的程序文案是官方简体中文）：
   `MCCX.Core/Ipc/RunnerHost.cs` 把子进程 UICulture 钉成 `zh-Hans`，日志里 MCC 自己的行
   （`正在 Ping 服务器…` / `正在登录...` / `已成功加入服务器。` / `/help` 尾部提示）都是中文。
   要等这类输出，挑**中英文都有**的锚点（例：`g_msgfilter` B4 等 `'/send /help'`），
   或者干脆等 MCCX 自己打的中文日志（`连接成功。` / `已进入游戏…`，这些和 MCC 资源无关）。
   页面/弹层里的 MCC 文案（对话框标题按钮等）也同理，别写死英文。
   同一条思路也适用于**游戏语言**：`MCCRuntime.Initialize` 把 `Advanced.Language` 钉成 `zh_cn`，
   日志里的物品名由 MCC 自己查官方语言表（`item.minecraft.diamond_sword` → `钻石剑`），
   别断言日志里会出现 `[Diamond Sword]` 这类英文名（`g_logcn` 已按新口径改）。
   注意 MCC 自带/下载的官方表是 **1.19.3** 的：表里没有的新物品（如 26.1 的金矛）MCC 原样打出
   翻译键 `[item.minecraft.golden_spear]`，这是 MCC 官方口径，不是 MCCX 的回归。

## 常用命令

```powershell
# 单个用例（输出落 tests\artifacts\<名字>_out.txt）
pwsh -ExecutionPolicy Bypass -File tests\suites\run_p.ps1 -Name p_ui

# 九套回归（编排 p_* / v_ui2，输出 SUMMARY x FAILURES=n）
pwsh -ExecutionPolicy Bypass -File tests\suites\run6.ps1

# 十三套日志区/键盘/连接回归（编排 g_*，输出 SUMMARY x FAILURES=n）
pwsh -ExecutionPolicy Bypass -File tests\suites\run_g.ps1

# g_logkeys 里"Ctrl+左键开链接"默认跳过（会真的拉起浏览器）；要连它也跑：
$env:MCCX_TEST_LINK = '1'; pwsh -ExecutionPolicy Bypass -File tests\suites\run_g.ps1
# 不想要鼠标类用例（拖选/Ctrl+点击需要前台焦点）时：$env:MCCX_TEST_NO_MOUSE = '1'

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
# 发射前先"视角向东 + 抬头看天"：平射的箭不到 1 秒就落地（落水直接消失），400ms 一次的轮询
# 会撞不上 → 2026-10-18 实测偶发 B5 假红；朝天打箭在空中约 6 秒，稳定能抓到。
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
#   D5/D6 多前缀列表（逗号分隔命中任一）/ D7/D8 前缀框右端展开图标 -> 多行编辑窗
#   （换行分隔的两个前缀保存后合并成 "[a], [b]" 且两个都生效）/ F 只屏蔽指定前缀：[mfblk] 挡其余放行、F5 多前缀 /
# E 恢复不过滤 + 前缀框保留。专用账号 TestMsgFilter 测完自删
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_msgfilter.ps1

# 2026-10-04 「取消密码框后无限重连」修复端到端：测试服自带 mccx_dialog 数据包，RCON 下发登录对话框
# A 没取消过时踢下线照常自动重连（负向对照）/ B 点弹窗「取消」后再踢下线不再重连 + 状态停在未连接 /
# C 点服务器动作 cancel 同样压制。会自建数据包并在需要时重启测试服；专用账号 TestDialog 测完自删
# （实测：SUMMARY g_dialog FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_dialog.ps1

# 2026-10-03 「常用命令」按钮冒烟：下拉 / 手动添加 / 列表 / ✕ 删除（测试数据自清）
# 2026-10-16：按钮长在命令栏里、命令栏要连上服务器才显示，所以先 Enter-TestSession 连本地测试服
# （专用账号 MccXNewTest，测完自删还原）
# 实测通过：SUMMARY g_new FAILURES=0
pwsh -ExecutionPolicy Bypass -File tests\cases\g_new.ps1

# 2026-10-14 日志搜索（搜索框占位/匹配计数/清空/放大镜图标）+ 命令输入框占位文案 + 命令建议浮层
# 2026-10-16：命令栏和搜索框都要连上服务器才显示——先钉"未连接时整块收起"，再 Enter-TestSession
# 连本地测试服（专用账号 MccXSearchTest，测完自删还原）跑其余断言；抢不到前台时键盘段报 [skip]
# 2026-10-16 追加：清空搜索后视图必须回到最底的最新日志（用户反馈：筛选结束后总停在顶部/第一条命中）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_search.ps1

# 2026-10-16 服务器容器窗口（箱子 / 商店 GUI）：摆箱子（RCON）+ MCC 内部命令 /useitem 打开 →
# 面板弹出（原版英文窗名 Chest 显示成中文）/ 格子内容与 64 叠数量 / 点格子"拿起-放下"用 RCON
# 回读箱子核对（走的是真点击的 Click 处理）/ 面板「关闭」后还能再打开 / 传送到远处由服务端关窗时
# 面板自动收起；顺带截浅色 + 暗色 + 收起后三张图到 artifacts\g_container\（第三张对着浅色那张
# 看，确认面板收起时蒙板也收起了，功能区 / 命令栏不再压暗）。
# 2026-10-16 第四轮：面板改成同「添加账号」的蒙板弹窗：蒙板盖住第 1 行功能区到第 3 行命令栏，
# 账号列表与第 0 行连接参数留在蒙板外（面板开着照样能切账号 / 点断开），蒙板范围靠上面几张图人眼确认。
# 2026-10-16 追加：图标画风统一（一律原版贴图、方块也是平铺贴图）。A12 断言缓存里的 oak_planks
# 是 64x64（16px 最近邻放大 4 倍），而不是第三方 128x127 渲染图；A13/A14 断言原版没有平铺贴图的
# 物品（旗帜）走兜底渲染图，且兜底图只落临时目录、不写进 item-icons-v3。没网下不到时这几条报 [skip]。
# 专用账号 MccXContTest 测完自删（实测：SUMMARY g_container FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_container.ps1

# 2026-10-16 玩家背包面板（第二轮需求：点功能区「背包」按钮看当前背包和快捷栏）：
# 2026-10-18 起按用户要求摆成**原版背包**的样子：顶上一行装备横排（窗口 5-8）+ 右边
# 空一小段挂副手（45）、下面 3×9 背包（9-35）、再下面 1×9 快捷栏（36-44）；合成格
# （窗口 0-4）不画、不摆玩家人像那块、分组小标题也去掉了 —— A8-A10 改成按
# BoundingRectangle 断言"每格都在 + 位置关系对"（四格 Y 相同 = 横排 / 自左而右头盔 → 靴子 /
# 副手与头盔同一行且在靴子右边 / 9 与 17 同行 / 36 与 9 左对齐且在背包下面）。
# 点格子走窗口 0 的点击（拿起 / 放下，RCON 回读
# 玩家 NBT 核对真的过了协议）；面板开着时服务端塞物品界面自己刷（增量）；收面板后背包再变
# 不会把面板弹回来（负向对照）；断开连接自动收起；顺带截浅色 + 暗色两张图到 artifacts\g_inventory\。
# 专用账号 MccXInvTest 测完自删（实测：SUMMARY g_inventory FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_inventory.ps1

# 2026-10-19 日志物品名走 MCC 官方语言表（用户要求：不要 MCCX 自己那层英文→中文映射）：
# RCON tellraw 造"和用户截图同形状"的消息（文本 + {"translate":"item.minecraft.xxx"} 组件）：
# 官方表里有的（item.minecraft.diamond_sword）直接显示 [钻石剑]，英文名不再出现；官方表里没有的
# 新版物品（MCC 自带表是 1.19.3 的，如 26.1 的金矛）MCC 原样打出翻译键 [item.minecraft.golden_spear]；
# 负向对照：中文自定义名 / 认不出的英文词 / [系统] 这类标签原样保留，普通文本里的英文名不动。
# 官方语言表没缓存又没网时整节 [skip] 中文断言（环境问题不算功能问题）。
# 专用账号 MccXLogCnTest 测完自删（实测：SUMMARY g_logcn FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_logcn.ps1

# 2026-10-18 连接状态下连接参数不可编辑（用户需求：连接状态时服务器地址栏等表单框禁用）：
# UIA 读四个参数框（ServerBox / PortBox / UserBox / VersionBox）的 IsEnabled，走一遍状态机：
# 未连接可编辑 → 连接途中采样到的「连接中」也禁用 → 已连接禁用 → 断开后恢复可编辑；
# 顺带确认「连接 / 断开」两个按钮的可用性没被带着改。
# 专用账号 MccXConnLock 测完自删（实测：SUMMARY g_connlock FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_connlock.ps1

# 2026-10-20 功能日志开关（用户需求：开关控制日志的**发出**，不是发出去再由界面过滤）：
# 打开砍怪 → 日志开时看得到「攻击 僵尸」→ 关掉「自动砍怪日志」后过程日志不再产生、
# 但 RCON 回读僵尸血量仍在掉（Bot 只是不说话）→ 状态提示「自动砍怪已关闭」照常显示 →
# 重新打开日志立刻恢复 → 关着日志重连后依旧静默且仍在攻击（开关值走 IPC 配置记账）→
# H 段：整个程序重启、没人动开关，过程日志依旧静默且仍照打（2026-10-22 修的
# 「重启后日志开关失效、要手动拨一次」：RunnerProcess 的配置记账按类型去重，把共用同一
# logswitch 类型的所有功能压成一个，补发时只剩最后一个）。
# 背景：旧实现按行首 [标签] 查表过滤，MCC 汉化后 [AutoFishing] 变 [自动钓鱼]，开关失效。
# 专用账号 TestLogSw 测完自删（实测：SUMMARY g_logswitch FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_logswitch.ps1

# 2026-10-22 「自动指令」端到端（弹层里多条指令，各自间隔 / 进服立即执行 / 执行方式与次数 / 每条日志开关）：
# A 新建测试账号连本地测试服 → 弹层里新建两行（第 1 行 /me acc_one、间隔 2 秒、勾进服立即执行；
#   第 2 行 /me acc_two、间隔 6 秒、不勾）→ 开总开关；
#   A9 专测「先连上再开开关也立刻生效」——MCC 的 AfterGameJoined 早过去了，靠会话层补 NotifyGameJoined()；
# B 断线重连：勾了的那条立刻由服务器回显（TestAutoCmd acc_one），没勾的 6 秒那条要等到点才发；
# C 定时循环：7 秒窗口里 2 秒那条至少两次（服务器回显计数）；
# D 每条自己的日志开关：两行都关掉后不再出现 [自动指令] 过程日志，但服务器回显照旧（指令照发）；
#   只把第 1 行打开，日志就只恢复第 1 行的；
# E 落盘：重启程序后两行文本 / 间隔 / 两个「进服立即执行」/ 两条日志开关 / 总开关都还在；
# F 执行方式与次数 + MCC 内部命令：`/send`（MCC 内部命令，不是服务器指令）能执行（服务器回显
#   出现、且没有"未知命令"）；「只执行一次」16 秒里只发一遍；次数=2 的那条发两遍后停
#   （日志里有「已执行完设置次数」）。
# 专用账号 TestAutoCmd 测完自删（实测：SUMMARY g_autocmd FAILURES=0）
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_autocmd.ps1

# 2026-10-23 服务器密码框两项需求（点眼睛常驻显示 + 记住密码/自动登录）+ 孤儿进程：
# D 密码框是 IsPassword；下方有「记住密码」「自动登录」两个勾选框；没勾记住密码时自动登录禁用，
#   勾上后变可选；点眼睛变明文（TextPattern 读回原文）、再点回到遮蔽；
# E 再弹密码框：自动填入记住的密码、1 秒后自动提交（日志有「自动登录」「已提交服务器对话框」），
#   全程没点任何按钮；另外断言密码没进日志；
# F 重启程序后「记住密码/自动登录/密码」仍在（重新连接 → 弹框 → 仍然自动提交）；
# G 强杀主进程（Stop-Process -Force）后：没有 --runner 孤儿子进程，RCON list 立刻看不到玩家。
# 对话框由本地测试服的 mccx_dialog 数据包提供（同 g_dialog）。
# 专用账号 TestLogin 测完自删（实测：SUMMARY g_loginopt FAILURES=0）
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_loginopt.ps1

# 2026-10-23/24 自动指令与命令栏 /useitem 修复：MCC 的 /useitem 会先射线找方块（命中就 PlaceBlock），
# 手上是末影珍珠这类非方块时服务端不收、物品不消耗，但日志照样"已执行"；命令栏走的是同一条分支，
# 于是出现"有的账号窗口能用、有的不能"（取决于该账号视线里有没有方块、选中格是不是空的）。
#   用例：视线正前方 2 格放石头（`execute at … run setblock ^ ^1.5 ^2 stone`，射线必中），
#   生存模式 + 主手 3 颗末影珍珠 → 跑一次 /useitem 后必须 3 → 2 且人被传送走（A 自动指令）；
#   对照：视线打空同样 3 → 2（B）；C 命令栏直接敲 `/useitem`（正前方有方块，仍必须 3 → 2）；
#   D 清空背包后必须回"手上没有物品"（不能假装成功）。计数用 `clear <玩家> <物品> 0`（只数不删），
#   不用 `data get entity Inventory`（1.21.9+ 不含副手）。
# 专用账号 TestItem 测完自删（实测：SUMMARY g_useitem FAILURES=0，A–D 全 PASS）
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_useitem.ps1

# 2026-10-03 暗色模式冒烟：开关在位 / 黑底亮度 / ui-settings.json 落盘 / 重启保持 /
# 暗色下弹窗也是深色 / 收尾拨回浅色并删测试设置文件（实测通过：SUMMARY g_theme FAILURES=0）
pwsh -ExecutionPolicy Bypass -File tests\cases\g_theme.ps1

# UI 需求验收
pwsh -ExecutionPolicy Bypass -File tests\suites\v_ui7.ps1

# README 8 图重拍（三处账号库备份/移除/还原 + MD5 校验 + 敏感词断言；本地测试服在跑就直接用、没跑自己起）
#   2026-10-19：脚本先换空账号库再启动 —— 启动时列表里一个账号都没有，MainViewModel 那几条
#   启动日志（含"用户数据目录：<本机路径>"）没有账号可写，画面天然干净；原来那句点「清空日志」
#   已删掉（命令栏与日志搜索 2026-10-16 起只在连上服务器后才显示，离线时截不到那个按钮）。
#   截前加了兜底断言：UIA 读到的当前账号日志里只要出现盘符路径就中止，绝不让本机路径进截图。
#   想再加一张「背包面板」的试过了、暂时没成：点开面板后 UIA 里能看到格子（如 `格子 36：钻石剑`），
#   但格子和「关闭」按钮的 BoundingRectangle 是 NaN（IsOffscreen=true），屏幕截图和 PrintWindow
#   都拍不到面板；同一个面板在 g_inventory 里（那个用例不抢前台）拍得到，所以是截图脚本环境的问题，
#   不是面板坏了（g_inventory 照常 FAILURES=0）。
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
