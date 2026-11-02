import os
import sys
import time
import json
import gzip
import base64
import socket
import struct
import shutil
import random
import re
import threading
import ctypes
from ctypes import wintypes

# ============================================================================
# CONSTANTES DO JOGO (Yu-Gi-Oh! GX Tag Force 3 - ULES-01183 v1.02)
# ============================================================================
GAME_ID = "ULES01183"
COOP_PORT = 27315
WS_PORT = 45450

DATA0_PLAYER_DECK_OFFSET = 0x4A40
DATA0_PARTNER_DECK_START = 0x4BAC
DATA0_DECK_SIZE = 0x16C          # 364 bytes por deck
DATA0_PARTNER_COUNT = 125        # 125 slots de parceiros (0x4BAC ate 0xFC1E)
DATA0_PLAYER_NAME_OFFSET = 0xFD5C
PSP_PARTNER_AI_FLAG = 0x09E02998 # 0 = Controle Humano no Parceiro, 1 = IA

# Win32 Menu Commands nativos do PPSSPP (Menu ID 101)
WM_COMMAND = 0x0111
ID_FILE_QUICKSAVESTATE = 40034   # Save State direto no PPSSPP
ID_FILE_QUICKLOADSTATE = 40035   # Load State direto no PPSSPP

VK_MAP = {
    "up": 0x26,       # Seta Cima
    "down": 0x28,     # Seta Baixo
    "left": 0x25,     # Seta Esquerda
    "right": 0x27,    # Seta Direita
    "cross": 0x5A,    # Z (X do PSP / Confirmar)
    "circle": 0x58,   # X (Bola do PSP / Cancelar)
    "square": 0x41,   # A (Quadrado do PSP)
    "triangle": 0x53, # S (Triangulo do PSP)
    "ltrigger": 0x51, # Q (L do PSP)
    "rtrigger": 0x57, # W (R do PSP)
    "start": 0x20,    # Espaco (Start)
    "select": 0x56,   # V (Select)
}

VK_F5 = 0x74  # Hotkey: Host sincroniza estado -> Amigo
VK_F6 = 0x75  # Hotkey: Passar Turno para o Amigo (Parceiro)
VK_F7 = 0x76  # Hotkey: Amigo finaliza Turno e devolve estado -> Host

user32 = ctypes.windll.user32


# ============================================================================
# XINPUT (Suporte opcional caso o jogador use controle no proprio PC)
# ============================================================================
class XINPUT_GAMEPAD(ctypes.Structure):
    _fields_ = [
        ("wButtons", wintypes.WORD),
        ("bLeftTrigger", wintypes.BYTE),
        ("bRightTrigger", wintypes.BYTE),
        ("sThumbLX", wintypes.SHORT),
        ("sThumbLY", wintypes.SHORT),
        ("sThumbRX", wintypes.SHORT),
        ("sThumbRY", wintypes.SHORT),
    ]

class XINPUT_STATE(ctypes.Structure):
    _fields_ = [
        ("dwPacketNumber", wintypes.DWORD),
        ("Gamepad", XINPUT_GAMEPAD),
    ]

_xinput = None
for _dll in ("xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"):
    try:
        _xinput = ctypes.windll.LoadLibrary(_dll)
        break
    except Exception:
        pass


def read_xinput_buttons():
    if not _xinput:
        return {}
    state = XINPUT_STATE()
    res = _xinput.XInputGetState(0, ctypes.byref(state))
    if res != 0:
        return {}
    w = state.Gamepad.wButtons
    lx = state.Gamepad.sThumbLX
    ly = state.Gamepad.sThumbLY
    return {
        "up": bool(w & 0x0001) or (ly > 16000),
        "down": bool(w & 0x0002) or (ly < -16000),
        "left": bool(w & 0x0004) or (lx < -16000),
        "right": bool(w & 0x0008) or (lx > 16000),
        "start": bool(w & 0x0010),
        "select": bool(w & 0x0020),
        "ltrigger": bool(w & 0x0100),
        "rtrigger": bool(w & 0x0200),
        "cross": bool(w & 0x1000),
        "circle": bool(w & 0x2000),
        "square": bool(w & 0x4000),
        "triangle": bool(w & 0x8000),
    }


