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
        public IActionResult Login([FromBody] LoginRequest request)
        {
            // 注意：在HTTP无状态API中，我们没有持久连接会话。
            // 我们可以直接返回响应。Session参数被省略或模拟。
            // 调整LoginHandler以返回逻辑结果。

            //这是一个最小的模拟集成
            var result = new LoginResponse
            {
                Success = true,
                Message = "登录成功",
                UserId = 1,
                Token = System.Guid.NewGuid().ToString()
            };
            return Ok(result);
        }

        /// <summary>
        /// 注册账户接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("register")]
        public IActionResult Register([FromBody] RegisterRequest request)
        {
            var result = new RegisterResponse
            {
                Success = true,
                Message = "注册成功"
            };
            return Ok(result);
        }

        /// <summary>
        /// 修改密码接口
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("change-password")]
        public IActionResult ChangePassword([FromBody] ChangePasswordRequest request)
        {
            var result = new ChangePasswordResponse
            {
                Success = true,
                Message = "更改密码成功"
            };
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
            // 需要从LoginHandler注入或调用FindPassword逻辑
            // 现在模仿以匹配集成
            var result = new FindPasswordResponse
            {
                Success = true,
                Message = "找回密码请求已收到"
            };
            return Ok(result);
        }
    }
}
