# BateriaTray

Mostra a bateria de mouses, headsets e teclados sem fio na bandeja do Windows, ao lado do relógio.
Cada dispositivo ganha um ícone com um símbolo pequeno e a porcentagem.

- **Leve**: cerca de 5 MB de RAM, CPU praticamente zero, nenhuma GPU. Usa só Win32 (sem WinForms nem WPF), roda numa única thread e lê os dispositivos uma vez por minuto.
- **Sem driver do fabricante**: conversa direto com o receptor USB via HID. O MCHOSE HUB e o driver da KYSOAN podem ficar fechados.

## Dispositivos suportados

Cada família de dispositivos tem seu protocolo. Todos usam só comandos de **leitura**.

| Protocolo | Dispositivos | Status |
|---|---|---|
| **Compx** (VID `3554`) | Mouses Kysona (M600, M600 V2, M511, Aztec) e outras marcas com o mesmo chip, sem fio ou no cabo | ✅ testado |
| **MCHOSE BLK** (`291D:385D`) | Headsets MCHOSE V9, V9 PRO, G9 PRO e S9 PRO | ✅ testado (V9 PRO) |
| **Logitech HID++ 2.0** | Mouses, teclados e headsets Logitech via Unifying, Lightspeed, Bolt ou cabo | ⚠️ experimental |
| **Razer** (protocolo do OpenRazer) | Mouses e teclados sem fio da Razer | ⚠️ experimental |
| **Bluetooth** | Qualquer fone, mouse ou teclado Bluetooth que informe bateria ao Windows (a mesma % de Configurações > Bluetooth) | ⚠️ experimental |

"Experimental" quer dizer que o protocolo segue a documentação pública, mas ainda não foi testado num aparelho real. Se funcionar (ou não) com o seu, abra uma issue com a saída do `--test`.

Os dispositivos são detectados sozinhos ao conectar. Cada um ganha um ícone, com um símbolo de mouse, headset, teclado ou bateria (para outros tipos).

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

- **Compx**: report `0x08` com o comando `0x04`. A resposta traz a % calculada pelo próprio mouse, o status de carga e a tensão.
- **MCHOSE BLK**: report `0x55` com `[0x65, 0x01]`. A resposta traz o nível e o status.
- **Logitech**: HID++ 2.0 pelo canal "long" (`0x11`). Procura as features `0x1004`, `0x1000`, `0x1001` ou `0x1F20` e lê o nome e o tipo pela `0x0005`.
- **Razer**: feature report de 90 bytes, classe `0x07`, comandos `0x80` (nível) e `0x84` (carregando).
- **Bluetooth**: lê a propriedade de bateria que o próprio Windows mantém para o dispositivo. Não conversa com o aparelho.

O código de cada protocolo está em [`Protocols.cs`](Protocols.cs).

Projeto independente, sem relação com a Kysona, MCHOSE, Logitech ou Razer.
