# Meu Bairro — API .NET (CitizenTech)

API RESTful do **Meu Bairro**, desenvolvida em **ASP.NET Core 10 com Minimal APIs** para a disciplina *Advanced Business Development with .NET* da Global Solution FIAP 2026/2.

O Meu Bairro é uma plataforma de **zeladoria urbana colaborativa**, dentro do tema **CitizenTech**: o cidadão registra problemas do bairro (buracos, iluminação, lixo, alagamentos, calçadas danificadas) e acompanha o atendimento feito pela equipe da prefeitura. O foco está na transparência: qualquer pessoa autenticada consulta o status, o histórico de atendimento e o prazo estimado de cada ocorrência, além de um painel com a situação do bairro (abertas, atrasadas e tempo médio de resolução).

## Objetivo da API

A API concentra as regras de negócio do atendimento e a persistência dos dados:

- cadastro e autenticação de cidadãos e de gestores da prefeitura;
- registro de ocorrências com foto, endereço e coordenadas;
- acompanhamento pelo gestor (status, prioridade e andamentos), com histórico;
- avaliação do atendimento pelo cidadão;
- indicadores de transparência por bairro.

O modelo de dados segue o esquema Oracle do grupo, com as tabelas `USUARIOS`, `FUNCIONARIOS`, `ENDERECOS`, `CATEGORIAS`, `SOLICITACOES`, `ANDAMENTOS`, `ANEXOS` e `AVALIACOES`. Em desenvolvimento e nos testes, a mesma estrutura roda em SQLite.

## Tecnologias

| Item | Versão |
| --- | --- |
| .NET / ASP.NET Core (Minimal APIs) | 10 (LTS) |
| Entity Framework Core + SQLite | 10.0.15 |
| Oracle.EntityFrameworkCore | 10.23.26400 |
| Autenticação JWT Bearer | 10.0.15 |
| OpenAPI nativo + Swagger UI (Swashbuckle) | 10.0.15 / 10.3.1 |
| Rate Limiting e Health Checks | nativos do ASP.NET Core |
| BCrypt.Net-Next (hash de senhas) | 4.0.3 |
| xUnit.net + `WebApplicationFactory` | 2.9.3 / 10.0.15 |

`dotnet list package --vulnerable --include-transitive` não aponta pacotes vulneráveis. A dependência transitiva `System.Security.Cryptography.Pkcs`, que o driver Oracle traz numa versão com vulnerabilidades conhecidas, está fixada numa versão corrigida.

## Estrutura do projeto

```text
MeuBairro.sln
├── src/MeuBairro.Api
│   ├── Program.cs              # composição da aplicação e pipeline HTTP
│   ├── Configuracao/           # classes de opções (appsettings)
│   ├── Dominio/                # entidades do esquema, regras de status e prazo
│   ├── Dados/                  # DbContext, mapeamento Oracle, proteção de escrita, inicialização
│   ├── Contratos/              # DTOs de entrada e saída
│   ├── Seguranca/              # JWT, BCrypt, papéis CIDADAO e GESTOR
│   ├── Servicos/               # regras de negócio, unidade de trabalho, fotos
│   ├── Infra/                  # erros, validação, idempotência, rate limiting, health checks, OpenAPI
│   └── Endpoints/              # rotas Minimal API
└── tests/MeuBairro.Tests
    ├── Unitarios/              # domínio, mapeamento e SQL Oracle, proteção de escrita, idempotência, segurança
    └── Integracao/             # requisições HTTP contra a API completa, com SQLite temporário
```

Decisões de design:

- **Minimal APIs** agrupadas por recurso, com um filtro de validação genérico (DataAnnotations) e respostas tipadas (`TypedResults`), que também alimentam o OpenAPI.
- **Modelo igual ao esquema Oracle**: entidades e mapeamento seguem as tabelas existentes, sem colunas novas; o que o esquema não expressa fica nas regras da aplicação.
- **Unidade de trabalho**: toda escrita abre a transação antes de qualquer trava em memória, o que evita espera circular com o bloqueio de escrita do SQLite.
- **Erros padronizados** em JSON, com `message` e, nas falhas de validação, a lista `fields` com o problema de cada campo.

