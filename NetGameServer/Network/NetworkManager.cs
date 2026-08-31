using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared;
using Network.Routing;

namespace Network;

/// <summary>
/// 管理所有网络服务实例的管理器，提供统一的启动、停止和获取服务接口。
/// 附带默认的全局消息路由器。
/// </summary>
public class NetworkManager
{
    private static readonly Lazy<NetworkManager> instance = new Lazy<NetworkManager>(() => new NetworkManager());

    /// <summary>
    /// 全局单例的便捷访问
    /// </summary>
    public static NetworkManager Instance => instance.Value;

    private readonly ConcurrentDictionary<string, INetworkServer> servers = new();

    /// <summary>
    /// 每个网络管理器的默认中央数据路由器
    /// </summary>
    public MessageRouter Router { get; } = new MessageRouter();

    /// <summary>
    /// 将指定名称与 INetworkServer 实例注册到内部集合并在成功时绑定到路由器；若名称已存在则记录错误。
    /// </summary>
    /// <remarks>尝试将服务器添加到内部集合并在成功后调用 Router.BindServer；如果名称已存在则记录错误且不替换现有项。</remarks>
    /// <param name="name">服务器的唯一名称，用于索引与识别。</param>
    /// <param name="server">要注册并绑定的 INetworkServer 实例。</param>
    public void RegisterServer(string name, INetworkServer server)
    {
        if (servers.TryAdd(name, server))
        {
            Router.BindServer(server);
        }
        else
        {
            Log.Error($"名为“{name}”的服务器已存在。");
        }
    }

    /// <summary>
    /// 从内部集合中移除指定名称的服务器，并在移除时解除其在路由器中的绑定。
    /// </summary>
    /// <remarks>移除为并发安全操作（使用 TryRemove）；移除成功后会调用 Router.UnbindServer 以解除路由绑定。</remarks>
    /// <param name="name">要移除的服务器名称。</param>
    /// <returns>如果找到并移除服务器则返回 true，否则返回 false。</returns>
    public bool UnregisterServer(string name)
    {
        if (servers.TryRemove(name, out var server))
        {
            Router.UnbindServer(server);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 返回与给定名称匹配的 INetworkServer 实例；若不存在则返回 null。
    /// </summary>
    /// <param name="name">要检索的服务器名称。</param>
    /// <returns>匹配的 INetworkServer 实例；未找到时返回 null。</returns>
    public INetworkServer? GetServer(string name)
    {
        servers.TryGetValue(name, out var server);
        return server;
    }

    /// <summary>
    /// 异步启动指定名称的服务器并使其在给定端口上开始监听。
    /// </summary>
    /// <remarks>如果未找到具有指定名称的服务器，将记录错误日志而不抛出异常。</remarks>
    /// <param name="name">要启动的服务器的名称标识符。</param>
    /// <param name="port">服务器应监听的端口号。</param>
    /// <returns>表示操作完成的任务。</returns>
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
    /// 异步停止指定名称的服务器并等待其停止；如果未找到则记录错误。
    /// </summary>
    /// <remarks>如果未找到同名服务器，方法不会抛出异常，而是记录错误并完成任务；若找到服务器则调用其 StopAsync 并等待完成。</remarks>
    /// <param name="name">要停止的服务器的名称。</param>
    /// <returns>表示操作完成的任务。</returns>
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
    /// 异步停止并等待所有已注册的服务器实例停止运行。
    /// </summary>
    /// <remarks>依次调用每个服务器的 StopAsync 并等待其完成；若任一 StopAsync 抛出异常，则该异常会传播到调用方。</remarks>
    /// <returns>在所有服务器停止并完成相关异步操作后完成的任务。</returns>
    public async Task StopAllAsync()
    {
        foreach (var server in servers.Values)
        {
            await server.StopAsync();
        }
    }
}