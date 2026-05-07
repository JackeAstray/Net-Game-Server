using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared;

namespace Network;

/// <summary>
/// 管理所有网络服务实例的管理器，提供统一的启动、停止和获取服务接口。
/// </summary>
public class NetworkManager
{
    private readonly ConcurrentDictionary<string, INetworkServer> servers = new();

    /// <summary>
    /// 注册一个网络服务实例
    /// </summary>
    public void RegisterServer(string name, INetworkServer server)
    {
        if (!servers.TryAdd(name, server))
        {
            Log.Error($"名为“{name}”的服务器已存在。");
        }
    }

    /// <summary>
    /// 移除一个网络服务实例
    /// </summary>
    public bool UnregisterServer(string name)
    {
        return servers.TryRemove(name, out _);
    }

    /// <summary>
    /// 获取指定名称的网络服务实例
    /// </summary>
    public INetworkServer? GetServer(string name)
    {
        servers.TryGetValue(name, out var server);
        return server;
    }

    /// <summary>
    /// 启动指定的网络服务
    /// </summary>
    public async Task StartServerAsync(string name, int port)
    {
        if (servers.TryGetValue(name, out var server))
        {
            await server.StartAsync(port);
        }
        else
        {
            Log.Error($"名为“{name}”的服务器未找到。");
        }
    }

    /// <summary>
    /// 停止指定的网络服务
    /// </summary>
    public async Task StopServerAsync(string name)
    {
        if (servers.TryGetValue(name, out var server))
        {
            await server.StopAsync();
        }
        else
        {
            Log.Error($"名为“{name}”的服务器未找到。");
        }
    }

    /// <summary>
    /// 停止所有网络服务
    /// </summary>
    public async Task StopAllAsync()
    {
        foreach (var server in servers.Values)
        {
            await server.StopAsync();
        }
    }
}