## Principais funcionalidades

Todas as rotas também respondem com o prefixo `/api` (por exemplo, `/api/ocorrencias`). ID que não é número inteiro responde `400`; ID inexistente responde `404`.

| Método | Rota | Acesso | Descrição |
| --- | --- | --- | --- |
| POST | `/auth/register` | público | cadastro de cidadão (endereço opcional) |
| POST | `/auth/login` | público | login de cidadão ou gestor |
| GET / PUT | `/usuarios/me` | autenticado | perfil; atualiza nome e telefone |
| GET / PUT | `/usuarios/me/endereco` | CIDADAO | endereço de cadastro |
| POST | `/usuarios/gestores` | GESTOR | cadastra funcionário com perfil GESTOR |
| GET | `/enderecos/{id}` | autenticado | próprio endereço ou local de ocorrência (gestor: qualquer) |
| GET | `/categorias`, `/categorias/{id}` | autenticado | categorias com código, nome e prazo |
| POST / PUT / DELETE | `/categorias[/{id}]` | GESTOR | manutenção de categorias |
| GET | `/ocorrencias` | autenticado | lista completa, das mais recentes para as mais antigas |
| GET | `/ocorrencias/paginado` | autenticado | paginação (`page`, `size` até 50, `sort`) e filtros (`status`, `categoria`, `prioridade`, `bairro`, `minhas`) |
| GET | `/ocorrencias/{id}` | autenticado | detalhe com prazo estimado e indicador de atraso |
| POST | `/ocorrencias` | CIDADAO | registro em multipart, com foto opcional |
| PUT / DELETE | `/ocorrencias/{id}` | dono (ABERTA) / gestor exclui | edição e exclusão |
| PATCH | `/ocorrencias/{id}/status` | GESTOR | `ABERTA`, `EM_ANALISE`, `RESOLVIDA` |
| PATCH | `/ocorrencias/{id}/prioridade` | GESTOR | `BAIXA`, `MEDIA`, `ALTA` |
| GET | `/ocorrencias/{id}/historico` | autenticado | histórico de atendimento |
| POST | `/ocorrencias/{id}/andamentos` | GESTOR | andamento sem mudança de status |
| GET / POST | `/ocorrencias/{id}/anexos` | autenticado / dono | fotos da ocorrência (até 5) |
| GET / POST | `/ocorrencias/{id}/avaliacao` | dono ou gestor / dono | avaliação de 1 a 5 depois de resolvida |
| GET | `/ocorrencias/painel?bairro=` | autenticado | painel de transparência do bairro |
| GET | `/ocorrencias/resumo` | GESTOR | totais por status e prioridade |
| GET | `/fotos/{arquivo}` | público | imagem referenciada em `fotoUrl` |
| GET | `/health`, `/health/live`, `/health/ready` | público | health checks |

Regras de negócio aplicadas pela API:

- Uma ocorrência **resolvida** não muda mais de status nem de prioridade e não recebe andamentos; trocar para o status atual responde `409`.
- O cidadão só edita, exclui ou envia fotos para as próprias ocorrências enquanto estiverem `ABERTA`; o gestor pode excluir qualquer uma.
- A mesma ocorrência ainda não resolvida (título, categoria, logradouro e bairro iguais, sem diferenciar maiúsculas de minúsculas) enviada de novo pelo mesmo cidadão responde `409`.
- Na listagem paginada, `sort=status` e `sort=prioridade` seguem a ordem do negócio (`ABERTA` → `EM_ANALISE` → `RESOLVIDA`; `BAIXA` → `MEDIA` → `ALTA`), e não a dos códigos gravados no banco.
- O prazo estimado vem da categoria (`CATEGORIA_PRAZO_DIAS`), e a ocorrência fica marcada como atrasada quando passa dele sem ser resolvida.
- A avaliação só é aceita depois da resolução, uma vez por ocorrência, e só o dono e os gestores a consultam.
- O código da categoria é derivado do nome (`Iluminação` → `ILUMINACAO`), já que `CATEGORIAS` não tem coluna de código. As seis categorias padrão (`BURACO`, `ILUMINACAO`, `LIXO`, `ALAGAMENTO`, `CALCADA` e `OUTRO`) não podem ser excluídas nem ter o código alterado.
- Um endereço usado por mais de um registro nunca é alterado no lugar, e o endereço residencial de um cidadão não fica visível para outros.
- Fotos são aceitas até 5 MB e identificadas pelo conteúdo (JPEG, PNG ou WEBP), não pela extensão; o nome gravado em disco é sempre gerado pela API.

