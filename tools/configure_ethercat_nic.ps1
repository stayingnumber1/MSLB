$ErrorActionPreference = 'Stop'
$adapter = Get-NetAdapter | Where-Object InterfaceGuid -eq '{ABE07EA1-A4FA-44F7-832A-B153DE3BE443}'
if (-not $adapter) { throw 'EtherCAT adapter not found.' }

$settings = @{
    '*EEE'                       = 0
    'EnableGreenEthernet'        = 0
    'PowerSavingMode'            = 0
    '*InterruptModeration'       = 0
    '*FlowControl'               = 0
    '*IPChecksumOffloadIPv4'     = 0
    '*LsoV2IPv4'                 = 0
    '*LsoV2IPv6'                 = 0
    '*TCPChecksumOffloadIPv4'    = 0
    '*TCPChecksumOffloadIPv6'    = 0
    '*UDPChecksumOffloadIPv4'    = 0
    '*UDPChecksumOffloadIPv6'    = 0
}

foreach ($setting in $settings.GetEnumerator()) {
    Set-NetAdapterAdvancedProperty -Name $adapter.Name `
        -RegistryKeyword $setting.Key -RegistryValue $setting.Value `
        -NoRestart -ErrorAction Stop
}

Restart-NetAdapter -Name $adapter.Name -Confirm:$false
