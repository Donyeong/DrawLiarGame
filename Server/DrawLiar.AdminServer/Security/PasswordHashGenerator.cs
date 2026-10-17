using Microsoft.AspNetCore.Identity;

namespace DrawLiar.Admin.Security;

public static class PasswordHashGenerator
{
    public static bool IsRequested(string[] args) => args.Contains("--generate-password-hash", StringComparer.Ordinal);

    public static int Run()
    {
        string? password = Environment.GetEnvironmentVariable("DRAWLIAR_ADMIN_PASSWORD");
        if (password == null)
        {
            if (Console.IsInputRedirected)
                password = Console.ReadLine();
            else
            {
                Console.Error.Write("관리자 비밀번호: ");
                var input = new System.Text.StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace && input.Length > 0) input.Length--;
                    else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
                }
                Console.Error.WriteLine();
                password = input.ToString();
            }
        }
        if (string.IsNullOrEmpty(password) || password.Length > 256)
        {
            Console.Error.WriteLine("비밀번호는 1~256자로 입력하세요.");
            return 1;
        }
        Console.WriteLine(new PasswordHasher<AdminUser>().HashPassword(new AdminUser(), password));
        return 0;
    }
}
