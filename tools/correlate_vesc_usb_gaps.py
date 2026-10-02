"""Correlate MSLB telemetry-delay events with successful USBPcap bulk URBs."""

import argparse
from bisect import bisect_left, bisect_right
from datetime import datetime, timezone
import json
import re

from analyze_usbpcap import records


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def crc16(data):
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xffff if crc & 0x8000 else (crc << 1) & 0xffff
    return crc


def valid_values_frames(recent_hex):
    data = bytes.fromhex(recent_hex)
    count = 0
    for index in range(len(data) - 4):
        if data[index] != 2:
            continue
        length = data[index + 1]
        total = length + 5
        if index + total > len(data) or data[index + total - 1] != 3:
            continue
        payload = data[index + 2:index + 2 + length]
        check = int.from_bytes(data[index + 2 + length:index + 4 + length], "big")
        if payload and payload[0] == 4 and crc16(payload) == check:
            count += 1
    return count


def valid_usb_values_frame(data):
    if len(data) < 5 or data[0] != 2 or len(data) != data[1] + 5 or data[-1] != 3:
        return False
    payload = data[2:-3]
    return bool(payload) and payload[0] == 4 and crc16(payload) == int.from_bytes(data[-3:-1], "big")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pcap")
    parser.add_argument("--device", type=int, required=True)
    parser.add_argument("events", nargs="+")
    args = parser.parse_args()
    ins, outs = [], []
    in_valid = {}
    in_payload = {}
    for row in records(args.pcap):
        if row["device"] != args.device or row["info"] != 1 or row["status"] != 0:
            continue
        if row["endpoint"] == 0x81 and row["data_len"] > 0:
            ins.append(row["timestamp"])
            in_valid[row["timestamp"]] = valid_usb_values_frame(row["payload"])
            in_payload[row["timestamp"]] = row["payload"]
        elif row["endpoint"] == 0x01:
            outs.append(row["timestamp"])
    for filename in args.events:
        with open(filename, encoding="utf-8") as stream:
            events = [json.loads(line) for line in stream]
        for index, event in enumerate(events):
            if event["code"] != "vesc_telemetry_delayed":
                continue
            match = re.search(r"lastTelemetryUtc=([^;]+)", event["message"])
            if not match:
                continue
            hex_match = re.search(r"recentRxHex=([0-9A-Fa-f]+)", event["message"])
            last_host = timestamp(match.group(1))
            delayed = timestamp(event["timestampUtc"])
            recovered_event = next((later for later in events[index + 1:]
                                    if later["code"] in ("vesc_telemetry_recovered", "vesc_telemetry_stale", "vesc_idle_telemetry_stale")), None)
            recovered = timestamp(recovered_event["timestampUtc"]) if recovered_event else None
            before_index = bisect_right(ins, last_host) - 1
            next_index = bisect_right(ins, last_host)
            previous_in = ins[before_index] if before_index >= 0 else None
            next_in = ins[next_index] if next_index < len(ins) else None
            in_count_until_recovery = (bisect_right(ins, recovered) - bisect_right(ins, last_host)) if recovered is not None else None
            valid_in_until_recovery = sum(in_valid[t] for t in ins[bisect_right(ins, last_host):bisect_right(ins, recovered)]) if recovered is not None else None
            gap_in_times = ins[bisect_right(ins, last_host):bisect_right(ins, recovered)] if recovered is not None else []
            gap_in_stream = b"".join(in_payload[t] for t in gap_in_times)
            last_in_before_recovery_index = bisect_right(ins, recovered) - 1 if recovered is not None else -1
            last_in_before_recovery = ins[last_in_before_recovery_index] if last_in_before_recovery_index >= 0 else None
            next_out_index = bisect_right(outs, last_host)
            next_out = outs[next_out_index] if next_out_index < len(outs) else None
            out_count_to_next_in = bisect_right(outs, next_in) - bisect_right(outs, previous_in) if previous_in is not None and next_in is not None else None
            print(json.dumps({
                "events": filename,
                "delayed_utc": event["timestampUtc"],
                "recovered_utc": recovered_event["timestampUtc"] if recovered_event else None,
                "recovery_code": recovered_event["code"] if recovered_event else None,
                "last_host_telemetry_utc": match.group(1),
                "previous_in_utc": datetime.fromtimestamp(previous_in, timezone.utc).isoformat() if previous_in else None,
                "next_in_utc": datetime.fromtimestamp(next_in, timezone.utc).isoformat() if next_in else None,
                "next_out_utc": datetime.fromtimestamp(next_out, timezone.utc).isoformat() if next_out else None,
                "in_gap_ms": round((next_in - previous_in) * 1000, 3) if previous_in is not None and next_in is not None else None,
                "in_completions_during_host_gap": in_count_until_recovery,
                "valid_usb_values_frames_during_host_gap": valid_in_until_recovery,
                "in_lengths_during_host_gap": [len(in_payload[t]) for t in gap_in_times],
                "valid_reassembled_usb_values_frames_during_host_gap": valid_values_frames(gap_in_stream.hex()),
                "out_completions_during_in_gap": out_count_to_next_in,
                "last_in_before_recovery_utc": datetime.fromtimestamp(last_in_before_recovery, timezone.utc).isoformat() if last_in_before_recovery else None,
                "host_recovery_after_last_in_ms": round((recovered - last_in_before_recovery) * 1000, 3) if recovered is not None and last_in_before_recovery is not None else None,
                "delay_event_after_last_in_ms": round((delayed - previous_in) * 1000, 3) if previous_in is not None else None,
                "valid_values_frames_in_recent_rx": valid_values_frames(hex_match.group(1)) if hex_match else None,
            }, ensure_ascii=False))


if __name__ == "__main__":
    main()
