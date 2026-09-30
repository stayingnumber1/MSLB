import json
import socket
import sys
from pathlib import Path


HOST = "192.168.155.249"
PORT = 5025


def command_bytes(command: str, expected: int | None = None) -> bytes:
    with socket.create_connection((HOST, PORT), timeout=5) as sock:
        sock.settimeout(1)
        sock.sendall((command + "\n").encode("ascii"))
        chunks: list[bytes] = []
        total = 0
        while expected is None or total < expected:
            try:
                chunk = sock.recv(min(65536, expected - total) if expected else 65536)
            except TimeoutError:
                break
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
        return b"".join(chunks)


def waveform_block(command: str) -> bytes:
    with socket.create_connection((HOST, PORT), timeout=5) as sock:
        sock.settimeout(3)
        sock.sendall((command + "\n").encode("ascii"))
        data = bytearray()
        while len(data) < 22:
            data.extend(sock.recv(22 - len(data)))
        marker = data.find(b"#9")
        if marker < 0:
            raise RuntimeError(f"Missing waveform block header: {bytes(data)!r}")
        payload_length = int(bytes(data[marker + 2:marker + 11]))
        target = marker + 11 + payload_length
        while len(data) < target:
            chunk = sock.recv(min(65536, target - len(data)))
            if not chunk:
                raise RuntimeError(f"Waveform ended at {len(data)} of {target} bytes")
            data.extend(chunk)
        return bytes(data)


def main() -> None:
    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)
    screen = command_bytes("SCDP")
    (output / "scope_screen.bmp").write_bytes(screen)

    waveforms = {}
    for channel in ("C3", "C4"):
        waveform = waveform_block(f"{channel}:WF? DAT2")
        (output / f"{channel.lower()}_waveform.bin").write_bytes(waveform)
        waveforms[channel] = {
            "bytes": len(waveform),
            "header": waveform[:22].decode("ascii", errors="replace"),
        }
    metadata = {
        "screenBytes": len(screen),
        "screenBmp": screen[:2] == b"BM",
        "waveforms": waveforms,
    }
    (output / "capture_metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata))


if __name__ == "__main__":
    main()
