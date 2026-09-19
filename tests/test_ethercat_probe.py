import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from ethercat_probe import as_text, decode_state, read_int, discover, inspect_slave, read_pdo_assignment, model_name_matches

class Slave:
    name = "test"
    man = 1
    id = 2
    rev = 3
    state = 2
    def __init__(self, values): self.values = values
    def sdo_read(self, index, sub): return self.values[(index, sub)]

class Tests(unittest.TestCase):
    def test_drive_model_name_must_match(self):
        self.assertTrue(model_name_matches("SV660N", "InoSV660N"))
        self.assertFalse(model_name_matches("SV660N", "InoSV635N"))

    def test_native_adapter_bytes(self):
        self.assertEqual(as_text(b'nic'), 'nic')
        self.assertEqual(as_text('nic'), 'nic')
    def test_all_states_with_extra_bits(self):
        states = {0x00:"not_ready",0x40:"switch_on_disabled",0x21:"ready_to_switch_on",
                  0x23:"switched_on",0x27:"operation_enabled",0x07:"quick_stop_active",
                  0x0f:"fault_reaction_active",0x08:"fault"}
        for word, expected in states.items():
            self.assertEqual(decode_state(word | 0x0280), expected)
        self.assertEqual(decode_state(0x6f), "unknown")
    def test_signed_and_width(self):
        s = Slave({(0x6077,0): b"\xff\xff", (0x606c,0): b"\x00"})
        self.assertEqual(read_int(s,0x6077,0,2,True),-1)
        with self.assertRaises(ValueError): read_int(s,0x606c,0,4,True)
    def test_partial_reads_do_not_invent_values(self):
        s = Slave({(0x6041,0): b"\x40\x00"})
        r = inspect_slave(s,1)
        self.assertTrue(r["coe_status_read_ok"])
        self.assertNotIn("velocity_raw",r["objects"])
        self.assertIn("velocity_raw",r["errors"])
        self.assertEqual(r["cia402_state"],"switch_on_disabled")
    def test_mapping(self):
        s = Slave({(0x1c12,0):b"\x01",(0x1c12,1):b"\x00\x16",
                   (0x1600,0):b"\x01",(0x1600,1):bytes.fromhex("10004060")})
        self.assertEqual(read_pdo_assignment(s,0x1c12)[0]["entries"],
                         [{"index":"0x6040","subindex":0,"bits":16}])
    def test_bus_confirmation_before_master_creation(self):
        with self.assertRaises(ValueError): discover(None,"nic",False)
    def test_missing_adapter_before_master_creation(self):
        module = SimpleNamespace(find_adapters=lambda: [])
        with self.assertRaises(ValueError): discover(module,"nic",True)
    def test_close_on_no_slave_and_no_output_calls(self):
        class Master:
            closed = False
            def open(self, name): pass
            def config_init(self): return 0
            def close(self): self.closed = True
        master = Master()
        module = SimpleNamespace(find_adapters=lambda:[SimpleNamespace(name=b"nic")],Master=lambda:master)
        with self.assertRaises(ConnectionError): discover(module,"nic",True)
        self.assertTrue(master.closed)
    def test_success_only_calls_read_interfaces(self):
        class Master:
            slaves = [Slave({(0x6041,0):b"\x40\x00"})]
            closed = False
            def open(self, name): pass
            def config_init(self): return 1
            def write_state(self): pass
            def state_check(self, expected, timeout): return expected
            def read_state(self): return 2
            def close(self): self.closed = True
        master = Master()
        module = SimpleNamespace(find_adapters=lambda:[SimpleNamespace(name=b"nic")],Master=lambda:master,PREOP_STATE=2)
        self.assertTrue(discover(module,"nic",True)[0]["coe_status_read_ok"])
        self.assertTrue(master.closed)

if __name__ == "__main__": unittest.main()
