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
`mongod` standalone, e a entrada de mercadoria grava lote, movimento e custo médio
juntos. Com Docker:

```bash
docker run -d --name storage-mongo -p 27017:27017 mongo:8 --replSet rs0
docker exec storage-mongo mongosh --quiet --eval "rs.initiate()"
```

Depois:

```bash
dotnet run --project src/Storage.Api
```

Em desenvolvimento a API roda como uma loja fixa (`Storage:DevelopmentTenantId` em
`appsettings.Development.json`) até o login existir. Esse atalho só é registrado no
ambiente Development.

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

**Estoque será um ledger.** Movimentos append-only: nunca atualizar, nunca apagar. Lote
vencido não é apagado — vira perda registrada, que é o que alimenta o relatório de perdas.

**Mapeamento fora do domínio.** Nenhum atributo BSON chega às entidades; o mapeamento vive
em `StorageBsonSerialization`.

## Contrato da API

- **Dinheiro em centavos inteiros** (`salePriceCents: 899`). Um número JSON vira `double` no
  navegador, e 8,99 não cabe exato num `double`; 899 cabe. O front formata.
- **Enums pelo nome** (`"baseUnit": "Unit"`), a mesma regra do banco.
- **Erros com código estável.** Toda recusa volta como problem details com `code`
  (`barcode.taken`, `category.move_into_own_branch`...). O front traduz o `code` para
  português; o `detail` é texto técnico em inglês e não deve ir para a tela. Qualquer outra
  falha é bug e volta como 500 sem mensagem.
- **404 no código de barras é caminho normal**: `GET /api/products/by-barcode/{codigo}`
  sem produto é a deixa para a tela abrir o cadastro já preenchido.
- **OpenAPI** em `/openapi/v1.json` no ambiente de desenvolvimento — é de onde o front gera
  o cliente tipado.

## Estado

Catálogo completo no backend: categorias em árvore e produtos com embalagens, expostos em
`/api/categories` e `/api/products`, com os dados de cada loja isolados. Faltam o login,
os testes contra um MongoDB de verdade e o front.
