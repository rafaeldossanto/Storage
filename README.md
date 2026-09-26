# Storage

Controle de estoque para mercado de bairro, vendido como serviço: o lojista abre no
navegador, sem instalar nada. Cadastro por código de barras, categorias em árvore,
descontos por subgrupo, validade e relatório de perdas.

Este repositório é o **backend**. O front-end (React) vive em
[StorageFront](https://github.com/rafaeldossanto/StorageFront).

- **Runtime:** .NET 10 (LTS, suporte até 14/11/2028)
- **API:** ASP.NET Core, JSON
- **Dados:** MongoDB, multi-loja por `TenantId`
- **Escopo da v1:** gestão de estoque. Frente de caixa (PDV) fica para depois — um caixa
  que depende de internet para vender não é aceitável, e a versão offline é um projeto à
  parte.

## Como rodar

Precisa de um MongoDB **em replica set** — transações multi-documento não funcionam num
`mongod` standalone, e cada mudança de estoque grava lotes, movimentos e o documento que
a causou (entrada, contagem) juntos. Com Docker:

```bash
docker run -d --name storage-mongo -p 27017:27017 mongo:8.0 --replSet rs0 --bind_ip_all
docker exec storage-mongo mongosh --quiet --eval 'rs.initiate({_id: "rs0", members: [{_id: 0, host: "localhost:27017"}]})'
```

Depois:

```bash
dotnet run --project src/Storage.Api --launch-profile http
```

## Testes

```bash
dotnet test
```

Os testes de integração sobem um MongoDB próprio, descartável, com **Testcontainers** — então
precisam do **Docker rodando**, mas não do container de desenvolvimento. É um replica set na
mesma versão fixada (`mongo:8.0`), e cada teste usa um banco novo. São eles que provam o que
só o banco garante: uma loja não enxerga os dados de outra, o índice único decide a disputa
por um código de barras, e sessões vencidas somem pelo índice TTL.

Para usar, crie uma loja com `POST /api/auth/sign-up` — ela já vem com uma árvore de
categorias de mercado — e use o token devolvido nas demais chamadas.

**Chave de assinatura dos tokens.** Em desenvolvimento, se `Auth:SigningKey` não estiver
configurada, a API gera uma aleatória ao subir: as sessões não sobrevivem a um restart, o
que é aceitável na máquina de quem desenvolve. Em qualquer outro ambiente a API **se
recusa a subir** sem ela. A chave tem no mínimo 32 bytes em base64 e vem sempre do
ambiente (`Auth__SigningKey`), nunca de um arquivo versionado.

## Autenticação

- **Token de acesso** (JWT, 15 minutos) no corpo da resposta. O front guarda em memória e
  manda em `Authorization: Bearer`. Carrega a pessoa, a loja e o papel.
- **Token de renovação** (30 dias sem uso) só num cookie `HttpOnly`, `SameSite=Strict`,
  restrito a `/api/auth` — nenhum script da página consegue ler. Só o hash dele é guardado.
  Cada renovação troca o token; um token já usado que reaparece encerra **todas** as
  sessões daquela pessoa, porque alguém guardou uma cópia. A troca é um compare-and-set no
  banco: de duas renovações simultâneas com o mesmo token só uma vence, e a outra conta
  como reuso. Por isso o front nunca renova duas vezes ao mesmo tempo, nem entre abas.
- **Senha** com o hasher do ASP.NET Core Identity (PBKDF2, HMAC-SHA512). Hash antigo é
  atualizado no próximo login certo. Mínimo de 8 caracteres, sem regra de composição.
- **Tentativa de adivinhar senha:** 5 erros seguidos bloqueiam a conta por 15 minutos, e
  as rotas de autenticação aceitam 10 requisições por minuto por endereço (acima disso,
  429 `auth.too_many_requests` com `Retry-After`). E-mail
  inexistente responde igual e no mesmo tempo que senha errada, para não revelar quem tem
  conta.
- **Seguro por padrão:** toda rota exige login; só cadastro, login, renovação, saída,
  `/health` e o contrato ficam abertos. Mexer na equipe é só para o dono.
- E-mail é o login, então é **único na plataforma inteira**, não por loja.

## Estrutura

| Projeto | Papel |
| --- | --- |
| `Storage.Domain` | Entidades, value objects e regras puras. **Sem dependência nenhuma.** |
| `Storage.Application` | Casos de uso e as interfaces que a infraestrutura implementa |
| `Storage.Infrastructure` | MongoDB: mapeamento, índices, repositórios |
| `Storage.Api` | Endpoints HTTP, CORS, resolução da loja por requisição |

O domínio não sabe onde os dados moram. Foi isso que permitiu trocar SQLite por MongoDB
sem alterar uma linha de `Storage.Domain` além da noção de loja.

## Convenções

**Idioma.** Identificadores, coleções, campos, enums, branches e mensagens de commit em
inglês. Todo texto que o lojista lê é responsabilidade do front-end, em português.

**Isolamento entre lojas.** Todo documento carrega `TenantId`. Os repositórios leem a loja
de `ITenantContext` — nunca de um parâmetro — e aplicam o filtro em toda consulta; uma
escrita de documento de outra loja é recusada. Não existe loja padrão: requisição sem loja
falha.

**Dinheiro nunca é `decimal` nem `double`.** Todo valor é `Money`, um `long` de centavos
gravado como Int64. `double` não representa 0,10 exatamente. Arredondamento é *half away
from zero*: meio centavo sobe, como numa etiqueta de preço.

**Código de barras** é gravado na forma normalizada de 14 dígitos, com índice único **por
loja** — duas lojas vendem a mesma lata, com o mesmo código GS1.

**Estoque é um ledger.** Movimentos append-only: nunca atualizar, nunca apagar. Lote
vencido não é apagado — vira perda registrada, que é o que alimenta o relatório de perdas.
Saldo e custo médio **não são gravados no produto**: saem dos lotes, então duas entradas
simultâneas não têm um total para sobrescrever. Um lote só muda por compare-and-set
(quantidade e situação esperadas); quem perde a disputa recebe 409
`stock.changed_concurrently` e nada é gravado.

**Mapeamento fora do domínio.** Nenhum atributo BSON chega às entidades; o mapeamento vive
em `StorageBsonSerialization`.

## Contrato da API

- **Dinheiro em centavos inteiros** (`salePriceCents: 899`). Um número JSON vira `double` no
  navegador, e 8,99 não cabe exato num `double`; 899 cabe. O front formata.
- **Enums pelo nome** (`"baseUnit": "Unit"`), a mesma regra do banco.
- **Erros com código estável.** Toda recusa volta como problem details com `code`
  (`barcode.taken`, `category.move_into_own_branch`...). O front traduz o `code` para
  português; o `detail` é texto técnico em inglês e não deve ir para a tela. Corpo que não
  é JSON válido volta 400 `request.malformed`. Qualquer outra falha é bug e volta como 500
  sem mensagem.
- **404 no código de barras é caminho normal**: `GET /api/products/by-barcode/{codigo}`
  sem produto é a deixa para a tela abrir o cadastro já preenchido.
- **OpenAPI** gerado a cada build em `openapi/storage-api.json`, versionado — é ali que o
  front confere o formato de uma rota antes de usá-la. Em desenvolvimento também sai em
  `/openapi/v1.json`.

## O que a API faz

| Área | Rotas | Resumo |
| --- | --- | --- |
| Contas | `/api/auth/*`, `/api/me`, `/api/team` | Cadastro de loja (já com árvore de categorias de mercado), login, renovação, equipe dono/funcionário |
| Catálogo | `/api/categories`, `/api/products` | Árvore de categorias, produtos com embalagens (fardo de 12 conta 12 unidades), busca por código e nome |
| Entrada | `/api/receipts`, `/api/suppliers` | Nota bipada linha a linha, com custo e validade; cada linha vira um lote |
| Estoque | `/api/stock/*` | Saldo por produto e por ramo, abaixo do mínimo, lotes na ordem de saída (FEFO), avaria e devolução |
| Validade | job + `/api/stock/expiring` | A cada 6 h, no fuso de cada loja, lote vencido vira perda com o custo. Painel de 3, 7, 15 e 30 dias com o valor em risco |
| Descontos | `/api/discounts`, `/api/products/{id}/price` | Regra por produto ou por ramo (Bebidas alcança Energéticos), prioridade, teto de cascata, janela "vence em N dias", prévia de alcance |
| Contagem | `/api/counts` | Vários celulares contando ao mesmo tempo; o fechamento ajusta o estoque com justificativa obrigatória |
| Relatórios | `/api/reports/losses` | Perdas do período por categoria, em reais |

Só o dono mexe na equipe, fecha ou cancela contagem e dispara a varredura de validade à
mão.

## Estado

Backend do MVP pronto: tudo da tabela acima, com testes de domínio, de casos de uso e de
integração contra um MongoDB de verdade. Faltam as telas (esperam a escolha da biblioteca
visual, no [StorageFront](https://github.com/rafaeldossanto/StorageFront)), deploy com
HTTPS e monitoramento, backup com restauração ensaiada, e termos de uso e privacidade.