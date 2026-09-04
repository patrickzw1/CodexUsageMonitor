[简体中文](README.md) | [English](README.en.md)

# Codex Usage Monitor

Windows 原生系统托盘应用，用本地 Codex 记录和官方 `codex app-server` 汇总使用情况。

本项目采用 [MIT License](LICENSE) 开源，可免费使用、修改和分发；保留版权与许可声明即可。

## 当前功能

- 通用每周额度、重置时间与按 7 天平均的消耗速度预测；服务端返回有效的 5 小时窗口时，在总览顶部自动显示通用 5 小时额度，否则整行隐藏。
- Spark 独立的 5 小时/每周额度；只对 Pro 及以上账户显示，并且必须由服务端返回 Spark 配额。
- 官方月度、7 天和 30 天总量只汇总 `account/usage/read` 返回的每日 Token；“今天”在官方当天桶尚未生成时可显示带“≈”的本地临时统计，官方值到达后立即替换且绝不相加。本地模型、缓存与成本分析仍独立显示。
- 按本地日志样本统计模型 Token、缓存命中率和 API 等价成本；成本使用“≈”标记，并显示本地样本的公开价格覆盖率，不给未公开定价的内部模型猜价格。
- 模型页支持“今天 / 7 天 / 30 天”真实时间筛选、排行进度条和用量环形图。
- 区分非缓存输入、缓存输入、可见输出和推理 Token，避免重复相加。
- 每次应用运行时并发读取一次 OpenAI 官方模型文档的 Markdown 价格表；只有全部支持模型都成功时才原子更新完整缓存，部分成功只用于本次合并展示，不覆盖完整缓存。
- SQLite 保存本地历史；模型页将今天 / 最近 7 天 / 最近 30 天所选区间的本地总 Token、当前 API 等价成本（按实际缓存价）、缓存命中、缓存节省和“同等 Token 不使用缓存时”的 API 等价成本集中显示，并明确标注价格来自本次启动获取、缓存或内置快照。历史页默认当前月，也可按月前后切换、显式选择年/月或选择含首尾的自定义日期区间；相同日历区间的本机样本卡同样比较当前缓存价成本与同等 Token 无缓存成本。官方区间汇总与本地样本始终分开；离线启动或官方每日用量暂时失败时显示缓存/混合新鲜度、最近成功时间及覆盖截至日期。
- 重置卡详情显示可用次数、授予时间、有效期、最近到期时间，以及本地观察到的发放/使用/过期记录。
- 可在到期前 7 天、3 天和 1 天发送 Windows 托盘通知；同一到期阈值在单次运行中只提醒一次。
- 顶栏设置浮层可切换浅色/暗黑模式、中文/英文，并可明确开启“开机自动启动（后台运行）”；偏好保存在本地并在下次启动时恢复，自启动默认关闭且不需要管理员权限。
- 默认匿名检查本仓库的最新正式 GitHub Release；一次成功检查后的 24 小时内不会重复自动联网，失败后可在后续启动重试，手动检查不受该间隔限制。检查只读取版本和官方发布页地址，应用不会下载更新文件，也不会替换、移动或删除安装目录。有新版本时标题栏提示，用户可打开官方 Release 页面或延后提醒；自动检查可在设置中关闭。
- 启动后每 15 分钟自动刷新；只有 app-server 进程级失败或三个账户方法全部失败才按 15 / 30 / 60 / 120 分钟全局退避，单个方法失败只让对应数据分区回退到历史值；手动刷新有 2 分钟冷却。
- 若服务端返回“有可用重置卡”但暂缺单卡明细，同一轮刷新最多补查 2 次；这些查询仍是只读操作，不会调用模型或消耗 Token。

> “API 等价成本”只是把本地 Token 按公开 API 单价换算的参考值，不代表 ChatGPT/Codex 订阅的实际账单。重置卡的使用或过期历史可能由连续快照推断，界面会标记为“推测”。

## 数据来源与隐私

