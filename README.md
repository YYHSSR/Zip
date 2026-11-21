# ZIP

Windows x64 归档工具，支持 Windows 和 Linux 常见归档格式。

## 运行

- 便携版：将 `ZIP-win-x64.zip` 解压到固定目录，打开 `ZIP.exe`。需要 Microsoft .NET 10 Desktop Runtime x64。
- 便携版右键：运行 `Enable-Context-Menu.cmd` 为当前账户注册菜单。Windows 11 从「显示更多选项」进入。移动或删除目录前运行 `Disable-Context-Menu.cmd`。
- Windows 11 新版右键：`ZIP-win11-x64.msix` 是本机测试包。将安装文件和包放在同一目录，运行 Install-ZIP.cmd，或在管理员 PowerShell 中运行下方命令。两套右键注册方式任选一种。
- 双击打开：便携版运行 `Enable-Context-Menu.cmd` 时会同时注册文件关联（每种格式独立的类型名称，并出现在「设置 → 默认应用」中）。之后在主窗口「⋯ → 设为默认压缩软件」，或运行 `ZIP.exe --set-default`，会跳到 ZIP 的默认应用设置页，在那里点「设置默认值」即可。Windows 10/11 不允许应用静默修改默认程序，这一步必须由用户确认。`ZIP.exe --associate` / `--unassociate` 可单独注册或移除关联。

```powershell
Add-AppxPackage -Path .\ZIP-win11-x64.msix -AllowUnsigned
```

新版测试包可以在「设置 → 应用 → 已安装的应用」卸载。微软要求含可执行内容的未签名测试包由管理员安装。[微软文档](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)

## 使用

- 右键快速压缩 ZIP 和 7z 使用较低的压缩级别。压缩选项窗口保留标准和最高级别。普通文件和文件夹的右键菜单依次是「压缩为 ZIP」「压缩为 7z」「压缩为其他格式」。第三项打开格式、压缩级别和保存位置设置。
- 压缩包的右键菜单包含「解压到此处」「解压到同名文件夹」「解压到其他文件夹」「用 ZIP 打开」。
- 支持创建 ZIP、7z、TAR、TAR.GZ、TAR.XZ、TAR.BZ2。只选择一个文件时，还可以创建 gzip、xz、bzip2。
- 可读取 ZIP、7z、RAR/RAR5、TAR、TAR.GZ/TGZ、TAR.XZ/TXZ、TAR.BZ2/TBZ2、TAR.ZST/TZST、GZ、XZ、BZ2、ZST、ISO、CAB、WIM、DEB、RPM。分卷需放在同一目录，打开入口文件，如 `name.7z.001` 或 `name.part01.rar`。
- 在列表双击文件会解压到临时目录并用系统默认程序打开（可执行文件会先确认），右键可选「打开方式…」。对 ZIP、7z、TAR、WIM，在外部程序里保存修改后，ZIP 会询问是否更新到压缩包。
- 编辑压缩包（ZIP、7z、TAR、WIM）：「添加」或把文件拖进列表，会加到当前文件夹；Delete 删除，F2 重命名。RAR、ISO、压缩的 TAR、分卷等格式只读。加密 7z 的文件名加密在编辑后保留。
- 同时打开多个压缩包时都在同一个进程里，各占一个窗口；再次打开已打开的压缩包会切换到它的窗口。资源管理器里选中多个压缩包解压时，用一个进度窗口依次处理，并先尝试上一个成功的密码。
- 「⋯ → 设置」：智能解压（只有一个顶层项目时不再多套一层文件夹，默认开启）、解压后打开文件夹、默认压缩级别、打开程序前确认和写回提示。设置保存在 `%LOCALAPPDATA%\ZIP\settings.json`。
- 地址栏的每一级都可点击跳转。「解压」按钮的下拉菜单提供「解压到当前文件夹」和「解压到同名文件夹」。
- 主窗口可在左侧目录树和文件列表之间切换。在列表双击文件夹可进入，按 Alt+↑ 返回上一级，按 Ctrl+F 搜索整个压缩包。列表支持按列排序、选中后解压、拖出、完整性校验。打开 ZIP 文件时也可切换文件名编码。密码只保留在当前归档窗口内，调用引擎时以短生命周期的进程参数传递。创建 AES ZIP 用可打印 ASCII 密码，需要中文密码时选择 7z。
- 直接压缩或完整解压 TAR 系列时，ZIP 尽量通过流处理归档。浏览、提取所选和保留下载安全标记时，ZIP 会按需使用临时文件。提取到已有目录时，遇到同名顶层文件或目录可保留两份或跳过。

## 构建

安装 .NET 10 SDK 和 MinGW-w64 x64 工具链。引擎目录要有同一版正式 7-Zip Windows x64 的 `7z.exe`、`7z.dll`、`License.txt`。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -EngineDirectory 'C:\Program Files\7-Zip' -Compiler 'E:\msys2\ucrt64\bin\g++.exe'
```

`-MakeAppx` 可指定 Windows SDK 的 `MakeAppx.exe` 以启用 MSIX 打包。输出目录由 `-OutputDirectory` 指定，默认为 `dist`。

随发布包提供 Igor Pavlov 的 7-Zip 26.06，按随附许可证中的 LGPL、BSD 和 unRAR 条款使用。引擎可以替换为兼容的新版 7-Zip。[官方项目](https://www.7-zip.org/) · [对应版本源码](https://github.com/ip7z/7zip/releases/tag/26.06) · [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
