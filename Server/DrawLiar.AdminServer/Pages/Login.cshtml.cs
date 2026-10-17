using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DrawLiar.Admin.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace DrawLiar.Admin.Pages;

[EnableRateLimiting("admin-login")]
public sealed class LoginModel(AdminAuthenticationService authentication) : PageModel
{
    [BindProperty, Required(ErrorMessage = "계정명을 입력하세요."), StringLength(100, ErrorMessage = "계정명은 100자 이내로 입력하세요.")]
    public string Username { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "비밀번호를 입력하세요."), StringLength(256, ErrorMessage = "비밀번호는 256자 이내로 입력하세요.")]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Redirect("./") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        string? username = authentication.ValidateCredentials(Username.Trim(), Password);
        if (username == null)
        {
            ModelState.AddModelError(string.Empty, "계정명 또는 비밀번호가 올바르지 않습니다.");
            return Page();
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        return Redirect("./");
    }
}
