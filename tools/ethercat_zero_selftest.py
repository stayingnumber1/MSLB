"""Hardware-safe CSV integration test: enable at zero speed, then disable."""
import json
from pathlib import Path
import subprocess
import sys
import time


def main():
    service = Path(__file__).with_name("ethercat_direct_service.py")
    process = subprocess.Popen(
        [sys.executable, "-u", str(service), "--max-rpm", "6000",
         "--max-ramp-rpm-per-s", "6000"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", bufsize=1)
    result = {"ok": False, "motion_commanded": False, "target_rpm": 0,
              "states": []}
    phase = "connect"
    deadline = time.monotonic() + 20
    try:
        while time.monotonic() < deadline:
            line = process.stdout.readline()
            if not line:
                if process.poll() is not None:
                    raise RuntimeError(f"service exited with code {process.returncode}")
                continue
            message = json.loads(line)
            if message.get("type") == "fatal":
                raise RuntimeError(message.get("message", "service fatal error"))
            if message.get("type") != "status":
                continue
            state = message["state"]
            if not result["states"] or result["states"][-1] != state:
                result["states"].append(state)
            if abs(message.get("target_rpm", 0)) > 0.01:
                raise RuntimeError("non-zero target detected during zero-speed test")
            if phase == "connect" and state == "switch_on_disabled":
                process.stdin.write('{"type":"enable"}\n')
                process.stdin.flush()
                phase = "enable"
            elif phase == "enable" and message.get("servo_on"):
                result["enabled_statusword"] = message["statusword"]
                result["enabled_speed_rpm"] = message["speed_rpm"]
                process.stdin.write('{"type":"disable"}\n')
                process.stdin.flush()
                phase = "disable"
            elif phase == "disable" and state == "switch_on_disabled":
                result["disabled_statusword"] = message["statusword"]
                result["ok"] = True
                return 0
        raise TimeoutError(f"zero-speed self-test timed out in phase {phase}")
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
        return 2
    finally:
        try:
            process.stdin.write('{"type":"shutdown"}\n')
            process.stdin.flush()
        except Exception:
            pass
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=3)
        print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.exit(main())
