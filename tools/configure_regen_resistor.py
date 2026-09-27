"""Configure and verify the commissioned SV630N external regenerative resistor.

This utility stays in PRE-OP, refuses to write while CiA 402 is operation-enabled,
checks the configured slave identity, and verifies every parameter by readback.
"""
import argparse
import json
from pathlib import Path
import struct

from ethercat_probe import as_text, decode_state, read_int
from ethercat_velocity import identity_matches


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, default=Path("config/ethercat.json"))
    parser.add_argument("--cooling", choices=("natural", "forced"), default="natural")
    parser.add_argument("--power-w", type=int, default=1500)
    parser.add_argument("--resistance-ohm", type=int, default=50)
    args = parser.parse_args()
    if not 1 <= args.power_w <= 65535 or not 1 <= args.resistance_ohm <= 1000:
        raise ValueError("external resistor values are outside the drive parameter ranges")

    import pysoem
    cfg = json.loads(args.config.read_text(encoding="utf-8-sig"))
    master = pysoem.Master()
    try:
        master.open(cfg["adapter"])
        master.sdo_read_timeout = 500_000
        master.sdo_write_timeout = 500_000
        count = master.config_init()
        if count != 1:
            raise ConnectionError(f"expected exactly one EtherCAT slave, found {count}")
        slave = master.slaves[0]
        if not identity_matches(slave, cfg.get("expectedIdentity", {})):
            raise ConnectionError(f"slave identity mismatch: {as_text(slave.name)}")
        master.state = pysoem.PREOP_STATE
        master.write_state()
        if master.state_check(pysoem.PREOP_STATE, 500_000) != pysoem.PREOP_STATE:
            raise ConnectionError("drive did not enter PRE-OP")

        statusword = read_int(slave, 0x6041, 0, 2)
        state = decode_state(statusword)
        if state == "operation_enabled":
            raise PermissionError("refusing parameter writes while Servo is operation-enabled")

        minimum = read_int(slave, 0x2002, 0x16, 2)
        heat_dissipation_pct = read_int(slave, 0x2002, 0x19, 2)
        before = {
            "type": read_int(slave, 0x2002, 0x1A, 2),
            "power_w": read_int(slave, 0x2002, 0x1B, 2),
            "resistance_ohm": read_int(slave, 0x2002, 0x1C, 2),
        }
        if args.resistance_ohm < minimum:
            raise ValueError(f"{args.resistance_ohm} ohm is below drive minimum {minimum} ohm")

        desired = {"type": 1 if args.cooling == "natural" else 2,
                   "power_w": args.power_w, "resistance_ohm": args.resistance_ohm}
        for subindex, value in ((0x1A, desired["type"]), (0x1B, desired["power_w"]),
                                (0x1C, desired["resistance_ohm"])):
            slave.sdo_write(0x2002, subindex, struct.pack("<H", value))

        after = {
            "type": read_int(slave, 0x2002, 0x1A, 2),
            "power_w": read_int(slave, 0x2002, 0x1B, 2),
            "resistance_ohm": read_int(slave, 0x2002, 0x1C, 2),
        }
        if after != desired:
            raise IOError(f"parameter readback mismatch: expected={desired}, actual={after}")
        print(json.dumps({"ok": True, "drive": as_text(slave.name), "cia402_state": state,
                          "minimum_resistance_ohm": minimum,
                          "heat_dissipation_coefficient_pct": heat_dissipation_pct,
                          "before": before, "after": after},
                         ensure_ascii=False, indent=2))
    finally:
        master.close()


if __name__ == "__main__":
    main()
