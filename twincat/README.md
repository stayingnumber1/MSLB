# TwinCAT实时层导入模板（未编译/未部署）
     
本目录为TwinCAT 3可导入XML源码，不是可直接运行的.tnzip/EtherCAT工程。无实际ESI、型号、TwinCAT环境时不能诚实生成并验证完整设备映射。
     
## 导入顺序
     
1. ST_Command.TcDUT、ST_Status.TcDUT。
2. GVL_Config、GVL_Drive、GVL_Hmi三个GVL。
3. FB_CiA402Axis、FB_SafetyInterlock、FB_LoadTorqueControl、MAIN。
4. 将MAIN绑定1ms循环任务；核实GVL_Config.CycleSeconds与任务实际周期/超时设置相符。
     
## 数据契约v1
     
.NET StructLayout(Pack=1)与PLC pack_mode=1对应：
     
- ST_Command为42字节；Enable/Disable/Reset/Stop采用BYTE（0/1）；Mode为INT（0恒矩/1恒功率/2转矩表）；UI始终发送Nm/W/rpm工程单位。
- ST_Status为126字节，State数字与BenchState枚举对应。量纲换算在PLC完成。
- UI先写Command结构，再增加CommandCommit；PLC在新Commit时复制请求。
- Status.RevisionStart / RevisionEnd用于检测读取撕裂；Heartbeat每PLC周期变化，UI检测冻结。
- ValidMask：1 DC Bus，2电机温度，4电阻温度，8电流，16外部扭矩+转速，32 DUT电压+电流。未知值不设置对应位。
- 外部采集代码必须按设备数据更新年龄维护Healthy/Valid，不能接上线就永远TRUE。P2串口采集适配尚待实际协议。
     
## 设备映射审核表
     
| 变量 | 标准基线 | 必须实物核对 |
|---|---|---|
| Controlword / Statusword | 6040 / 6041 | 对象类型、PDO、状态机/QuickStop参数 |
| ModeCommand / ModeDisplay | 6060 / 6061，CST通常10 | 实际驱动支持的CST、进入条件 |
| TargetTorqueRaw / ActualTorqueRaw | 6071 / 6077，模板INT | 额定电机基准、单位、符号、上下限 |
| ActualVelocityRaw | 606C，模板DINT | 是否rpm/编码器单位、实际倍率 |
| ErrorCode | 603F，模板UINT | 型号/固件故障码表 |
| DcBus / 温度 / Current | 未指定 | 厂商对象、比例、采样更新率 |
| EcatOp / WkcOk | TwinCAT主站反馈 | 连线到真实OP/WKC，不得写常量TRUE |
| EmergencyOk / StoOk / GuardOk | 真实反馈 | 正逻辑、断线行为、硬件回路和安全诊断 |
| RegenerationOk / BrakeTemp | 实际回生系统 | 热开关、制动单元反馈及故障响应 |
     
联机前必须人工逐项签核。未核实项采用0/FALSE并阻止使能。硬件STO不通过普通PLC输出“模拟实现”。
     
## 配置与锁
     
根目录tools/export_plc_config.py将当前JSON转换成artifacts/plc-export/GVL_Config.TcGVL；固定保留Commissioned=false。限值/曲线/符号/缩放修改无需重编译C#，但需要重新导出并由TwinCAT工程师核对/更新PLC配置。导入后再修改上位机JSON文本会改变指纹，写入被阻止。
     
实时层模板包含：心跳失联降载、严重故障立即目标清零/QuickStop、真实转矩回零后禁能、停止超时、温度降额、功率/转矩/速度包络、峰值时限及再加载冷却。
     
PLC故障位是锁存的。驱动自身Fault首次显式Reset脉冲传给CiA402；驱动故障清除后可能需要再次显式Reset清除系统锁存。绝不复位后自动加载。
     
不直接复制示波器/文档中的阈值到驱动器。模式/厂商对象未核实则继续使用仿真和只读联机。
     
## 必须补做的验收
     
- TwinCAT编译、工程单元/仿真与PDO类型/内存对齐检查。
- 实际传感器健康与数据年龄，PLC周期/WKC/模式反馈，任务停止/重启和配置缺失。
- PC进程强杀、网线断开、PLC停止、驱动断电，验证实际转矩/PWM与硬件安全链。
- 使能/加载/停止/复位的所有状态和正反方向，峰值时限和温度降额，失联后不恢复。
- 停止不能只看Target=0；同时确认实际扭矩和机械转速。STO是撤销转矩能力，不保证旋转件停稳。
- 1ms任务并不使Windows日志变成1kHz；高频无损记录需PLC缓冲/ADS通知，另做吞吐和数据完整性验证。
     
在这些步骤完成前，模板不具备实机验收结论。
