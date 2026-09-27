import argparse
import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from ethercat_velocity import (encoder_resolution_for_drive, pack_rx, raw_velocity_for_rpm, rpm_for_raw_velocity,
                               require_run_gates, unpack_tx)
from ethercat_direct_service import (drive_fault_message, is_user_reset_allowed,
                                     pack_service_rx, slave_count_error)


class VelocityTests(unittest.TestCase):
    def test_sv630_18_bit_speed_and_limit(self):
        encoder = encoder_resolution_for_drive("InoSV630N", 14101)
        self.assertEqual(encoder, 262144)
        self.assertEqual(raw_velocity_for_rpm(500, encoder, 1, 1), 2184533)
        self.assertAlmostEqual(rpm_for_raw_velocity(-2184533, encoder, 1, 1), -500, places=3)
        self.assertEqual(raw_velocity_for_rpm(600, encoder, 1, 1), 2621440)

    def test_same_motor_code_retains_legacy_drive_resolution(self):
        for name in ("InoSV635N", "InoSV660N"):
            self.assertEqual(encoder_resolution_for_drive(name, 14101), 8388608)

    def test_unverified_scaling_is_rejected(self):
        for name, code in (("unknown", 14101), ("InoSV630N", 14000)):
            with self.assertRaises(ValueError):
                encoder_resolution_for_drive(name, code)

    def test_runaway_fault_is_explicitly_non_resettable(self):
        message = drive_fault_message(0x0234)
        self.assertIn("E234.0", message)
        self.assertIn("飞车保护", message)
        self.assertIn("不可在线复位", message)

    def test_encoder_fault_uses_manufacturer_auxiliary_code(self):
        resettable = drive_fault_message(0x7305, 0x07310731)
        fatal = drive_fault_message(0x7305, 0x27400740)
        self.assertIn("E731.0", resettable)
        self.assertIn("可由用户执行故障复位", resettable)
        self.assertIn("E740.2", fatal)
        self.assertIn("不可在线复位", fatal)

    def test_slave_count_error_reports_observed_count_and_adapter(self):
        message = slave_count_error(0, "npf-test")
        self.assertIn("扫描结果为0", message)
        self.assertIn("npf-test", message)
        self.assertIn("TwinCAT", message)

    def test_only_commissioned_encoder_faults_allow_user_reset(self):
        self.assertTrue(is_user_reset_allowed(0x7305, 0x07310731))
        self.assertTrue(is_user_reset_allowed(0x7305, 0x07330733))
        self.assertTrue(is_user_reset_allowed(0x7305, 0x07350735))
        self.assertFalse(is_user_reset_allowed(0x7305, 0x27400740))
        self.assertFalse(is_user_reset_allowed(0x7305, None))
        self.assertFalse(is_user_reset_allowed(0x0234, None))

    def test_500_rpm_23_bit_one_to_one(self):
        raw = raw_velocity_for_rpm(500, 1 << 23, 1, 1)
        self.assertEqual(raw, 69905067)
        self.assertAlmostEqual(rpm_for_raw_velocity(raw, 1 << 23, 1, 1), 500, places=4)

    def test_1703_layout(self):
        data = pack_rx(0x000F, 69905067, 100)
        self.assertEqual(len(data), 17)
        self.assertEqual(struct.unpack("<HiibHHH", data), (15, 0, 69905067, 9, 0, 100, 100))

    def test_1b04_layout(self):
        data = struct.pack("<HHihbiHiii", 0, 0x27, 123, -2, 9, 4, 5, 6, 7, 69905067)
        status = unpack_tx(data)
        self.assertEqual(status["cia402_state"], "operation_enabled")
        self.assertEqual(status["velocity_raw"], 69905067)

    def test_1702_cst_layout(self):
        data = pack_service_rx(0x000F, 0, -84, 10, 838860800)
        self.assertEqual(len(data), 19)
        self.assertEqual(struct.unpack("<HiihbHI", data),
                         (15, 0, 0, -84, 10, 0, 838860800))

    def test_1702_accepts_motor_peak_torque(self):
        data = pack_service_rx(0x000F, 0, 3500, 10, 838860800)
        self.assertEqual(struct.unpack("<HiihbHI", data)[3], 3500)

    def test_run_is_locked_by_default(self):
        args = argparse.Namespace(action="run", i_confirm_shaft_clear=False, i_confirm_estop_tested=False,
                                  rpm=500, ramp_seconds=5, hold_seconds=5)
        with self.assertRaises(PermissionError):
            require_run_gates({"drive": {}, "limits": {"maxSpeedRpm": 500}}, args)

    def test_all_gates_required(self):
        drive = {name: True for name in ("modelVerified", "pdoVerified", "directionVerified",
                                          "regenerationVerified", "hardwareSafetyVerified",
                                          "allowHardwareWrites")}
        args = argparse.Namespace(action="run", i_confirm_shaft_clear=True, i_confirm_estop_tested=True,
                                  rpm=500, ramp_seconds=5, hold_seconds=5)
        require_run_gates({"drive": drive, "limits": {"maxSpeedRpm": 500}}, args)


if __name__ == "__main__":
    unittest.main()
