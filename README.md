# AYCE OC2 Bridge

第一阶段开发版支持读取 OC2DIYLevel 发布包，并转换已支持的关卡与菜谱数据。Windows x64 Steam AYCE Build `22894075` 上，OC2 剧情 `1-1`（`s_sushi_1_1`）支持静态台面移位和菜谱覆盖。`DTsimple 7.1` 第 4 关可从 **战役模式 → 配菜关卡 → 自定义关卡** 独立开局，使用剧情凯文 2 的原生厨具重建源布局、碰撞和出生点。其他社区包可先导出兼容报告。

应用覆盖要求 Steam 离线模式。从剧情地图测试覆盖时，使用空存档或专用存档，因为结算可能记录该存档的原版关卡进度。从自定义关卡菜单启动则使用无存档槽的临时会话。观察模式可用于确认加载器、场景与游戏订单回调。

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

### DTsimple 自定义入口测试

在支持的 Steam 版本上启用下列覆盖。以 Steam 离线模式启动游戏，用已解锁凯文 2 的存档进入 OC2 故事地图并按 **F8**。入口暂用凯文 2 厨房测试源菜单和规则。开局目标时限为 **4:30**，单人开局及结算的四档阈值为 **100、200、300、1600**。交付蒸点心并等到结算，回到地图时比对凯文 2 的原有阈值、星数和最高分。

```powershell
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Custom -Overlay '.\release\dtsimple-4-custom-entry.json'
```

核对日志中的 `BRIDGE_CUSTOM_LAUNCH`、服务端和客户端的 `BRIDGE_CUSTOM_TIMER`、`BRIDGE_CUSTOM_RESULT`、`BRIDGE_CUSTOM_STAR_THRESHOLD` 和 `BRIDGE_CUSTOM_PROGRESS_BLOCKED`。若按键无响应，从 `BRIDGE_READY` 起检查 `BRIDGE_DISABLED`、`BRIDGE_CUSTOM_INPUT_UNAVAILABLE` 和 `BRIDGE_CUSTOM_LAUNCH_REJECTED`。

### DTsimple 第 4 关布局预览

用下列覆盖，在 Steam 离线模式下以已解锁凯文 2 的存档进入故事地图并按 **F8**。此预览复用 AYCE 厨具，按源关卡重建地面、墙体、49 个摆放点和 4 个厨师出生点。喷火机关和原场景美术暂未迁移，地面与墙边显示为简易几何体。

```powershell
.\scripts\Install-Bridge.ps1 -GamePath $game -Mode Custom -Overlay '.\release\dtsimple-4-layout.json'
```

先检查四个食材箱和三台搅拌机。用第三台搅拌机完成一次混合，将点心蒸熟、装盘、交付并等到结算。布局安装会留下 `BRIDGE_KITCHEN_GRID` 和 `BRIDGE_KITCHEN_APPLIED`。若场景仍是原版凯文 2，离开关卡并检查 `BRIDGE_KITCHEN_REJECTED` 或 `BRIDGE_SCENE_REJECTED` 附近的日志。用 `examples/dtsimple-4-layout.json` 重新转换时，`target --layout` 需用 `--dependency` 提供本机 AYCE 的 `duplicateassetisolation_assets_all.bundle`、`duplicateassetisolation_assets_all1.bundle`、`duplicateassetisolation_assets_all2.bundle` 和 `duplicateassetisolation_assets_all4.bundle`。

### 额外内容中的自定义关卡

用上述布局覆盖启动游戏后，进入 **战役模式 → 配菜关卡 → 自定义关卡**。列表包含当前布局以及 `BepInEx/plugins/AyceOc2Bridge/levels` 中通过检查的布局 JSON。Steam 离线模式下点选关卡即可从菜单直接开局。插件创建无存档槽的临时本机会话。完成并离开结算画面后会返回自定义列表，按关卡、版本和本地人数分别显示最高分与星数。成绩保存在 `BepInEx/config/ayce-oc2-bridge-scores.json`。F8 调试入口继续使用故事地图。

实机验收关注 `BRIDGE_STANDALONE_LAUNCH`、`BRIDGE_KITCHEN_APPLIED`、`BRIDGE_LOCAL_SCORE` 和 `BRIDGE_MENU_RETURN_COMPLETE`。逐个关卡的导入错误记录为 `BRIDGE_MENU_LEVEL_REJECTED`。重新打开「配菜关卡」时，列表会读取磁盘上的关卡包。

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

可选 `recipeMap` 用已经解析出的源菜谱名映射到游戏日志 `BRIDGE_RECIPES` 中的 AYCE 精确名称。插件在预设和随机选择结束后筛选结果，替换项从该模板已有的菜谱池取得。`BRIDGE_RECIPE_SELECTED` 记录替换结果，`BRIDGE_SERVER_ORDER` 和 `BRIDGE_CLIENT_ORDER` 分别记录服务端入队和客户端显示的菜谱。合并食材、自制图标和加工逻辑仍需实现。带移动平台、独立碰撞或其它脚本的内容在兼容报告中供人工评估。`examples/dtsimple-4-custom-entry.json` 的 `customEntry` 开关额外导出各人数的时限、出单间隔、订单保留时间和星级阈值。

设 `matchRecipes` 为 `true` 时，转换器使用来源菜谱的资源路径在所选 AYCE 模板中匹配等价菜谱。`examples/dtsimple-4-steamed-recipes.json` 只转换菜单，因此映射文件可以省去 `stationMap`。用 `--dependency` 指定 OC2DIYLevel 官方 ZIP，转换器只解析其中的 Unity 数据。