# ============================================================================
# FUNCOES DE JANELA DO PPSSPP (WM_COMMAND 40034 / 40035)
# ============================================================================
def find_ppsspp_hwnd():
    """Encontra o HWND da janela principal do PPSSPP (classe PPSSPPWnd)."""
    hwnd = user32.FindWindowW("PPSSPPWnd", None)
    if hwnd:
        return hwnd

    result = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def callback(h, lparam):
        if user32.IsWindowVisible(h):
            cls_buf = ctypes.create_unicode_buffer(128)
            user32.GetClassNameW(h, cls_buf, 128)
            if cls_buf.value == "PPSSPPWnd":
                result.append(h)
                return False
            length = user32.GetWindowTextLengthW(h)
            if length > 0:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(h, buf, length + 1)
                title = buf.value
                if ("PPSSPP" in title or "ULES01183" in title) and "Launcher" not in title:
                    result.append(h)
                    return False
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)
    return result[0] if result else None


def is_ppsspp_focused():
    hwnd = find_ppsspp_hwnd()
    if not hwnd:
        return False
    return user32.GetForegroundWindow() == hwnd


def trigger_ppsspp_savestate():
    """Envia o comando nativo ID_FILE_QUICKSAVESTATE (40034) para o PPSSPP."""
    hwnd = find_ppsspp_hwnd()
    if not hwnd:
        return False
    user32.PostMessageW(hwnd, WM_COMMAND, ID_FILE_QUICKSAVESTATE, 0)
    return True


def trigger_ppsspp_loadstate():
    """Envia o comando nativo ID_FILE_QUICKLOADSTATE (40035) para o PPSSPP."""
    hwnd = find_ppsspp_hwnd()
    if not hwnd:
        return False
    user32.PostMessageW(hwnd, WM_COMMAND, ID_FILE_QUICKLOADSTATE, 0)
    return True


def read_local_psp_buttons():
    if not is_ppsspp_focused():
        return {k: False for k in VK_MAP}
    btns = {}
    for name, vk in VK_MAP.items():
        btns[name] = bool(user32.GetAsyncKeyState(vk) & 0x8000)
    xbtns = read_xinput_buttons()
    for name, val in xbtns.items():
        if val:
            btns[name] = True
    return btns


# ============================================================================
# CLIENTE WEBSOCKET NATIVO (ws://127.0.0.1:45450/debugger)
# ============================================================================
class PPSSPPWebSocket:
    def __init__(self, host="127.0.0.1", port=WS_PORT):
        self.host = host
        self.port = port
        self.sock = None
        self.lock = threading.Lock()
        self.connected = False

    def connect(self):
        with self.lock:
            if self.connected and self.sock:
                return True
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(1.5)
                s.connect((self.host, self.port))
                key = base64.b64encode(os.urandom(16)).decode("ascii")
                req = (
                    f"GET /debugger HTTP/1.1\r\n"
                    f"Host: {self.host}:{self.port}\r\n"
                    f"Upgrade: websocket\r\n"
                    f"Connection: Upgrade\r\n"
                    f"Sec-WebSocket-Key: {key}\r\n"
                    f"Sec-WebSocket-Version: 13\r\n\r\n"
                )
                s.sendall(req.encode("ascii"))
                resp = s.recv(1024)
                if b"101" in resp:
                    s.settimeout(0.3)
                    self.sock = s
                    self.connected = True
                    return True
                s.close()
            except Exception:
                pass
            self.connected = False
            self.sock = None
            return False

    def send_json(self, payload):
        if not self.connected and not self.connect():
            return False
        raw = json.dumps(payload).encode("utf-8") if isinstance(payload, dict) else payload.encode("utf-8")
        mask_key = os.urandom(4)
        header = bytearray([0x81])
        length = len(raw)
        if length < 126:
            header.append(0x80 | length)
        elif length < 65536:
            header.append(0x80 | 126)
            header.extend(struct.pack(">H", length))
        else:
            header.append(0x80 | 127)
            header.extend(struct.pack(">Q", length))
        header.extend(mask_key)
        masked = bytearray(length)
        for i in range(length):
            masked[i] = raw[i] ^ mask_key[i & 3]

        with self.lock:
            try:
                self.sock.sendall(header + masked)
                return True
            except Exception:
                self.connected = False
                try:
                    self.sock.close()
                except Exception:
                    pass
                self.sock = None
                return False

    def send_buttons(self, buttons_dict):
        return self.send_json({
            "event": "input.buttons.send",
            "buttons": buttons_dict
        })

    def write_u32(self, address, value):
        return self.send_json({
            "event": "memory.write_u32",
            "address": address,
            "value": value
        })


