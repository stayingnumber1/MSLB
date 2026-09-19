"""Recover SV635/SV660 EE08.0 online without enabling motion.

Keeps target velocity zero and never sends CiA402 operation-enable (0x000F).
"""
import json
import argparse
from pathlib import Path
import struct
import sys
import threading
import time

from ethercat_probe import decode_state, read_int
from ethercat_velocity import (assign_fixed_pdo, identity_matches, pack_rx,
                               raw_velocity_for_rpm, rpm_for_raw_velocity)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-500", action="store_true")
    parser.add_argument("--i-confirm-shaft-clear", action="store_true")
    parser.add_argument("--i-confirm-estop-tested", action="store_true")
    parser.add_argument("--keepalive-seconds", type=int, default=1800)
    args = parser.parse_args()
    if args.run_500 and not (args.i_confirm_shaft_clear and args.i_confirm_estop_tested):
        print(json.dumps({"ok": False, "motion_commanded": False,
                          "error": "physical safety confirmations required"}, indent=2))
        return 2
    import pysoem
    cfg = json.loads(Path("config/ethercat.json").read_text(encoding="utf-8-sig"))
    master = pysoem.Master()
    slave = None
    mapped = False
    sync_active = False
    stop = threading.Event()
    lock = threading.Lock()
    pump_errors = []
    pump_thread = None
    result = {"ok": False, "motion_commanded": False, "target_velocity_raw": 0}

    def pump():
        next_tick = time.perf_counter()
        try:
            while not stop.is_set():
                with lock:
                    master.send_processdata(release_gil=True)
                    master.receive_processdata(3000, release_gil=True)
                next_tick += 0.004
                time.sleep(max(0, next_tick - time.perf_counter()))
        except Exception as exc:
            pump_errors.append(exc)

    try:
        master.open(cfg["adapter"])
        if master.config_init() != 1:
            raise ConnectionError("expected exactly one slave")
        slave = master.slaves[0]
        if not identity_matches(slave, cfg.get("expectedIdentity", {})):
            raise ConnectionError("slave identity mismatch")
        master.state = pysoem.PREOP_STATE
        master.write_state()
        if master.state_check(pysoem.PREOP_STATE, 500_000) != pysoem.PREOP_STATE:
            raise ConnectionError("PRE-OP failed")

        slave.sdo_write(0x2002, 1, struct.pack("<H", 9))
        slave.sdo_write(0x6060, 0, struct.pack("<b", 9))
        slave.config_func = lambda position: assign_fixed_pdo(master.slaves[position])
        master.config_map()
        mapped = True
        if not master.config_dc():
            raise ConnectionError("distributed clocks unavailable")
        slave.dc_sync(True, 4_000_000)
        sync_active = True
        slave.output = pack_rx(0, 0, 100)
        if master.state_check(pysoem.SAFEOP_STATE, 500_000) != pysoem.SAFEOP_STATE:
            master.state = pysoem.SAFEOP_STATE
            master.write_state()
            if master.state_check(pysoem.SAFEOP_STATE, 500_000) != pysoem.SAFEOP_STATE:
                raise ConnectionError("SAFE-OP failed")

        pump_thread = threading.Thread(target=pump, name="ethercat-recovery", daemon=True)
        pump_thread.start()
        time.sleep(0.1)
        master.state = pysoem.OP_STATE
        master.write_state()
        if master.state_check(pysoem.OP_STATE, 1_000_000) != pysoem.OP_STATE:
            raise ConnectionError("OP failed")

        # First use the standard CiA402 rising edge while zero velocity is maintained.
        slave.output = pack_rx(0x0080, 0, 100)
        time.sleep(0.15)
        slave.output = pack_rx(0, 0, 100)
        time.sleep(0.15)

        # Also request the documented manufacturer reset while the fault source
        # (missing SYNC in OP) is no longer present. Serialize it with PDO access.
        with lock:
            slave.sdo_write(0x200D, 2, struct.pack("<H", 1))
        time.sleep(0.3)
        with lock:
            error_in_op = read_int(slave, 0x603F, 0, 2)
            status_in_op = read_int(slave, 0x6041, 0, 2)
        result.update({"error_in_op": f"0x{error_in_op:04X}",
                       "status_in_op": f"0x{status_in_op:04X}"})

        # Prove RPDO control-word delivery at zero velocity without ever setting
        # bit 3 (operation enable).
        state_proof = []
        for command in (0x0006, 0x0007, 0x0000):
            slave.output = pack_rx(command, 0, 100)
            time.sleep(0.2)
            with lock:
                word = read_int(slave, 0x6041, 0, 2)
                error = read_int(slave, 0x603F, 0, 2)
            state_proof.append({"controlword": f"0x{command:04X}",
                                "statusword": f"0x{word:04X}",
                                "state": decode_state(word), "error": f"0x{error:04X}"})
        result["zero_speed_state_proof"] = state_proof

        if args.run_500:
            encoder = 1 << 23
            with lock:
                numerator = read_int(slave, 0x6091, 1, 4)
                denominator = read_int(slave, 0x6091, 2, 4)

            def snapshot():
                if pump_errors:
                    raise RuntimeError(f"PDO pump failed: {pump_errors[0]}")
                with lock:
                    word = read_int(slave, 0x6041, 0, 2)
                    error = read_int(slave, 0x603F, 0, 2)
                    raw = read_int(slave, 0x606C, 0, 4, signed=True)
                return word, error, rpm_for_raw_velocity(raw, encoder, numerator, denominator)

            def command_and_wait(command, expected, timeout=2):
                slave.output = pack_rx(command, 0, 100)
                deadline = time.perf_counter() + timeout
                while time.perf_counter() < deadline:
                    word, error, rpm = snapshot()
                    if error:
                        raise RuntimeError(f"drive fault 0x{error:04X}")
                    if decode_state(word) == expected:
                        return
                    time.sleep(0.02)
                raise TimeoutError(f"did not reach {expected}, status=0x{word:04X}")

            command_and_wait(0x0006, "ready_to_switch_on")
            command_and_wait(0x0007, "switched_on")
            command_and_wait(0x000F, "operation_enabled")

            def ramp(start_rpm, end_rpm, seconds, overspeed):
                count = round(seconds / 0.02)
                readings = []
                for i in range(1, count + 1):
                    target_rpm = start_rpm + (end_rpm - start_rpm) * i / count
                    target = raw_velocity_for_rpm(target_rpm, encoder, numerator, denominator)
                    slave.output = pack_rx(0x000F, target, 100)
                    word, error, actual = snapshot()
                    if error or decode_state(word) != "operation_enabled":
                        raise RuntimeError(f"motion state lost: status=0x{word:04X}, error=0x{error:04X}")
                    if abs(actual) > overspeed:
                        raise RuntimeError(f"overspeed {actual:.1f} rpm")
                    readings.append(actual)
                    time.sleep(0.02)
                return readings

            ramp(0, 50, 5, 75)
            proof = []
            proof_target = raw_velocity_for_rpm(50, encoder, numerator, denominator)
            for _ in range(50):
                slave.output = pack_rx(0x000F, proof_target, 100)
                word, error, actual = snapshot()
                if error or decode_state(word) != "operation_enabled":
                    raise RuntimeError(f"50 rpm proof state lost: 0x{word:04X}/0x{error:04X}")
                proof.append(actual)
                time.sleep(0.02)
            proof_mean = sum(proof) / len(proof)
            if not 25 <= proof_mean <= 75:
                raise RuntimeError(f"50 rpm proof failed: {proof_mean:.1f} rpm")
            print(json.dumps({"event": "direction_proof_passed", "rpm_mean": proof_mean}), flush=True)

            ramp(50, 500, 5, 550)
            hold = []
            target_500 = raw_velocity_for_rpm(500, encoder, numerator, denominator)
            for _ in range(250):
                slave.output = pack_rx(0x000F, target_500, 100)
                word, error, actual = snapshot()
                if error or decode_state(word) != "operation_enabled" or abs(actual) > 550:
                    raise RuntimeError(f"500 rpm hold failed: {actual:.1f}, 0x{word:04X}/0x{error:04X}")
                hold.append(actual)
                time.sleep(0.02)
            hold_mean = sum(hold) / len(hold)
            ramp(500, 0, 5, 550)
            stop_deadline = time.perf_counter() + 5
            while True:
                slave.output = pack_rx(0x000F, 0, 100)
                word, error, actual = snapshot()
                if abs(actual) < 5:
                    break
                if time.perf_counter() >= stop_deadline:
                    raise TimeoutError(f"motor did not stop: {actual:.1f} rpm")
                time.sleep(0.02)
            command_and_wait(0x0007, "switched_on")
            slave.output = pack_rx(0, 0, 100)
            result.update({"motion_commanded": True, "direction_proof_rpm": proof_mean,
                           "hold_500_rpm_mean": hold_mean, "motor_stopped": True})
            print(json.dumps({"event": "motion_complete", "direction_proof_rpm": proof_mean,
                              "hold_500_rpm_mean": hold_mean, "motor_stopped": True,
                              "keeping_op_sync": True}), flush=True)
            keepalive_deadline = time.perf_counter() + args.keepalive_seconds
            while time.perf_counter() < keepalive_deadline:
                slave.output = pack_rx(0, 0, 100)
                time.sleep(0.5)

        # Stop monitoring EtherCAT as the active command source while OP/SYNC is
        # still healthy. Only then leave OP, otherwise firmware raises EE08 again.
        with lock:
            slave.sdo_write(0x6060, 0, struct.pack("<b", 0))
            slave.sdo_write(0x2002, 1, struct.pack("<H", 2))
        time.sleep(0.2)

        # Leave OP before stopping SYNC0, which avoids creating EE08.0 again.
        master.state = pysoem.SAFEOP_STATE
        master.write_state()
        master.state_check(pysoem.SAFEOP_STATE, 500_000)
        stop.set()
        pump_thread.join(timeout=1)
        slave.dc_sync(False, 4_000_000)
        sync_active = False
        master.state = pysoem.PREOP_STATE
        master.write_state()
        master.state_check(pysoem.PREOP_STATE, 500_000)
        time.sleep(0.2)
        error_final = read_int(slave, 0x603F, 0, 2)
        status_final = read_int(slave, 0x6041, 0, 2)
        result.update({"error_final": f"0x{error_final:04X}",
                       "status_final": f"0x{status_final:04X}",
                       "control_source_restored": read_int(slave, 0x2002, 1, 2) == 2,
                       "ok": error_final == 0})
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        stop.set()
        if pump_thread:
            pump_thread.join(timeout=1)
        if slave is not None and mapped:
            try:
                slave.output = pack_rx(0, 0, 100)
                master.state = pysoem.SAFEOP_STATE
                master.write_state()
                master.state_check(pysoem.SAFEOP_STATE, 300_000)
            except Exception:
                pass
        if slave is not None and sync_active:
            try:
                slave.dc_sync(False, 4_000_000)
            except Exception:
                pass
        if slave is not None:
            try:
                master.state = pysoem.PREOP_STATE
                master.write_state()
                master.state_check(pysoem.PREOP_STATE, 300_000)
                slave.sdo_write(0x6060, 0, struct.pack("<b", 0))
                slave.sdo_write(0x2002, 1, struct.pack("<H", 2))
            except Exception:
                pass
        master.close()
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["ok"] else 2


if __name__ == "__main__":
    sys.exit(main())