- 配额、套餐、官方每日总量和重置卡：仅启动本机受信任的 OpenAI Codex 可执行文件并通过标准输入/输出运行 `codex app-server --stdio`，不直接读取或复制 `auth.json`。
- 模型、输入/输出、缓存和推理拆分：只读扫描 `%USERPROFILE%\.codex\sessions` 与 `archived_sessions` 下的 rollout JSONL。
- 本地数据库与价格快照：`%LOCALAPPDATA%\CodexUsageMonitor`。
- SQLite 会保存 rollout 来源的哈希标识和解析 checkpoint（其中可能包含本地 session id），以及 quota、重置卡和每日用量元数据；不会保存提示词或会话正文。当前版本默认长期保留这些统计，没有自动保留期或界面内清理按钮；需要清空时，请先退出应用，再删除该目录中的 `usage.db`、`usage.db-wal` 和 `usage.db-shm`。
- 除了启动时访问 OpenAI 官方价格页，不上传本地会话内容。
- 更新检查只访问 `https://api.github.com/repos/patrickzw1/CodexUsageMonitor/releases/latest`，请求不含 GitHub Token，也不会发送 Codex 用量、账号、提示词、会话正文、本地路径或设备标识。应用只缓存最新正式版本号和经过约束的本仓库官方 Release 页面地址；不会在应用内下载 ZIP、创建更新暂存目录或修改安装文件。
- 刷新只执行本地文件扫描和账户/额度读取，不创建模型推理请求，也不消耗模型 Token；它仍会产生少量经过 Codex 身份验证的元数据读取。
- 重置卡提醒只使用服务端返回的 `expiresAt`。如果服务端只返回次数或授予时间，应用不会推算有效期，提醒开关会暂时禁用。

> `account/read`、`account/rateLimits/read` 和 `account/usage/read` 是本机 Codex app-server 的可变协议依赖，不属于本项目能够保证稳定兼容的公开 API。通用 5 小时/每周额度、Spark、重置卡、汇总和每日用量会按返回字段判断可用性；方法不存在、空数组、字段变化或进程启动失败时，只把受影响分区标为“不可用”或展示带时间的上次成功值，本地 rollout Token/成本刷新仍会继续。

安全边界：应用不会从任意 `PATH` 目录执行同名程序。环境变量 `CODEX_USAGE_MONITOR_CODEX_PATH` 只接受绝对本地路径，默认候选限制在 Codex 安装/本地复制目录并校验 OpenAI 发布者签名。app-server 单个 stdout JSON 帧上限为 1 MiB 字符，stderr 只保留 64 KiB；六个官方价格响应各自限制为 2 MiB；rollout 单行上限为 1 MiB，并分批持久化。

## 下载与校验