Exemplo de criação de ocorrência pelo terminal, com a conta de demonstração (`foto.jpg` é qualquer imagem JPEG, PNG ou WEBP na pasta atual; sem foto, basta tirar o último `-F`):

```powershell
# Windows PowerShell: use curl.exe, porque no PowerShell 5.1 "curl" é um apelido do Invoke-WebRequest
$login = Invoke-RestMethod -Method Post -Uri http://localhost:5080/auth/login -ContentType "application/json" `
  -Body '{"email":"cidadao@meubairro.com","senha":"Teste@123"}'
curl.exe -i -X POST http://localhost:5080/api/ocorrencias `
  -H "Authorization: Bearer $($login.token)" -H "Idempotency-Key: 7f6c1d0e-5a1b-4c1e-9f0a-1b2c3d4e5f60" `
  -F "titulo=Buraco na esquina da escola" -F "descricao=Buraco fundo perto da faixa de pedestres" `
  -F "categoria=BURACO" -F "endereco=Rua das Flores, 120" -F "bairro=Centro" `
  -F "latitude=-23.5505" -F "longitude=-46.6333" -F "foto=@foto.jpg"
```

```bash
# Bash (Linux, macOS ou Git Bash)
TOKEN=$(curl -s -X POST http://localhost:5080/auth/login -H "Content-Type: application/json" \
  -d '{"email":"cidadao@meubairro.com","senha":"Teste@123"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')
curl -i -X POST http://localhost:5080/api/ocorrencias \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: 7f6c1d0e-5a1b-4c1e-9f0a-1b2c3d4e5f60" \
  -F "titulo=Buraco na esquina da escola" -F "descricao=Buraco fundo perto da faixa de pedestres" \
  -F "categoria=BURACO" -F "endereco=Rua das Flores, 120" -F "bairro=Centro" \
  -F "latitude=-23.5505" -F "longitude=-46.6333" -F "foto=@foto.jpg"
```

Repetir o mesmo comando devolve a resposta original (mesmo `Location`) com `Idempotent-Replayed: true`, sem criar outra ocorrência.

O endereço pode ser enviado em texto livre (`endereco`), em campos detalhados (`logradouro`, `numero`, `bairro`, `cidade`, `estado`, `cep`) ou por `enderecoId`.

Formato dos erros (resposta real de um cadastro inválido):

```json
{
  "timestamp": "2026-11-17T10:00:56",
  "status": 400,
  "error": "Bad Request",
  "message": "Existem campos inválidos na requisição",
  "path": "/auth/register",
  "fields": [
    { "field": "nome", "message": "O nome é obrigatório" },
    { "field": "email", "message": "O e-mail informado é inválido" },
    { "field": "senha", "message": "A senha deve ter entre 6 e 60 caracteres" }
  ]
}
```

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) (o `global.json` aceita qualquer versão 10.0.x a partir da 10.0.100).
- Nenhum banco precisa ser instalado: o padrão é SQLite local, criado automaticamente.

## Como executar

```bash
dotnet restore
dotnet run --project src/MeuBairro.Api --launch-profile http
```

- Swagger UI: <http://localhost:5080/swagger> (a raiz `/` redireciona para ele)
- Documento OpenAPI: <http://localhost:5080/openapi/v1.json>
- Health check: <http://localhost:5080/health>

