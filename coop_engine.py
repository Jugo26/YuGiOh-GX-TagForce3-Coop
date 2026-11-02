import os
import sys
import time
import json
import zlib
import base64
import socket
import struct
import hashlib
import threading
import ctypes
from ctypes import wintypes

# ============================================================================
# CONSTANTES DO JOGO (Yu-Gi-Oh! GX Tag Force 3 - ULES-01183)
# ============================================================================
GAME_ID = "ULES01183"
COOP_PORT = 27315
WS_PORT = 45450

# Offsets dentro do arquivo SAVEDATA/ULES011830001/DATA0 (e estrutura na RAM)
DATA0_PLAYER_DECK_OFFSET = 0x4A40
DATA0_PARTNER_DECK_START = 0x4BAC
DATA0_DECK_SIZE = 0x16C          # 364 bytes por deck
DATA0_PARTNER_COUNT = 125        # 125 slots de parceiros (0x4BAC ate 0xFC1E)
DATA0_PLAYER_NAME_OFFSET = 0xFD5C
DATA0_TEAM_NAME_OFFSET = 0xFD8E
DATA0_DP_OFFSET = 0xFDFC

# Ponteiros na RAM do PSP (ULES-01183)
PSP_STORY_PTR_ADDR = 0x08ECD964  # [0x08ECD964] + 0x7018 == Offset 0xFDFC do DATA0
PSP_DATA0_BASE_DELTA = 0x8DE4    # Base da estrutura DATA0 na RAM = [0x08ECD964] - 0x8DE4
PSP_PARTNER_AI_FLAG = 0x09E02998 # 0 = Controle Humano no Parceiro, 1 = IA

# Teclas Virtual-Key do Windows (padrao controls.ini do PPSSPP)
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

VK_F2 = 0x71  # Save State no PPSSPP
VK_F4 = 0x73  # Load State no PPSSPP
VK_F5 = 0x74  # Hotkey: Host sincroniza estado -> Amigo
VK_F6 = 0x75  # Hotkey: Passar Turno para o Amigo (Parceiro)
VK_F7 = 0x76  # Hotkey: Amigo finaliza Turno e devolve estado -> Host

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32


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
        "cross": bool(w & 0x1000),    # A no Xbox
        "circle": bool(w & 0x2000),   # B no Xbox
        "square": bool(w & 0x4000),   # X no Xbox
        "triangle": bool(w & 0x8000), # Y no Xbox
    }


# ============================================================================
# FUNCOES DE JANELA DO PPSSPP (Windows API)
# ============================================================================
def find_ppsspp_hwnd():
    """Encontra o HWND da janela principal do PPSSPP."""
    result = []

    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def callback(hwnd, lparam):
        if user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length > 0:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, buf, length + 1)
                title = buf.value
                if "PPSSPP" in title and "Launcher" not in title:
                    result.append(hwnd)
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)
    return result[0] if result else None


def is_ppsspp_focused():
    hwnd = find_ppsspp_hwnd()
    if not hwnd:
        return False
    fg = user32.GetForegroundWindow()
    return fg == hwnd


def send_key_to_ppsspp(vk_code):
    """Envia uma tecla (ex: F2 Save State ou F4 Load State) direto para a janela do PPSSPP."""
    hwnd = find_ppsspp_hwnd()
    if not hwnd:
        return False
    scan = user32.MapVirtualKeyW(vk_code, 0)
    lparam_down = (scan << 16) | 1
    lparam_up = (scan << 16) | 0xC0000001
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk_code, lparam_down)
    time.sleep(0.03)
    user32.PostMessageW(hwnd, WM_KEYUP, vk_code, lparam_up)
    return True


