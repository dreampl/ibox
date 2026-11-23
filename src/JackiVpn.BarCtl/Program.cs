// Hooks hand over UTF-8 JSON; Console.In would decode it with the OEM code page and garble non-ASCII text.
using var stdin = Console.IsInputRedirected
    ? new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false))
    : null;
return await JackiVpn.Agent.BarCtl.RunAsync(args, (System.IO.TextReader?)stdin ?? Console.In, Console.Out, Console.Error);