O perfil `http` usa o ambiente `Development`, que cria dados fictícios de demonstração: o cidadão `cidadao@meubairro.com` e o gestor `gestor@meubairro.com`, ambos com a senha `Teste@123`, e duas ocorrências. Essas contas só são criadas em `Development`, mesmo que `DadosExemplo:Habilitado` seja ligado em outro ambiente.

Se a porta 5080 estiver ocupada:

```bash
dotnet run --project src/MeuBairro.Api --launch-profile http --urls http://localhost:5090
```

### Configuração

Os valores ficam em `src/MeuBairro.Api/appsettings.json`. Segredos não ficam no repositório; use *user-secrets* ou variáveis de ambiente:

```bash
dotnet user-secrets --project src/MeuBairro.Api set "Jwt:Segredo" "uma-chave-com-pelo-menos-32-caracteres"
```

Também é possível usar variáveis de ambiente, só para o terminal atual, trocando `:` por `__`: `$env:Jwt__Segredo = "..."` no PowerShell ou `export Jwt__Segredo="..."` no Bash. O mesmo vale para as demais chaves da tabela abaixo.

Sem `Jwt:Segredo`, em `Development` a API gera uma chave aleatória (os tokens deixam de valer quando ela reinicia); em qualquer outro ambiente, a API não inicia.

| Chave | Padrão | Uso |
| --- | --- | --- |
| `ConnectionStrings:MeuBairro` | `Data Source=data/meubairro.db` | banco principal (SQLite) |
| `ConnectionStrings:Controle` | vazio (mesmo arquivo do principal) | armazenamento da idempotência |
| `Banco:Provedor` | `Sqlite` | `Sqlite` ou `Oracle` |
| `Jwt:ExpiracaoMinutos` | `120` | validade do token |
| `LimiteRequisicoes:*` | ver tabela de Rate Limiting | limites por política |
| `Proxy:IpsConfiaveis` / `RedesConfiaveis` | vazio | proxies reversos cujo `X-Forwarded-For` é aceito |
| `Idempotencia:RetencaoHoras` / `BloqueioSegundos` | `24` / `30` | retenção da chave e tempo de bloqueio |
| `Fotos:Pasta` / `Fotos:TamanhoMaximoBytes` | `data/fotos` / 5 MB | armazenamento das fotos |
| `EnderecoPadrao:*` | `Não informado`, `São Paulo/SP`, CEP `00000000` | completa o endereço quando a requisição traz só o texto do logradouro ou nenhum endereço |
| `Cors:Origens` | vazio | origens de navegador autorizadas a chamar a API (o Swagger UI não precisa, pois é servido pela própria API) |
| `Swagger:Habilitado` | `true` | documentação interativa |

## Banco de dados

### SQLite (padrão)

Na inicialização, a API cria as oito tabelas com a mesma estrutura do esquema Oracle (`EnsureCreated`), a tabela auxiliar `IDEMPOTENCIA_REQUISICOES` no mesmo arquivo e as seis categorias padrão. Não há migrations: o esquema de referência é o do Oracle, e a aplicação nunca o altera. Para recomeçar do zero, pare a API e apague a pasta `src/MeuBairro.Api/data` (no PowerShell: `Remove-Item -Recurse -Force src/MeuBairro.Api/data`).

- O banco roda em modo WAL, com chaves estrangeiras ativas e sem exclusão em cascata: a aplicação remove os registros filhos explicitamente, dentro da mesma transação.
- Os códigos de status e prioridade são gravados em `CHAR(1)` (`A`/`E`/`R` e `B`/`M`/`A`).
- Datas gravadas ficam no fuso `America/Sao_Paulo`, sem fração de segundo (como no `DATE` do Oracle), e trafegam no JSON sem offset; só o `expiraEm` do login é informado em UTC.
- O `lower()` nativo do SQLite só converte letras ASCII. A API registra uma versão com regras Unicode em cada conexão, para que filtros e comparações sem diferenciar maiúsculas funcionem também com letras acentuadas (`Água Branca` = `ÁGUA BRANCA`), como no `LOWER` do Oracle.
- A edição da ocorrência usa o status como token de concorrência, sem coluna extra: se outra operação mudar o status entre a leitura e a gravação, a edição responde `409`.

