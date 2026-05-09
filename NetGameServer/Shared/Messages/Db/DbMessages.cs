using System;

namespace Shared.Messages.Db
{
    public class GetMaxUidRequest
    {
    }

    public class GetMaxUidResponse
    {
        public int MaxUid { get; set; }
    }

    public class LoginVerifyRequest
    {
        public string Account { get; set; }
        public string Password { get; set; }
    }

    public class LoginVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public long UserId { get; set; }
    }

    public class RegisterVerifyRequest
    {
        public string Account { get; set; }
        public string Password { get; set; }
        public string Nickname { get; set; }
        public long Uid { get; set; }
    }

    public class RegisterVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}