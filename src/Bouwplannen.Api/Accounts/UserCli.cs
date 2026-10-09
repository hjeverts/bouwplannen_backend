using System.Text;
using Bouwplannen.Api.Projects;
using Microsoft.Extensions.Options;

namespace Bouwplannen.Api.Accounts;

/// <summary>
/// Account management from the command line: "Bouwplannen.Api users …". In the Docker setup the
/// deploy repo wraps this as ./user.sh. Works while the API runs: the API picks up changes itself.
/// </summary>
public static class UserCli
{
    public const string Usage = """
        Gebruik: users <opdracht> [opties]

          add <naam> [--admin] [--name "Volledige naam"]   account aanmaken (vraagt wachtwoord)
          passwd <naam>                                    nieuw wachtwoord (logt overal uit)
          list                                             alle accounts tonen
          admin <naam> on|off                              beheerdersrechten geven of afnemen
          disable <naam>                                   account blokkeren (logt overal uit)
          enable <naam>                                    geblokkeerd account weer toestaan
          logout <naam>                                    overal uitloggen

        Het wachtwoord wordt gevraagd, of gelezen uit de omgevingsvariabele BOUWPLANNEN_PASSWORD.
        Het eerste account wordt altijd beheerder.
        """;

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error, Func<string?>? readSecret = null)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var storage = config.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        var store = new AccountStore(Options.Create(storage), TimeProvider.System);
        readSecret ??= () => ReadHidden(input, output);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            switch (args[0])
            {
                case "add":
                {
                    var name = Arg(args, 1);
                    var isAdmin = args.Contains("--admin");
                    var display = Option(args, "--name");
                    var password = AskPassword(readSecret, output);
                    var user = store.CreateUser(name, display, password, isAdmin);
                    output.WriteLine($"Account '{user.Username}' aangemaakt{(user.IsAdmin ? " (beheerder)" : "")}, met een eigen privégroep.");
                    return 0;
                }
                case "passwd":
                {
                    var user = Find(store, Arg(args, 1));
                    store.SetPassword(user.Id, AskPassword(readSecret, output));
                    output.WriteLine($"Wachtwoord van '{user.Username}' gewijzigd. Bestaande logins zijn verlopen.");
                    return 0;
                }
                case "list":
                {
                    var data = store.Snapshot();
                    if (data.Users.Count == 0)
                    {
                        output.WriteLine("Nog geen accounts. Maak er een met: users add <naam>");
                        return 0;
                    }
                    output.WriteLine($"{"Naam",-20} {"Volledige naam",-24} {"Rol",-11} {"Status",-11} Groepen");
                    foreach (var u in data.Users.OrderBy(u => u.Username))
                    {
                        var groups = data.Groups.Where(g => g.Members.Contains(u.Id) && g.PersonalOf is null).Select(g => g.Name);
                        output.WriteLine($"{u.Username,-20} {Trim(u.DisplayName, 24),-24} {(u.IsAdmin ? "beheerder" : "gebruiker"),-11} {(u.Disabled ? "geblokkeerd" : "actief"),-11} {string.Join(", ", groups)}");
                    }
                    return 0;
                }
                case "admin":
                {
                    var user = Find(store, Arg(args, 1));
                    var on = Arg(args, 2) switch
                    {
                        "on" or "aan" => true,
                        "off" or "uit" => false,
                        _ => throw new AccountException("Gebruik: admin <naam> on|off"),
                    };
                    if (!on && store.Snapshot().Users.Count(u => u.IsAdmin && !u.Disabled) == 1 && user.IsAdmin)
                        throw new AccountException("Dit is de laatste beheerder; maak eerst iemand anders beheerder.");
                    store.ModifyUser(user.Id, u => u.IsAdmin = on);
                    output.WriteLine($"'{user.Username}' is nu {(on ? "beheerder" : "gewone gebruiker")}.");
                    return 0;
                }
                case "disable":
                case "enable":
                {
                    var user = Find(store, Arg(args, 1));
                    var disable = args[0] == "disable";
                    store.ModifyUser(user.Id, u => u.Disabled = disable, newStamp: disable);
                    output.WriteLine(disable ? $"'{user.Username}' is geblokkeerd en overal uitgelogd." : $"'{user.Username}' kan weer inloggen.");
                    return 0;
                }
                case "logout":
                {
                    var user = Find(store, Arg(args, 1));
                    store.ModifyUser(user.Id, _ => { }, newStamp: true);
                    output.WriteLine($"'{user.Username}' is op alle apparaten uitgelogd.");
                    return 0;
                }
                default:
                    error.WriteLine($"Onbekende opdracht '{args[0]}'.");
                    output.WriteLine(Usage);
                    return 2;
            }
        }
        catch (AccountException ex)
        {
            error.WriteLine("FOUT: " + ex.Message);
            return 1;
        }
    }

    private static string Arg(string[] args, int index) =>
        args.Length > index && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? args[index]
            : throw new AccountException("Gebruikersnaam ontbreekt. Zie: users help");

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static UserRecord Find(AccountStore store, string name) =>
        store.FindByUsername(name) ?? throw new AccountException($"Er is geen gebruiker '{AccountStore.NormalizeUsername(name)}'.");

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string AskPassword(Func<string?> readSecret, TextWriter output)
    {
        var fromEnv = Environment.GetEnvironmentVariable("BOUWPLANNEN_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        output.Write($"Wachtwoord (minstens {PasswordHasher.MinLength} tekens): ");
        var first = readSecret() ?? "";
        if (PasswordHasher.Check(first) is { } problem) throw new AccountException(problem);
        output.Write("Nog een keer: ");
        var second = readSecret() ?? "";
        if (first != second) throw new AccountException("De wachtwoorden zijn niet gelijk.");
        return first;
    }

    /// <summary>Reads a line without echoing it when typed at a terminal.</summary>
    private static string? ReadHidden(TextReader input, TextWriter output)
    {
        if (Console.IsInputRedirected || !ReferenceEquals(input, Console.In)) return input.ReadLine();
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        output.WriteLine();
        return sb.ToString();
    }
}