### Mapeamento Oracle (preparado, desativado por padrão)

O mapeamento do EF Core é explícito: nomes de tabelas e colunas, `VARCHAR2`, `CHAR(1)`, `CHAR(2)`, `CHAR(8)`, `NUMBER(9,6)`, `NUMBER(3,0)`, `NUMBER(1,0)`, `DATE`, nulidade, chaves estrangeiras e unicidades. Para usar o Oracle:

```powershell
dotnet user-secrets --project src/MeuBairro.Api set "ConnectionStrings:Oracle" "User Id=...;Password=...;Data Source=host:1521/servico"
$env:Banco__Provedor = "Oracle"                    # Bash: export Banco__Provedor=Oracle
$env:Banco__Oracle__Esquema = "DONO_DAS_TABELAS"   # opcional; Bash: export Banco__Oracle__Esquema=DONO_DAS_TABELAS
```

- **A escrita fica bloqueada** enquanto `Banco:Oracle:PermitirEscrita=false` (padrão). Um interceptador recusa o `SaveChanges` antes de abrir conexão, e outro só deixa passar comandos `SELECT`/`WITH`, bloqueando `ExecuteUpdate`, `ExecuteDelete`, SQL manual, `FOR UPDATE` e `NEXTVAL`. As operações de escrita respondem `503` com uma mensagem explicativa. A API nunca cria nem altera tabelas no Oracle.
- Com `Banco:Provedor=Oracle` e sem `ConnectionStrings:Oracle`, a API não inicia. Com SQLite (padrão), nenhuma conexão com o Oracle é feita.
- Pontos do esquema ainda não confirmados ficam configuráveis, sem valores fixos no código: os códigos `CHAR(1)` de status (`Banco:Codigos:Status`) e de prioridade (`Banco:Codigos:Prioridade`), a geração de IDs (`Banco:Oracle:EstrategiaId` = `Identity` ou `Sequence`, com `Banco:Oracle:Sequencias:<TABELA>`) e o código das categorias (`Categorias:Codigos`).
- Consultas com listas de IDs são feitas em lotes de até 1000 valores, o limite do Oracle para `IN`.

## Autenticação e autorização (JWT)

1. `POST /auth/register` com `{ "nome", "email", "senha" }` cria um cidadão; o endereço é opcional.
2. `POST /auth/login` com `{ "email", "senha" }` devolve `{ token, tipo, expiraEm, usuario }`.
3. Envie `Authorization: Bearer <token>` nas demais rotas. No Swagger, clique em **Authorize** e cole só o token: o prefixo `Bearer` é acrescentado automaticamente.

- Cidadãos ficam em `USUARIOS` (papel `CIDADAO`) e funcionários em `FUNCIONARIOS` (papel `GESTOR`). Como os IDs das duas tabelas podem coincidir, o papel do token indica em qual tabela a conta precisa existir, e essa existência é conferida a cada requisição (conta removida responde `401`).
- Senhas com BCrypt (fator 10, sal aleatório), entre 6 e 60 caracteres e até 72 bytes. Como o BCrypt só compara os primeiros 72 bytes, uma senha maior é recusada no cadastro e nunca autentica no login (sem essa regra, a senha real seguida de qualquer sufixo seria aceita). Um hash corrompido no banco é tratado como credencial inválida. Senha e hash nunca aparecem em respostas ou logs.
- Login com e-mail inexistente e com senha errada devolve a mesma mensagem e leva um tempo parecido, para não revelar quais contas existem.
- Token HS256 com emissor, audiência e expiração validados (tolerância de 30 s). Tokens com `alg: none`, assinatura inválida, payload adulterado ou expirados são recusados.
- `401` para token ausente, inválido ou expirado; `403` quando o papel não tem permissão.
- Todas as rotas exigem autenticação por padrão; só são públicas as de cadastro, login, fotos, health checks e documentação. Caminho inexistente, método ou `Content-Type` não aceitos pela rota respondem `404`, `405` ou `415` mesmo sem token, para que o cliente veja o erro real (por exemplo, login enviado como `text/plain`).
- Funcionários não têm cadastro público: um gestor cadastra outro em `POST /usuarios/gestores`.

