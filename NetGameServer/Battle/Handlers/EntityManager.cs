using System;
using System.Collections.Generic;
using System.Linq;

namespace Battle.Handlers
{
    /// <summary>
    /// 场景实体管理器：继承框架实体管理器，额外提供会话 ID 视角（兼容旧代码）。
    /// </summary>
    public class EntityManager : Framework.Entity.EntityManager
    {
        /// <summary>所有实体会话 ID（兼容旧代码）。</summary>
        public IEnumerable<long> GetAllSessionIds()
        {
            return GetAllEntityIds();
        }
    }
}
