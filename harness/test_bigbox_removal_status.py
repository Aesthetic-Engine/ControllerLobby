"""Final removal verification must control the actual CLI return status (M)."""
import importlib.util
from pathlib import Path
import sys
from unittest.mock import patch

spec=importlib.util.spec_from_file_location("adapter",Path(__file__).with_name("bigbox_install.py"))
adapter=importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)
argv=["bigbox_install.py","remove","--receipt","synthetic","--record","synthetic","--package-sha256","synthetic"]
for verdict,expected in [("NOT REMOVED synthetic remaining file",5),("removed: 6 planned files absent, both entries original",0)]:
    with patch.object(sys,"argv",argv),patch.object(adapter,"configure"),patch.object(adapter.tx,"remove",return_value="removed"),patch.object(adapter.tx,"check_removed",return_value=verdict):
        actual=adapter.main()
        assert actual==expected,(verdict,actual,expected)
print("PASS final verification failure and success propagate through actual CLI")
