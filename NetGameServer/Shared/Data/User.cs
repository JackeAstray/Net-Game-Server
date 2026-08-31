using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Shared.Data
{
    public class User
    {
        [Key]
        [Display(Name = "编号")]
        public int Id { get; set; }

        [Display(Name = "随机唯一ID")]
        public string UniqueId { get; set; } = string.Empty;

        [Required]
        [Display(Name = "账号")]
        public string Account { get; set; } = string.Empty;

        [Required]
        [Display(Name = "密码")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "邮箱")]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "昵称")]
        public string Nickname { get; set; } = string.Empty;

        [Display(Name = "注册时间")]
        public DateTime RegistrationTime { get; set; }

        [Display(Name = "最后登录时间")]
        public DateTime LastLoginTime { get; set; }

        [Display(Name = "登录IP")]
        public string LoginIP { get; set; } = string.Empty;

        [Display(Name = "登录次数")]
        public int LoginCount { get; set; }

        [Display(Name = "是否启用")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "是否锁定")]
        public bool IsLocked { get; set; } = false;

        [Display(Name = "是否登录")]
        public bool IsLoggedIn { get; set; } = false;

        [Display(Name = "是否管理员")]
        public bool IsAdmin { get; set; } = false;
    }
}