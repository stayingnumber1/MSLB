import argparse
import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from ethercat_velocity import (pack_rx, raw_velocity_for_rpm, rpm_for_raw_velocity,
                               require_run_gates, unpack_tx)


class VelocityTests(unittest.TestCase):
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
