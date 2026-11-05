"""Root-only NX Python wrapper; return value is not acceptance evidence."""
import NXOpen
import os


def main():
    wrapper = os.environ.get("F18_WRAPPER_DLL")
    if not wrapper or not os.path.isfile(wrapper):
        raise RuntimeError("F18_WRAPPER_DLL must name the unique offline-compiled journal wrapper")
    result = NXOpen.Session.GetSession().Execute(wrapper, "NXJournal", "Run", [])
    print("F18 wrapper invocation returned: " + str(result) + "; inspect native report for outcome")


if __name__ == "__main__":
    main()