## Idempotência

As criações (`POST /ocorrencias`, anexos, avaliação, andamentos, categorias e gestores) aceitam o cabeçalho `Idempotency-Key`:

| Situação | Resposta |
| --- | --- |
| chave nova | processa normalmente (`Idempotent-Replayed: false`) |
| mesma chave e mesmo conteúdo | devolve a resposta original (status, corpo e `Location`) com `Idempotent-Replayed: true`, sem repetir a operação |
| mesma chave com conteúdo diferente | `409` |
| mesma chave ainda em processamento | `409` com `Retry-After` |
| erro no processamento (4xx, 5xx ou exceção) | nada é mantido e a chave é liberada para nova tentativa |
| cabeçalho vazio, repetido, com espaços ou acima de 100 caracteres | `400` |

- A chave vale por **usuário + método + rota**, tem de 1 a 100 caracteres ASCII visíveis e fica guardada por 24 horas; um serviço em segundo plano remove as expiradas.
- Em multipart, o conteúdo é comparado campo a campo e pelo SHA-256 de cada arquivo, porque o *boundary* muda a cada envio.
- As chaves ficam na tabela `IDEMPOTENCIA_REQUISICOES`, e o índice único (escopo, chave) decide qual requisição concorrente vence. Por isso as chaves sobrevivem a reinícios da API.
- **Commit atômico**: no SQLite, a tabela fica no mesmo arquivo do banco principal, e a requisição roda numa transação que grava os dados do negócio e o resultado da chave juntos. Se o processo cair antes do commit, nada do negócio fica gravado; a chave continua "em processamento" até o fim do bloqueio (30 s), quando uma nova tentativa a assume e executa com segurança. Se outra tentativa tiver assumido a chave nesse meio-tempo, a requisição antiga desfaz tudo em vez de duplicar.
- Se o armazenamento de idempotência estiver indisponível, requisições com a chave recebem `503` em vez de correr o risco de duplicar; sem a chave, a API continua atendendo.
- Sem o cabeçalho, a API funciona normalmente e ainda recusa com `409` a mesma ocorrência aberta enviada duas vezes pelo mesmo cidadão.

## Rate Limiting

Middleware nativo `Microsoft.AspNetCore.RateLimiting`, com janelas fixas configuráveis em `LimiteRequisicoes`:

| Política | Padrão | Partição |
| --- | --- | --- |
| Global | 300 requisições/min | IP |
| Autenticação (login e cadastro) | 10 requisições/min | IP |
| Leitura (rotas protegidas) | 120 requisições/min | usuário do token |
| Escrita (criação e alteração) | 30 requisições/min | usuário do token |

Ao exceder o limite, a resposta é `429` com `Retry-After` e corpo JSON, e a requisição não chega a ser processada. O IP vem da conexão TCP: `X-Forwarded-For` só é considerado quando a conexão vem de um proxy listado em `Proxy:IpsConfiaveis`/`RedesConfiaveis` (vazio por padrão), então o cliente não troca de partição manipulando cabeçalhos. Os health checks não são limitados.

## Health Checks

| Rota | Verificações | Uso |
| --- | --- | --- |
| `/health/live` | aplicação | liveness: o processo está no ar (não consulta o banco) |
| `/health/ready` | banco, armazenamento de idempotência, pasta de fotos | readiness: pronta para receber tráfego |
| `/health` | todas | visão completa |