def read_local_psp_buttons():
    """Le os botoes pressionados no teclado/controle local apenas quando o PPSSPP esta em foco."""
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
# CLIENTE WEBSOCKET NATIVO (Puro Python stdlib para ws://127.0.0.1:45450/debugger)
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
        raw = json.dumps(payload).encode("utf-8")
        mask_key = os.urandom(4)
        header = bytearray([0x81])  # FIN + Text frame
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

    def write_memory(self, address, data_bytes):
        b64 = base64.b64encode(data_bytes).decode("ascii")
        return self.send_json({
            "event": "memory.write",
            "address": address,
            "base64": b64
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
    """
    Le o Deck Principal (364 bytes em 0x4A40) e o Nome do Jogador (0xFD5C)
    do arquivo SAVEDATA/ULES011830001/DATA0 local.
    """
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
            "deck_b64": base64.b64encode(deck_bytes).decode("ascii"),
        }
    except Exception:
        return None


def inject_partner_deck_into_host_save(deck_b64, friend_name="Amigo"):
    """
    Injeta o Deck de 364 bytes do Amigo em TODOS os slots de Parceiros (0x4BAC..0xFC1E)
    no arquivo DATA0 do Host, para que qualquer parceiro na campanha use o deck do Amigo!
    """
    dirs = get_ppsspp_dirs()
    data0_path = os.path.join(dirs["savedata"], "DATA0")
    if not os.path.exists(data0_path):
        return False, "Save DATA0 nao encontrado no Host."
    try:
        deck_bytes = bytearray(base64.b64decode(deck_b64))
        if len(deck_bytes) != DATA0_DECK_SIZE:
            return False, "Tamanho de deck invalido."

        with open(data0_path, "rb") as f:
            save_data = bytearray(f.read())

        # Preserva os 4 bytes de flags de personagem de cada slot (bytes 68..76) ou mantem estrutura
        for i in range(DATA0_PARTNER_COUNT):
            off = DATA0_PARTNER_DECK_START + (i * DATA0_DECK_SIZE)
            if off + DATA0_DECK_SIZE <= len(save_data):
                # Copia as cartas (bytes 76..364) e o nome do deck (bytes 0..64) do amigo
                save_data[off : off + 64] = deck_bytes[0:64]
                save_data[off + 76 : off + DATA0_DECK_SIZE] = deck_bytes[76:DATA0_DECK_SIZE]

        with open(data0_path, "wb") as f:
            f.write(save_data)

        return True, f"Deck de {friend_name} aplicado em todos os parceiros da campanha!"
    except Exception as e:
        return False, str(e)


def configure_ppsspp_for_coop(is_host=True, host_ip="26.196.166.86"):
    """
    Configura automaticamente ppsspp.ini, controls.ini, SAVEDATA e ULES01183.ini
    para que o Modo Co-op via Radmin funcione sem configuracao manual em qualquer PC.
    """
    import shutil
    import random
    import re

    base_folder = os.path.dirname(os.path.abspath(__file__))
    dirs = get_ppsspp_dirs()
    os.makedirs(dirs["system"], exist_ok=True)
    os.makedirs(dirs["cheats"], exist_ok=True)
    os.makedirs(dirs["state"], exist_ok=True)
    os.makedirs(os.path.dirname(dirs["savedata"]), exist_ok=True)

    # 0. Se o PC do amigo ainda nao tiver Save ou SYSTEM, instala da base inclusa na pasta
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

    # 1. Configura ppsspp.ini
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

        # Se for o Amigo (Parceiro) e estiver com o mesmo MAC padrao do Host, gera MAC unico
        if not is_host and "58:87:b8:25:4f:2d" in content:
            new_mac = ":".join(f"{random.randint(0, 255):02x}" for _ in range(6))
            content = content.replace("58:87:b8:25:4f:2d", new_mac)

        # Ajusta servidor AdHoc e IP do Radmin
        content = re.sub(r"EnableAdhocServer\s*=\s*\w+", f"EnableAdhocServer = {'True' if is_host else 'False'}", content)
        content = re.sub(r"proAdhocServer\s*=\s*[^\r\n]+", f"proAdhocServer = {host_ip}", content)
        content = re.sub(r"RemoteISOPort\s*=\s*\d+", f"RemoteISOPort = {WS_PORT}", content)

        with open(ppsspp_ini, "w", encoding="utf-8") as f:
            f.write(content)

    # 2. Configura controls.ini (Adiciona Save State = F2 [1-132] e Load State = F4 [1-134])
    controls_ini = os.path.join(dirs["system"], "controls.ini")
    if os.path.exists(controls_ini):
        with open(controls_ini, "r", encoding="utf-8", errors="ignore") as f:
            c_text = f.read()
        if "Save State" not in c_text:
            c_text = c_text.rstrip() + "\r\nSave State = 1-132\r\n"
        if "Load State" not in c_text:
            c_text = c_text.rstrip() + "\r\nLoad State = 1-134\r\n"
        with open(controls_ini, "w", encoding="utf-8") as f:
            f.write(c_text)

    # 3. Garante o cheat ULES01183.ini com Controle Manual do Parceiro
    cheat_path = os.path.join(dirs["cheats"], "ULES01183.ini")
    cheat_content = (
        "_S ULES-01183\n"
        "_G Yu-Gi-Oh! GX Tag Force 3 (Europe) [PT-BR]\n\n"
        "_C1 Modo Co-op (Controlar Parceiro no Duelo)\n"
        "_L 0x21602998 0x00000000\n\n"
        "_C1 Todas as cartas - 3 copias\n"
        "_L 0x402B7000 0x0FFF0001\n"
        "_L 0x00030304 0x00000000\n\n"
        "_C1 Todas as receitas\n"
        "_L 0x606CD964 0x0000011F\n"
        "_L 0x000401CD 0x00005F06\n"
        "_L 0x90000001 0x00000000\n\n"
        "_C1 Desbloquear Decks de Parceiros\n"
        "_L 0xC02A5684 0x00000001\n"
        "_L 0x602A5680 0x00000015\n"
        "_L 0x00000002 0x0000517D\n"
        "_L 0x90000002 0x00000000\n"
        "_L 0x602A5680 0x00000015\n"
        "_L 0x0000000F 0x00005183\n"
        "_L 0x90000002 0x00000000\n"
    )
    with open(cheat_path, "w", encoding="utf-8") as f:
        f.write(cheat_content)


# ============================================================================
# PROTOCOLO TCP SOBRE RADMIN VPN (Mensagens com Prefixo de 4 bytes de Tamanho)
# ============================================================================
def send_packet(sock, msg_dict):
    raw = json.dumps(msg_dict).encode("utf-8")
    compressed = zlib.compress(raw, level=1)
    header = struct.pack(">I", len(compressed))
    sock.sendall(header + compressed)


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def recv_packet(sock):
    hdr = recv_exact(sock, 4)
    if not hdr:
        return None
    (size,) = struct.unpack(">I", hdr)
    if size > 50 * 1024 * 1024:
        return None
    comp = recv_exact(sock, size)
    if not comp:
        return None
    raw = zlib.decompress(comp)
    return json.loads(raw.decode("utf-8"))


# ============================================================================
# MOTOR DE SINCRONIZACAO CO-OP (HOST & CLIENTE PARCEIRO)
# ============================================================================
class RadminCoopSession:
    def __init__(self, on_status_change=None, on_log=None):
        self.on_status_change = on_status_change or (lambda s: None)
        self.on_log = on_log or (lambda m: None)
        self.running = False
        self.role = None  # "host" ou "partner"
        self.active_turn = "host"  # "host" ou "partner"
        self.peer_sock = None
        self.server_sock = None
        self.ws = PPSSPPWebSocket()
        self.partner_info = None
        self.last_sent_buttons = {}

    def log(self, msg):
        self.on_log(msg)

    def set_status(self, text):
        self.on_status_change(text)

    def get_state_file_path(self):
        dirs = get_ppsspp_dirs()
        return os.path.join(dirs["state"], "ULES01183_1.00_0.ppst")

    def capture_and_send_savestate(self, new_turn=None):
        """
        Aciona F2 (Save State) no PPSSPP local, aguarda a gravacao do arquivo .ppst
        e envia pela rede Radmin para o outro jogador carregar automaticamente (F4)!
        """
        if not self.peer_sock:
            self.log("[Aviso] Nenhum amigo conectado na sala ainda.")
            return False

        state_path = self.get_state_file_path()
        old_mtime = os.path.getmtime(state_path) if os.path.exists(state_path) else 0

        if not send_key_to_ppsspp(VK_F2):
            self.log("[Aviso] Janela do PPSSPP nao encontrada para salvar estado.")
            return False

        # Aguarda ate 1.2s para o PPSSPP terminar de gravar o .ppst
        deadline = time.time() + 1.2
        while time.time() < deadline:
            if os.path.exists(state_path) and os.path.getmtime(state_path) > old_mtime:
                time.sleep(0.08)
                break
            time.sleep(0.03)

        if not os.path.exists(state_path):
            self.log("[Erro] Arquivo de estado (.ppst) nao foi gerado pelo PPSSPP.")
            return False

        try:
            with open(state_path, "rb") as f:
                raw_state = f.read()
            if new_turn:
                self.active_turn = new_turn
            pkt = {
                "type": "savestate",
                "turn": self.active_turn,
                "state_b64": base64.b64encode(raw_state).decode("ascii"),
            }
            send_packet(self.peer_sock, pkt)
            turn_label = "SEU AMIGO (Parceiro)" if self.active_turn == "partner" else "HOST (Voce)"
            self.log(f"[Sync] Partida sincronizada ({len(raw_state)//1024} KB)! Vez ativa: {turn_label}")
            self.set_status(f"Conectado | Vez Ativa: {turn_label}")
            return True
        except Exception as e:
            self.log(f"[Erro Sync] Falha ao enviar estado: {e}")
            return False

    def apply_received_savestate(self, state_b64, turn):
        """Salva o .ppst recebido do Radmin VPN e aciona F4 (Load State) no PPSSPP local."""
        state_path = self.get_state_file_path()
        try:
            raw_state = base64.b64decode(state_b64)
            os.makedirs(os.path.dirname(state_path), exist_ok=True)
            with open(state_path, "wb") as f:
                f.write(raw_state)
            self.active_turn = turn
            time.sleep(0.04)
            send_key_to_ppsspp(VK_F4)
            turn_label = "PARCEIRO (Amigo)" if self.active_turn == "partner" else "HOST"
            self.log(f"[Sync] Estado da Campanha carregado no seu PPSSPP! Vez ativa: {turn_label}")
            self.set_status(f"Sincronizado | Vez Ativa: {turn_label}")
        except Exception as e:
            self.log(f"[Erro LoadState] {e}")

    def start_host(self, bind_ip="0.0.0.0", port=COOP_PORT):
        if self.running:
            return
        self.running = True
        self.role = "host"
        self.active_turn = "host"
        threading.Thread(target=self._host_Loop, args=(bind_ip, port), daemon=True).start()
        threading.Thread(target=self._input_and_hotkey_loop, daemon=True).start()

    def _host_Loop(self, bind_ip, port):
        try:
            srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            srv.bind((bind_ip, port))
            srv.listen(1)
            self.server_sock = srv
            self.set_status(f"Aguardando Amigo conectar na porta {port}...")
            self.log(f"[Servidor Radmin] Sala Co-op aberta! Aguardando seu amigo conectar...")

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
                self.set_status(f"Amigo Conectado ({addr[0]}) | Vez: HOST")
                self._peer_receive_loop(client)
        except Exception as e:
            self.log(f"[Erro Servidor] {e}")
            self.set_status(f"Erro no servidor: {e}")

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

                # Envia automaticamente o Deck proprio do Amigo (lido do DATA0 local)
                deck_info = read_local_player_deck()
                if deck_info:
                    if custom_name:
                        deck_info["player_name"] = custom_name
                    send_packet(s, {
                        "type": "partner_deck",
                        "info": deck_info
                    })
                    self.log(
                        f"[Deck Enviado] Seu deck '{deck_info['deck_name']}' "
                        f"({deck_info['card_count']} cartas) foi sincronizado com o Parceiro da Campanha!"
                    )

                self._peer_receive_loop(s)
            except Exception as e:
                if self.running:
                    self.set_status(f"Aguardando Host ({host_ip})... Reconectando em 3s")
                    time.sleep(3.0)

    def send_my_deck_now(self, custom_name=""):
        if not self.peer_sock:
            self.log("[Aviso] Conecte-se ao Host primeiro.")
            return
        deck_info = read_local_player_deck()
        if not deck_info:
            self.log("[Erro] Nao foi possivel ler seu deck em SAVEDATA/ULES011830001/DATA0.")
            return
        if custom_name:
            deck_info["player_name"] = custom_name
        try:
            send_packet(self.peer_sock, {
                "type": "partner_deck",
                "info": deck_info
            })
            self.log(f"[Deck Sincronizado] Deck '{deck_info['deck_name']}' ({deck_info['card_count']} cartas) enviado ao Host!")
        except Exception as e:
            self.log(f"[Erro] {e}")

    def _peer_receive_loop(self, sock):
        try:
            while self.running:
                msg = recv_packet(sock)
                if not msg:
                    break
                mtype = msg.get("type")

                if mtype == "partner_deck" and self.role == "host":
                    info = msg.get("info", {})
                    self.partner_info = info
                    ok, status_msg = inject_partner_deck_into_host_save(
                        info.get("deck_b64", ""),
                        info.get("player_name", "Amigo")
                    )
                    self.log(f"[Deck do Amigo] {status_msg} (Deck: {info.get('deck_name')} - {info.get('card_count')} cartas)")
                    # Garante tambem flag de controle manual do parceiro na RAM via WebSocket
                    self.ws.write_u32(PSP_PARTNER_AI_FLAG, 0)

                elif mtype == "savestate":
                    self.apply_received_savestate(msg["state_b64"], msg.get("turn", "host"))

                elif mtype == "buttons":
                    btns = msg.get("buttons", {})
                    # Injeta os botoes recebidos via Radmin direto no PPSSPP local via WebSocket
                    self.ws.send_buttons(btns)

                elif mtype == "request_sync" and self.role == "host":
                    self.capture_and_send_savestate(new_turn=msg.get("turn", self.active_turn))

        except Exception as e:
            self.log(f"[Desconectado] Conexao encerrada: {e}")
        finally:
            self.peer_sock = None
            if self.running:
                self.set_status("Amigo desconectado. Aguardando reconexao...")

    def _input_and_hotkey_loop(self):
        """
        Monitora:
        - F5: Sincronizar Estado do Host -> Amigo
        - F6: Passar Vez/Controle para o Amigo (Sincroniza .ppst e ativa turno do Parceiro)
        - F7: Amigo devolve Vez/Estado para o Host (Sincroniza .ppst do Amigo -> Host)
        - Botoes do Jogo: Transmite os comandos do jogador da vez para o PPSSPP do outro jogador!
        """
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
                        self.capture_and_send_savestate(new_turn="host")
                    elif self.peer_sock:
                        send_packet(self.peer_sock, {"type": "request_sync", "turn": "host"})

                if f6_now and not f6_prev:
                    if self.role == "host":
                        self.capture_and_send_savestate(new_turn="partner")
                    elif self.peer_sock:
                        send_packet(self.peer_sock, {"type": "request_sync", "turn": "partner"})

                if f7_now and not f7_prev:
                    if self.role == "partner":
                        self.capture_and_send_savestate(new_turn="host")
                    elif self.role == "host":
                        self.capture_and_send_savestate(new_turn="host")

                f5_prev, f6_prev, f7_prev = f5_now, f6_now, f7_now

                # Se este jogador esta no turno ativo e com o PPSSPP focado, transmite botoes em tempo real
                if self.peer_sock and self.active_turn == self.role:
                    btns = read_local_psp_buttons()
                    if btns != self.last_sent_buttons:
                        self.last_sent_buttons = dict(btns)
                        send_packet(self.peer_sock, {
                            "type": "buttons",
                            "buttons": btns
                        })
            except Exception:
                pass

            time.sleep(0.016)  # ~60 Hz

    def stop(self):
        self.running = False
        for s in (self.peer_sock, self.server_sock):
            if s:
                try:
                    s.close()
                except Exception:
                    pass
