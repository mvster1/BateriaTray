# BateriaTray

Mostra a bateria do mouse e do headset sem fio na bandeja do Windows, ao lado do relógio.
Cada dispositivo ganha um ícone com um símbolo pequeno e a porcentagem.

- **Leve**: cerca de 5 MB de RAM, CPU praticamente zero, nenhuma GPU. Usa só Win32 (sem WinForms nem WPF), roda numa única thread e lê os dispositivos uma vez por minuto.
- **Sem driver do fabricante**: conversa direto com o receptor USB via HID. O MCHOSE HUB e o driver da KYSOAN podem ficar fechados.

## Dispositivos suportados

| Dispositivo | Receptor (VID:PID) |
|---|---|
| Mouse KYSONA M600 / M600 V2 / M511 / Aztec | `3554:F57C`, `F57D`, `F57E`, `F512`, `F5D5` |
| Headset MCHOSE V9 PRO | `291D:385D` |

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
- **Clique direito**: Atualizar agora, Mostrar ícone do dispositivo, Iniciar com o Windows, Sair.
- O mouse só responde quando está acordado. Se aparecer `--`, mexa o mouse e clique no ícone.

## Compilar

```
dotnet publish -c Release -o app
```

Para testar a leitura sem abrir a bandeja, rode `BateriaTray.exe --test <pasta>`. O resultado fica em `teste.txt` e uma prévia dos ícones em `strip.bmp`.

## Como funciona

- **Mouse** (chip Compx): envia o report `0x08` com o comando `0x04`. A resposta traz a tensão em mV, que é convertida em % pela tabela do driver oficial.
- **Headset** (protocolo BLK): envia o report `0x55` com `[0x65, 0x01]`. A resposta traz o nível e o status (descarregando, carregando, cheio ou dormindo).

Projeto independente, sem relação com a Kysona ou a MCHOSE.