- **banco**: consulta real de leitura à tabela `CATEGORIAS`, com tempo limite; `Unhealthy` se falhar e `Degraded` se passar de `Banco:LimiteLatenciaMs`.
- **idempotência** e **pasta de fotos**: `Degraded` em caso de falha, porque a API continua atendendo o restante.
- `Healthy` e `Degraded` respondem `200`; `Unhealthy` responde `503`. As respostas não usam cache e não expõem exceções nem strings de conexão.
- As dependências verificadas são as que a API usa para funcionar: o banco de dados (SQLite local ou Oracle, quando configurado), o armazenamento de idempotência e a pasta de fotos. A API não consome serviços nem APIs de terceiros, por isso não há verificação desse tipo.

Exemplo real de `GET /health/ready`:

```json
{
  "status": "Healthy",
  "duracaoMs": 24.1,
  "verificacoes": [
    { "nome": "banco", "status": "Healthy", "descricao": "Banco de dados acessível", "duracaoMs": 23.4,
      "dados": { "provedor": "Sqlite", "latenciaMs": 21 } },
    { "nome": "idempotencia", "status": "Healthy", "descricao": "Armazenamento de idempotência acessível", "duracaoMs": 22, "dados": {} },
    { "nome": "armazenamento-fotos", "status": "Healthy", "descricao": "Pasta de fotos disponível para gravação", "duracaoMs": 2.9, "dados": {} }
  ]
}
```

## Swagger / OpenAPI

O documento é gerado pelo OpenAPI nativo do ASP.NET Core (`/openapi/v1.json`) e exibido pelo Swagger UI em `/swagger`. Ele descreve as 23 rotas, inclusive os health checks, com parâmetros de consulta, DTOs de entrada e saída, o formulário multipart com a foto em binário, o esquema Bearer (botão **Authorize**), o cabeçalho `Idempotency-Key` nas operações idempotentes e as respostas possíveis em cada operação, incluindo `429` e `503`.

## Testes

```bash
dotnet test                                              # suíte completa
dotnet test --filter "FullyQualifiedName~Unitarios"     # só unitários
dotnet test --filter "FullyQualifiedName~Integracao"    # só integração
dotnet test -c Release
```

São **327 testes xUnit** (109 unitários e 218 de integração), todos aprovados em Debug e em Release, sem testes ignorados. Os de integração sobem a API completa com `WebApplicationFactory`, cada classe com bancos SQLite temporários próprios, e fazem requisições HTTP reais cobrindo:

- **autenticação**: cadastro válido e inválido, limites de tamanho, e-mail duplicado e normalização de caixa, senhas Unicode e com caracteres especiais, senha acima de 72 bytes no login, senha incorreta, token ausente, malformado, expirado, adulterado, com chave, emissor ou audiência errados, `alg: none`, conta removida, papéis cidadão e gestor, rotas públicas com método ou `Content-Type` errados, inicialização recusada sem segredo JWT;
- **regras de negócio**: CRUD, IDs inválidos e inexistentes, categoria e endereço inválidos, tamanhos das colunas, paginação (inclusive página acima do limite de `int`), ordenação (inclusive status e prioridade com códigos configurados), filtros com letras acentuadas, resultados vazios, transições de status, ocorrência resolvida, acesso de outro usuário, histórico, avaliação duplicada e privada, painel e prazo;
- **confiabilidade**: repetição e concorrência com a mesma chave, chave reutilizada com outro conteúdo, chave vazia, multipart incompleto, falha ao abrir a transação, ao gravar o negócio e ao gravar o resultado da chave (rollback completo, foto removida, chave liberada), reinício da API, expiração e limpeza automática das chaves, banco e Oracle indisponíveis, `429` de cada política, rajada concorrente e proxy reverso;
- **segurança**: SQL injection, mass assignment, escalada de privilégio, ausência de senha e hash nas respostas e de senha, token e segredo nos logs, upload inválido e acima de 5 MB, path traversal, CORS, contas de demonstração fora de `Development`, exceção inesperada sem vazamento de detalhes e rotas sem autorização.

