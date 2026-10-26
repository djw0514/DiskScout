# DiskScout 磁盘侦察

DiskScout 是一个面向 Windows 的只读磁盘空间排查工具。它用于快速回答：哪个目录最大、哪些大文件长期未动、空间主要被什么类型的文件占用，以及哪些位置因为权限未能统计。

## 已实现功能

- 扫描系统盘、普通盘或任意指定目录
- 实时显示已扫描容量、文件数、目录数和当前路径
- 按递归容量排列目录，按阈值筛选大文件
- 汇总文档、图片、视频、压缩包、程序、开发文件等类别
- 自动给出大目录、旧大文件、归档/虚拟磁盘和权限问题提示
- 记录无权限、路径过长和 I/O 异常，不因单个目录中断全盘扫描
- 跳过目录重解析点，避免符号链接/联接造成循环和重复统计
- 支持取消、管理员权限重启、资源管理器定位、复制路径和 CSV 导出
- 默认不提供删除按钮，避免误删系统文件
- 可选 AI 元数据审查，为候选大文件给出保留、转移、考虑删除或禁止删除建议

## AI 审查

完成磁盘扫描后，打开“AI 审查”页：

1. 默认使用 OpenAI Responses API；也可切换为兼容 Chat Completions 的服务。
2. API Key 可在当前界面临时输入，或通过 `OPENAI_API_KEY` 环境变量提供。密钥不会保存到磁盘或写入日志。
3. 默认不发送文件名、完整路径或文件内容，只发送匿名编号、扩展名、逻辑大小、修改日期、类别和本地判定的位置类型。
4. 如确实需要更具体的判断，可主动勾选“允许发送文件名和脱敏路径”；用户目录前缀会替换为 `%USERPROFILE%`。
5. 每批最多 25 项，可限制审查前 50/100/200/500 个大文件，控制费用和等待时间。

Windows、Program Files、ProgramData、分页文件、休眠文件等受保护位置由本地规则强制标记为“禁止删除”，不会发送给模型。AI 结果只用于辅助判断，程序不会根据模型输出自动删除或移动文件。

“每个文件”不等于把整个磁盘逐文件上传。当前版本审查扫描结果中的候选大文件；全盘数百万文件逐项调用 API 会产生不合理的费用、等待时间和隐私风险。

## 运行

优先使用与电脑“系统类型”一致的完整目录版，解压整个 ZIP 后再运行，不能只把 EXE 单独拖出：

```powershell
.\DiskScout.exe
```

- 常见 Intel/AMD 轻薄本：`DiskScout-portable-win-x64.zip`
- Windows ARM 笔记本：`DiskScout-portable-win-arm64.zip`
- 32 位旧电脑：`DiskScout-portable-win-x86.zip`

如果程序初始化失败，会在 `%LOCALAPPDATA%\DiskScout\startup.log` 生成诊断日志。Windows 8.1 及更早系统不在 .NET 8 支持范围内。

### 无法启动时

1. 不要直接在 ZIP 压缩包里双击；先完整解压。
2. Intel/AMD 处理器使用 x64 包，骁龙处理器使用 ARM64 包。
3. 如果 Windows SmartScreen 提示未知发布者，可选择“更多信息 → 仍要运行”。
4. 若仍失败，把 `%LOCALAPPDATA%\DiskScout\startup.log` 发给开发者。

从源码运行：

```powershell
dotnet run --project .\DiskScout.App\DiskScout.App.csproj
```

完整扫描系统盘时，建议点击右上角“管理员重启”。即使使用管理员权限，Windows 仍可能保护少数目录，这是正常现象。

## 构建与验证

```powershell
dotnet build .\DiskScout.sln
dotnet run --project .\DiskScout.SmokeTests\DiskScout.SmokeTests.csproj
dotnet run --project .\DiskScout.UiSmokeTests\DiskScout.UiSmokeTests.csproj
dotnet publish .\DiskScout.App\DiskScout.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\DiskScout-win-x64
```

核心测试覆盖容量、分类、深层目录、Top N、阈值边界、取消和并发扫描；WPF 测试覆盖连续扫描时的筛选、取消后重扫，以及旧 AI 请求结束时的界面状态。WPF 测试不会显示窗口，AI 请求使用本地模拟响应，无需 API Key 或网络。

### 扫描性能验证

```powershell
# 自动创建临时样本，预热 3 次后测量 7 次扫描，完成后清理样本
dotnet run --project .\DiskScout.SmokeTests -c Release -- --benchmark

# 也可只读测量现有目录；不会删除或修改该目录
dotnet run --project .\DiskScout.SmokeTests -c Release -- --benchmark "E:\待测目录"
```

2026-10-26 在 Windows x64、.NET 8.0.8 下，以 20,000 个文件、1,001 个目录、Top 2,000 为样本，优化前后测量结果如下：

| 指标 | 优化前 | 优化后 |
|---|---:|---:|
| 热缓存扫描中位耗时 | 136.27 ms | 69.07 ms |
| 每次扫描托管内存分配中位数 | 14.82 MiB | 9.33 MiB |

扫描直接使用当前目录对象累加文件数据，按目录发现顺序逆向汇总，省去父路径解析、目录字典查询和汇总前排序；Top N 堆只处理能够进入榜单的候选文件。耗时统计包含结果排序和分析提示生成。以上是合成样本的热缓存测量，实际全盘扫描还受磁盘速度、缓存和访问权限影响；托管分配量不等于进程峰值内存。

## 统计口径

DiskScout 统计文件的逻辑大小。NTFS 压缩、稀疏文件、硬链接、卷影副本、系统还原点、回收站权限及文件系统元数据，都可能导致扫描合计与 Windows 显示的磁盘已用空间不同。此差异本身也是排查线索。

方案选择和后续演进见 [docs/方案预研.md](docs/方案预研.md)。
