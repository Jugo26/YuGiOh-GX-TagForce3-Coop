import os
import sys
import threading
import subprocess
import tkinter as tk
from tkinter import messagebox

import coop_engine
import auto_updater

GAME_DIR = os.path.dirname(os.path.abspath(__file__))
GAME_CSO = os.path.join(GAME_DIR, "Yu-Gi-Oh! GX Tag Force 3 PTBR.cso")
BUNDLED_PPSSPP_EXE = os.path.join(GAME_DIR, "PPSSPP", "PPSSPPWindows64.exe")
SYSTEM_PPSSPP_EXE = r"C:\Program Files\PPSSPP\PPSSPPWindows64.exe"
CHEATS_INI = os.path.join(os.path.expanduser("~"), "Documents", "PPSSPP", "PSP", "Cheats", "ULES01183.ini")
GUIDE_TXT = os.path.join(GAME_DIR, "COMO_JOGAR_COOP.txt")
ICON_PATH = os.path.join(GAME_DIR, "game_icon.ico")
DEFAULT_RADMIN_IP = "26.196.166.86"

session = None


def find_ppsspp_executable():
    candidates = [
        BUNDLED_PPSSPP_EXE,
        SYSTEM_PPSSPP_EXE,
        r"C:\Program Files (x86)\PPSSPP\PPSSPPWindows64.exe",
        r"C:\Program Files\PPSSPP\PPSSPPWindows.exe",
        os.path.join(os.path.expanduser("~"), "Desktop", "PPSSPP", "PPSSPPWindows64.exe"),
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    return BUNDLED_PPSSPP_EXE


def launch_ppsspp_process():
    exe = find_ppsspp_executable()
    if not os.path.exists(exe):
        messagebox.showerror("Erro", f"PPSSPP nao encontrado em:\n{exe}")
        return False
    if not os.path.exists(GAME_CSO):
        messagebox.showerror("Erro", f"Arquivo do jogo nao encontrado em:\n{GAME_CSO}")
        return False
    if not coop_engine.find_ppsspp_hwnd():
        subprocess.Popen([exe, GAME_CSO], cwd=os.path.dirname(exe))
    return True


def append_log(msg):
    def _do():
        txt_log.configure(state="normal")
        txt_log.insert("end", msg + "\n")
        txt_log.see("end")
        txt_log.configure(state="disabled")
    root.after(0, _do)


def update_status_label(text):
    root.after(0, lambda: lbl_net_status.config(text=text))


def run_auto_update_check(force=False):
    def _worker():
        updated, ver, msg = auto_updater.check_and_apply_updates(on_log=append_log, force=force)
        if updated:
            def _prompt_restart():
                lbl_version.config(text=f"v{ver} (Atualizado do GitHub!)")
                if messagebox.askyesno(
                    "Atualizacao Concluida",
                    f"Uma nova versao (v{ver}) do Modo Co-op foi baixada do GitHub!\n\nDeseja reiniciar o Launcher agora para aplicar?"
                ):
                    if session:
                        session.stop()
                    subprocess.Popen([sys.executable, os.path.abspath(__file__)], cwd=GAME_DIR)
                    root.destroy()
            root.after(0, _prompt_restart)
    threading.Thread(target=_worker, daemon=True).start()


def start_as_host():
    global session
    host_ip = entry_ip.get().strip() or DEFAULT_RADMIN_IP
    coop_engine.configure_ppsspp_for_coop(is_host=True, host_ip=host_ip)
    if session is None:
        session = coop_engine.RadminCoopSession(on_status_change=update_status_label, on_log=append_log)
        session.start_host(bind_ip="0.0.0.0", port=coop_engine.COOP_PORT)
    launch_ppsspp_process()
    btn_host.config(text="✓ SALA HOST ATIVA (PPSSPP ABERTO)", bg="#15803d")
    btn_partner.config(state="disabled")


def start_as_partner():
    global session
    host_ip = entry_ip.get().strip() or DEFAULT_RADMIN_IP
    friend_name = entry_name.get().strip() or "Amigo"
    coop_engine.configure_ppsspp_for_coop(is_host=False, host_ip=host_ip)
    if session is None:
        session = coop_engine.RadminCoopSession(on_status_change=update_status_label, on_log=append_log)
        session.start_partner(host_ip=host_ip, port=coop_engine.COOP_PORT, custom_name=friend_name)
    launch_ppsspp_process()
    btn_partner.config(text="✓ CONECTADO COMO PARCEIRO", bg="#0369a1")
    btn_host.config(state="disabled")


def sync_now_host():
    if session and session.role == "host":
        session.capture_and_send_savestate(new_turn="host")
    elif session and session.role == "partner":
        session.send_my_deck_now(entry_name.get().strip())
    else:
        append_log("[Aviso] Inicie como Host ou Parceiro primeiro.")


def pass_turn_to_partner():
    if session:
        session.capture_and_send_savestate(new_turn="partner")
    else:
        append_log("[Aviso] Inicie a sessao Co-op primeiro.")


def return_turn_to_host():
    if session:
        session.capture_and_send_savestate(new_turn="host")
    else:
        append_log("[Aviso] Inicie a sessao Co-op primeiro.")


def send_deck_clicked():
    if session and session.role == "partner":
        session.send_my_deck_now(entry_name.get().strip())
    else:
        deck_info = coop_engine.read_local_player_deck()
        if deck_info:
            ok, msg = coop_engine.inject_partner_deck_into_host_save(
                deck_info["deck_b64"], entry_name.get().strip() or "Parceiro"
            )
            append_log(f"[Deck Local] {msg}")


def copy_radmin_ip():
    ip_val = entry_ip.get().strip() or DEFAULT_RADMIN_IP
    root.clipboard_clear()
    root.clipboard_append(ip_val)
    root.update()
    append_log(f"[Sistema] IP {ip_val} copiado para a area de transferencia!")


def open_cheats():
    coop_engine.configure_ppsspp_for_coop(is_host=True, host_ip=entry_ip.get().strip() or DEFAULT_RADMIN_IP)
    if os.path.exists(CHEATS_INI):
        os.system(f'notepad.exe "{CHEATS_INI}"')


def open_guide():
    if os.path.exists(GUIDE_TXT):
        os.system(f'notepad.exe "{GUIDE_TXT}"')


def on_close():
    if session:
        session.stop()
    root.destroy()


# Inicializa configuracao base (Save/System/Cheats) se for primeira execucao no PC
coop_engine.configure_ppsspp_for_coop(is_host=True, host_ip=DEFAULT_RADMIN_IP)
local_ver_info = auto_updater.get_local_version_info()

# ============================================================================
# INTERFACE GRAFICA DO LAUNCHER CO-OP RADMIN + AUTO-UPDATER GIT
# ============================================================================
root = tk.Tk()
root.title(f"Yu-Gi-Oh! GX Tag Force 3 - Launcher Co-op v{local_ver_info.get('version', '1.0.0')}")
root.geometry("650x700")
root.resizable(False, False)
root.configure(bg="#111318")
root.protocol("WM_DELETE_WINDOW", on_close)

if os.path.exists(ICON_PATH):
    try:
        root.iconbitmap(ICON_PATH)
    except Exception:
        pass

# Centraliza na tela
root.update_idletasks()
x = (root.winfo_screenwidth() // 2) - (650 // 2)
y = (root.winfo_screenheight() // 2) - (700 // 2)
root.geometry(f"+{x}+{y}")

# Header
frame_header = tk.Frame(root, bg="#181b22", pady=12)
frame_header.pack(fill="x")

tk.Label(
    frame_header,
    text="YU-GI-OH! GX TAG FORCE 3 [PT-BR]",
    font=("Segoe UI", 15, "bold"),
    fg="#f59e0b",
    bg="#181b22"
).pack()

lbl_version = tk.Label(
    frame_header,
    text=f"Campanha Co-op via Radmin VPN | Auto-Updater Git Ativo (v{local_ver_info.get('version', '1.0.0')})",
    font=("Segoe UI", 9),
    fg="#38bdf8",
    bg="#181b22"
)
lbl_version.pack(pady=(2, 0))

# Painel de Configuracao Radmin e Deck Local
frame_cfg = tk.LabelFrame(
    root,
    text=" 1. Conexao Radmin VPN & Seu Deck Proprio ",
    font=("Segoe UI", 9, "bold"),
    fg="#38bdf8",
    bg="#181b22",
    padx=14,
    pady=10
)
frame_cfg.pack(fill="x", padx=20, pady=(12, 8))

row1 = tk.Frame(frame_cfg, bg="#181b22")
row1.pack(fill="x", pady=3)
tk.Label(row1, text="IP do Host no Radmin VPN:", font=("Segoe UI", 9), fg="#e2e8f0", bg="#181b22").pack(side="left")
entry_ip = tk.Entry(row1, font=("Consolas", 10, "bold"), width=18, bg="#0f1115", fg="#38bdf8", insertbackground="#ffffff")
entry_ip.insert(0, DEFAULT_RADMIN_IP)
entry_ip.pack(side="left", padx=8)

tk.Button(
    row1,
    text="📋 Copiar IP",
    font=("Segoe UI", 8, "bold"),
    bg="#262b36",
    fg="#38bdf8",
    relief="flat",
    cursor="hand2",
    command=copy_radmin_ip
).pack(side="left", padx=4)

tk.Button(
    row1,
    text="🔄 Verificar Att (Git)",
    font=("Segoe UI", 8, "bold"),
    bg="#1e293b",
    fg="#4ade80",
    relief="flat",
    cursor="hand2",
    command=lambda: run_auto_update_check(force=False)
).pack(side="right", padx=4)

row2 = tk.Frame(frame_cfg, bg="#181b22")
row2.pack(fill="x", pady=3)
tk.Label(row2, text="Seu Nome / Apelido:", font=("Segoe UI", 9), fg="#e2e8f0", bg="#181b22").pack(side="left")
entry_name = tk.Entry(row2, font=("Segoe UI", 9), width=18, bg="#0f1115", fg="#ffffff", insertbackground="#ffffff")
local_deck = coop_engine.read_local_player_deck()
entry_name.insert(0, local_deck["player_name"] if local_deck else "Jogador")
entry_name.pack(side="left", padx=8)

deck_str = (
    f"Deck no Save: {local_deck['deck_name']} ({local_deck['card_count']} cartas)"
    if local_deck else "Deck no Save: Padrao"
)
tk.Label(row2, text=deck_str, font=("Segoe UI", 8, "bold"), fg="#22c55e", bg="#181b22").pack(side="right")

# Painel de Escolha: HOST ou PARCEIRO
frame_modes = tk.LabelFrame(
    root,
    text=" 2. Iniciar Jogo (PPSSPP Incluso na Pasta!) ",
    font=("Segoe UI", 9, "bold"),
    fg="#f59e0b",
    bg="#181b22",
    padx=14,
    pady=10
)
frame_modes.pack(fill="x", padx=20, pady=6)

btn_host = tk.Button(
    frame_modes,
    text="👑  CRIAR SALA CO-OP E ABRIR JOGO (SOU O HOST)",
    font=("Segoe UI", 11, "bold"),
    bg="#b91c1c",
    fg="#ffffff",
    activebackground="#dc2626",
    activeforeground="#ffffff",
    relief="flat",
    cursor="hand2",
    pady=8,
    command=start_as_host
)
btn_host.pack(fill="x", pady=(0, 6))

btn_partner = tk.Button(
    frame_modes,
    text="🤝  ENTRAR NA CAMPANHA DO HOST (SOU O AMIGO / PARCEIRO)",
    font=("Segoe UI", 11, "bold"),
    bg="#0284c7",
    fg="#ffffff",
    activebackground="#0369a1",
    activeforeground="#ffffff",
    relief="flat",
    cursor="hand2",
    pady=8,
    command=start_as_partner
)
btn_partner.pack(fill="x")

# Painel de Comandos de Sincronizacao de Turno e Deck
frame_sync = tk.LabelFrame(
    root,
    text=" 3. Sincronizacao de Campanha, Turnos e Deck (Atalhos no Jogo: F5, F6, F7) ",
    font=("Segoe UI", 9, "bold"),
    fg="#22c55e",
    bg="#181b22",
    padx=14,
    pady=10
)
frame_sync.pack(fill="x", padx=20, pady=6)

lbl_net_status = tk.Label(
    frame_sync,
    text="Status: Pronto para iniciar (Escolha Host ou Parceiro acima)",
    font=("Segoe UI", 9, "bold"),
    fg="#eab308",
    bg="#181b22"
)
lbl_net_status.pack(anchor="w", pady=(0, 6))

row_sync_btns = tk.Frame(frame_sync, bg="#181b22")
row_sync_btns.pack(fill="x", pady=2)

tk.Button(
    row_sync_btns,
    text="🔄 [F5] Sincronizar Tela/Estado",
    font=("Segoe UI", 9, "bold"),
    bg="#262b36",
    fg="#ffffff",
    relief="flat",
    cursor="hand2",
    pady=5,
    command=sync_now_host
).pack(side="left", fill="x", expand=True, padx=(0, 4))

tk.Button(
    row_sync_btns,
    text="👉 [F6] Passar Vez p/ Amigo",
    font=("Segoe UI", 9, "bold"),
    bg="#1e3a8a",
    fg="#38bdf8",
    relief="flat",
    cursor="hand2",
    pady=5,
    command=pass_turn_to_partner
).pack(side="left", fill="x", expand=True, padx=4)

tk.Button(
    row_sync_btns,
    text="👈 [F7] Fim do Turno do Amigo",
    font=("Segoe UI", 9, "bold"),
    bg="#14532d",
    fg="#4ade80",
    relief="flat",
    cursor="hand2",
    pady=5,
    command=return_turn_to_host
).pack(side="left", fill="x", expand=True, padx=(4, 0))

row_deck_btn = tk.Frame(frame_sync, bg="#181b22")
row_deck_btn.pack(fill="x", pady=(6, 0))

tk.Button(
    row_deck_btn,
    text="🃏 Enviar / Atualizar Meu Deck Proprio no Parceiro da Campanha",
    font=("Segoe UI", 9),
    bg="#262b36",
    fg="#f59e0b",
    relief="flat",
    cursor="hand2",
    pady=4,
    command=send_deck_clicked
).pack(side="left", fill="x", expand=True, padx=(0, 4))

tk.Button(
    row_deck_btn,
    text="⚙ Cheats",
    font=("Segoe UI", 9),
    bg="#262b36",
    fg="#cbd5e1",
    relief="flat",
    cursor="hand2",
    pady=4,
    command=open_cheats
).pack(side="left", padx=4)

tk.Button(
    row_deck_btn,
    text="📖 Guia",
    font=("Segoe UI", 9),
    bg="#262b36",
    fg="#cbd5e1",
    relief="flat",
    cursor="hand2",
    pady=4,
    command=open_guide
).pack(side="left")

# Log de Eventos
frame_log = tk.LabelFrame(
    root,
    text=" Registro de Conexao, Sincronizacao e Auto-Updater Git ",
    font=("Segoe UI", 8, "bold"),
    fg="#94a3b8",
    bg="#181b22",
    padx=10,
    pady=6
)
frame_log.pack(fill="both", expand=True, padx=20, pady=(6, 12))

txt_log = tk.Text(
    frame_log,
    height=7,
    font=("Consolas", 8),
    bg="#0b0d11",
    fg="#a7f3d0",
    relief="flat",
    state="disabled"
)
txt_log.pack(fill="both", expand=True)

append_log(f"[Sistema] Pacote Co-op Completo pronto! Emulador PPSSPP incluso na pasta.")
append_log("[Dica] No jogo: F5 sincroniza a partida | F6 passa o turno para o Amigo | F7 devolve para o Host.")

# Verifica automaticamente no GitHub ao abrir o Launcher
root.after(400, lambda: run_auto_update_check(force=False))

root.mainloop()
