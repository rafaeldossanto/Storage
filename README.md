# Storage

Controle de estoque com leitura de código de barras e frente de caixa para mercado de
bairro. Roda no PC que a loja já tem, num processo só, e **continua vendendo com a
internet fora**.

- **Runtime:** .NET 10 (LTS, suporte até 14/11/2028)
- **Interface:** Blazor Server, aberta no navegador em `localhost`
- **Dados:** SQLite local — um arquivo, zero servidor para instalar
- **Leitura:** leitor USB (HID, se comporta como teclado) e câmera do celular na mesma rede

Plano completo do projeto: https://claude.ai/artifact/BRcVLePSwNgW8ro6ZEQhUa

## Como rodar

```bash
dotnet run --project src/Storage.Web
```

Em desenvolvimento o banco é criado em `src/Storage.Web/.data/storage.db` (configurável por
`Storage:DataDirectory`). Em produção fica em `%ProgramData%\Storage`, porque o app roda como
serviço da máquina e não como dado de um usuário.

## Estrutura

| Projeto | Papel |
| --- | --- |
| `Storage.Domain` | Entidades, value objects e regras puras. **Sem dependência nenhuma.** |
| `Storage.Application` | Casos de uso e interfaces de repositório |
| `Storage.Infrastructure` | EF Core, SQLite, migrations, jobs |
| `Storage.Web` | Blazor Server, Kestrel, composição |

As duas regras que decidem dinheiro do lojista — resolução de desconto e consumo FEFO —
ficam no `Domain`, puras e testáveis sem banco.

## Convenções

**Idioma.** Identificadores, tabelas, colunas, enums, branches e mensagens de commit em
inglês. Tudo que o lojista lê vem de `Resources/UiText.resx` via `IStringLocalizer`, com a
cultura fixada em `pt-BR` no `Program.cs`. Nenhum texto de tela escrito direto no
componente.

**Dinheiro nunca é `decimal`.** SQLite guardaria como TEXT (que ordena "9,90" depois de
"10,00") ou REAL (que não representa 0,10 exatamente). Todo valor é `Money`, um `long` de
centavos em coluna INTEGER. Arredondamento é *half away from zero*: meio centavo sobe,
como numa etiqueta de preço.

**Estoque é um ledger.** `StockMovement` é append-only: nunca `UPDATE`, nunca `DELETE`.
Estorno é movimento contrário. Lote vencido não é apagado — vira status `EXPIRED` e um
movimento `EXPIRY_LOSS`, que é o que alimenta o relatório de perdas.

**Saldo é derivado** dos lotes disponíveis, nunca denormalizado.

**Banco.** WAL ligado (o caixa lê enquanto a entrada de mercadoria escreve), `synchronous`
em NORMAL e `busy_timeout` aplicados por conexão via interceptor. Backup é
`VACUUM INTO` — copiar o arquivo na mão com o banco aberto produz cópia corrompida.

## Estado

Fase 1 (fundação) em andamento. Sem entidades de negócio ainda: o que existe são os value
objects `Money` e `Gtin`, o `DbContext` configurado e a localização funcionando ponta a
ponta.