# ============================================================================
# GERENCIADOR DE SAVE / DECKS E CONFIGURACAO AUTOMATICA DO PPSSPP
# ============================================================================
def get_ppsspp_dirs():
    docs = os.path.join(os.path.expanduser("~"), "Documents", "PPSSPP", "PSP")
    return {
        "base": docs,
        "system": os.path.join(docs, "SYSTEM"),
        "cheats": os.path.join(docs, "Cheats"),
        "savedata": os.path.join(docs, "SAVEDATA", "ULES011830001"),
        "state": os.path.join(docs, "PPSSPP_STATE"),
    }


def read_local_player_deck():
    dirs = get_ppsspp_dirs()
    data0_path = os.path.join(dirs["savedata"], "DATA0")
    if not os.path.exists(data0_path):
        return None
    try:
        with open(data0_path, "rb") as f:
            data = f.read()
        if len(data) < 0x10000:
            return None
        deck_bytes = data[DATA0_PLAYER_DECK_OFFSET : DATA0_PLAYER_DECK_OFFSET + DATA0_DECK_SIZE]
        deck_name = deck_bytes[:64].decode("utf-16le", errors="ignore").split("\x00")[0].strip()
        player_name = data[DATA0_PLAYER_NAME_OFFSET : DATA0_PLAYER_NAME_OFFSET + 32].decode("utf-16le", errors="ignore").split("\x00")[0].strip()
        cards = struct.unpack("<144H", deck_bytes[76:364])
        card_count = sum(1 for c in cards if c != 0)
        return {
            "player_name": player_name or "Parceiro",
            "deck_name": deck_name or "Deck Customizado",
            "card_count": card_count,
            "deck_bytes": deck_bytes,
            "deck_b64": base64.b64encode(deck_bytes).decode("ascii"),
        }
    except Exception:
        return None


def inject_partner_deck_into_host_save(deck_b64_or_bytes, friend_name="Amigo"):
    dirs = get_ppsspp_dirs()
    data0_path = os.path.join(dirs["savedata"], "DATA0")
    if not os.path.exists(data0_path):
        return False, "Save DATA0 nao encontrado no Host."
    try:
        if isinstance(deck_b64_or_bytes, (bytes, bytearray)):
            deck_bytes = bytearray(deck_b64_or_bytes)
        else:
            deck_bytes = bytearray(base64.b64decode(deck_b64_or_bytes))
        if len(deck_bytes) != DATA0_DECK_SIZE:
            return False, "Tamanho de deck invalido."

        with open(data0_path, "rb") as f:
            save_data = bytearray(f.read())

        for i in range(DATA0_PARTNER_COUNT):
            off = DATA0_PARTNER_DECK_START + (i * DATA0_DECK_SIZE)
            if off + DATA0_DECK_SIZE <= len(save_data):
                save_data[off : off + 64] = deck_bytes[0:64]
                save_data[off + 76 : off + DATA0_DECK_SIZE] = deck_bytes[76:DATA0_DECK_SIZE]

        with open(data0_path, "wb") as f:
            f.write(save_data)

        return True, f"Deck de {friend_name} injetado em todos os parceiros da campanha!"
    except Exception as e:
        return False, str(e)


