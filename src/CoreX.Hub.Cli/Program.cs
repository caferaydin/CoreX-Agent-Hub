using System.Text;
using CoreX.Hub.Cli;

Console.OutputEncoding = Encoding.UTF8;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await HubCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
