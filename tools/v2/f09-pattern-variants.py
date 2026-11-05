"""NX Python Journal wrapper for the isolated F09 builder API variants probe."""
import NXOpen


WRAPPER_DLL = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\workspace\v2\evidence\F09PatternVariantsJournal.dll"
NXOpen.Session.GetSession().Execute(WRAPPER_DLL, "NXJournal", "Run", [])