def configure_ppsspp_for_coop(is_host=True, host_ip="26.196.166.86"):
    base_folder = os.path.dirname(os.path.abspath(__file__))
    dirs = get_ppsspp_dirs()
    os.makedirs(dirs["system"], exist_ok=True)
    os.makedirs(dirs["cheats"], exist_ok=True)
    os.makedirs(dirs["state"], exist_ok=True)
    os.makedirs(os.path.dirname(dirs["savedata"]), exist_ok=True)

    savedata_base = os.path.join(base_folder, "SAVEDATA_BASE", "ULES011830001")
    if not os.path.exists(os.path.join(dirs["savedata"], "DATA0")) and os.path.exists(savedata_base):
        shutil.copytree(savedata_base, dirs["savedata"], dirs_exist_ok=True)

    system_base = os.path.join(base_folder, "SYSTEM_BASE")
    ppsspp_ini = os.path.join(dirs["system"], "ppsspp.ini")
    controls_ini = os.path.join(dirs["system"], "controls.ini")

    if not os.path.exists(ppsspp_ini) and os.path.exists(os.path.join(system_base, "ppsspp.ini")):
        shutil.copy2(os.path.join(system_base, "ppsspp.ini"), ppsspp_ini)
    if not os.path.exists(controls_ini) and os.path.exists(os.path.join(system_base, "controls.ini")):
        shutil.copy2(os.path.join(system_base, "controls.ini"), controls_ini)

    if os.path.exists(ppsspp_ini):
        with open(ppsspp_ini, "r", encoding="utf-8", errors="ignore") as f:
            content = f.read()

        replacements = {
            "EnableCheats = False": "EnableCheats = True",
            "RemoteISOPort = 0": f"RemoteISOPort = {WS_PORT}",
            "RemoteISOManualConfig = False": "RemoteISOManualConfig = True",
            "RemoteDebuggerOnStartup = False": "RemoteDebuggerOnStartup = True",
            "RemoteDebuggerLocal = False": "RemoteDebuggerLocal = True",
            "PauseOnLostFocus = True": "PauseOnLostFocus = False",
            "EnableWlan = False": "EnableWlan = True",
        }
        for old, new in replacements.items():
            content = content.replace(old, new)

        if not is_host and "58:87:b8:25:4f:2d" in content:
            new_mac = ":".join(f"{random.randint(0, 255):02x}" for _ in range(6))
            content = content.replace("58:87:b8:25:4f:2d", new_mac)

        content = re.sub(r"EnableAdhocServer\s*=\s*\w+", f"EnableAdhocServer = {'True' if is_host else 'False'}", content)
        content = re.sub(r"proAdhocServer\s*=\s*[^\r\n]+", f"proAdhocServer = {host_ip}", content)
        content = re.sub(r"RemoteISOPort\s*=\s*\d+", f"RemoteISOPort = {WS_PORT}", content)
        content = re.sub(r"StateSlot\s*=\s*\d+", "StateSlot = 0", content)

        with open(ppsspp_ini, "w", encoding="utf-8") as f:
            f.write(content)

    cheat_path = os.path.join(dirs["cheats"], "ULES01183.ini")
    local_cheat = os.path.join(base_folder, "ULES01183.ini")
    if os.path.exists(local_cheat):
        shutil.copy2(local_cheat, cheat_path)


# ============================================================================
# PROTOCOLO BINARIO UNIFICADO (100% Compativel entre C# Launcher_Coop.exe e Python)
# Formato: [1 byte tipo][4 bytes tamanho big-endian][payload]
# ============================================================================
def send_frame(sock, frame_type, payload_bytes):
    if not sock:
        return
    hdr = struct.pack(">BI", frame_type, len(payload_bytes))
    sock.sendall(hdr + payload_bytes)


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def recv_frame(sock):
    hdr = recv_exact(sock, 5)
    if not hdr:
        return None, None
    ftype, size = struct.unpack(">BI", hdr)
    if size > 50 * 1024 * 1024:
        return None, None
    payload = recv_exact(sock, size) if size > 0 else b""
    if size > 0 and payload is None:
        return None, None
    return ftype, payload


