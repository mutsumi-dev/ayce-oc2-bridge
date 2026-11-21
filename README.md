# AYCE OC2 Bridge

第一阶段开发版支持读取 OC2DIYLevel 发布包，并转换已支持的关卡与菜谱数据。Windows x64 Steam AYCE Build `22894075` 上，OC2 剧情 `1-1`（`s_sushi_1_1`）支持静态台面移位和菜谱覆盖，剧情凯文 2（`s_city_h2`）支持菜谱覆盖。覆盖沿用 AYCE 的地面、出生点、关卡设置和计分。社区包中新菜谱的元数据可先导出到兼容报告。

应用覆盖要求 Steam 离线模式。测试时使用空存档或专用存档，因为进入的是游戏自带关卡，结算可能记录该存档的原版关卡进度。观察模式记录日志，适合先确认加载器、场景与游戏订单回调。

## 游戏内首测

下列命令在仓库根目录的 PowerShell 中执行。首测使用 `release` 中的预编译文件。首次加载 BepInEx 生成 interop 时保持电脑联网，并给它几分钟。游戏退出后再更改模式。

```powershell
Set-Location 'D:\Codex\ayce-oc2-bridge'
$game = 'G:\SteamLibrary\steamapps\common\Overcooked! All You Can Eat'
$loader = '.\release\BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip'
.\scripts\Install-Bridge.ps1 -GamePath $game -BepInExZip $loader -Mode Observe
```

1. 从 Steam 启动游戏，进「故事模式 → 胡闹厨房 2 → 1-1」。做一道鱼或虾刺身并上菜。关闭游戏后检查日志。加载正常应有 `BRIDGE_READY`、`BRIDGE_SCENE`、`BRIDGE_ORDER_CREATED` 和 `BRIDGE_DELIVERY accepted=True`。如果只到主菜单，日志中只有加载标记也属于正常现象。
2. 关闭游戏，运行以下命令启用模板布局测试。在 Steam 菜单切换到「进入离线模式」，重新打开游戏并再次进入同一关。

```powershell
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Demo
```

3. `Demo` 把同一列的一个切菜台从厨房一边移到对侧，并将普通桌台移回原切菜台位置。面对移过去的切菜台时，确认对应台面高亮，原位置的台面不跟着亮。实际用移动后的切菜台切鱼或虾、装盘、上菜，检查加分和结算。回到地图再进一次，随后用同一台电脑的两名玩家重复。期望日志包含 `BRIDGE_MOVE`、`BRIDGE_HIGHLIGHT_REMAP`、`BRIDGE_DELIVERY accepted=True` 和离开时的 `BRIDGE_SCENE_EXIT`。
4. 模板测试通过后可换用真实样本 `DTsimple 7.1` 的部分迁移。这个覆盖把源关卡的两个切菜台按原相对位置迁移到模板的对侧，期望有四条 `BRIDGE_MOVE`。源图的其他工作台和菜谱的处理情况在 `release/dtsimple-4-report.json` 中。

```powershell
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Custom -Overlay '.\release\dtsimple-4-preview.json'
```

5. 如果有失败，发回从 `BRIDGE_READY` 到退出该关的日志。优先提供 `BRIDGE_DISABLED`、`BRIDGE_SCENE_REJECTED` 或 `BRIDGE_RECIPE_SKIPPED` 附近的完整异常。结束后可运行 `-Mode Disable` 回到只观察模式。也可以关闭游戏后移走 `BepInEx/plugins/AyceOc2Bridge/AyceOc2Bridge.dll` 停用插件。日志会在重启时重写，每轮测试后先保存需要的行。

```powershell
Select-String -LiteralPath "$game\BepInEx\LogOutput.log" -Pattern 'BRIDGE_' -Context 0,3
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Disable
```

当前 DLL 已编译完成。上述期望日志和游戏内行为需要实机测试确认。插件和导入都在本地运行。

### DTsimple 第 4 关原菜单

`release/dtsimple-4-kevin2-recipes.json` 使用官方 OC2DIYLevel 依赖，从 `DTsimple 7.1` 第 4 关原包解析并转换三道菜谱。安装后以 Steam 离线模式，在测试档进入 OC2 剧情的 **凯文 2**。订单应为胡萝卜、肉或虾蒸点心。普通蒸鱼应从菜单中筛除。做并交付任意一道点心来确认计分。

