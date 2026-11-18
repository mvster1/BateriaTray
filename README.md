# BateriaTray

Mostra a bateria de mouses, headsets e teclados sem fio na bandeja do Windows, ao lado do relógio.
Cada dispositivo ganha um ícone com um símbolo pequeno e a porcentagem.

- **Leve**: cerca de 5 MB de RAM, CPU praticamente zero, nenhuma GPU. Usa só Win32 (sem WinForms nem WPF), roda numa única thread e lê os dispositivos uma vez por minuto.
- **Sem software do fabricante**: conversa direto com o receptor USB via HID. Os apps oficiais podem ficar fechados.

## Dispositivos suportados

Todos os protocolos usam só comandos de **leitura**: nada de configuração, luz ou DPI é alterado.

| Marca / protocolo | Modelos | Status |
|---|---|---|
| **Compx** (VID `3554`) | Kysona M600 / M600 V2 / M511 / Aztec, VXE / ATK (R1, R1 Pro Max), VGN Dragonfly, Lamzu Atlantis, GravaStar, Pulsar (receptores Compx), G-Wolves, Dark Project, Redragon M916 PRO / M913 Impact Elite / M991 / G49 | ✅ testado (Kysona M600) |
| **MCHOSE** | Headsets V9, V9 PRO, G9 PRO, S9 PRO | ✅ testado (V9 PRO) |
| **Logitech** (HID++ 2.0) | Mouses, teclados e headsets via Unifying, Lightspeed (ex.: PRO X2 SUPERSTRIKE, G502 X), Bolt ou cabo | ⚠️ não testado |
| **Razer** | Mouses e teclados sem fio | ⚠️ não testado |
| **HyperX** | Cloud III Wireless, Cloud III S, Cloud II Wireless, Cloud II Core, Cloud Alpha Wireless, Cloud Alpha 2 Wireless, Cloud Flight, Pulsefire Haste 2 Wireless / Core Wireless | ⚠️ não testado |
| **SteelSeries** | Arctis 1 / 7 / 9 / 7+, Arctis Nova 3P / 5 / 7 / Pro Wireless, Aerox 3 / 5 / 9 Wireless, Prime Wireless, Rival 3 Wireless, Rival 650 | ⚠️ não testado |
| **Corsair** | Slipstream (mouses e teclados), HS80, Virtuoso, Void V2 (protocolo "Bragi"); Void / Void Pro / Void Elite / HS70 | ⚠️ não testado |
| **Glorious** e afins | Model O / D / O- / D- Wireless, Model O 2 / D 2 PRO, Hitscan Hyperlight, G-Wolves, WLMouse YING | ⚠️ não testado |
| **LAMZU** | Maya, Maya X, Thorn, Inca, Paro, Tachi, Atlantis Mini / OG Champion, Orcus | ⚠️ não testado |
| **Pulsar** (chip Sonix) | X2 V3, X2 V3 eS e outros nos receptores 1K/8K | ⚠️ não testado |
| **Endgame Gear** | OP1w 4k, XM2w 4k (v1 e v2) | ⚠️ não testado |
| **Attack Shark / Delux** (receptor Beken `1D57`) | X3, X6, X11, R1, V3 Pro, V6, Delux M900 Pro | ⚠️ não testado |
| **Redragon** (receptor Areson `25A7`) | M913, M915 | ⚠️ não testado |
| **HID padrão** | Qualquer aparelho USB que informe bateria no formato padrão do HID | ⚠️ não testado |
| **Bluetooth** | Qualquer fone, mouse ou teclado que mostre a bateria em Configurações > Bluetooth | ⚠️ não testado |

"Não testado" quer dizer que o protocolo segue a documentação de projetos abertos (ver abaixo), mas ainda não foi testado com o aparelho aqui. Se funcionar (ou não) com o seu, abra uma issue com o `teste.txt` gerado pelo `--test`.

Aparelhos que podem não ter bateria (por exemplo um teclado Corsair com fio) só ganham ícone depois da primeira leitura válida. Cada aparelho tem o próprio ícone, com um símbolo de mouse, headset, teclado ou bateria.

**Ainda sem suporte** (protocolo não documentado): MSI, headsets Razer (Kraken, BlackShark, Barracuda), HyperX Pulsefire Haste (primeiro modelo), headset do Xbox e Redragon com chip Sinowealth ou Nordic.

## Cores

| | Significado |
|---|---|
| 🟢 número verde | 60% ou mais |
| 🟡 número amarelo | 21% a 59% |
| 🔴 número vermelho | 20% ou menos (notificação ao chegar em 15%) |
| símbolo azul | carregando |
| símbolo cinza | dormindo/desligado (o número mostra a última leitura) |
| `--` | ainda sem leitura |

100% aparece como **F**. A porcentagem exata, o status e o horário da última leitura ficam no tooltip.

## Instalação

1. Instale o [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0), caso ainda não tenha.
2. Baixe o `BateriaTray.zip` em [Releases](../../releases) e extraia numa pasta fixa.
3. Execute `BateriaTray.exe`.
4. Para abrir junto com o Windows: clique direito no ícone e marque **Iniciar com o Windows**.

## Uso

- **Clique esquerdo**: atualiza na hora.
- **Clique direito**: Atualizar agora, Procurar dispositivos, Mostrar ícone do dispositivo, Iniciar com o Windows, Sair.
- Dispositivos sem fio só respondem acordados. Se aparecer `--`, use o dispositivo e clique no ícone.
- Dispositivos Bluetooth pareados mas desconectados não aparecem.

## Compilar

```
dotnet publish -c Release -o app
```

Para testar sem abrir a bandeja, rode `BateriaTray.exe --test <pasta>`. Ele lista as interfaces HID encontradas e o resultado da leitura de cada dispositivo em `teste.txt`, e salva uma prévia dos ícones em `strip.bmp`.

## Como funciona

- A varredura lista as interfaces HID, compara os IDs com a tabela de cada protocolo e abre só as que interessam. Ela acontece ao iniciar, ao conectar um USB e a cada 5 minutos.
- Cada aparelho é lido uma vez por minuto, ou a cada 20 s enquanto não responde. Tudo roda numa única thread, sem timers extras.
- O código de cada protocolo está em [`Protocols.cs`](Protocols.cs) (Compx, MCHOSE, Logitech, Razer, Bluetooth) e [`Brands.cs`](Brands.cs) (demais marcas).

**Referências dos protocolos** (só os bytes foram consultados; o código foi escrito do zero): [HeadsetControl](https://github.com/Sapd/HeadsetControl), [Wireless-Device-Battery-Monitor](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor), [OpenRazer](https://github.com/openrazer/openrazer), [Solaar](https://github.com/pwr-Solaar/Solaar), [rivalcfg](https://github.com/flozz/rivalcfg), [ckb-next](https://github.com/ckb-next/ckb-next), [glorious-ctl](https://github.com/louis4craft/glorious-ctl), [PulsarBattery](https://github.com/darthsoup/PulsarBattery), [vole](https://github.com/johanneszab/vole) e [attackSharkOnLinux](https://github.com/KaiSta/attackSharkOnLinux).

Projeto independente, sem relação com nenhum dos fabricantes citados.
