# syntax=docker/dockerfile:1

# =====================================================================
# Net-Game-Server 一键集群镜像（迭代 21：Docker 部署）
# 构建全部 6 个服务器节点（Gateway/Login/Game/DB/Center/Battle），
# 产物按 /app/<Node>/ 目录隔离，运行时通过 working_dir + command 指定。
# 构建上下文 = 仓库根目录（docker compose 中 context: ..）。
# =====================================================================

# ---------- 阶段 1：SDK 构建 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先拷工程文件以利用层缓存（可选；直接全量拷贝更省事）
COPY . .

RUN dotnet restore NetGameServer/NetGameServer.slnx \
 && dotnet publish NetGameServer/Gateway/Gateway.csproj -c Release -o /out/Gateway --no-restore \
 && dotnet publish NetGameServer/Login/Login.csproj     -c Release -o /out/Login --no-restore \
 && dotnet publish NetGameServer/Game/Game.csproj       -c Release -o /out/Game --no-restore \
 && dotnet publish NetGameServer/DB/DB.csproj           -c Release -o /out/DB --no-restore \
 && dotnet publish NetGameServer/Center/Center.csproj   -c Release -o /out/Center --no-restore \
 && dotnet publish NetGameServer/Battle/Battle.csproj   -c Release -o /out/Battle --no-restore \
 && mkdir -p /out/Battle/scripts \
 && cp NetGameServer/GameLogic/scripts/*.csx /out/Battle/scripts/

# ---------- 阶段 2：运行时镜像（Center 依赖 ASP.NET Core，故用 aspnet 运行时） ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./

# 对外端口（节点 + 健康检查 = 端口+10000）：
# 31300 Gateway / 31302 Login / 31304 Game / 31305 DB / 31306 Center / 31307 Battle / 31316 CenterHttp
EXPOSE 31300 31302 31304 31305 31306 31307 31316 \
       41300 41302 41304 41305 41306 41307
