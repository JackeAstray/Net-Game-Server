using System;
using System.Threading.Tasks;
using Shared;
using Log = Shared.Log;

namespace Center
{
    /// <summary>
    /// 中心服务器/调度服务器
    /// 负责管理整个集群的状态，记录玩家所在节点，处理跨服社交等调度任务。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 初始化日志组件
            Log.Configure(true, "Logs/Center.log");
            Log.Info("中心服务器(Center Server)正在启动...");

            // 启动网络通信监听网关/Login/Game的内部RPC和指令
            await CenterServerApp.StartNetworkAsync();

            Log.Info("中心服务器启动完成，等待其他服务节点接入...");

            // 保持进程运行
            await Task.Delay(-1);
        }
    }
}
