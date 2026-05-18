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
        /// 修改密码接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            var result = await loginHandler.HandleChangePasswordRequestAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 更改昵称接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("change-nickname")]
        public IActionResult ChangeNickname([FromBody] ChangeNicknameRequest request)
        {
            var result = new ChangeNicknameResponse
            {
                Success = true,
                Message = "更改昵称成功"
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
        /// 查询账户信息接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("query-account")]
        public async Task<IActionResult> QueryAccount([FromBody] AccountQueryRequest request)
        {
            var result = await loginHandler.HandleAccountQueryRequestAsync(request);
            return Ok(result);
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