# ============================================================================
# MOTOR DE SINCRONIZACAO CO-OP (HOST & CLIENTE PARCEIRO)
# ============================================================================
class RadminCoopSession:
    def __init__(self, on_status_change=None, on_log=None):
        self.on_status_change = on_status_change or (lambda s: None)
        self.on_log = on_log or (lambda m: None)
        self.running = False
        self.role = None
        self.active_turn = "host"
        self.peer_sock = None
        self.server_sock = None
        self.send_lock = threading.Lock()
        self.ws = PPSSPPWebSocket()
        self.last_btn_json = ""

    def log(self, msg):
        self.on_log(msg)

    def set_status(self, text):
        self.on_status_change(text)

    def find_newest_state_file(self, after_mtime):
        state_dir = get_ppsspp_dirs()["state"]
        if not os.path.exists(state_dir):
            return None
        best_file = None
        best_time = after_mtime
        for fname in os.listdir(state_dir):
            if fname.startswith("ULES01183") and fname.endswith(".ppst") and not fname.endswith("undo.ppst"):
                full = os.path.join(state_dir, fname)
                mt = os.path.getmtime(full)
                if mt > best_time:
                    best_time = mt
                    best_file = full
        return best_file

    def capture_and_send_savestate(self, new_turn="host"):
        if not self.peer_sock:
            self.log("[Aviso] Nenhum amigo conectado na sala ainda.")
            return False

        before_time = time.time() - 0.1
        if not trigger_ppsspp_savestate():
            self.log("[Aviso] Janela do PPSSPP nao encontrada. Abra o jogo primeiro.")
            return False

        found_state = None
        deadline = time.time() + 2.5
        while time.time() < deadline:
            found_state = self.find_newest_state_file(before_time)
            if found_state:
                time.sleep(0.12)
                break
            time.sleep(0.04)

        if not found_state:
            p102 = os.path.join(get_ppsspp_dirs()["state"], "ULES01183_1.02_0.ppst")
            if os.path.exists(p102):
                found_state = p102

        if not found_state or not os.path.exists(found_state):
            self.log("[Aviso] Certifique-se de que o jogo ja iniciou dentro do PPSSPP antes de sincronizar.")
            return False

        try:
            with open(found_state, "rb") as f:
                raw_state = f.read()
            self.active_turn = new_turn
            turn_byte = b"\x01" if new_turn == "partner" else b"\x00"
            payload = turn_byte + gzip.compress(raw_state, compresslevel=1)
            with self.send_lock:
                send_frame(self.peer_sock, 2, payload)
            t_label = "SEU AMIGO (Parceiro)" if self.active_turn == "partner" else "HOST (Voce)"
            self.log(f"[Sync OK] Campanha sincronizada ({len(raw_state)//1024} KB)! Vez ativa: {t_label}")
            self.set_status(f"Sincronizado | Vez Ativa: {t_label}")
            return True
        except Exception as e:
            self.log(f"[Erro Sync] {e}")
            return False

    def apply_received_savestate(self, payload):
        try:
            if not payload or len(payload) < 2:
                return
            self.active_turn = "partner" if payload[0] == 1 else "host"
            raw_state = gzip.decompress(payload[1:])
            state_dir = get_ppsspp_dirs()["state"]
            os.makedirs(state_dir, exist_ok=True)
            for fname in ("ULES01183_1.02_0.ppst", "ULES01183_1.00_0.ppst"):
                with open(os.path.join(state_dir, fname), "wb") as f:
                    f.write(raw_state)
            time.sleep(0.05)
            trigger_ppsspp_loadstate()
            t_label = "PARCEIRO (Sua Vez!)" if self.active_turn == "partner" else "HOST"
            self.log(f"[Sync OK] Campanha do Host carregada no seu PPSSPP! Vez ativa: {t_label}")
            self.set_status(f"Sincronizado | Vez Ativa: {t_label}")
        except Exception as e:
            self.log(f"[Erro LoadState] {e}")

    def start_host(self, bind_ip="0.0.0.0", port=COOP_PORT):
        if self.running:
            return
        self.running = True
        self.role = "host"
        self.active_turn = "host"
        threading.Thread(target=self._host_loop, args=(bind_ip, port), daemon=True).start()
        threading.Thread(target=self._input_and_hotkey_loop, daemon=True).start()

    def _host_loop(self, bind_ip, port):
        try:
            srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            srv.bind((bind_ip, port))
            srv.listen(1)
            self.server_sock = srv
            self.set_status(f"Aguardando Amigo conectar na porta {port}...")
            self.log(f"[Servidor Radmin] Sala Co-op aberta na porta {port}! Aguardando seu amigo...")

            while self.running:
                try:
                    srv.settimeout(1.0)
                    client, addr = srv.accept()
                except socket.timeout:
                    continue
                except Exception:
                    break

                client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                self.peer_sock = client
                self.log(f"[Conectado] Amigo conectou pelo Radmin VPN ({addr[0]})!")
                self.set_status(f"Amigo Conectado ({addr[0]}) | Sincronizando...")

                # Sincroniza automaticamente a partida do Host para o Amigo 1.5s apos conectar!
                threading.Thread(target=lambda: (time.sleep(1.5), self.capture_and_send_savestate("host")), daemon=True).start()

                self._peer_receive_loop(client)
        except Exception as e:
            self.log(f"[Erro Servidor] {e}")

    def start_partner(self, host_ip, port=COOP_PORT, custom_name=""):
        if self.running:
            return
        self.running = True
        self.role = "partner"
        self.active_turn = "host"
        threading.Thread(target=self._partner_loop, args=(host_ip, port, custom_name), daemon=True).start()
        threading.Thread(target=self._input_and_hotkey_loop, daemon=True).start()

    def _partner_loop(self, host_ip, port, custom_name):
        while self.running:
            try:
                self.set_status(f"Conectando ao Host ({host_ip}:{port})...")
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(4.0)
                s.connect((host_ip, port))
                s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                s.settimeout(None)
                self.peer_sock = s
                self.log(f"[Conectado] Conectado a Campanha do Host ({host_ip}) via Radmin VPN!")
                self.set_status(f"Conectado ao Host ({host_ip})!")
                self.send_my_deck_now(custom_name)
                self._peer_receive_loop(s)
            except Exception:
                if self.running:
                    self.set_status(f"Aguardando Host ({host_ip})... Tentando em 3s")
                    time.sleep(3.0)

    def send_my_deck_now(self, custom_name=""):
        if not self.peer_sock:
            self.log("[Aviso] Conecte-se ao Host primeiro.")
            return
        deck_info = read_local_player_deck()
        if not deck_info:
            return
        fname = custom_name or deck_info["player_name"]
        meta = f"{fname}|{deck_info['deck_name']}|{deck_info['card_count']}".encode("utf-8")
        pkt = deck_info["deck_bytes"] + meta
        try:
            with self.send_lock:
                send_frame(self.peer_sock, 1, pkt)
            self.log(f"[Deck Enviado] Seu deck '{deck_info['deck_name']}' ({deck_info['card_count']} cartas) foi sincronizado com o Parceiro da Campanha!")
        except Exception as e:
            self.log(f"[Erro] {e}")

    def _peer_receive_loop(self, sock):
        try:
            while self.running:
                ftype, payload = recv_frame(sock)
                if ftype is None:
                    break

                if ftype == 1 and len(payload) >= DATA0_DECK_SIZE:
                    deck_bytes = payload[:DATA0_DECK_SIZE]
                    meta = payload[DATA0_DECK_SIZE:].decode("utf-8", errors="ignore").split("|")
                    fname = meta[0] if len(meta) > 0 else "Amigo"
                    dname = meta[1] if len(meta) > 1 else "Deck"
                    ccount = meta[2] if len(meta) > 2 else "?"
                    ok, msg = inject_partner_deck_into_host_save(deck_bytes, f"{fname} ({dname} - {ccount} cartas)")
                    self.log(f"[Deck do Amigo] {msg}")
                    self.ws.write_u32(PSP_PARTNER_AI_FLAG, 0)

                elif ftype == 2:
                    self.apply_received_savestate(payload)

                elif ftype == 3:
                    ws_json = payload.decode("utf-8", errors="ignore")
                    self.ws.send_json(ws_json)

                elif ftype == 4 and self.role == "host":
                    req_turn = payload.decode("utf-8", errors="ignore") or "host"
                    self.capture_and_send_savestate(req_turn)

        except Exception as e:
            self.log(f"[Desconectado] Conexao encerrada: {e}")
        finally:
            self.peer_sock = None
            if self.running:
                self.set_status("Amigo desconectado. Aguardando reconexao...")

    def _input_and_hotkey_loop(self):
        f5_prev = False
        f6_prev = False
        f7_prev = False

        while self.running:
            try:
                f5_now = bool(user32.GetAsyncKeyState(VK_F5) & 0x8000)
                f6_now = bool(user32.GetAsyncKeyState(VK_F6) & 0x8000)
                f7_now = bool(user32.GetAsyncKeyState(VK_F7) & 0x8000)

                if f5_now and not f5_prev:
                    if self.role == "host":
                        self.capture_and_send_savestate("host")
                    elif self.peer_sock:
                        with self.send_lock:
                            send_frame(self.peer_sock, 4, b"host")

                if f6_now and not f6_prev:
                    if self.role == "host":
                        self.capture_and_send_savestate("partner")
                    elif self.peer_sock:
                        with self.send_lock:
                            send_frame(self.peer_sock, 4, b"partner")

                if f7_now and not f7_prev:
                    self.capture_and_send_savestate("host")

                f5_prev, f6_prev, f7_prev = f5_now, f6_now, f7_now

                if self.peer_sock and self.active_turn == self.role and is_ppsspp_focused():
                    btns = read_local_psp_buttons()
                    cur_json = json.dumps({"event": "input.buttons.send", "buttons": btns})
                    if cur_json != self.last_btn_json:
                        self.last_btn_json = cur_json
                        with self.send_lock:
                            send_frame(self.peer_sock, 3, cur_json.encode("utf-8"))
            except Exception:
                pass

            time.sleep(0.016)

    def stop(self):
        self.running = False
        for s in (self.peer_sock, self.server_sock):
            if s:
                try:
                    s.close()
                except Exception:
                    pass
