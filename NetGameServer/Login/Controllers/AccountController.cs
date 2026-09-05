using Microsoft.AspNetCore.Mvc;
using Login.Handlers;
using Shared.Messages.Login;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Login.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AccountController : ControllerBase
    {
        private readonly LoginHandler loginHandler;

        public AccountController(LoginHandler loginHandler)
        {
            this.loginHandler = loginHandler;
        }

        /// <summary>
        /// 登录账户接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var result = await loginHandler.HandleLoginRequestAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 注册账户接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            var result = await loginHandler.HandleRegisterRequestAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 修改密码接口。
        /// 需要同时满足：有效 API Key + 有效登录 Token（X-Auth-Token）且仅允许修改本人账户。
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            string? token = Request.Headers["X-Auth-Token"].FirstOrDefault();
            var (allowed, reason, response) = await loginHandler.HandleChangePasswordWithTokenAsync(request, token);
            if (!allowed || response == null)
            {
                return Unauthorized(new ChangePasswordResponse { Success = false, Message = reason ?? "未授权" });
            }
            return Ok(response);
        }

        /// <summary>
        /// 更改昵称接口。
        /// P2 修复：原实现是无操作的假成功（无论请求如何都返回"更改成功"，但服务端并未持久化昵称）。
        /// 与 find-password 的约定一致，显式返回未实现，避免客户端误以为昵称已修改。
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("change-nickname")]
        public IActionResult ChangeNickname([FromBody] ChangeNicknameRequest request)
        {
            var result = new ChangeNicknameResponse
            {
                Success = false,
                Message = "昵称修改功能尚未实现，请勿依赖此接口"
            };
            return Ok(result);
        }

        /// <summary>
        /// 找回密码接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("find-password")]
        public async Task<IActionResult> FindPassword([FromBody] FindPasswordRequest request)
        {
            var result = await loginHandler.HandleFindPasswordRequestAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 查询账户信息接口。
        /// P2 修复：除 API Key 外，还要求携带登录 Token（X-Auth-Token 头）且只能查询 Token 持有人本人的账户，
        /// 防止共享 Key 泄露后任意枚举/窥探他人账户状态（含 Email 等个人数据）。
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("query-account")]
        public async Task<IActionResult> QueryAccount([FromBody] AccountQueryRequest request)
        {
            string? token = Request.Headers["X-Auth-Token"].FirstOrDefault();
            var (allowed, reason, response) = await loginHandler.HandleAccountQueryWithTokenAsync(request, token);
            if (!allowed || response == null)
            {
                return Unauthorized(new AccountQueryResponse { Exists = false, Message = reason ?? "未授权" });
            }
            return Ok(response);
        }

        /// <summary>
        /// 查询在线统计接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpGet("online-stats")]
        public async Task<IActionResult> OnlineStats([FromQuery] OnlineStatsRequest request)
        {
            var result = await loginHandler.HandleOnlineStatsRequestAsync(request);
            return Ok(result);
        }
    }
}
