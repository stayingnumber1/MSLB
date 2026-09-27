# MSLB · 汇川 SV660N 伺服负载测试台

Windows 上位机可由 PC 主站直连 EtherCAT/CiA402，也保留 TwinCAT ADS 兼容入口。当前已接通 SV660N 兼容对象字典的 OP/SYNC 与 CSV 速度控制；CST 力矩加载仍等待 N·m 标定和母线保护验证。

## 当前状态

- P0 无硬件仿真链路已实现：Servo 状态、恒转矩/恒功率/转矩表、联锁、斜坡回零、自动配方、CSV/JSON 和趋势显示。
- 自动测试页可按“转速 × 转矩”生成曲线扫描配方，并自动剔除超出功率、转矩包络和全局限值的点。
- ADS 实机入口默认只读。只有型号、ESI/PDO、缩放、方向、回生、STO/硬件安全全部核实后，配置校验才允许写入。
- “EtherCAT 直连 / OP-SYNC”会独占专用网卡，并在整个会话内持续维持 OP/SYNC。
- 已开放 CSV 零速使能、±500 rpm 内斜坡速度命令、回零并失能。CST 按钮尚未开放。
- 2026-09-19 首次实机只读发现的 EEPROM 名称为 `InoSV635N`，与目标 SV660N 不一致；详见 `docs/COMMISSIONING_2026-09-19.md`，问题关闭前禁止写入。

## 运行

直接双击项目根目录的 `run.cmd` 打开上位机。默认只打开界面，不自动占用 EtherCAT 网卡。

在下拉框选择“EtherCAT 直连 / OP-SYNC”，点击“连接”。显示 `EtherCAT DIRECT · OP/SYNC` 后，确认两项机械/安全联锁，依次点击“速度使能（零速）”和“运行到目标”。停机使用“回零并失能”，等待实际转速回到零后再断开。直连期间不得运行 CoE 扫描、TwinCAT 或第二个 EtherCAT 主站。

仅在没有其他 EtherCAT 主站或在线保持进程时，才可运行 `run.cmd scan`，它会在启动后执行一次 PRE-OP 诊断扫描。

安装 .NET 8 SDK 后，在仓库目录执行：

```powershell
.\build.ps1
.\run.ps1
```

默认选择“仿真 / 无硬件”。实机调试前先阅读 `docs/ACCEPTANCE.md` 和 `docs/ETHERCAT_ARCHITECTURE.md`。

## 安全边界

软件 Stop、ADS 和 EtherCAT 不能替代独立急停/STO。回生硬件、机械护罩、方向和低风险加载必须按 P1→P2→P3 的阶段现场验收；未确认的 SV660N 参数不得猜测或写死。
