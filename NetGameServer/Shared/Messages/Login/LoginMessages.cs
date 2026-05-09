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
    /// 表示找回密码操作的请求信息。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装找回密码操作所需的账户和邮箱信息。
    /// 通常与用户找回密码 API 一起使用。所有属性均为必填项，
    /// 调用方应确保提供有效的账户和邮箱值。
    /// </remarks>
    public class FindPasswordRequest
    {
        public string Account { get; set; }
        public string Email { get; set; }
    }

    /// <summary>
    /// 表示找回密码操作的结果。
    /// </summary>
    /// <remarks>
    /// 此类型用于封装找回密码操作的响应状态和相关消息。
    /// 通常与用户找回密码 API 一起返回，帮助调用方判断操作是否成功并获取详细提示。
    /// </remarks>
    public class FindPasswordResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}