# Fallback do viewer para TURN após falha de conexão direta

## Contexto

Em 24/09/2026, o FrameRelay entrou com sucesso na sessão compartilhada e completou a
sinalização. O viewer selecionou ICE direto por UDP e chegou ao estado ICE `connected`, mas
a conexão WebRTC permaneceu em `connecting`, mudou para `failed` e nenhum frame da nova
tentativa foi apresentado. O emissor continuou codificando vídeo. O problema, portanto,
ocorre depois da sinalização e da seleção do par ICE direto, antes de a conexão de mídia ficar
operacional.

O `RtcVideoWatchHost` carrega servidores ICE, mas cria o viewer com `ForceRelay: false`. O
projeto já consegue criar conexões relay-only. O backend RelayControl roteia
`webrtc.renegotiate` peer-to-peer e trata o payload como JSON opaco; não é necessário alterar
o backend para pedir uma nova negociação.

## Objetivo

Quando a conexão direta inicial falhar antes de se conectar, fazer uma única nova negociação
daquela conexão usando TURN, mantendo a sessão e o compartilhamento existentes. Conexões
diretas que funcionam devem continuar sem relay.

## Desenho

O viewer detecta a transição terminal para `RTCPeerConnectionState.failed`. Se a conexão nunca
chegou a `connected` e o par ICE selecionado foi classificado como `Direct`, o viewer inicia
uma única tentativa de recuperação:

1. Gera um `negotiationId` UUID novo e registra-o como a geração de recuperação esperada.
2. Descarta a peer connection direta e seus candidatos pendentes.
3. Cria uma peer connection viewer com `iceTransportPolicy=relay`, usando os mesmos servidores
   TURN e credenciais efêmeras obtidos da API.
4. Envia ao publisher `webrtc.renegotiate` com o payload
   `{ "reason": "direct_connection_failed", "negotiationId": "<uuid>", "iceTransportPolicy": "relay" }`.
5. Aguarda uma nova offer do publisher para esse `negotiationId`; aplica a offer e responde
   pela peer connection relay-only.

O viewer aguarda a offer relay por até 15 segundos. Depois de aplicar a offer, aguarda a
conexão WebRTC chegar a `connected` por até 30 segundos. Esgotar qualquer prazo encerra a
tentativa com estado de mídia de falha e diagnóstico identificando a etapa que expirou.

Ao receber essa solicitação de um viewer ativo, o publisher substitui somente a peer connection
daquele participante por uma relay-only, envia uma nova offer e associa o mesmo `negotiationId`
à offer e aos candidatos ICE dessa geração. A captura, o encoder, outros viewers e a sessão
continuam ativos.

O `negotiationId` identifica offers, answers e candidatos ICE em ambas as direções. Mensagens
de gerações antigas são ignoradas depois da troca, para que pacotes de sinalização atrasados da
conexão direta não contaminem a negociação relay. Offers legadas sem identificador continuam
válidas para a negociação inicial; a geração inicial passa a incluir um identificador nos
clients atualizados.

## Limites e falhas

- Há no máximo uma tentativa relay por participante e sessão. Uma falha relay é terminal para
  essa tentativa; não há alternância infinita entre políticas.
- A recuperação só é iniciada quando a conexão WebRTC falha antes de atingir `connected` e há
  evidência de que o par selecionado era direto. Falhas sem par selecionado mantêm o tratamento
  existente.
- Se o outro client não suportar `webrtc.renegotiate`, ou se a nova offer não chegar, a
  tentativa termina após 15 segundos com diagnóstico explícito e estado de mídia de falha.
- Se a conexão relay não chegar a `connected` nos 30 segundos após aplicar a nova offer, a
  tentativa termina com diagnóstico de timeout; não se inicia outra tentativa.
- A tentativa relay usa TURN para todo o tráfego de mídia desse viewer, aumentando custo e
  latência somente para a conexão recuperada.
- O backend não recebe mídia nem inspeciona SDP, candidatos, credenciais TURN ou bytes de
  mídia. O payload de renegociação contém apenas motivo, política e ID aleatório de geração.

## Escopo

Alterar somente o cliente `dotnet_FrameRelay`: roteamento de sinalização da sessão, publisher,
subscriber e fábricas/diagnósticos de peer connection, com cobertura de teste dessas
transições. Não alterar RelayControl, contratos HTTP, captura, codificação, persistência ou o
protocolo de criação de sessão.

## Critérios de aceitação

1. Uma conexão direta que chega a `connected` não solicita renegociação e segue o fluxo atual.
2. Uma conexão direta que falha antes de `connected` solicita uma renegociação relay-only uma
   vez e reinicia somente os peers publisher/viewer daquele participante.
3. A nova offer, answer e candidatos ICE são aceitos somente quando seus `negotiationId`
   correspondem à geração relay esperada; mensagens atrasadas da geração anterior são
   ignoradas.
4. Uma conexão relay que falha não inicia outra tentativa.
5. Encerrar/remover o viewer durante a recuperação limpa peer connections, filas de mídia,
   candidatos e timers/handlers associados.
6. A troca usa o roteamento existente de `webrtc.renegotiate` sem exigir alteração no backend.

## Verificação planejada

Testes unitários devem cobrir os critérios 1–5 com peer connections e sinalização simulados.
Os testes de peer connection SIPSorcery devem confirmar a política relay-only nas fábricas do
publisher e viewer. A suíte de integração simulada deve verificar a ordem renegotiate → nova
offer/answer → candidatos da mesma geração e demonstrar que o compartilhamento e outros
viewers permanecem ativos. A verificação final também deve compilar a solução completa.
