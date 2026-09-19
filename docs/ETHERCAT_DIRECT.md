# PC 直连 EtherCAT CoE / CiA402

采用 PC EtherCAT 主站 → 独立有线网卡 → SV660N EtherCAT IN，不使用 PLC，也不依赖 InoDriverShop API。USB 转有线网卡必须先被 Windows 识别为网络适配器；USB 调试线不等同于 EtherCAT 网卡。

## 已实现

PySOEM 1.1.13（SOEM 封装）的独立调试入口，支持枚举抓包网卡、初始化从站、CoE SDO 读取、CiA402 状态解析，以及读取 1C12/1C13 当前 PDO 分配和映射。对象读取失败保留具体错误，不补零、不伪造在线状态。速度、转矩保留 raw，未核对设备缩放前不标作 rpm/N·m。

该阶段是 PRE-OP 调试工具，**不是已完成的连续转矩控制器**。没有 SDO 写参数、PDO 输出、Servo ON、故障复位或 OP 状态请求。config_init 会初始化所选总线所有从站，可能改变原有状态；因此扫描必须选定独立、已停机且没有其他主站占用的总线，不能称为被动扫描。

## 使用

工程独立环境已按 requirements-ethercat.txt 安装；其他电脑先执行：

```powershell
python -m venv .tools/ethercat-python
.tools/ethercat-python/Scripts/python.exe -m pip install -r requirements-ethercat.txt
```

Windows 需要适用的 Npcap/WinPcap 兼容抓包驱动；不从 InoDriverShop 目录随意复制旧版 DLL。主站进程与抓包 DLL 位数必须匹配。安装系统驱动之后可能需要重新启动应用。此工程不会自动修改系统驱动或网卡配置。

```cmd
ethercat.cmd
```

该命令仅枚举网卡，不发送 EtherCAT 帧。将确认的专用有线网卡 NPF 名称写入 config/ethercat.json 的 adapter。只有确认总线可供调试后才设 dedicatedAdapterConfirmed=true：

```cmd
ethercat.cmd scan --output artifacts/ethercat/scan.json
```

报告包含 EEPROM 标识、1018 身份对象、6041 状态字、6061 模式显示、603F 错误码、6064/606C/6077 原始值、6502 支持模式和 PDO 映射。扫描成功不等于 CST 已运行。

## 后续控制接入

1. 实读 Vendor/Product/Revision，对照当前硬件固件的官方 ESI/对象字典。核对转矩单位、速度单位、方向与限值。
2. 已从实机只读确认固定映射 `1703h/1B04h` 可提供 CSV 所需的 6040、60FF、6060、6041、6061、606C 以及正负转矩限值；控制程序仍会逐次核对身份和映射，不能按假定偏移下发。
3. 采用独立循环主站执行 CiA402 状态转换、零转矩使能、斜坡、负载限幅、WKC/掉站监控和失联处理；WPF 负责操作与记录。普通 Windows/Python 及 USB 网卡不作为已验证的实时保证，需实测周期分布、最坏抖动和从站超时。需要确定性时采用经过验证的 PC 实时主站运行环境，仍不要求 PLC。

速度控制调试入口：

```powershell
ethercat.cmd velocity-check
```

它只做 PRE-OP 读取和 500 rpm 原始量换算，不写驱动。`tools/ethercat_velocity.py run` 已实现 DC、OP、CiA402、50 rpm 方向预检、500 rpm 斜坡、WKC 监控和回零，但只有所有 `bench.json` 实机门禁均经现场验收设为 true，且本次命令同时给出轴区清空与急停测试确认时才允许运行。
4. 核实驱动器通信故障反应与独立停机链，现场验证回生、转向、断线及超速工况，再开放加载。主机发出的回零指令不能替代通信断开后的驱动器行为。

## 官方资料

- https://pysoem.readthedocs.io/en/latest/basics.html
- https://pysoem.readthedocs.io/en/latest/master.html
- https://pysoem.readthedocs.io/en/latest/cdef_slave.html
- https://www.inovance.eu/fileadmin/downloads/Brochures/EN/SV660N_FIyer_Spreads_Web_EN_v0.6.pdf

汇川产品资料明确 SV660N 支持 CoE/CiA402 及 CST；具体对象映射仍应以设备版本资料和现场读取结果为准。