普通用户可从 [GitHub Releases 最新版本](https://github.com/patrickzw1/CodexUsageMonitor/releases/latest) 下载版本化的 Windows 便携 ZIP 和 `SHA256SUMS.txt`。

应用内只提供更新提醒和官方 Release 页面入口。手动更新时：先关闭旧程序；从官方 Release 页面下载便携 ZIP；将 ZIP 解压到一个新的空目录；再从新目录运行 `CodexUsageMonitor.exe`。不要覆盖解压到旧安装目录。

用户数据和设置位于 `%LOCALAPPDATA%\CodexUsageMonitor`，不需要从旧安装目录复制。旧程序目录以及早期版本可能留下的同级 `.update-backup-*` / `.update-failed-*` 目录完全由用户自行审阅和处理；应用不会自动移动或删除它们。

下载后可在文件所在目录核对 SHA-256（下面以 `0.2.1` 为例）：

```powershell
Get-FileHash .\CodexUsageMonitor-0.2.1-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

两处哈希应一致。GitHub 自动生成的 **Source code (zip/tar.gz)** 只是源码快照，不是可直接运行的软件；普通用户应下载 Release assets 中由本项目生成的便携 ZIP。

## 版本规则

项目采用语义化版本号。Git 标签使用 `vX.Y.Z`，构建脚本中的版本号不带 `v`：

- `0.Y.0`：1.0 之前的功能版本；新增用户可见功能、页面或重要数据能力时递增 `Y`。
- `0.Y.Z`：同一功能版本内的修复版本；只包含 Bug、安全、性能、文档或打包修复时递增 `Z`。
- `1.0.0`：产品功能、数据格式和发布流程达到长期稳定状态。
- `X.0.0`（`X >= 2`）：存在不向后兼容的设置、数据格式或核心行为变化。
- 预发布版本使用 `v0.3.0-beta.1` 这类标签，不替代同版本的正式 Release。

## 开发与运行

需要 Windows 10/11 和仓库 `global.json` 固定的 .NET SDK 8.0.424。自包含发布运行时固定为 .NET 8.0.30：

```powershell
dotnet restore CodexUsageMonitor.sln -r win-x64 --locked-mode
dotnet build CodexUsageMonitor.sln
dotnet run --project src\CodexUsageMonitor\CodexUsageMonitor.csproj -- --show
dotnet test CodexUsageMonitor.sln -c Release -m:1 --no-restore
```

匿名性能回归基准只生成确定性的临时 JSONL，不读取真实 `.codex` 或现有 `usage.db`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\measure-performance.ps1
```

双击启动时会直接显示窗口；重复启动会唤醒已有窗口，不会创建多个托盘实例。点击窗口外会自动收起到托盘，标题区空白位置可拖动窗口，右上角减号可手动收起，齿轮按钮会在导航上方打开设置浮层，不会移动“总览 / 模型 / 历史”标签。左键托盘图标可重新打开，右键会显示玻璃风格菜单，可打开、刷新、访问数据目录或退出。需要开机静默启动时使用 `--hidden`；`--page=models`、`--page=reset` 和 `--page=history` 可直接打开对应页面。

开机自启动默认关闭。用户在顶栏设置浮层开启后，应用只写入当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下名为 `CodexUsageMonitor` 的独占值，内容为带引号的当前 EXE 绝对路径和 `--hidden` 参数；不会使用 `cmd.exe`、PowerShell、计划任务或管理员权限，也不会读取、覆盖或删除其他软件的启动项。关闭开关只删除该值；软件移动目录后，下次运行会在开关仍启用时把自己的值更新到当前位置。若界面无法关闭，可退出应用后运行：

```powershell
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CodexUsageMonitor /f
```

Windows 11 使用系统 Desktop Acrylic 和原生圆角；不支持该效果的系统会自动使用对应浅色或暗色的半透明后备界面。

## 便携发布

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 0.2.1
```

正式打包拒绝脏工作树。开发中的本地验证必须显式使用 `-Preview`，并建议把输出放在临时目录，避免覆盖已有 `dist`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 `
  -Version 0.2.1 -Preview -OutputRoot "$env:TEMP\CodexUsageMonitor-preview"
```

目标电脑无需安装 Python 或 .NET Runtime；解压版本化 ZIP 后直接运行 `CodexUsageMonitor.exe`。目标电脑需要已经登录并能运行 Codex；统计会读取该电脑自己的本地历史。

发布脚本严格执行 locked restore → tests → self-contained 多文件 publish → 可选 Authenticode 签名 → 签名校验 → 文档/许可证/发布运行时 SBOM → ZIP → SHA-256。ZIP 包含完整 EXE、DLL、`.deps.json`、`.runtimeconfig.json` 和原生运行时文件；脚本逐文件验证 publish、打包暂存目录与 ZIP 内容一致，并确认 README、项目 MIT LICENSE、第三方 NOTICE、`licenses/`、release metadata 和 CycloneDX SBOM 均为同一版本。该 SBOM 只描述实际发布的应用、运行时依赖和内嵌字体，不包含未随包分发的测试依赖。

如有签名证书，可传入绝对 `-SignToolPath` 与 `-CertificateThumbprint`；签名发生在压缩和最终哈希之前。没有证书时脚本明确报告 `NotSigned`，不会伪造签名。

当前便携包未做商业代码签名；复制到其他电脑后，Windows SmartScreen 可能首次提示“未知发布者”。确认文件来源后可选择“更多信息 → 仍要运行”。

## 开源与正规发布

可以同时提供源码和可直接使用的 Release。建议的公开发布顺序是：

1. 根目录已经采用 MIT License，源码可以公开使用、修改和分发。
2. 用 GitHub Actions 从版本标签构建，并在 GitHub Releases 同时发布源码、便携 ZIP、SHA-256 校验值和变更记录。
3. 面向普通用户优先提供 Microsoft Store 的 MSIX；Store 会重新签名，最能减少 SmartScreen 拦截。
4. 如果走 GitHub 直装包，使用可信的 Authenticode/Artifact Signing 身份对 EXE 和安装器签名并加时间戳，持续使用同一发布者身份。
5. 不做 UPX 压缩、混淆或静默自更新，并让构建脚本保持公开、可复现。

代码签名能证明发布者和文件完整性，但不能保证新版本第一次下载时绝不出现 SmartScreen 提示；未签名版本更容易被拦截。

## 统计口径

- “官方本月 Tokens”和模型页“官方 Tokens”仅为对应日期范围内官方每日返回值之和，与历史页每日列表使用相同数据源。SQLite 只存储官方返回值，同一天刷新时覆盖旧值，不把本地事件或历史累计汇总再加进去。
- 总览不再重复展示自然月本地日志统计；本地分析集中在模型页并全部跟随同一个时间选择。“同等 Token 不使用缓存时”的 API 等价成本只把缓存输入改按普通输入价，总 Token、输出和模型组合保持不变；在正常公开价格下，它等于当前估算加缓存节省。两种成本都是公开 API 单价情景估算，不是订阅账单。
- 历史页按月或自定义区间的开始、结束日期均包含在内；结束日期不能晚于最新快照的本地日期。官方区间卡只对实际返回日计算合计、返回天数、平均值和峰值；官方明确返回的零值计作一天，缺失日期则不补零。
- 官方每日卡右上角可切换列表/趋势图，默认列表，选择仅在本次运行保留。趋势图按真实日期间隔显示折线和浅色面积，缺失日期断开；悬停或聚焦后用左右方向键、Home/End 查看准确日期与 Token。修改日期后需点击“应用区间”，刷新或切语言不会替你应用待确认的日期；切换图形不会新增数据查询。
- 历史页“本地日志样本”对完全相同的本地日历区间单独读取现有 SQLite 事件，以 2×2 显示样本 Token、当前 API 等价成本、缓存命中与同等 Token 无缓存成本，缓存节省作为辅助关系行，并继续显示同一组已定价模型的覆盖率和价格来源。无缓存情景只把缓存输入改按普通输入价，总 Token、输出和模型组合不变；切换区间不会请求 app-server 或改写官方日用量，这部分也不代表官方账单。
- 官方没有返回的日期不按零处理，也不用本地日志补齐月度、7 天、30 天或历史；界面显示已返回天数、截至日期及获取/缓存时间。唯一例外是模型页“今天”：当天官方桶缺失时显示明确标注的本机 rollout 临时样本，官方零值或缓存值仍优先，后续官方值到达后直接替换而不相加。
- 本地模型排行、Token 组成、缓存命中和 API 等价成本属于独立的日志样本分析，不是官方总量的拆分；模型占比和定价覆盖率的分母也仅是本地样本。
- 缓存输入是输入 Token 的子集；非缓存输入为 `input - cached_input`。
- 推理 Token 是输出 Token 的子集；可见输出为 `output - reasoning_output`。
- 本地样本总 Token 使用 rollout 原值；缺失时只用 `input + output`，不会再次加上缓存或推理，也不会用于替代官方总量。
- 同一累计快照会按会话与累计计数去重；每个已处理前缀保存 SHA-256 指纹，检测到追加时只有旧前缀指纹匹配才从 checkpoint 继续解析。
- 首次运行可能需要扫描较多历史文件；后续以长度、修改时间和文件身份快速跳过未变化来源，不会每 15 分钟重读全部历史。截断、文件替换、常规原地改写或追加时前缀不匹配会安全重建来源索引；刻意保持“同长度且同修改时间”的原地篡改不会在稳态轮询中主动重读，后续元数据变化时才会被重新验证。
- 全量重建先分批写入暂存表；只有完整解析成功后，才在单一 SQLite 事务中原子替换正式事件与 checkpoint。取消、I/O 异常或进程崩溃会保留上一次完整索引。
- 本地 Token/缓存统计来自 rollout 的 `token_count.last_token_usage`，是日志聚合值；API 等价成本不是订阅账单，也暂未处理超长上下文加价、区域加价或未公开定价模型。
