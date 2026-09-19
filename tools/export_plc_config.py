"""Export commissioning constants. Never deploys PLC or enables the commissioning lock."""
import hashlib,json,math,pathlib,sys,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1]
source=pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else root/'config/bench.json'
raw=source.read_text(encoding='utf-8-sig')
cfg=json.loads(raw)
drive=cfg['drive'];limits=cfg['limits']
fingerprint=int(hashlib.sha256(raw.encode()).hexdigest()[:8],16)
tree=ET.parse(root/'twincat/GVL_Config.TcGVL')
decl=tree.find('.//Declaration').text
import re
def replace(name,value):
 global decl
 if isinstance(value,float) and not math.isfinite(value): raise ValueError(name)
 text=str(value)
 decl,count=re.subn(r'('+re.escape(name)+r'\s*:\s*[A-Z]+\s*:=\s*)[^;]+;',lambda m:m[1]+text+';',decl)
 if count!=1: raise ValueError(name)
replace('ProfileFingerprint',fingerprint)
mapping={'maxTorqueNm':'MaxTorqueNm','continuousTorqueNm':'ContinuousTorqueNm','peakSeconds':'PeakSeconds',
'peakCooldownSeconds':'PeakCooldownSeconds','maxSpeedRpm':'MaxSpeedRpm','minLoadSpeedRpm':'MinLoadSpeedRpm',
'maxPowerW':'MaxPowerW','motorWarnC':'MotorWarnC','motorTripC':'MotorTripC','brakeTripC':'BrakeTripC',
'dcBusTripV':'DcBusTripV','maxRampNmPerSec':'MaxRampNmPerSec','stopRampNmPerSec':'StopRampNmPerSec',
'zeroTorqueNm':'ZeroTorqueNm','stopTimeoutSeconds':'StopTimeoutSeconds','overTorqueMarginNm':'OverTorqueMarginNm'}
for key,name in mapping.items(): replace(name,limits[key] or 0)
replace('HeartbeatTimeoutSeconds',limits['heartbeatTimeoutMs']/1000)
replace('OverTorqueDelaySeconds',limits['overTorqueDelayMs']/1000)
for key,name in [('rawTorquePerNm','RawTorquePerNm'),('rpmPerRawVelocity','RpmPerRawVelocity'),
 ('installationSign','InstallationSign'),('expectedRotationSign','RotationSign'),('cstMode','CstMode')]:
 replace(name,drive[key] or 0)
for prefix,points in [('Map',cfg['torqueMap']),('Envelope',limits['torqueEnvelope'])]:
 if not 2<=len(points)<=16: raise ValueError('map count must be 2..16')
 if any(not math.isfinite(p[k]) or p[k]<0 for p in points for k in ['rpm','torqueNm']): raise ValueError('invalid map')
 if any(a['rpm']>=b['rpm'] for a,b in zip(points,points[1:])): raise ValueError('map order')
 replace(prefix+'Count',len(points))
 for suffix,key in [('Rpm','rpm'),('Torque','torqueNm')]:
  decl=re.sub(r'('+prefix+suffix+r'\s*:\s*ARRAY\[1\.\.16\] OF LREAL\s*:=\s*)\[[^]]*\]',
              lambda m:m[1]+'['+','.join(str(p[key]) for p in points)+']',decl)
# Always locked: the exported file is an offline artifact for engineering review.
decl=re.sub(r'Commissioned\s*:\s*BOOL\s*:=\s*[^;]+;', 'Commissioned : BOOL := FALSE;', decl)
out=root/'artifacts/plc-export';out.mkdir(parents=True,exist_ok=True)
text='<?xml version="1.0" encoding="utf-8"?>\n<TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4024.0"><GVL Name="GVL_Config" Id="{00000000-0000-4000-8000-000000000005}"><Declaration><![CDATA['+decl+']]></Declaration></GVL></TcPlcObject>'
(out/'GVL_Config.TcGVL').write_text(text,encoding='utf-8')
print('Exported LOCKED config:',out/'GVL_Config.TcGVL','profile fingerprint',hex(fingerprint))
