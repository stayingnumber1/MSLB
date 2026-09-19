"""PC EtherCAT/CoE commissioning. No drive SDO writes, PDO output or OP request.
Discovery initializes ALL slaves on the selected dedicated bus to PRE-OP.
This is not a passive packet sniffer and must not share an active master bus.
"""
import argparse
import datetime
import json
from pathlib import Path
import sys

def as_text(value):
    return value.decode('utf-8', errors='replace') if isinstance(value, bytes) else str(value)

def decode_state(word):
    for mask, value, name in [
        (0x4f, 0x00, "not_ready"), (0x4f, 0x40, "switch_on_disabled"),
        (0x6f, 0x21, "ready_to_switch_on"), (0x6f, 0x23, "switched_on"),
        (0x6f, 0x27, "operation_enabled"), (0x6f, 0x07, "quick_stop_active"),
        (0x4f, 0x0f, "fault_reaction_active"), (0x4f, 0x08, "fault"),
    ]:
        if word & mask == value:
            return name
    return "unknown"

def read_int(slave, index, subindex, size, signed=False):
    data = slave.sdo_read(index, subindex)
    if len(data) != size:
        raise ValueError(f"0x{index:04X}:{subindex:02X}: expected {size} bytes, got {len(data)}")
    return int.from_bytes(data, "little", signed=signed)

def read_pdo_assignment(slave, assignment):
    count = read_int(slave, assignment, 0, 1)
    result = []
    for sub in range(1, count + 1):
        pdo = read_int(slave, assignment, sub, 2)
        if not 0x1600 <= pdo <= 0x1bff:
            raise ValueError(f"Invalid PDO mapping object 0x{pdo:04X}")
        entries = []
        for i in range(1, read_int(slave, pdo, 0, 1) + 1):
            raw = read_int(slave, pdo, i, 4)
            entries.append({"index": f"0x{raw >> 16:04X}", "subindex": (raw >> 8) & 255, "bits": raw & 255})
        result.append({"mapping": f"0x{pdo:04X}", "entries": entries})
    return result

def inspect_slave(slave, position):
    result = {"position": position, "name": as_text(slave.name),
              "vendor_id": slave.man, "product_code": slave.id, "revision": slave.rev,
              "ethercat_state": slave.state, "objects": {}, "errors": {}}
    # Standard CiA402 objects. Raw values deliberately retain their original units.
    specs = [
        (0x6041, 0, 2, False, "statusword"),
        (0x6061, 0, 1, True, "mode_display"),
        (0x603f, 0, 2, False, "error_code"),
        (0x6064, 0, 4, True, "position_raw"),
        (0x606c, 0, 4, True, "velocity_raw"),
        (0x6077, 0, 2, True, "torque_raw"),
        (0x6502, 0, 4, False, "supported_modes"),
    ] + [(0x1018, i, 4, False, name) for i, name in enumerate(
        ["identity_vendor", "identity_product", "identity_revision", "identity_serial"], 1)]
    for index, sub, size, signed, name in specs:
        try:
            result["objects"][name] = read_int(slave, index, sub, size, signed)
        except Exception as exc:
            result["errors"][name] = str(exc)
    if "statusword" in result["objects"]:
        result["cia402_state"] = decode_state(result["objects"]["statusword"])
    for index, name in [(0x1c12, "rx_pdo"), (0x1c13, "tx_pdo")]:
        try:
            result[name] = read_pdo_assignment(slave, index)
        except Exception as exc:
            result["errors"][name] = str(exc)
    result["coe_status_read_ok"] = "statusword" in result["objects"]
    return result

def discover(module, adapter, dedicated_bus):
    if not adapter or not dedicated_bus:
        raise ValueError("Set adapter and dedicatedAdapterConfirmed=true in config/ethercat.json first. Discovery changes bus state.")
    adapters = {as_text(a.name) for a in module.find_adapters()}
    if adapter not in adapters:
        raise ValueError("Configured adapter is absent from the capture driver adapter list")
    master = module.Master()
    opened = False
    try:
        master.open(adapter)
        opened = True
        master.sdo_read_timeout = 300000
        if master.config_init() <= 0:
            raise ConnectionError("No EtherCAT slave found: check dedicated NIC, cable and drive EtherCAT IN")
        master.read_state()
        return [inspect_slave(s, i) for i, s in enumerate(master.slaves, 1)]
    finally:
        if opened:
            master.close()

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["adapters", "scan"])
    parser.add_argument("--config", type=Path, default=Path("config/ethercat.json"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    report = {"timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "action": args.action, "drive_writes": False, "ok": False}
    code = 0
    try:
        import pysoem
        report["pysoem_version"] = getattr(pysoem, "__version__", "unknown")
        if args.action == "adapters":
            report["adapters"] = [{"name": as_text(a.name), "description": as_text(a.desc)}
                                  for a in pysoem.find_adapters()]
            report["ok"] = True
            report["note"] = "Enumeration only; no device connection or EtherCAT frames."
        else:
            config = json.loads(args.config.read_text(encoding="utf-8-sig"))
            confirmed = config.get("dedicatedAdapterConfirmed") is True
            report["slaves"] = discover(pysoem, config.get("adapter", ""), confirmed)
            report["ok"] = all(s["coe_status_read_ok"] for s in report["slaves"])
            report["note"] = "PRE-OP commissioning only. Not an operational CST connection."
            if not report["ok"]:
                code = 2
    except Exception as exc:
        report["error"] = f"{type(exc).__name__}: {exc}"
        report["hint"] = "Windows requires a compatible packet capture driver and a dedicated wired NIC. USB commissioning is a different interface."
        code = 2
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
    print(text)
    return code

if __name__ == "__main__":
    sys.exit(main())
