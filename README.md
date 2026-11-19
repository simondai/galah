# Galah

Windows 桌面启动器(launcher),以作者家的宠物凤头鹦鹉命名。绿色单文件、零安装、零第三方依赖——一个 exe 拷到哪都能跑,数据就存在它旁边。

![平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows-blue) ![运行时](https://img.shields.io/badge/%E8%BF%90%E8%A1%8C%E6%97%B6-.NET%208-lightgrey) ![依赖](https://img.shields.io/badge/NuGet%E4%BE%9D%E8%B5%96-0-success)

## 它是什么

把常用的程序、文件、文件夹、网址、系统设置页收进一个面板:分类页签 + 图标磁贴网格,单击/双击启动,托盘常驻后台,全局热键(Alt+Space)随时唤出。所有数据(分类、条目、设置、窗口状态)都落在 exe 同目录的 `GalahData/` 文件夹,随文件夹一起搬走。

- **绿色单文件发布**:自包含 ~68MB exe,不装运行时、不写注册表、不留后台服务
- **零第三方 NuGet**:全部 .NET BCL + Win32 P/Invoke,连图标都是程序自绘
- **默认不联网**:没有任何自动网络行为;唯一的联网操作是你在编辑网址条目时手动点"获取网站图标",且只访问该站点本身
- **持久可靠**:写盘原子替换 + 双备份轮换(`.bak.1`/`.bak.2`),损坏自动隔离,一次用户动作一次写盘

## 功能

### 面板与条目

| 功能 | 说明 |
|---|---|
| 分类页签 | 顶部横排或左侧竖排(设置可切),重命名/前移/后移/删除,拖动排序;＋ 圆形按钮紧跟列表尾部新建 |
| 磁贴网格 | 拖动排序、跨分类拖动;五种条目类型:程序、文件、文件夹、网址、协议链接 |
| 协议链接 | 支持 `ms-settings:` 等 Windows 协议条目(白名单校验,ShellExecute 启动) |
| 窗口记忆 | 大小、位置、最大化状态、当前分类,关了再开原样恢复 |

### 添加条目

| 功能 | 说明 |
|---|---|
| 添加选择器 | 四大类一键添加:系统工具(任务管理器/设备管理器等 30 项 + 注册表 App Paths 枚举)、设置项(55 个 ms-settings 页面)、标准文件夹(17 个)、已安装应用(注册表扫描);顶部即时过滤,双击或回车即加,窗口常开支持连续添加 |
| 其他方式 | 右键按路径添加、从资源管理器直接拖进面板 |

### 唤出与导航

| 功能 | 说明 |
|---|---|
| 全局热键 | 默认 Alt+Space,设置页可改 |
| 面板位置 | 记住上次位置 / 跟随鼠标 / 屏幕居中(鼠标模式在无光标环境自动退化为居中) |
| 键盘导航 | Ctrl+1..9 直达分类、Ctrl+Tab / Ctrl+Shift+Tab 循环;滚轮在磁贴区滚到边缘时切分类 |
| 托盘常驻 | 左键唤出面板、右键菜单(显示/隐藏/添加快捷方式/设置/退出),退出有二次确认 |
| 桌面快捷方式 | 设置页一键创建(已存在则更新) |

### 维护与数据

| 功能 | 说明 |
|---|---|
| 库维护 | 清理失效项、清理重复项(同目标保留第一个)、按名称排序、重建图标缓存——先预览后确认,一次动作一次写盘 |
| 数据导出/导入 | 单 JSON 备份;合并(只补缺失,重新分配 id 防碰撞)或替换两种模式;非本产品/超前版本/损坏文件整单拒绝 |
| 网站图标 | URL 条目手动获取站点图标:只访问该站点、4 秒预算、512KB 上限、非图片内容(如返回网页)一律拦截 |

### 外观

| 功能 | 说明 |
|---|---|
| 主题 | 亮/暗两套,Apple 设计语言(单一 Action Blue 交互色,parchment 画布) |
| 面板材质 | 不透明 / 半透明(透出桌面),设置即时切换 |
| 图标大小 / 点击方式 | 小/中/大,单击/双击启动,设置即时生效 |

## 快速开始

```
git clone https://github.com/simondai/galah.git
cd galah
dotnet publish Galah/Galah.csproj -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -o publish
publish\Galah.exe
```

直接双击 `publish\Galah.exe` 即可。启动后驻留托盘(不弹面板),按 Alt+Space 或点托盘图标唤出。

## 目录结构

```
galah/
├── Galah/              主程序(WPF,net8.0-windows)
│   ├── Views/         窗口与控件
│   ├── ViewModels/    MVVM 视图模型
│   ├── Services/      托盘、热键、持久化、图标、维护、迁移、选择器数据源等服务
│   └── Models/        数据文档(catalog/settings/ui-state)与常量
├── Galah.Tests/        单元测试控制台(T1–T22,含数据源/维护/迁移/位置/快捷方式/favicon)
├── Galah.Acceptance/   进程级验收工具(真实 shell/鼠标/UIA 驱动活体进程,25 断言)
├── DESIGN.md          UI 设计规范
├── ROADMAP.md         功能路线图
├── IMPLEMENTATION-NOTES.md   工程决策记录
└── publish/           发布产物(git 忽略)
```

## 测试

```
dotnet run --project Galah.Tests -c Release                      # 317 断言,单元级
dotnet run --project Galah.Acceptance -c Release -- <GalahData 目录>   # 25 断言,进程级
```

单元测试覆盖:条目循环与校验、回调解码(三种 shell 报文布局)、图标管线、选择器数据源(每个候选都能通过提交校验)、维护检测(预览零写盘/排序幂等/素材不被清理)、迁移(坏包拒绝/合并防碰撞)、面板位置数学、桌面快捷方式往返、favicon 全部守卫(本地服务器实测)。

验收工具驱动一个真实运行的 Galah 进程:托盘菜单在全新启动(无任何窗口)时也必须弹出、选择器选中的设置页真的写入数据文件、面板位置切换后实测落点、退出确认框可见且点击后进程真的终止。

## 系统要求

- Windows 10 1709+ / Windows 11(x64)
- 运行时自包含,无需安装 .NET

## 名字

Galah(凤头鹦鹉)是作者家的宠物。粉红凤头鹦鹉聪明、话多、精力旺盛——大概也是一个好启动器的样子。

## 许可

[MIT](LICENSE)
