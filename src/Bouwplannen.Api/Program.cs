using Bouwplannen.Api;
using Bouwplannen.Api.Accounts;

// "users …" manages accounts from the command line instead of starting the web server.
if (args.Length > 0 && args[0] == "users")
    return UserCli.Run(args[1..], Console.In, Console.Out, Console.Error);

var app = BouwplannenApp.Build(args);
app.Run();
return 0;
