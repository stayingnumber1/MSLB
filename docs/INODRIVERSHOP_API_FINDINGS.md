# InoDriverShop 本机接口检查（2026-09-18）

用户已明确要求：电脑直接对接 InoDriverShop，不使用 PLC。此前 ADS 连接失败不代表 SV660N 与 InoDriverShop 连接失败。

## 检查范围及结果

递归检查 C:\Inovance\InoDriverShop；根目录仅有 Servo。MainVersion.ini 记录版本 3.7.6.10。
未发现独立 SDK、C/C++ 头文件、导入库、类型库、IDL 或接口示例工程。存在 help\InoDriverShopManual.chm（8312455 字节）；本次 hh.exe 解包未产生内容，因此不能断言该帮助文档没有接口说明。

- ThirdPartyCommunicationPanel.dll：存在“第三方通信调用”资源；静态类型名称包括 CReadRequest、CWriteRequest、CRequestAgent、CResponseAgent、ISNSTCPClient。发现 127.0.0.1、“连接服务器”、示波器与参数上传下载相关字符串。这是第三方通信实现线索，不足以确定协议、端口、报文格式或外部调用方法。
- USBDriver.dll、InoLinkDriver.dll、DriverAgent.dll：32 位原生插件，导出 CreateInterFace、SafeRelease、SetQueryInterfaceCallback、SupportedInterface。缺少接口 ID、虚函数定义与生命周期约定，不能仅按函数名称直接调用。
- InoCommunication.dll：反射读取公开签名，主要是 Login、Upload、DoGet、DownLoad、QueryByUser 等，以及用户、文件上传下载模型；未发现伺服参数读取或转矩控制公开方法。
- 按正在运行的 InoDriverShop 进程查询 TCP 连接，本次未返回记录；不能据此排除未开启的通信插件、客户端连接或其他 IPC 方式。

本次只做文件和元数据检查，未调用驱动库、修改驱动器参数、使能或施加载荷。尚未完成自研工具到 InoDriverShop 的实际通信连接。

## 接入所缺资料

优先取得与当前版本匹配的“第三方通信调用”协议/示例：如何启用、TCP 客户端/服务端角色、端口及消息格式、设备选择、参数只读命令与响应、错误码。还需确认是否支持 SV660N 转矩指令、允许刷新周期、通信中断处理。拿到后先实现设备识别及状态只读验证，再实现经过确认的控制功能。

现有工具的 ADS 适配器不适用于本次用户指定的连接方式，不能把仿真连接或 InoDriverShop 在线状态当作新工具接口连接成功。