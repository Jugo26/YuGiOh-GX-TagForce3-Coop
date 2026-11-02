# Yu-Gi-Oh! GX Tag Force 3 [PT-BR] - Modo Campanha Co-op (Radmin VPN)

Sistema completo de **Campanha Co-op Direta via Radmin VPN** para *Yu-Gi-Oh! GX Tag Force 3 (ULES-01183)* no PPSSPP, com **Sincronização do Deck Próprio do Parceiro** e **Auto-Updater integrado via GitHub**.

## Recursos Principais

- **Sem necessidade de Parsec ou conectar controle:** Cada jogador abre o jogo no seu próprio PC pelo `Abrir_Launcher.bat`.
- **Deck Próprio do Amigo na Campanha:** O motor (`coop_engine.py`) lê automaticamente o Deck Principal (`364 bytes` em `0x4A40` do `DATA0`) do Save do amigo, envia pelo Radmin VPN e injeta em todos os parceiros da campanha do Host (`0x4BAC..0xFC1E`).
- **Controle Manual do Parceiro nos Duelos (`0x09E02998 = 0`):** Desativa a IA no turno do parceiro para que o amigo jogue o próprio turno.
- **Sincronização de Estado e Comandos (`F5`, `F6`, `F7`):**
  - `F5`: Sincroniza a partida do Host para o PPSSPP do Amigo.
  - `F6`: Passa a vez/turno do duelo para o Amigo jogar no PPSSPP dele.
  - `F7`: Amigo finaliza o turno dele e devolve o estado atualizado para o Host.
- **Auto-Updater GitHub (`auto_updater.py`):** Ao abrir o Launcher, verifica automaticamente no repositório `Jugo26/YuGiOh-GX-TagForce3-Coop` (`VERSION.json`) se há uma atualização disponível e baixa os novos arquivos sozinho.
