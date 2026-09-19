# EtherCAT 联机记录 · 2026-09-19

## 结论

PC 通过专用网卡 `以太网 3`（Realtek PCIe GbE Family Controller #3）成功发现 1 个 EtherCAT 从站，并完成 PRE-OP CoE 只读。当前不能进入周期转矩控制：配置目标为 SV660N，但从站 EEPROM 名称为 `InoSV635N`，且当前 RxPDO 是位置目标映射，不含目标转矩 `0x6071`。

## 实读身份

- NPF：`\Device\NPF_{9F9BCCE8-6557-49AC-B6F9-6C5BB3C2B158}`
- 从站位置：1
- EEPROM 名称：`InoSV635N`
- Vendor ID：`0x00100000`
- Product Code：`0x000C010E`
- Revision：`0x00010000`
- Identity Serial：`0`
- CiA402 Statusword：`0x0033`（switched on，非 operation enabled）
- Error Code：`0x0000`
- Supported Modes raw：`0x000003AD`

原始报告位于 `artifacts/ethercat/scan.json`（运行产物默认不提交）。

## 当前 PDO

RxPDO `0x1701`：`6040:00/16`、`607A:00/32`、`60B8:00/16`、`60FE:01/32`。

TxPDO `0x1B01`：`603F:00/16`、`6041:00/16`、`6064:00/32`、`6077:00/16`、`60F4:00/32`、`60B9:00/16`、`60BA:00/32`、`60BC:00/32`、`60FD:00/32`。

## 写入解锁前置条件

1. 人工核对驱动器铭牌、完整型号与固件，解释 SV660N 配置和 `InoSV635N` EEPROM 名称差异。
2. 获取与 Vendor/Product/Revision 对应的官方 ESI 和对象字典。
3. 确认 CST/等效力矩模式、`0x6071`/`0x6077` 单位与缩放，以及速度单位。
4. 确认允许的 PDO 分配方式，并在零转矩、下使能条件下验证映射。
5. 完成方向、回生制动、STO、急停和机械护罩现场核验。

在上述条件完成前，`allowHardwareWrites` 必须保持 `false`，不得执行 Servo ON、PDO 输出或 OP 周期加载。

## CSV 速度模式只读核验

- 官方高级用户手册确认 CSV 模式为 `6060h=9`，目标速度为 `60FFh`。
- 实机固定 RPDO `1703h` 含 `6040/607A/60FF/6060/60B8/60E0/60E1`；固定 TPDO `1B04h` 含 `603F/6041/6064/6077/6061/60F4/60B9/60BA/60BC/606C`。
- 实读电机代码 `14101`（23 位编码器），电子齿轮比 `1:1`。
- 手册公式：`motor RPM = raw velocity × gear ratio ÷ encoder resolution × 60`。因此 500 rpm 的目标原始量为 `69905067`，`rpmPerRawVelocity=60/8388608`。
- `tools/ethercat_velocity.py check --rpm 500` 已在实机通过，报告写入 `artifacts/ethercat/velocity.json`；该动作无 SDO/PDO 写入。
- 控制路径已实现但仍被配置门禁锁定。尚需现场确认轴端/联轴器状态、护罩、独立急停/STO、允许方向和回生路径，之后才可进行 50 rpm 方向预检，再升至 500 rpm。

## 现场型号决定

用户确认机身为 SV635，并授权按 SV660N 兼容对象字典操作。实读固定 PDO、CSV 对象、编码器类型和电子齿轮比与所用 SV660N 手册一致，因此 `modelVerified` 已设为 `true`。EEPROM 身份四元组仍固定为实读值，连接时必须逐项匹配；该决定不解除机械安全、方向、回生和硬件写入门禁。

## 50 rpm 试转结果

- 用户确认轴端环境、急停/STO 与回生条件允许试转。
- 多次尝试均在 Servo ON 和非零速度命令之前由软件中止，电机未旋转。
- 实读发现原始 `H02-00/0x2002:01=2`，试验期间按手册临时切换为 EtherCAT 模式 9；结束后已恢复为原值 2，`6060` 也恢复为 0。
- 普通映射与 overlap 映射均能进入 OP，但过程数据 WKC 恒为 2、TPDO 缓冲区全零，无法获得有效 `6041/6061/606C`，因此禁止 Servo ON。
- 一次退出顺序在 OP 状态关闭 SYNC0，触发 `EE08.0 / 0x0E08`（SYNC signal loss）。程序现已修正为先退 SAFE-OP 再关闭 SYNC0。
- CiA402 bit 7 和厂商 `200D:02` 复位均未清除 EE08.0。最终状态：`H02-00=2`、`6060=0`、`6041=0x0038`、`603F=0x0E08`；需要在驱动器面板复位或安全断电重启。
- `allowHardwareWrites` 已重新设为 `false`。当时计划改用实时主站；随后通过下述“持续 OP/SYNC + SDO 监测”专用路径完成了受限试转，但通用加载控制仍不开放。

## 在线保持模式试转成功

- 无法断电重启时，采用“全程保持 OP/SYNC0、RPDO 下发、OP 内通过 SDO 监测”的在线恢复路径；这不是屏蔽 EE08，而是持续满足其同步条件。
- 零速状态验证：`0x06 → ready_to_switch_on`、`0x07 → switched_on`、`0x00 → switch_on_disabled`，全程错误码 0。
- 50 rpm 方向验证平均值：`50.0244 rpm`。
- 500 rpm 保持段平均值：`500.2107 rpm`。
- 已斜坡回零，实读速度低于 5 rpm 后失能；程序继续保持 OP/SYNC、目标速度 0、控制字 0，避免在不能重启的情况下重新触发 EE08。
- `directionVerified` 已设为 `true`。通用 `allowHardwareWrites` 仍保持 `false`；只有专用在线保持进程正在占用 EtherCAT 网卡，严禁同时启动第二个主站。