```powershell
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Custom -Overlay '.\release\dtsimple-4-kevin2-recipes.json'
```

## 检查和转换自己的社区包

需要 Python 3.11 或更新版本。`inspect` 读取 ZIP 或已解压目录，列出每关的工作台路径、世界坐标、菜谱引用、每人数参数和识别到的机关。可以用一个或多个 `--dependency` 加入同版本的社区资源包。指向可执行文件的输入只会出现在忽略清单，导入器处理 Unity 数据文件。

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
New-Item -ItemType Directory -Path local -Force | Out-Null
.\.venv\Scripts\python.exe -m bridge inspect 'C:\maps\DTsimple_v7.1.zip' --out local\source.json
.\.venv\Scripts\python.exe -m bridge target $game s_sushi_1_1 --out local\target.json
.\.venv\Scripts\python.exe -m bridge convert 'C:\maps\DTsimple_v7.1.zip' --profile local\target.json --mapping examples\dtsimple-4-map.json --out local\custom.json --report local\report.json --allow-partial
```

以 `examples/dtsimple-4-map.json` 为例，`stationMap` 将同一类源工作台映射到模板对象。`offset` 是统一的坐标平移，保留源对象之间的相对位置。`vacate` 可把占位的模板工作台移到另一模板对象的原位置。转换器验证路径唯一、基本碰撞、目标高度和最大位移。游戏仍需核验桌台的实际可达性、碰撞与交互。

使用静态合批模型的桌台需要移到另一座参与重排的同类台面原位。插件据此匹配目标格的台面渲染器，让交互高亮跟随站点，同时利用模板已烘焙的桌台外观。

可选 `recipeMap` 用已经解析出的源菜谱名映射到游戏日志 `BRIDGE_RECIPES` 中的 AYCE 精确名称。插件在预设和随机选择结束后筛选结果，替换项从该模板已有的菜谱池取得。`BRIDGE_RECIPE_SELECTED` 记录替换结果，`BRIDGE_SERVER_ORDER` 和 `BRIDGE_CLIENT_ORDER` 分别记录服务端入队和客户端显示的菜谱。合并食材、自制图标和加工逻辑仍需实现。导入不会默默替换任何不可解析的菜谱。带移动平台、独立碰撞或其它脚本的内容在兼容报告中供人工评估。每人数计时和星级阈值当前仅导出到兼容报告。

设 `matchRecipes` 为 `true` 时，转换器使用来源菜谱的资源路径在所选 AYCE 模板中匹配等价菜谱。`examples/dtsimple-4-steamed-recipes.json` 只转换菜单，因此映射文件可以省去 `stationMap`。用 `--dependency` 指定 OC2DIYLevel 官方 ZIP，转换器只解析其中的 Unity 数据。

```powershell
.\.venv\Scripts\python.exe -m bridge target $game s_city_h2 --out local\kevin2.json
.\.venv\Scripts\python.exe -m bridge convert 'C:\maps\DTsimple_v7.1.zip' --dependency 'C:\maps\OC2DIYLevel-v0.11.1.zip' --profile local\kevin2.json --mapping examples\dtsimple-4-steamed-recipes.json --out local\steamed.json --report local\steamed-report.json --allow-partial
```

## 构建

使用已解压的 BepInEx 6.0.0-pre.2 Unity IL2CPP x64 `BepInEx/core` 目录。编译脚本支持 .NET 6 SDK，或带 Roslyn 的 PowerShell 7 和本机 .NET 6 运行时。安装脚本可以接受同一版的未解压官方 ZIP。首次下载与运行环境说明见 BepInEx 官方手册。

```powershell
.\scripts\Build-Bridge.ps1 -BepInExCore 'C:\dependencies\BepInEx\core'
.\tests\Test-OrderFilter.ps1 -BepInExCore 'C:\dependencies\BepInEx\core'
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

- [OC2DIYLevel 编辑器与手册](https://github.com/gua248/Overcooked2-LevelEditor)
- [DTsimple 7.1 发布页](https://gitcode.com/CH3NGY2/OC2-Map-Bundle/releases/tag/DTsimple)
- [BepInEx 6.0.0-pre.2 官方发布](https://github.com/BepInEx/BepInEx/releases/tag/v6.0.0-pre.2)
- [BepInEx IL2CPP 安装手册](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