Os unitários conferem o mapeamento Oracle (nomes, tipos, tamanhos, nulidade, chaves e unicidade), o SQL que o provedor Oracle geraria a partir dos serviços (sem abrir conexão), o bloqueio de escrita, o serviço de idempotência, os tokens, o BCrypt, a detecção de imagens e as regras de domínio.

## Roteiro de demonstração

1. `dotnet run --project src/MeuBairro.Api --launch-profile http` e abrir <http://localhost:5080/swagger>.
2. `POST /auth/login` com `cidadao@meubairro.com` / `Teste@123`, copiar o valor de `token` e colá-lo em **Authorize** (sem o prefixo `Bearer`).
3. `POST /ocorrencias` (multipart, com foto) informando um `Idempotency-Key`; repetir o envio e mostrar `Idempotent-Replayed: true` e o mesmo `Location`.
4. `GET /ocorrencias/paginado?bairro=...` e `GET /ocorrencias/painel?bairro=...` (transparência).
5. Login do gestor (`gestor@meubairro.com`) e `PATCH /ocorrencias/{id}/status` para `EM_ANALISE` e depois `RESOLVIDA`; com o token do cidadão, a mesma chamada responde `403`.
6. `GET /ocorrencias/{id}/historico` e `POST /ocorrencias/{id}/avaliacao` (a segunda avaliação responde `409`).
7. Repetir o login com senha errada mais de 10 vezes em um minuto para mostrar o `429`.
8. `GET /health/ready` e, por fim, `dotnet test`.

## Limitações conhecidas

- **Oracle**: a API nunca foi conectada ao banco Oracle do grupo. O mapeamento foi validado pelo modelo do EF Core e pelo SQL Oracle gerado nos testes, sem conexão, o que é uma validação parcial. Os códigos `CHAR(1)`, a geração de IDs, eventuais restrições `CHECK` e o formato das senhas já gravadas continuam sem confirmação; senhas que não estejam em BCrypt não autenticam. Os limites de tamanho são validados em caracteres; se as colunas `VARCHAR2` usarem semântica de bytes, textos acentuados perto do limite podem exceder a coluna.
- Com o Oracle como banco principal, a tabela de idempotência fica num SQLite à parte (para não alterar o esquema compartilhado) e deixa de ser atômica com os dados do negócio. Se o processo cair entre o commit do negócio e o registro do resultado, uma nova tentativa depois do bloqueio de 30 s pode reexecutar a operação; se nesse intervalo ocorrer uma exceção (por exemplo, ao montar a resposta), a chave é liberada na hora e o mesmo pode acontecer. A regra de ocorrência duplicada e as unicidades do esquema ainda barram parte dessas repetições com `409`.
- `ANDAMENTOS` exige `FUNCIONARIO_ID`, então ações do cidadão não viram andamento: a entrada de criação do histórico vem de `SOLICITACAO_DATA_ABERTURA`, e edições feitas pelo cidadão não ficam registradas.
- `ENDERECOS` exige bairro, cidade, estado e CEP. Quando a requisição traz só o texto do logradouro, ou o cadastro vem sem endereço, os campos que faltam recebem os valores de `EnderecoPadrao`.
- Rate limiting, travas de concorrência e fotos ficam em memória ou em disco local e valem por instância; não há distribuição entre várias instâncias.
- Tokens JWT valem até expirar; não há revogação além da remoção da conta.
- `GET /ocorrencias` não é paginado e o painel calcula os indicadores em memória; para volumes maiores, use `/ocorrencias/paginado`.
- Filtros e a regra de duplicidade não diferenciam maiúsculas de minúsculas, mas diferenciam acentos (`Agua` e `Água` são textos diferentes).
- O perfil `http` é para uso local, sem HTTPS. Em produção, o TLS deve ser terminado num proxy reverso (configurado em `Proxy:IpsConfiaveis`), e o Swagger pode ser desligado com `Swagger:Habilitado=false`.
