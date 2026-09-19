"""Commissioning-only SV660N/SV635 EtherCAT CSV velocity runner.

The default ``check`` action is read-only.  ``run`` is deliberately locked behind
both persistent commissioning flags and two per-run physical-safety affirmations.
It first proves direction at 50 rpm, then ramps to the requested speed, and always
returns the target to zero before disabling the drive.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import math
from pathlib import Path
import struct
import sys
import threading
import time

from ethercat_probe import as_text, decode_state, read_int

ENCODER_RESOLUTION_BY_MOTOR_CODE = {14000: 1 << 20, 14101: 1 << 23}
CSV_MODE = 9
RX_PDO = 0x1703
TX_PDO = 0x1B04
RX_SIZE = 17
TX_SIZE = 29


def stable_read_int(slave, index, subindex, size, signed=False):
    """Require two identical CoE reads; this drive can briefly return stale mailbox data."""
    previous = None
    for _ in range(5):
        value = read_int(slave, index, subindex, size, signed)
        if value == previous:
            return value
        previous = value
        time.sleep(0.02)
    raise IOError(f"0x{index:04X}:{subindex:02X} did not produce two stable reads")


def raw_velocity_for_rpm(rpm: float, encoder_resolution: int, gear_numerator: int,
                         gear_denominator: int) -> int:
    if not math.isfinite(rpm) or encoder_resolution <= 0 or gear_numerator <= 0 or gear_denominator <= 0:
        raise ValueError("invalid velocity scaling")
    # Manual: motor rpm = reference-unit/s * gear ratio / encoder resolution * 60.
    raw = rpm * encoder_resolution * gear_denominator / (60 * gear_numerator)
    if not -(1 << 31) <= raw < (1 << 31):
        raise OverflowError("target velocity exceeds Int32")
    return round(raw)


def rpm_for_raw_velocity(raw: int, encoder_resolution: int, gear_numerator: int,
                         gear_denominator: int) -> float:
    return raw * gear_numerator / gear_denominator / encoder_resolution * 60


def pack_rx(controlword: int, target_velocity: int, torque_limit_tenths_percent: int) -> bytes:
    if not 0 <= torque_limit_tenths_percent <= 3000:
        raise ValueError("torque limit must be 0..3000 (0.1% units)")
    # 1703: 6040, 607A, 60FF, 6060, 60B8, 60E0, 60E1.
    return struct.pack("<HiibHHH", controlword, 0, target_velocity, CSV_MODE, 0,
                       torque_limit_tenths_percent, torque_limit_tenths_percent)


def unpack_tx(data: bytes) -> dict:
    if len(data) != TX_SIZE:
        raise ValueError(f"1B04 expected {TX_SIZE} bytes, got {len(data)}")
    # 1B04: 603F,6041,6064,6077,6061,60F4,60B9,60BA,60BC,606C.
    error, status, position, torque, mode, following, probe, edge1, edge2, velocity = \
        struct.unpack("<HHihbiHiii", data)
    return {"error_code": error, "statusword": status, "cia402_state": decode_state(status),
            "position_raw": position, "torque_raw": torque, "mode_display": mode,
            "following_error": following, "touch_probe": probe,
            "touch_edge_1": edge1, "touch_edge_2": edge2, "velocity_raw": velocity}


def require_run_gates(bench: dict, args) -> None:
    drive = bench.get("drive", {})
    required = ["modelVerified", "pdoVerified", "regenerationVerified",
                "hardwareSafetyVerified", "allowHardwareWrites"]
    if args.action == "run":
        required.append("directionVerified")
    missing = [name for name in required if drive.get(name) is not True]
    if missing:
        raise PermissionError("hardware write lock: set only after commissioning: " + ", ".join(missing))
    if not args.i_confirm_shaft_clear or not args.i_confirm_estop_tested:
        raise PermissionError("run requires --i-confirm-shaft-clear and --i-confirm-estop-tested")
    limit = float(bench.get("limits", {}).get("maxSpeedRpm", 0))
    if abs(args.rpm) > limit or limit <= 0:
        raise ValueError(f"requested {args.rpm:g} rpm exceeds configured {limit:g} rpm limit")
    if args.action == "prove-direction" and abs(args.rpm) > 50:
        raise ValueError("direction proof is limited to 50 rpm")
    if args.rpm == 0:
        raise ValueError("run target must be non-zero")
    if not 2 <= args.ramp_seconds <= 30 or not 1 <= args.hold_seconds <= 30:
        raise ValueError("ramp must be 2..30 s and hold must be 1..30 s")


def identity_matches(slave, expected: dict) -> bool:
    return all((as_text(slave.name) == as_text(expected.get("name", "")),
                slave.man == expected.get("vendorId"), slave.id == expected.get("productCode"),
                slave.rev == expected.get("revision")))


def assign_fixed_pdo(slave) -> None:
    # SV660N manual: assignment may only be changed in PRE-OP and is not saved to EEPROM.
    for assignment, mapping in ((0x1C12, RX_PDO), (0x1C13, TX_PDO)):
        slave.sdo_write(assignment, 0, b"\x00")
        slave.sdo_write(assignment, 1, mapping.to_bytes(2, "little"))
        slave.sdo_write(assignment, 0, b"\x01")


class CsvSession:
    def __init__(self, pysoem, ethercat: dict, cycle_ms: int, torque_limit: int):
        self.p = pysoem
        self.cfg = ethercat
        self.cycle_s = cycle_ms / 1000
        self.cycle_ns = cycle_ms * 1_000_000
        self.torque_limit = torque_limit
        self.master = pysoem.Master()
        self.slave = None
        self.expected_wkc = 0
        self.last = None
        self.mapped = False
        self.operational = False
        self.minimum_wkc = None
        self.overlap = True

    def open(self):
        if self.cfg.get("dedicatedAdapterConfirmed") is not True:
            raise PermissionError("dedicated EtherCAT adapter is not confirmed")
        self.master.open(self.cfg["adapter"])
        self.master.sdo_read_timeout = 300000
        self.master.sdo_write_timeout = 300000
        if self.master.config_init() != 1:
            raise ConnectionError("expected exactly one EtherCAT slave")
        self.slave = self.master.slaves[0]
        if not identity_matches(self.slave, self.cfg.get("expectedIdentity", {})):
            raise ConnectionError("slave identity tuple does not match commissioned device")
        self.master.state = self.p.PREOP_STATE
        self.master.write_state()
        if self.master.state_check(self.p.PREOP_STATE, 500_000) != self.p.PREOP_STATE:
            raise ConnectionError("slave did not reach PRE-OP")
        # The device briefly returns stale/initial object data immediately after PRE-OP.
        # Prove mailbox readiness with statusword before reading commissioning values.
        stable_read_int(self.slave, 0x6041, 0, 2)
        time.sleep(0.1)

    def scaling(self):
        # Read 6502 before 6091. Firmware V1.0 of the detected SV635_ECAT identity
        # aliases/returns 6091:00 (=2) from 6502 after a 6091 access.
        supported = stable_read_int(self.slave, 0x6502, 0, 4)
        if (supported & (1 << (CSV_MODE - 1))) == 0:
            raise ValueError(f"drive does not report CSV mode support (0x6502=0x{supported:08X})")
        motor_code = stable_read_int(self.slave, 0x2000, 1, 2)
        encoder = ENCODER_RESOLUTION_BY_MOTOR_CODE.get(motor_code)
        if encoder is None:
            raise ValueError(f"unsupported/unverified motor code {motor_code}")
        numerator = stable_read_int(self.slave, 0x6091, 1, 4)
        denominator = stable_read_int(self.slave, 0x6091, 2, 4)
        return motor_code, encoder, numerator, denominator

    def configure_op(self):
        # H02-00 selects the command source. Value 9 is the documented EtherCAT mode.
        self.slave.sdo_write(0x2002, 1, (9).to_bytes(2, "little"))
        if stable_read_int(self.slave, 0x2002, 1, 2) != 9:
            raise ConnectionError("H02-00/0x2002:01 did not retain EtherCAT mode 9")
        self.slave.sdo_write(0x6060, 0, bytes([CSV_MODE]))
        if stable_read_int(self.slave, 0x6060, 0, 1, signed=True) != CSV_MODE:
            raise ConnectionError("6060 SDO did not retain CSV mode 9")
        self.slave.config_func = lambda position: assign_fixed_pdo(self.master.slaves[position])
        size = self.master.config_overlap_map()
        if size not in (max(RX_SIZE, TX_SIZE), RX_SIZE + TX_SIZE):
            raise ValueError(f"unexpected overlap IO map size {size}")
        assigned_rx = stable_read_int(self.slave, 0x1C12, 1, 2)
        assigned_tx = stable_read_int(self.slave, 0x1C13, 1, 2)
        if (assigned_rx, assigned_tx) != (RX_PDO, TX_PDO):
            raise ConnectionError(f"PDO assignment is 0x{assigned_rx:04X}/0x{assigned_tx:04X}, "
                                  f"expected 0x{RX_PDO:04X}/0x{TX_PDO:04X}")
        if not self.master.config_dc():
            raise ConnectionError("drive did not expose distributed clocks")
        self.slave.dc_sync(True, self.cycle_ns)
        self.slave.output = pack_rx(0, 0, self.torque_limit)
        self.mapped = True
        self.master.state = self.p.SAFEOP_STATE
        self.master.write_state()
        if self.master.state_check(self.p.SAFEOP_STATE, 1_000_000) != self.p.SAFEOP_STATE:
            raise ConnectionError("slave did not reach SAFE-OP")
        # Allow SYNC0/DC and the process-data watchdog to observe stable zero output
        # before requesting OP. Tight back-to-back frames are not a valid DC cycle.
        for _ in range(max(25, round(0.2 / self.cycle_s))):
            self.exchange()
            time.sleep(self.cycle_s)
        self.master.state = self.p.OP_STATE
        pump_stop = threading.Event()
        pump_error = []

        def pump():
            next_tick = time.perf_counter()
            try:
                while not pump_stop.is_set():
                    self.master.send_overlap_processdata(release_gil=True)
                    self.master.receive_processdata(max(2000, int(self.cycle_s * 500_000)), release_gil=True)
                    next_tick += self.cycle_s
                    time.sleep(max(0, next_tick - time.perf_counter()))
            except Exception as exc:
                pump_error.append(exc)

        pump_thread = threading.Thread(target=pump, name="ethercat-pdo", daemon=True)
        pump_thread.start()
        try:
            time.sleep(self.cycle_s * 2)
            self.master.write_state()
            reached_op = False
            for _ in range(40):
                if self.master.state_check(self.p.OP_STATE, 50_000) == self.p.OP_STATE:
                    reached_op = True
                    break
                if pump_error:
                    raise pump_error[0]
            if reached_op:
                pump_stop.set()
                pump_thread.join(timeout=1)
                observed = []
                for _ in range(max(10, round(0.1 / self.cycle_s))):
                    status = self.exchange(0, 0, allow_fault=True)
                    observed.append(status["wkc"])
                    time.sleep(self.cycle_s)
                if min(observed) < 2 or len(set(observed)) != 1:
                    raise ConnectionError(f"unstable OP WKC samples: {observed}")
                # With this NIC/slave combination SOEM uses split LWR/LRD and returns 2,
                # while Master.expected_wkc reports the LRW-style value 3. Mode echo above
                # proves that both RPDO and TPDO are live before adopting the observed value.
                self.minimum_wkc = observed[0]
                self.operational = True
                return
        finally:
            pump_stop.set()
            pump_thread.join(timeout=1)
        self.master.read_state()
        details = [{"state": s.state, "al_status": f"0x{s.al_status:04X}"}
                   for s in self.master.slaves]
        raise ConnectionError(f"slave did not reach OP: {details}")

    def exchange(self, controlword=0, target=0, allow_fault=False):
        self.slave.output = pack_rx(controlword, target, self.torque_limit)
        if self.overlap:
            self.master.send_overlap_processdata()
        else:
            self.master.send_processdata()
        wkc = self.master.receive_processdata(max(2000, int(self.cycle_s * 500_000)))
        # SAFE-OP only returns TPDO; full expected WKC applies after OP is confirmed.
        if self.operational and self.minimum_wkc and wkc < self.minimum_wkc:
            raise ConnectionError(f"process-data WKC {wkc} below commissioned {self.minimum_wkc}")
        if self.master.expected_wkc:
            self.expected_wkc = self.master.expected_wkc
        self.last = unpack_tx(bytes(self.slave.input))
        self.last["wkc"] = wkc
        if not allow_fault and (self.last["error_code"] or self.last["cia402_state"] in ("fault", "fault_reaction_active")):
            raise RuntimeError(f"drive fault 0x{self.last['error_code']:04X}, status 0x{self.last['statusword']:04X}")
        return self.last

    def clear_fault(self):
        status = self.exchange(0, 0, allow_fault=True)
        if status["cia402_state"] not in ("fault", "fault_reaction_active") and not status["error_code"]:
            return
        deadline = time.perf_counter() + 2
        while time.perf_counter() < deadline:
            status = self.exchange(0x0080, 0, allow_fault=True)
            time.sleep(self.cycle_s)
            status = self.exchange(0, 0, allow_fault=True)
            if status["cia402_state"] not in ("fault", "fault_reaction_active") and not status["error_code"]:
                return
        raise RuntimeError(f"fault reset failed; last={status}")

    def wait_mode(self, timeout=2.0):
        deadline = time.perf_counter() + timeout
        while time.perf_counter() < deadline:
            status = self.exchange(0, 0)
            if status["mode_display"] == CSV_MODE:
                return status
            time.sleep(self.cycle_s)
        raise TimeoutError(f"CSV mode echo did not reach 9; last={self.last}")

    def wait_state(self, controlword, expected, timeout=2.0):
        deadline = time.perf_counter() + timeout
        while time.perf_counter() < deadline:
            status = self.exchange(controlword, 0)
            if status["cia402_state"] == expected and status["mode_display"] == CSV_MODE:
                return status
            time.sleep(self.cycle_s)
        raise TimeoutError(f"CiA402 did not reach {expected}; last={self.last}")

    def ramp(self, start, target, seconds, check=None):
        count = max(1, round(seconds / self.cycle_s))
        next_tick = time.perf_counter()
        for i in range(1, count + 1):
            command = round(start + (target - start) * i / count)
            status = self.exchange(0x000F, command)
            if check:
                check(status, i, count)
            next_tick += self.cycle_s
            time.sleep(max(0, next_tick - time.perf_counter()))

    def shutdown(self):
        if self.mapped and self.slave is not None:
            # Best effort must continue even when feedback/WKC is already unhealthy.
            for command in (0x0002, 0x0006, 0x0000):
                for _ in range(5):
                    try:
                        self.slave.output = pack_rx(command, 0, self.torque_limit)
                        if self.overlap:
                            self.master.send_overlap_processdata()
                        else:
                            self.master.send_processdata()
                        self.master.receive_processdata(2000)
                    except Exception:
                        pass
            try:
                self.master.state = self.p.SAFEOP_STATE
                self.master.write_state()
                self.master.state_check(self.p.SAFEOP_STATE, 500_000)
            except Exception:
                pass
            try:
                self.slave.dc_sync(False, self.cycle_ns)
            except Exception:
                pass
            try:
                self.master.state = self.p.INIT_STATE
                self.master.write_state()
            except Exception:
                pass
        self.master.close()


def run(args):
    import pysoem
    ethercat = json.loads(args.config.read_text(encoding="utf-8-sig"))
    bench = json.loads(args.bench.read_text(encoding="utf-8-sig"))
    if args.action in ("prove-direction", "run"):
        require_run_gates(bench, args)
    session = CsvSession(pysoem, ethercat, args.cycle_ms, args.torque_limit)
    report = {"timestamp_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "action": args.action,
              "drive_writes": False, "target_rpm": args.rpm, "ok": False}
    try:
        session.open()
        motor_code, encoder, numerator, denominator = session.scaling()
        target = raw_velocity_for_rpm(args.rpm, encoder, numerator, denominator)
        report.update({"slave": as_text(session.slave.name), "motor_code": motor_code,
                       "encoder_resolution": encoder,
                       "gear_ratio": {"numerator": numerator, "denominator": denominator},
                       "target_velocity_raw": target})
        if args.action == "check":
            report["ok"] = True
            report["note"] = "Read-only validation passed; drive remains PRE-OP and disabled."
            return report
        report["drive_writes"] = True
        session.configure_op()
        session.clear_fault()
        session.wait_mode()
        session.wait_state(0x0006, "ready_to_switch_on")
        session.wait_state(0x0007, "switched_on")
        session.wait_state(0x000F, "operation_enabled")
        proof_rpm = math.copysign(min(50, abs(args.rpm)), args.rpm)
        proof_raw = raw_velocity_for_rpm(proof_rpm, encoder, numerator, denominator)
        session.ramp(0, proof_raw, max(2, args.ramp_seconds * abs(proof_rpm / args.rpm)))
        proof_samples = []
        for _ in range(max(1, round(1 / session.cycle_s))):
            status = session.exchange(0x000F, proof_raw)
            proof_samples.append(rpm_for_raw_velocity(status["velocity_raw"], encoder, numerator, denominator))
            time.sleep(session.cycle_s)
        proof_actual = sum(proof_samples) / len(proof_samples)
        if abs(proof_actual) < abs(proof_rpm) * .5 or math.copysign(1, proof_actual) != math.copysign(1, proof_rpm):
            raise RuntimeError(f"50 rpm direction proof failed; measured {proof_actual:.1f} rpm")
        session.ramp(proof_raw, target, args.ramp_seconds)
        samples = []
        for _ in range(max(1, round(args.hold_seconds / session.cycle_s))):
            status = session.exchange(0x000F, target)
            samples.append(rpm_for_raw_velocity(status["velocity_raw"], encoder, numerator, denominator))
            time.sleep(session.cycle_s)
        session.ramp(target, 0, args.ramp_seconds)
        stop_deadline = time.perf_counter() + 5
        while time.perf_counter() < stop_deadline:
            status = session.exchange(0x000F, 0)
            if abs(rpm_for_raw_velocity(status["velocity_raw"], encoder, numerator, denominator)) < 5:
                break
            time.sleep(session.cycle_s)
        else:
            raise TimeoutError("motor did not decelerate below 5 rpm before disable")
        report.update({"proof_rpm_actual": proof_actual, "hold_rpm_mean": sum(samples) / len(samples),
                       "final_status": session.last, "ok": True})
        return report
    finally:
        session.shutdown()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["check", "prove-direction", "run"], nargs="?", default="check")
    parser.add_argument("--config", type=Path, default=Path("config/ethercat.json"))
    parser.add_argument("--bench", type=Path, default=Path("config/bench.json"))
    parser.add_argument("--rpm", type=float, default=500)
    parser.add_argument("--ramp-seconds", type=float, default=5)
    parser.add_argument("--hold-seconds", type=float, default=5)
    parser.add_argument("--cycle-ms", type=int, choices=[1, 2, 4, 8], default=4)
    parser.add_argument("--torque-limit", type=int, default=100,
                        help="positive/negative torque limit in 0.1%% units; default 10%%")
    parser.add_argument("--i-confirm-shaft-clear", action="store_true")
    parser.add_argument("--i-confirm-estop-tested", action="store_true")
    parser.add_argument("--output", type=Path, default=Path("artifacts/ethercat/velocity.json"))
    args = parser.parse_args(argv)
    code = 0
    try:
        report = run(args)
    except Exception as exc:
        report = {"timestamp_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "action": args.action,
                  "drive_writes": args.action != "check" and not isinstance(exc, PermissionError), "ok": False,
                  "error": f"{type(exc).__name__}: {exc}"}
        code = 2
    args.output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(report, ensure_ascii=False, indent=2)
    args.output.write_text(text + "\n", encoding="utf-8")
    print(text)
    return code


if __name__ == "__main__":
    sys.exit(main())
