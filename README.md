# MSLB · 汇川 SV660N 伺服负载测试台

Windows 上位机通过 TwinCAT ADS 控制 EtherCAT/CiA402 实时层，让 SV660N + MS1H4 伺服以力矩模式为 DUT 提供阻尼负载。项目包含 WPF 上位机、仿真桥、ADS 桥、自动测试、数据记录、TwinCAT ST 骨架和只读 EtherCAT 调试工具。

## 当前状态

- P0 无硬件仿真链路已实现：Servo 状态、恒转矩/恒功率/转矩表、联锁、斜坡回零、自动配方、CSV/JSON 和趋势显示。
- 自动测试页可按“转速 × 转矩”生成曲线扫描配方，并自动剔除超出功率、转矩包络和全局限值的点。
- ADS 实机入口默认只读。只有型号、ESI/PDO、缩放、方向、回生、STO/硬件安全全部核实后，配置校验才允许写入。
- EtherCAT 直连工具目前仅用于网卡枚举和 PRE-OP 只读诊断，不承担周期加载控制。
- 2026-09-19 首次实机只读发现的 EEPROM 名称为 `InoSV635N`，与目标 SV660N 不一致；详见 `docs/COMMISSIONING_2026-09-19.md`，问题关闭前禁止写入。

## 运行

直接双击项目根目录的 `run.cmd` 打开上位机。默认只打开界面，不自动占用 EtherCAT 网卡。

仅在没有其他 EtherCAT 主站或在线保持进程时，才可运行 `run.cmd scan`，它会在启动后执行一次 PRE-OP 诊断扫描。

安装 .NET 8 SDK 后，在仓库目录执行：

```powershell
.\build.ps1
.\run.ps1
```

默认选择“仿真 / 无硬件”。实机调试前先阅读 `docs/ACCEPTANCE.md` 和 `docs/ETHERCAT_ARCHITECTURE.md`。

## 安全边界

软件 Stop、ADS 和 EtherCAT 不能替代独立急停/STO。回生硬件、机械护罩、方向和低风险加载必须按 P1→P2→P3 的阶段现场验收；未确认的 SV660N 参数不得猜测或写死。