```powershell
.\.venv\Scripts\python.exe -m bridge target $game s_city_h2 --out local\kevin2.json
.\.venv\Scripts\python.exe -m bridge convert 'C:\maps\DTsimple_v7.1.zip' --dependency 'C:\maps\OC2DIYLevel-v0.11.1.zip' --profile local\kevin2.json --mapping examples\dtsimple-4-steamed-recipes.json --out local\steamed.json --report local\steamed-report.json --allow-partial
```

在 `inspect` 上使用 `--layout` 可导出源场景的预制件摆放和地面、墙体碰撞盒。在 `target` 上使用相同选项可列出 AYCE 场景中的原生厨具和它们的组件、坐标及动态父级。`target --dependency` 可读取额外的本地 AYCE 资源包，解析食材箱等对象的外部引用。上述布局预览使用这两份清单生成适用于当前模板的覆盖。

### 批量检查发布包

`inventory` 接收一个或多个 ZIP，也可以读取目录下的 ZIP。以包含布局信息的凯文 2 配置为模板，报告逐关列出场景数据、厨具、菜谱和当前的转换限制。若提供本机 AYCE 的 `catalog.json`，报告还会标出原版菜谱路径的精确匹配。这些匹配可用于选择原生资源，仍需验证游戏中的出单与交付。

```powershell
$catalog = Join-Path $game 'Overcooked All You Can Eat_Data\StreamingAssets\aa\Windows\catalog.json'
.\.venv\Scripts\python.exe -m bridge inventory 'C:\maps\community' --dependency 'C:\maps\OC2DIYLevel-v0.11.1.zip' --profile local\target-city-h2-layout.json --ayce-catalog $catalog --out local\community-inventory.json
```

`preview-exportable` 表示通过当前布局、菜谱和规则导出检查。关卡原包中的未迁移内容列在 `omittedFromPreview`。实际场景载入、交互和结算仍以游戏内测试为准。

加上 `--export-previews local\level-previews` 可在本地生成通过检查的布局 JSON。重复执行会复用内容相同的预览文件。如果同名文件内容不同，报告会在该关的 `previewExportError` 中记录冲突。已验证的布局可复制到 `BepInEx/plugins/AyceOc2Bridge/levels`，重新打开配菜关卡后即可在列表里看到。

对带有 AYCE 菜谱目录的清单运行 `batches`，即可按共用的转换条件生成工作批次。每张关卡只进入一个主批次，机关、缺失素材和菜谱引用可同时记录多个问题。`stock-recipes-static-layout` 表示现有扫描找到了所需的原版菜谱及基本静态厨房数据。导出和实机结果仍需分别验收。

```powershell
.\.venv\Scripts\python.exe -m bridge batches local\community-inventory.json --out local\migration-batches.json
```

`inspect --recipe-graph` 会按原资源引用导出自定义菜谱的原料、子菜谱、加工步骤和图标依赖。多道菜共用的节点在同一关中只导出一次；缺失节点保留原引用，便于整批核对源库。

`prepare-static` 按发布包和关卡集版本重新读取静态批次，提取原生菜谱地址、每人数规则、厨具摆放和碰撞盒。使用本机完整的 AYCE `catalog.json`，以核对每道菜的原生类型和运行时地址。输出供后续的 AYCE 场景适配器读取，游戏内加载使用 `inventory --export-previews` 生成的布局 JSON。

```powershell
.\.venv\Scripts\python.exe -m bridge prepare-static local\migration-batches.json 'C:\maps\community' --dependency 'C:\maps\OC2DIYLevel-v0.11.1.zip' --ayce-catalog $catalog --out local\static-layout-inputs.json
```

### 首批静态关卡测试

`pack-static` 用同一套厨具映射生成游戏可加载的静态关卡，并从当前 AYCE 安装生成禁用所有根对象的本地厨具供体。`Install-StaticBatch.ps1` 校验并安装清单里的关卡和供体。构建好的插件 DLL 放到测试包根目录，供体资源随本机游戏版本生成并留在本地。

```powershell
.\.venv\Scripts\python.exe -m bridge pack-static $game local\static-layout-inputs.json --profile local\target-city-h2-layout.json --stations examples\ayce-static-stations.json --archives 'C:\maps\community' --dependency 'C:\maps\OC2DIYLevel-v0.11.1.zip' --out release\stock-test
.\scripts\Build-Bridge.ps1 -BepInExCore 'C:\dependencies\BepInEx\core' -OutputDir .\release\stock-test
.\scripts\Install-StaticBatch.ps1 -GamePath $game -Package .\release\stock-test -ActiveOverlay .\release\dtsimple-4-layout.json
```

在 Steam 离线模式下启动游戏，打开 **战役模式 → 配菜关卡 → 自定义关卡**。可先用「暴吵萌厨 1-1」验证取菜、切菜、装盘、交付和结算，再用「星火夜话」验证篝火和烤具。「十二生肖-戌狗」可用于检验同一关里的搅拌、油炸和烤串。物理边界独立于美术渲染，空气墙在游戏中不可见。原发布包提供的静态网格和贴图随关卡导入，装饰引用有匹配时复用本机 AYCE 模型，其余区域使用主题地面。关卡原始光照、动画、美术脚本和未匹配的模型仍需适配。实机运行时可用 `BRIDGE_STOCK_AUTHORED_ART`、`BRIDGE_STOCK_VISUALS` 和 `BRIDGE_LOCAL_SCORE` 核对每关。遇到载入失败时保存包含 `BRIDGE_STOCK_PREPARATION_FAILED` 或 `BRIDGE_KITCHEN_REJECTED` 的日志。

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
