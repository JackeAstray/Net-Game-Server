using System;

namespace Shared.Messages.Login
{
    /// <summary>
    /// 表示登录操作的请求信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装登录操作所需的账户和密码信息。
    /// 通常与用户登录 API 一起使用。所有属性均为必填项，
    /// 调用方应确保提供有效的账户和密码值。
    /// </remarks>
    public class LoginRequest
    {
        public string Account { get; set; }
        public string Password { get; set; }
    }

    /// <summary>
    /// 表示登录操作的结果信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装登录操作的响应状态和相关消息。
    /// 通常与用户登录 API 一起返回，帮助调用方判断操作是否成功并获取详细提示。
    /// </remarks>
    public class LoginResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int UserId { get; set; }
        public string Token { get; set; }
        public string UniqueId { get; set; }
        public string Nickname { get; set; }
        public string Email { get; set; }
        public DateTime LastLoginTime { get; set; }
        public int LoginCount { get; set; }
        public bool IsAdmin { get; set; }
    }

    /// <summary>
    /// 表示用于用户注册操作的数据请求对象。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装注册流程中所需的账户和密码信息。
    /// 通常与用户注册 API 一起使用。所有属性均为必填项，
    /// 调用方应确保提供有效的账户和密码值。
    /// </remarks>
    public class RegisterRequest
    {
        public string Account { get; set; }
        public string Password { get; set; }
        public string Nickname { get; set; }
    }

    /// <summary>
    /// 表示用户注册操作的结果信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装注册操作的响应状态和相关消息。
    /// 通常与用户注册 API 一起返回，帮助调用方判断操作是否成功并获取详细提示。
    /// </remarks>
    public class RegisterResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示用户注销操作的请求信息。包含用户标识以便服务器识别要注销的用户。
    /// </summary>
    public class LogoutRequest
    {
        public int UserId { get; set; }
    }

    /// <summary>
    /// 表示用户注销操作的结果信息。
    /// </summary>
    public class LogoutResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示更改密码操作的请求信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装更改密码操作所需的账户和密码信息。
    /// 通常与用户更改密码 API 一起使用。所有属性均为必填项，
    /// 调用方应确保提供有效的账户和密码值。
    /// </remarks>
    public class ChangePasswordRequest
    {
        public string Account { get; set; }
        public string OldPassword { get; set; }
        public string NewPassword { get; set; }
    }

    /// <summary>
    /// 表示更改密码操作的结果。包含操作是否成功以及相关的消息信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装更改密码操作的响应状态和相关消息。
    /// 通常与用户更改密码 API 一起返回，帮助调用方判断操作是否成功并获取详细提示。
    /// </remarks>
    public class ChangePasswordResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示用于更改用户昵称的请求数据。包含用户标识和新的昵称信息。
    /// </summary>
    /// <remarks>
    /// 通常用于将用户的昵称更新为新的值。
    /// 请确保在提交请求前验证新昵称的有效性和唯一性，
    /// 以避免冲突或无效输入。
    /// </remarks>
    public class ChangeNicknameRequest
    {
        public int UserId { get; set; }
        public string NewNickname { get; set; }
    }

    /// <summary>
    /// 表示更改昵称操作的结果信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装更改昵称操作的响应状态和相关消息。
    /// 通常与用户更改昵称 API 一起返回，帮助调用方判断操作是否成功并获取详细提示。
    /// </remarks>
    public class ChangeNicknameResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示找回密码发送验证码的请求信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装找回密码时的账号和邮箱信息，用于向邮箱发送一次性验证码。
    /// </remarks>
    public class FindPasswordRequest
    {
        public string Account { get; set; }
        public string Email { get; set; }
    }

    /// <summary>
    /// 表示找回密码发送验证码的结果。
    /// </summary>
    public class FindPasswordResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示验证码重置密码的请求信息。
    /// </summary>
    public class ResetPasswordWithCodeRequest
    {
        public string Account { get; set; }
        public string Email { get; set; }
        public string Code { get; set; }
        public string NewPassword { get; set; }
    }

    /// <summary>
    /// 表示验证码重置密码的结果。
    /// </summary>
    public class ResetPasswordWithCodeResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示用户被顶号的通知信息。
    /// </summary>
    public class KickedOffMessage
    {
        public string Reason { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// 表示账户查询请求信息。
    /// </summary>
    public class AccountQueryRequest
    {
        public string Account { get; set; }
    }

    /// <summary>
    /// 表示账户查询操作的结果信息。
    /// </summary>
    public class AccountQueryResponse
    {
        public bool Exists { get; set; }
        public bool IsOnline { get; set; }
        public bool IsLocked { get; set; }
        public bool IsAdmin { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 表示在线统计请求信息。
    /// </summary>
    public class OnlineStatsRequest
    {
    }

    /// <summary>
    /// 表示在线统计操作的结果信息。
    /// </summary>
    public class OnlineStatsResponse
    {
        public int OnlineCount { get; set; }
        public int OfflineCount { get; set; }
        public int TotalCount { get; set; }
    }
}