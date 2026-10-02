"""Read-only USBPcap/pcap integrity and endpoint timeline inspection.

USBPcap captures host URBs, not D+/D- waveforms. An OUT completion does not
prove that the device application parsed the command.
"""

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
from pathlib import Path
import struct


USB_HEADER = struct.Struct("<HQIHBHHBBI")


def records(path):
    with Path(path).open("rb") as stream:
        header = stream.read(24)
        if len(header) != 24 or header[:4] != b"\xd4\xc3\xb2\xa1":
            raise ValueError("Expected little-endian microsecond pcap")
        version_major, version_minor, _, _, snaplen, linktype = struct.unpack_from("<HHIIII", header, 4)
        if (version_major, version_minor, linktype) != (2, 4, 249):
            raise ValueError(f"Unexpected pcap version/linktype: {version_major}.{version_minor}/{linktype}")
        number = 0
        while True:
            offset = stream.tell()
            packet_header = stream.read(16)
            if not packet_header:
                return
            if len(packet_header) != 16:
                raise ValueError(f"Truncated record header at {offset}")
            seconds, micros, captured, original = struct.unpack("<IIII", packet_header)
            if micros >= 1_000_000 or captured > snaplen or captured > original:
                raise ValueError(f"Invalid record header at {offset}")
            packet = stream.read(captured)
            if len(packet) != captured:
                raise ValueError(f"Truncated record data at {offset}")
            if captured < USB_HEADER.size:
                raise ValueError(f"Short USBPcap record at {offset}")
            header_len, irp_id, status, function, info, bus, device, endpoint, transfer, data_len = USB_HEADER.unpack_from(packet)
            if header_len < USB_HEADER.size or header_len > captured:
                raise ValueError(f"Invalid USB header length at {offset}: {header_len}")
            number += 1
            yield {
                "n": number,
                "utc": datetime.fromtimestamp(seconds + micros / 1_000_000, timezone.utc).isoformat(),
                "timestamp": seconds + micros / 1_000_000,
                "irp": irp_id,
                "status": status,
                "function": function,
                "info": info,
                "bus": bus,
                "device": device,
                "endpoint": endpoint,
                "transfer": transfer,
                "data_len": data_len,
                "payload": packet[header_len:],
            }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pcap")
    parser.add_argument("--device", type=int)
    parser.add_argument("--start-utc", help="ISO timestamp, inclusive")
    parser.add_argument("--end-utc", help="ISO timestamp, exclusive")
    parser.add_argument("--limit", type=int, default=30, help="Maximum detailed records to print")
    args = parser.parse_args()
    start = datetime.fromisoformat(args.start_utc.replace("Z", "+00:00")).timestamp() if args.start_utc else None
    end = datetime.fromisoformat(args.end_utc.replace("Z", "+00:00")).timestamp() if args.end_utc else None
    count = 0
    first = last = None
    devices = Counter()
    endpoint_counts = Counter()
    per_second = Counter()
    nonzero_status = Counter()
    matching = []
    descriptors = []
    for row in records(args.pcap):
        count += 1
        first = first or row["utc"]
        last = row["utc"]
        devices[(row["bus"], row["device"])] += 1
        payload = row["payload"]
        if b"\x83\x04\x40\x57" in payload:
            descriptors.append((row["utc"], row["bus"], row["device"], row["endpoint"], row["n"]))
        if args.device is not None and row["device"] != args.device:
            continue
        if start is not None and row["timestamp"] < start:
            continue
        if end is not None and row["timestamp"] >= end:
            continue
        key = (row["bus"], row["device"], f"0x{row['endpoint']:02x}", row["info"], row["transfer"])
        endpoint_counts[key] += 1
        if start is not None or end is not None:
            per_second[(row["utc"][:19], f"0x{row['endpoint']:02x}", row["info"])] += 1
        if row["status"]:
            nonzero_status[f"0x{row['status']:08x}"] += 1
        if start is not None or end is not None:
            matching.append({k: row[k] for k in ("utc", "n", "irp", "status", "function", "info", "bus", "device", "endpoint", "transfer", "data_len")}
                        | {"payload_hex": payload[:24].hex()})
    print(json.dumps({
        "file": str(Path(args.pcap).resolve()), "records": count,
        "first_utc": first, "last_utc": last,
        "devices": [[*key, value] for key, value in devices.most_common()],
        "vid_0483_pid_5740_descriptors": descriptors[:20],
        "filtered_endpoint_counts": [[*key, value] for key, value in endpoint_counts.most_common()],
        "filtered_per_second": [[*key, value] for key, value in sorted(per_second.items())],
        "filtered_nonzero_status": dict(nonzero_status),
        "filtered_records": matching[:max(0, args.limit)],
        "filtered_record_total": len(matching),
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
