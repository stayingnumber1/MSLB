"""Long-running JSON-lines EtherCAT CSV service for the WPF application.

Stdout contains JSON status/events only. Stdin accepts JSON commands. The process
owns the EtherCAT adapter for its complete lifetime and keeps OP/SYNC active.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import queue
import struct
import sys
import threading
import time

from ethercat_probe import as_text, decode_state, read_int
from ethercat_velocity import (encoder_resolution_for_drive, identity_matches, raw_velocity_for_rpm,
                               rpm_for_raw_velocity, unpack_tx)

SERVICE_RX_PDO = 0x1702
SERVICE_TX_PDO = 0x1B04
PDO_PERIOD_S = 0.004


def enable_windows_realtime_timing():
    """Reduce Windows scheduler jitter for the 4 ms PDO owner thread."""
    if sys.platform != "win32":
        return False
    # Python's default 5 ms GIL interval is longer than one EtherCAT cycle.
    sys.setswitchinterval(0.001)
    winmm = ctypes.WinDLL("winmm")
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    winmm.timeBeginPeriod.argtypes = [wintypes.UINT]
    winmm.timeBeginPeriod.restype = wintypes.UINT
    winmm.timeEndPeriod.argtypes = [wintypes.UINT]
    winmm.timeEndPeriod.restype = wintypes.UINT
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.SetPriorityClass.restype = wintypes.BOOL
    if winmm.timeBeginPeriod(1) != 0:
        raise OSError("timeBeginPeriod(1) failed")
    # ABOVE_NORMAL keeps the process responsive without using REALTIME priority.
    if not kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x00008000):
        winmm.timeEndPeriod(1)
        raise ctypes.WinError(ctypes.get_last_error())
    return True


def raise_current_thread_priority():
    if sys.platform != "win32":
        return
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentThread.restype = wintypes.HANDLE
    kernel32.SetThreadPriority.argtypes = [wintypes.HANDLE, ctypes.c_int]
    kernel32.SetThreadPriority.restype = wintypes.BOOL
    # THREAD_PRIORITY_HIGHEST; avoid TIME_CRITICAL so NIC/USB drivers still run.
    if not kernel32.SetThreadPriority(kernel32.GetCurrentThread(), 2):
        raise ctypes.WinError(ctypes.get_last_error())


def wait_until(deadline):
    """Sleep most of the cycle, then spin briefly to avoid multi-ms oversleep."""
    while True:
        remaining = deadline - time.perf_counter()
        if remaining <= 0:
            return
        if remaining > 0.0007:
            time.sleep(remaining - 0.0005)


def slave_count_error(count, adapter, slaves=()):
    """Build an actionable discovery error without hiding the observed count."""
    if count == 0:
        return (f"EtherCAT从站扫描结果为0（要求1台）；网卡={adapter}。"
                "请检查伺服EtherCAT IN端口、网线和驱动器上电状态，并关闭TwinCAT或其他占用该专用网卡的主站。")
    names = ", ".join(getattr(slave, "name", "") or f"slave-{index}"
                      for index, slave in enumerate(slaves, start=1))
    suffix = f"；检测到：{names}" if names else ""
    return f"EtherCAT从站扫描结果为{count}（要求1台）；网卡={adapter}{suffix}"


def drive_fault_message(error, auxiliary=None):
    if error == 0x0234:
        return ("驱动器故障 E234.0 / 0x0234：飞车保护（不可在线复位）。"
                "保持零指令；请切断主回路电源并等待CHARGE灯熄灭，检查编码器方向、UVW相序、"
                "电机参数及转矩方向后再上电。禁止继续使能。")
    if error == 0x0E08:
        return "驱动器故障 EE08 / 0x0E08：EtherCAT同步信号丢失"
    if error == 0x3210 and auxiliary == 0x09200920:
        return "驱动器警告 E920.0 / 0x3210：回生电阻过载（可复位三级警告）"
    if error == 0x3210 and auxiliary == 0x03200320:
        return "驱动器故障 E320.0 / 0x3210：制动电阻过载（可复位）"
    if error == 0x7305:
        encoder_faults = {
            0x07310731: ("E731.0", "编码器电池失效", True),
            0x07330733: ("E733.0", "编码器多圈计数错误", True),
            0x07350735: ("E735.0", "编码器多圈计数溢出", True),
            0x27400740: ("E740.2", "绝对值编码器错误", False),
            0x37400740: ("E740.3", "绝对值编码器单圈解算错误", False),
            0x67400740: ("E740.6", "编码器写入故障", False),
            0x0A330A33: ("EA33.0", "编码器读写校验异常", False),
        }
        if auxiliary in encoder_faults:
            code, name, resettable = encoder_faults[auxiliary]
            action = "排除原因后可由用户执行故障复位" if resettable else "不可在线复位，需检修编码器系统并重新上电"
            return f"驱动器故障 {code}：{name}（0x7305 / 辅助码0x{auxiliary:08X}）；{action}，程序禁止自动使能"
        suffix = f"，辅助码0x{auxiliary:08X}" if auxiliary is not None else "，辅助码读取失败"
        return f"驱动器编码器类故障 0x7305{suffix}；具体子故障未识别，程序禁止自动复位和使能"
    return f"驱动器存在未验证故障 0x{error:04X}；为防止误复位，程序不会自动使能"


def assign_service_pdo(slave):
    for assignment, mapping in ((0x1C12, SERVICE_RX_PDO), (0x1C13, SERVICE_TX_PDO)):
        slave.sdo_write(assignment, 0, b"\x00")
        slave.sdo_write(assignment, 1, mapping.to_bytes(2, "little"))
        slave.sdo_write(assignment, 0, b"\x01")


def is_user_reset_allowed(error, auxiliary=None):
    """Allow online reset only for commissioned, recoverable fault families."""
    if error == 0x0E08:
        return True
    if error == 0x7305:
        return auxiliary in (0x07310731, 0x07330733, 0x07350735)
    if error == 0x3210:
        return auxiliary == 0x03200320
    return False


def pack_service_rx(controlword, target_velocity, target_torque, mode, max_velocity):
    if not -3500 <= target_torque <= 3500:
        raise ValueError("target torque must be -3500..3500 (0.1% units)")
    if mode not in (9, 10):
        raise ValueError("service supports CSV(9) and CST(10) only")
    # 1702: 6040,607A,60FF,6071,6060,60B8,607F.
    return struct.pack("<HiihbHI", controlword, 0, target_velocity, target_torque,
                       mode, 0, max_velocity)


def emit(value):
    print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)


class DirectService:
    def __init__(self, pysoem, ethercat, max_rpm, max_ramp_rpm_per_s,
                 allow_external_load=False):
        self.p = pysoem
        self.cfg = ethercat
        self.max_rpm = max_rpm
        self.max_ramp_rpm_per_s = max_ramp_rpm_per_s
        self.allow_external_load = allow_external_load
        self.master = pysoem.Master()
        self.slave = None
        self.lock = threading.Lock()
        self.stop_pump = threading.Event()
        self.pump_error = None
        self.pump_thread = None
        self.minimum_wkc = 0
        self.low_wkc_count = 0
        self.last_wkc = 0
        self.wkc_grace_until = 0.0
        self.latest_pdo = None
        self.feedback = None
        self.last_feedback_read = 0.0
        self.commands = queue.Queue()
        self.controlword = 0
        self.target_rpm = 0.0
        self.output_rpm = 0.0
        self.target_torque_raw = 0.0
        self.output_torque_raw = 0.0
        self.torque_ramp_raw_per_s = 100.0
        self.mode = 9
        self.last_torque_command = time.monotonic()
        self.ramp_rpm_per_s = 100.0
        self.disable_after_stop = False
        self.enable_requested = False
        self.shutdown_requested = False
        self.heartbeat = 0
        self.encoder = 0  # Set only after checking the actual drive and motor identity.
        self.gear_numerator = 1
        self.gear_denominator = 1
        self.monitor = {"phase_current_a": None, "dc_bus_v": None, "module_temp_c": None,
                        "load_percent": None}
        self.last_monitor_read = 0.0
        self.monitor_error_reported = False
        self.pdo_cycles = 0
        self.pdo_max_gap_ms = 0.0
        self.pdo_deadline_misses = 0
        self.auxiliary_fault = None
        self.fault_detail = None
        self.fault_detail_error = None
        self.reset_deadline = None
        self.reset_original_fault = None
        self.runaway_protection_original = None
        self.windows_timer_period_enabled = False

    def open(self):
        self.windows_timer_period_enabled = enable_windows_realtime_timing()
        adapter = self.cfg["adapter"]
        available = {as_text(item.name): item for item in self.p.find_adapters()}
        if adapter not in available:
            raise ConnectionError(f"配置的EtherCAT网卡当前不存在：{adapter}；请重新选择专用有线网卡")
        self.master.open(adapter)
        self.master.sdo_read_timeout = 300_000
        self.master.sdo_write_timeout = 300_000
        slave_count = self.master.config_init()
        if slave_count != 1:
            raise ConnectionError(slave_count_error(slave_count, adapter, self.master.slaves))
        self.slave = self.master.slaves[0]
        if not identity_matches(self.slave, self.cfg.get("expectedIdentity", {})):
            raise ConnectionError("slave identity mismatch")
        self.master.state = self.p.PREOP_STATE
        self.master.write_state()
        if self.master.state_check(self.p.PREOP_STATE, 500_000) != self.p.PREOP_STATE:
            raise ConnectionError("PRE-OP failed")
        motor_code = read_int(self.slave, 0x2000, 1, 2)
        if motor_code != 14101:
            raise ValueError(f"unverified motor code {motor_code}")
        self.encoder = encoder_resolution_for_drive(self.slave.name, motor_code)
        self.gear_numerator = read_int(self.slave, 0x6091, 1, 4)
        self.gear_denominator = read_int(self.slave, 0x6091, 2, 4)
        runaway_protection = read_int(self.slave, 0x200A, 0x0D, 2)
        if runaway_protection not in (0, 1):
            raise ValueError(f"unexpected H0A-12 value {runaway_protection}")
        if self.allow_external_load:
            self.runaway_protection_original = runaway_protection
            self.slave.sdo_write(0x200A, 0x0D, struct.pack("<H", 0))
            if read_int(self.slave, 0x200A, 0x0D, 2) != 0:
                raise ConnectionError("H0A-12 temporary disable readback failed")
            emit({"type": "event", "level": "info",
                  "message": "H0A-12 temporarily disabled for externally driven CST load"})
        elif runaway_protection != 1:
            raise PermissionError("H0A-12 runaway protection is disabled; normal service refuses to run")
        # Mailbox SDO reads can block the 4 ms PDO pump on this drive. Capture
        # auxiliary monitors before OP; motion feedback itself is always TxPDO.
        self._read_monitor(time.monotonic())
        self.slave.sdo_write(0x2002, 1, struct.pack("<H", 9))
        self.slave.sdo_write(0x6060, 0, struct.pack("<b", 9))
        self.slave.config_func = lambda position: assign_service_pdo(self.master.slaves[position])
        # Keep RxPDO and TxPDO in separate logical-map regions.  The service
        # PDOs are asymmetric (19-byte Rx / 29-byte Tx); overlap mapping made
        # pysoem expose a zero-filled slave.input buffer on this NIC/drive.
        mapped_size = self.master.config_map()
        if mapped_size != 48:
            raise ValueError(f"unexpected IO map size {mapped_size}")
        if not self.master.config_dc():
            raise ConnectionError("distributed clocks unavailable")
        self.slave.dc_sync(True, 4_000_000)
        self.slave.output = pack_service_rx(0, 0, 0, self.mode, self._max_velocity_raw())
        self.master.state = self.p.SAFEOP_STATE
        self.master.write_state()
        if self.master.state_check(self.p.SAFEOP_STATE, 1_000_000) != self.p.SAFEOP_STATE:
            raise ConnectionError("SAFE-OP failed")
        # Let DC/SYNC0 and the watchdog observe stable zero output before OP.
        # A newly changed fixed PDO can require more than one watchdog window.
        for _ in range(125):
            self.master.send_processdata()
            self.master.receive_processdata(3000)
            wait_until(time.perf_counter() + PDO_PERIOD_S)
        reached_op = False
        op_wkc = []
        for attempt in range(2):
            self.master.state = self.p.OP_STATE
            self.master.write_state()
            # Keep all Master access on this thread during the transition.
            # Concurrent state_check/write_state and PDO calls on one pysoem
            # Master can lose the OP request without producing an AL error.
            for _ in range(250):
                self.master.send_processdata()
                wkc = self.master.receive_processdata(3000)
                self.last_wkc = wkc
                op_wkc.append(wkc)
                if self.master.state_check(self.p.OP_STATE, 1_000) == self.p.OP_STATE:
                    reached_op = True
                    break
                wait_until(time.perf_counter() + PDO_PERIOD_S)
            if reached_op:
                break
            # Keep zero command and re-establish SAFE-OP before one automatic retry.
            self.master.read_state()
            if self.slave.state & self.p.STATE_ERROR:
                self.slave.state = self.p.SAFEOP_STATE + self.p.STATE_ACK
                self.slave.write_state()
                self.slave.state_check(self.p.SAFEOP_STATE, 500_000)
            self.master.state = self.p.SAFEOP_STATE
            self.master.write_state()
            self.master.state_check(self.p.SAFEOP_STATE, 500_000)
            time.sleep(0.25)
        if not reached_op:
            self.master.read_state()
            raise ConnectionError(f"OP failed: slave_state=0x{self.slave.state:02X}, "
                                  f"AL=0x{self.slave.al_status:04X}, WKC={self.last_wkc}, "
                                  f"expected_WKC={self.master.expected_wkc}, "
                                  f"RxPDO={len(self.slave.output)}, TxPDO={len(self.slave.input)}")
        self.minimum_wkc = 2
        self.pump_thread = threading.Thread(target=self._pump, name="ethercat-pdo", daemon=True)
        self.pump_thread.start()
        first_pdo_deadline = time.monotonic() + 1.0
        while self.latest_pdo is None and self.pump_error is None:
            if time.monotonic() >= first_pdo_deadline:
                raise RuntimeError("timed out waiting for first valid TxPDO")
            time.sleep(0.001)
        # Only explicitly commissioned faults have an online recovery procedure.
        # Never issue a blanket reset for motion/encoder faults such as E234.
        self._refresh_feedback(force=True)
        initial_error = self._read()[1]
        if initial_error == 0:
            return
        if initial_error != 0x0E08:
            try:
                with self.lock:
                    self.auxiliary_fault = read_int(self.slave, 0x203F, 0, 4)
                    self.wkc_grace_until = time.monotonic() + 0.08
            except Exception:
                self.auxiliary_fault = None
            startup_e320_reset = (self.allow_external_load and initial_error == 0x3210 and
                                  self.auxiliary_fault == 0x03200320)
            if not startup_e320_reset:
                self.fault_detail = drive_fault_message(initial_error, self.auxiliary_fault)
                self.fault_detail_error = initial_error
                emit({"type": "event", "level": "warning", "message": self.fault_detail})
                return
            emit({"type": "event", "level": "warning",
                  "message": "Resetting commissioned E320.0 at zero torque before enable"})

        # Recover commissioned EE08/E320 while target remains zero; bit 3 is never set.
        self._set_output(0x0080, 0)
        time.sleep(0.15)
        self._set_output(0, 0)
        with self.lock:
            self.slave.sdo_write(0x200D, 2, struct.pack("<H", 1))
        time.sleep(0.3)
        self._refresh_feedback(force=True)
        error = self._read()[1]
        if error:
            raise RuntimeError("同步故障在线恢复失败；" + drive_fault_message(error))

    def _pump(self):
        raise_current_thread_priority()
        next_tick = time.perf_counter()
        previous_tick = None
        try:
            while not self.stop_pump.is_set():
                cycle_tick = time.perf_counter()
                if previous_tick is not None:
                    gap_ms = (cycle_tick - previous_tick) * 1000
                    self.pdo_max_gap_ms = max(self.pdo_max_gap_ms, gap_ms)
                    if gap_ms > 8:
                        self.pdo_deadline_misses += 1
                previous_tick = cycle_tick
                self.pdo_cycles += 1
                with self.lock:
                    self.master.send_processdata(release_gil=True)
                    wkc = self.master.receive_processdata(3000, release_gil=True)
                    self.last_wkc = wkc
                    if wkc >= self.minimum_wkc and len(self.slave.input) == 29:
                        self.latest_pdo = bytes(self.slave.input)
                if time.monotonic() < self.wkc_grace_until:
                    self.low_wkc_count = 0
                elif self.minimum_wkc and wkc < self.minimum_wkc:
                    self.low_wkc_count += 1
                    if self.low_wkc_count >= 5:
                        raise ConnectionError(f"PDO WKC remained below {self.minimum_wkc}: {wkc}")
                else:
                    self.low_wkc_count = 0
                next_tick += PDO_PERIOD_S
                wait_until(next_tick)
        except Exception as exc:
            self.pump_error = exc

    def _set_output(self, controlword, rpm):
        raw = raw_velocity_for_rpm(rpm, self.encoder, self.gear_numerator, self.gear_denominator)
        self.controlword = controlword
        data = pack_service_rx(controlword, raw if self.mode == 9 else 0,
                               round(self.output_torque_raw) if self.mode == 10 else 0,
                               self.mode, self._max_velocity_raw())
        if self.pump_thread is not None and self.pump_thread.is_alive():
            with self.lock:
                self.slave.output = data
        else:
            self.slave.output = data

    def _max_velocity_raw(self):
        return abs(raw_velocity_for_rpm(self.max_rpm, self.encoder,
                                        self.gear_numerator, self.gear_denominator))

    def _read(self):
        if self.pump_error:
            raise RuntimeError(f"PDO pump failed: {self.pump_error}")
        with self.lock:
            data = self.latest_pdo
        if data is None:
            raise RuntimeError("waiting for first valid TxPDO")
        status = unpack_tx(data)
        # SV635_ECAT V1.0 acknowledges the fixed 1B04 mapping and contributes
        # WKC, but on the commissioned adapter its TxPDO bytes remain zero.
        # Keep deterministic PDO output/OP-SYNC alive and use a deliberately
        # rate-limited CoE snapshot for the state machine and displayed values.
        if status["statusword"] == 0 and status["mode_display"] == 0:
            self._refresh_feedback()
            if self.feedback is None:
                raise RuntimeError("drive feedback unavailable")
            status = self.feedback
        word = status["statusword"]
        error = status["error_code"]
        raw_velocity = status["velocity_raw"]
        raw_torque = status["torque_raw"]
        mode = status["mode_display"]
        rpm = rpm_for_raw_velocity(raw_velocity, self.encoder, self.gear_numerator, self.gear_denominator)
        return word, error, rpm, raw_torque, mode

    def _refresh_feedback(self, force=False):
        now = time.monotonic()
        if not force and self.feedback is not None and now - self.last_feedback_read < 0.2:
            return
        with self.lock:
            self.feedback = {
                "error_code": read_int(self.slave, 0x603F, 0, 2),
                "statusword": read_int(self.slave, 0x6041, 0, 2),
                "velocity_raw": read_int(self.slave, 0x606C, 0, 4, signed=True),
                "torque_raw": read_int(self.slave, 0x6077, 0, 2, signed=True),
                "mode_display": read_int(self.slave, 0x6061, 0, 1, signed=True),
            }
            # Mailbox traffic temporarily occupies the EtherCAT datagram path
            # on this firmware. Ignore the immediately following PDO samples;
            # a sustained loss after this bounded grace period is still fatal.
            self.wkc_grace_until = time.monotonic() + 0.08
        self.last_feedback_read = now

    def _read_monitor(self, now):
        """Refresh auxiliary vendor monitors at 1 Hz without disturbing the 4 ms PDO loop."""
        if now - self.last_monitor_read < 1.0:
            return
        self.last_monitor_read = now
        try:
            with self.lock:
                phase_current_raw = read_int(self.slave, 0x200B, 0x19, 2)
                dc_bus_raw = read_int(self.slave, 0x200B, 0x1B, 2)
                module_temp_raw = read_int(self.slave, 0x200B, 0x1C, 2, signed=True)
                load_percent_raw = read_int(self.slave, 0x200B, 0x0D, 2)
                self.wkc_grace_until = time.monotonic() + 0.08
            self.monitor = {
                "phase_current_a": phase_current_raw * 0.01,
                "dc_bus_v": dc_bus_raw * 0.1,
                "module_temp_c": float(module_temp_raw),
                "load_percent": load_percent_raw * 0.1,
            }
            self.monitor_error_reported = False
        except Exception as exc:
            if not self.monitor_error_reported:
                emit({"type": "event", "level": "warning", "message": f"drive monitor read failed: {exc}"})
                self.monitor_error_reported = True

    def _reader(self):
        for line in sys.stdin:
            try:
                self.commands.put(json.loads(line))
            except Exception as exc:
                emit({"type": "event", "level": "error", "message": f"invalid command: {exc}"})
        self.commands.put({"type": "shutdown"})

    def _apply_command(self, command, state):
        kind = command.get("type")
        if kind == "enable":
            if abs(self.output_rpm) > 1:
                raise ValueError("enable requires zero target")
            if state not in ("switch_on_disabled", "ready_to_switch_on", "switched_on", "operation_enabled"):
                raise RuntimeError(f"cannot enable from {state}")
            self.enable_requested = True
            self.mode = 9
            self.disable_after_stop = False
        elif kind == "enable_torque":
            if state == "operation_enabled" and self.mode != 10:
                raise RuntimeError("disable servo before switching from CSV to CST")
            if state not in ("switch_on_disabled", "ready_to_switch_on", "switched_on", "operation_enabled"):
                raise RuntimeError(f"cannot enable CST from {state}")
            self.target_rpm = self.output_rpm = 0.0
            self.target_torque_raw = self.output_torque_raw = 0.0
            self.mode = 10
            self.enable_requested = True
            self.disable_after_stop = False
            self.last_torque_command = time.monotonic()
        elif kind == "velocity":
            rpm = float(command.get("rpm", 0))
            ramp = float(command.get("ramp_rpm_per_s", 100))
            if state != "operation_enabled":
                raise RuntimeError("Servo must be enabled before velocity command")
            if not (-self.max_rpm <= rpm <= self.max_rpm) or not (10 <= ramp <= self.max_ramp_rpm_per_s):
                raise ValueError("velocity/ramp exceeds configured commissioning limit")
            self.target_rpm = rpm
            self.target_torque_raw = 0.0
            self.ramp_rpm_per_s = ramp
            self.disable_after_stop = False
        elif kind == "torque":
            if self.mode != 10:
                if state == "operation_enabled":
                    raise RuntimeError("disable servo before switching to CST")
                self.mode = 10
            target = int(command.get("target_raw", 0))
            ramp = float(command.get("ramp_raw_per_s", 1))
            if not -3500 <= target <= 3500 or not 1 <= ramp <= 3500:
                raise ValueError("CST torque/ramp exceeds protocol limits")
            self.target_torque_raw = target
            self.torque_ramp_raw_per_s = ramp
            self.last_torque_command = time.monotonic()
            if command.get("disable", False):
                self.target_torque_raw = 0
                self.disable_after_stop = True
                self.enable_requested = False
        elif kind in ("stop", "disable"):
            self.target_rpm = 0
            self.target_torque_raw = 0
            self.ramp_rpm_per_s = min(500, max(50, float(command.get("ramp_rpm_per_s", 100))))
            self.disable_after_stop = kind == "disable"
            if kind == "disable":
                self.enable_requested = False
        elif kind == "reset":
            if state in ("fault", "fault_reaction_active"):
                error = self._read()[1]
                if not is_user_reset_allowed(error, self.auxiliary_fault):
                    raise RuntimeError(drive_fault_message(error, self.auxiliary_fault))
                # CiA-402 fault reset is a rising pulse. Reset never restores
                # the previous speed/torque request or enables the power stage.
                self.target_rpm = self.output_rpm = 0
                self.target_torque_raw = self.output_torque_raw = 0
                self.enable_requested = False
                self.disable_after_stop = True
                self.reset_original_fault = self.fault_detail or drive_fault_message(error, self.auxiliary_fault)
                self.reset_deadline = time.monotonic() + 2.0
                self._set_output(0x0080, 0)
        elif kind == "shutdown":
            self.target_rpm = 0
            self.target_torque_raw = 0
            self.disable_after_stop = True
            self.enable_requested = False
            self.shutdown_requested = True
        else:
            raise ValueError(f"unknown command {kind}")

    def run(self):
        threading.Thread(target=self._reader, name="command-reader", daemon=True).start()
        emit({"type": "event", "level": "info", "message": "EtherCAT direct service OP/SYNC ready"})
        last = time.perf_counter()
        while True:
            now = time.perf_counter()
            dt = min(0.2, now - last)
            last = now
            word, error, rpm, raw_torque, mode = self._read()
            if error == 0:
                self.auxiliary_fault = None
                self.fault_detail = None
                self.fault_detail_error = None
            elif self.fault_detail_error != error:
                # A fault can arise after startup. Capture 0x203F once for each
                # new error instead of loading the bus with an SDO every cycle.
                try:
                    with self.lock:
                        self.auxiliary_fault = read_int(self.slave, 0x203F, 0, 4)
                        self.wkc_grace_until = time.monotonic() + 0.08
                except Exception:
                    self.auxiliary_fault = None
                self.fault_detail = drive_fault_message(error, self.auxiliary_fault)
                self.fault_detail_error = error
                emit({"type": "event", "level": "warning", "message": self.fault_detail})
            # Do not issue mailbox/SDO requests while the 4 ms PDO loop is in OP.
            # On SV630N, a monitor read can hold the shared master long enough for
            # the drive to report EE08. Keep the PRE-OP snapshot during this run.
            state = decode_state(word)
            if self.reset_deadline is not None:
                if error == 0 and state not in ("fault", "fault_reaction_active"):
                    emit({"type": "event", "level": "info",
                          "message": "故障复位成功；驱动保持禁能，需重新执行使能确认"})
                    self.reset_deadline = None
                    self.reset_original_fault = None
                elif now >= self.reset_deadline:
                    emit({"type": "event", "level": "error",
                          "message": "故障复位失败：驱动器仍处于Fault；" +
                                     (self.fault_detail or self.reset_original_fault or
                                      drive_fault_message(error, self.auxiliary_fault))})
                    self.reset_deadline = None
                    self.reset_original_fault = None
            while True:
                try:
                    command = self.commands.get_nowait()
                except queue.Empty:
                    break
                try:
                    self._apply_command(command, state)
                except Exception as exc:
                    emit({"type": "event", "level": "error", "message": str(exc)})

            if self.mode == 10 and now - self.last_torque_command > 0.25:
                self.target_torque_raw = 0
                self.disable_after_stop = True
                self.enable_requested = False

            delta = self.target_rpm - self.output_rpm
            step = self.ramp_rpm_per_s * dt
            self.output_rpm += max(-step, min(step, delta))
            torque_delta = self.target_torque_raw - self.output_torque_raw
            torque_step = self.torque_ramp_raw_per_s * dt
            self.output_torque_raw += max(-torque_step, min(torque_step, torque_delta))
            if self.enable_requested and state == "switch_on_disabled":
                self._set_output(0x0006, 0)
            elif self.enable_requested and state == "ready_to_switch_on":
                self._set_output(0x0007, 0)
            elif self.enable_requested and state == "switched_on":
                self._set_output(0x000F, 0)
            elif state == "operation_enabled":
                self._set_output(0x000F, self.output_rpm)
            elif self.controlword == 0x0080:
                self._set_output(0, 0)

            if self.disable_after_stop and abs(self.output_rpm) < 0.5 and abs(self.output_torque_raw) < 1:
                if state == "operation_enabled":
                    self._set_output(0x0007, 0)
                elif state == "switched_on":
                    self._set_output(0, 0)
                elif state == "ready_to_switch_on":
                    self._set_output(0, 0)
                elif state == "switch_on_disabled" and self.shutdown_requested:
                    break
                elif state in ("fault", "fault_reaction_active", "not_ready") and self.shutdown_requested:
                    break

            self.heartbeat += 1
            emit({"type": "status", "connected": True, "ethercat_online": True,
                  "state": state, "statusword": word, "error_code": error, "mode": mode,
                  "servo_on": state == "operation_enabled", "drive_ready": state not in ("fault", "fault_reaction_active", "not_ready"),
                  "speed_rpm": rpm, "target_rpm": self.output_rpm,
                  "torque_raw": raw_torque, "torque_percent": raw_torque / 10.0,
                  "target_torque_raw": round(self.output_torque_raw),
                  "auxiliary_fault": self.auxiliary_fault,
                  "fault_detail": self.fault_detail,
                  "phase_current_a": self.monitor["phase_current_a"],
                  "dc_bus_v": self.monitor["dc_bus_v"],
                  "module_temp_c": self.monitor["module_temp_c"],
                  "load_percent": self.monitor["load_percent"],
                  "monitor_age_s": max(0.0, now - self.last_monitor_read),
                  "pdo_cycles": self.pdo_cycles,
                  "pdo_max_gap_ms": self.pdo_max_gap_ms,
                  "pdo_deadline_misses": self.pdo_deadline_misses,
                  "heartbeat": self.heartbeat})
            time.sleep(0.05)

    def close(self):
        if self.slave is None:
            self.master.close()
            if self.windows_timer_period_enabled:
                ctypes.WinDLL("winmm").timeEndPeriod(1)
                self.windows_timer_period_enabled = False
            return
        try:
            self.target_rpm = self.output_rpm = 0
            self.target_torque_raw = self.output_torque_raw = 0
            self._set_output(0, 0)
            time.sleep(0.1)
            self.master.state = self.p.SAFEOP_STATE
            self.master.write_state()
            self.master.state_check(self.p.SAFEOP_STATE, 500_000)
        except Exception:
            pass
        self.stop_pump.set()
        if self.pump_thread:
            self.pump_thread.join(timeout=1)
        restore_error = None
        try:
            self.slave.dc_sync(False, 4_000_000)
            self.master.state = self.p.PREOP_STATE
            self.master.write_state()
            self.master.state_check(self.p.PREOP_STATE, 300_000)
            self.slave.sdo_write(0x6060, 0, struct.pack("<b", 0))
            self.slave.sdo_write(0x2002, 1, struct.pack("<H", 2))
        except Exception:
            pass
        if self.runaway_protection_original is not None:
            try:
                self.slave.sdo_write(0x200A, 0x0D,
                                     struct.pack("<H", self.runaway_protection_original))
                restored = read_int(self.slave, 0x200A, 0x0D, 2)
                if restored != self.runaway_protection_original:
                    raise ConnectionError(f"H0A-12 restore readback was {restored}")
                emit({"type": "event", "level": "info",
                      "message": f"H0A-12 restored and read back as {restored}"})
            except Exception as exc:
                restore_error = exc
        self.master.close()
        if self.windows_timer_period_enabled:
            ctypes.WinDLL("winmm").timeEndPeriod(1)
            self.windows_timer_period_enabled = False
        if restore_error is not None:
            raise RuntimeError(f"H0A-12 restoration failed: {restore_error}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("config/ethercat.json"))
    parser.add_argument("--max-rpm", type=float, default=500)
    parser.add_argument("--max-ramp-rpm-per-s", type=float, default=500)
    parser.add_argument("--external-load", action="store_true",
                        help="temporarily disable H0A-12 for externally driven CST loading")
    args = parser.parse_args()
    service = None
    try:
        import pysoem
        service = DirectService(pysoem, json.loads(args.config.read_text(encoding="utf-8-sig")), args.max_rpm,
                                args.max_ramp_rpm_per_s, args.external_load)
        service.open()
        service.run()
        return 0
    except Exception as exc:
        emit({"type": "fatal", "message": f"{type(exc).__name__}: {exc}"})
        return 2
    finally:
        if service:
            service.close()


if __name__ == "__main__":
    sys.exit(main())
