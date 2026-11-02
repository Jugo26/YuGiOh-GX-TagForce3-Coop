import os
import sys
import json
import time
import urllib.request
import urllib.error
import subprocess

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOCAL_VERSION_FILE = os.path.join(BASE_DIR, "VERSION.json")

DEFAULT_REPO = "Jugo26/YuGiOh-GX-TagForce3-Coop"
DEFAULT_BRANCH = "main"


def parse_version(v_str):
    try:
        return tuple(int(x) for x in str(v_str).strip().lstrip("vV").split("."))
    except Exception:
        return (0, 0, 0)


def get_local_version_info():
    if os.path.exists(LOCAL_VERSION_FILE):
        try:
            with open(LOCAL_VERSION_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {
        "version": "1.0.0",
        "repo": DEFAULT_REPO,
        "branch": DEFAULT_BRANCH,
        "files": [
            "VERSION.json",
            "coop_engine.py",
            "auto_updater.py",
            "Launcher.pyw",
            "Abrir_Launcher.bat",
            "JOGAR.bat",
            "ULES01183.ini",
            "COMO_JOGAR_COOP.txt",
        ],
    }


def fetch_remote_version_info(repo=DEFAULT_REPO, branch=DEFAULT_BRANCH):
    url = f"https://raw.githubusercontent.com/{repo}/{branch}/VERSION.json?t={int(time.time())}"
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": "YuGiOh-GX-TagForce3-Coop-Updater/1.0",
            "Cache-Control": "no-cache",
        },
    )
    with urllib.request.urlopen(req, timeout=5.0) as resp:
        raw = resp.read().decode("utf-8")
        return json.loads(raw)


def download_remote_file(repo, branch, rel_path):
    url = f"https://raw.githubusercontent.com/{repo}/{branch}/{rel_path}?t={int(time.time())}"
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": "YuGiOh-GX-TagForce3-Coop-Updater/1.0",
            "Cache-Control": "no-cache",
        },
    )
    with urllib.request.urlopen(req, timeout=8.0) as resp:
        data = resp.read()

    dest_path = os.path.join(BASE_DIR, rel_path)
    os.makedirs(os.path.dirname(dest_path), exist_ok=True)
    tmp_path = dest_path + ".tmp"
    with open(tmp_path, "wb") as f:
        f.write(data)
    os.replace(tmp_path, dest_path)


def check_and_apply_updates(on_log=None, force=False):
    """
    Verifica no GitHub se existe uma versao mais recente do Modo Co-op.
    Se houver, baixa automaticamente todos os arquivos atualizados.
    Retorna: (updated: bool, new_version: str, message: str)
    """
    log = on_log or (lambda m: None)
    local_info = get_local_version_info()
    local_ver = local_info.get("version", "1.0.0")
    repo = local_info.get("repo", DEFAULT_REPO)
    branch = local_info.get("branch", DEFAULT_BRANCH)

    try:
        log(f"[Auto-Update] Verificando atualizacoes no GitHub ({repo})...")
        remote_info = fetch_remote_version_info(repo, branch)
        remote_ver = remote_info.get("version", local_ver)

        if not force and parse_version(remote_ver) <= parse_version(local_ver):
            msg = f"Voce ja esta na versao mais recente (v{local_ver})."
            log(f"[Auto-Update] {msg}")
            return False, local_ver, msg

        log(f"[Auto-Update] Nova atualizacao encontrada no GitHub: v{remote_ver} (Atual: v{local_ver})!")
        files_to_update = remote_info.get("files", local_info.get("files", []))

        for rel_file in files_to_update:
            if rel_file == "VERSION.json":
                continue
            log(f"[Auto-Update] Baixando {rel_file}...")
            download_remote_file(repo, branch, rel_file)

        # Por ultimo atualiza o VERSION.json
        download_remote_file(repo, branch, "VERSION.json")

        changelog = remote_info.get("changelog", "")
        msg = f"Atualizado com sucesso para v{remote_ver}! {changelog}"
        log(f"[Auto-Update] {msg}")
        return True, remote_ver, msg

    except urllib.error.HTTPError as e:
        msg = f"Repositorio GitHub verificado (HTTP {e.code} - v{local_ver} ativa)."
        log(f"[Auto-Update] {msg}")
        return False, local_ver, msg
    except Exception as e:
        msg = f"Sem conexao com GitHub no momento (Modo Offline v{local_ver} ativo)."
        log(f"[Auto-Update] {msg}")
        return False, local_ver, msg
