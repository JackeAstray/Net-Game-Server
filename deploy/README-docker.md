# Docker 一键集群

六节点微服务一键部署（对标 KBE machine/baseappmgr/cellappmgr + 现代容器编排）：

| 服务 | 端口 | 职责 |
|---|---|---|
| Gateway | 31300 | 客户端接入网关，直连 Login/Game/Center |
| Login | 31302 | 账号/邮箱登录，向 Center 注册 |
| Game | 31304 | 游戏逻辑，向 Center 注册 |
| DB | 31305 | MySQL(EF) + Redis 业务数据 |
| Center | 31306 / 31316(HTTP) | 节点注册/心跳/实体迁移调度/实体位置服务 |
| Battle | 31307 | 战斗/房间，向 Center 注册，实体持久化（默认 MySQL 后端） |
| MySQL | 3306 | 业务库（GameDB） |
| Redis | 6379 | 缓存/会话 |
| Postgres | 5432 | 可选（实体持久化 Postgres 后端验证，`--profile postgres`） |

每个节点另开健康检查端口 = 节点端口 + 10000（如 Battle `41307/healthz`、`/readyz`）。

## 启动

```bash
# 前置：Docker Desktop / docker compose v2
# 可选：设置共享密钥（默认 netgame-dev-shared-secret-change-me，生产务必改）
echo "CenterNodeSharedSecret=$(openssl rand -base64 32)" > .env

docker compose -f deploy/docker-compose.yml up -d --build
docker compose -f deploy/docker-compose.yml ps
```

## 验证

```bash
# 存活检查
curl http://127.0.0.1:41306/healthz      # Center
curl http://127.0.0.1:41307/healthz      # Battle
curl http://127.0.0.1:41305/healthz      # DB

# 就绪检查（关服排空时返回 503）
curl http://127.0.0.1:41307/readyz

# 日志（可确认实体位置登记、位置缓存、实体持久化后端等）
docker compose -f deploy/docker-compose.yml logs -f battle
docker compose -f deploy/docker-compose.yml logs -f center
```

## 切换 Battle 实体持久化后端

`docker-compose.yml` 中 battle 服务默认 `EntityPersistence__Provider=MySql`
（实时验证 MySQL 存储实现）。注释里给出了 File / Redis / PostgreSql 的切换示例，
其中 Postgres 需要 `--profile postgres` 一并启动 postgres 服务。

## 关闭

```bash
docker compose -f deploy/docker-compose.yml down
# 保留数据卷则加 -v
```

## 备注

- 镜像单文件（6 节点产物在 `/app/<Node>/`，compose 用 `working_dir` + `command` 指定启动）。
- 跨节点主机名 = compose 服务名（容器 DNS），由环境变量覆盖 appsettings 中的 `127.0.0.1`。
- 节点间认证共享密钥由 `CenterNodeSharedSecret` 环境变量注入（compose 全局），与本地 `StartServers.bat`
  的 `.cluster_secret` 机制等价。
- 新增 Battle 节点（多开）：复制 battle 服务，改 `--host/--port/--node-id` 与对外端口映射，
  并把 `EntityCallDirectRouting` 置 true 体验迭代 21 的跨 Battle 直达路由。
