import os
import subprocess
import sys

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CS_PATH = os.path.join(BASE_DIR, "Launcher_Coop.cs")
EXE_PATH = os.path.join(BASE_DIR, "Launcher_Coop.exe")
ICON_PATH = os.path.join(BASE_DIR, "game_icon.ico")


def main():
    # Compila o Launcher_Coop.cs mais recente para garantir que Host e Amigo usem exatamente o mesmo Launcher v1.3.0
    csc_paths = [
        r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe",
    ]
    csc = next((p for p in csc_paths if os.path.exists(p)), None)
    if csc and os.path.exists(CS_PATH):
        try:
            need_compile = not os.path.exists(EXE_PATH) or (os.path.getmtime(CS_PATH) > os.path.getmtime(EXE_PATH))
            if need_compile:
                cmd = [csc, "/nologo", "/target:winexe", f"/out:{EXE_PATH}"]
                if os.path.exists(ICON_PATH):
                    cmd.append(f"/win32icon:{ICON_PATH}")
                cmd.append(CS_PATH)
                subprocess.run(cmd, cwd=BASE_DIR, creationflags=0x08000000, timeout=10)
        except Exception:
            pass

    if os.path.exists(EXE_PATH):
        subprocess.Popen([EXE_PATH], cwd=BASE_DIR)
        sys.exit(0)


if __name__ == "__main__":
    main()
