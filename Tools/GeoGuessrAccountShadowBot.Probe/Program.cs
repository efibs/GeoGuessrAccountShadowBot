// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;
using GeoGuessrAccountShadowBot.Probe;

const string Usage = """
    Usage: dotnet run --project Tools/GeoGuessrAccountShadowBot.Probe -- <command>

      whoami                     Print the account the token belongs to
      get <path>                 GET an API path; the body goes to captures/
      write <METHOD> <path>      Send a changing request (--allow-write, optional --body <json>)
      listen                     Connect to the socket and record frames
          --seconds <n>              Stop after n seconds (default: until Ctrl+C)
          --tab-id <uuid>            Connection tabId (default: random)
          --topic <topic>            Extra topic to subscribe to (repeatable)
          --keepalive-timeout <s>    Use protocol pings, aborting if unanswered
          --no-heartbeat             Skip the 15 s application heartbeat
          --resubscribe-after <s>    Subscribe a second time, to test idempotence
          --send-dm <peerId>         Send --text as a DM after subscribing (--allow-write)
          --send-topic <topic>       Send --text to a chat topic after subscribing (--allow-write)
      scrub <capture.ndjson>     Write committable fixtures to captures/scrubbed/
    """;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

ProbeArguments arguments;
try
{
    arguments = new ProbeArguments(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

if (arguments.Positional.Count == 0)
{
    Console.WriteLine(Usage);
    return 2;
}

if (arguments.Positional[0] == "scrub" && arguments.Positional.Count == 2)
{
    return Scrub(arguments.Positional[1]);
}

ProbeSettings settings;
try
{
    settings = ProbeSettings.Load();
}
catch (Exception exception) when (exception is InvalidOperationException or JsonException or UriFormatException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

var redactor = new TokenRedactor(settings.NcfaToken);
try
{
    return arguments.Positional switch
    {
        ["whoami"] => await WhoAmIAsync(settings, cancellation.Token),
        ["get", var path] => await SendHttpAsync(settings, HttpMethod.Get, path, null, false, cancellation.Token),
        ["write", var method, var path] => await WriteAsync(settings, arguments, method, path, cancellation.Token),
        ["listen"] => await ListenAsync(settings, arguments, cancellation.Token),
        _ => PrintUsage()
    };
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine(redactor.Redact(exception.ToString()));
    return 1;
}

int PrintUsage()
{
    Console.WriteLine(Usage);
    return 2;
}

static async Task<int> WhoAmIAsync(ProbeSettings settings, CancellationToken cancellationToken)
{
    using var capture = new CaptureWriter("whoami");
    using var http = new HttpProbe(settings, false);
    var userId = await http.ReadOwnUserIdAsync(capture, cancellationToken);
    Console.WriteLine($"user id: {userId}");
    Console.WriteLine($"capture: {capture.Path}");
    return 0;
}

static async Task<int> SendHttpAsync(ProbeSettings settings, HttpMethod method, string path, string? body, bool allowWrite, CancellationToken cancellationToken)
{
    using var capture = new CaptureWriter("http");
    using var http = new HttpProbe(settings, allowWrite);
    await http.SendAsync(method, path, body, capture, cancellationToken);
    Console.WriteLine($"capture: {capture.Path}");
    return 0;
}

static Task<int> WriteAsync(ProbeSettings settings, ProbeArguments arguments, string method, string path, CancellationToken cancellationToken)
{
    if (arguments.AllowWrite)
    {
        return SendHttpAsync(settings, new HttpMethod(method.ToUpperInvariant()), path, arguments.Get("--body"), true, cancellationToken);
    }

    Console.Error.WriteLine("write changes the account: pass --allow-write to confirm.");
    return Task.FromResult(2);
}

static async Task<int> ListenAsync(ProbeSettings settings, ProbeArguments arguments, CancellationToken cancellationToken)
{
    var sendTopic = arguments.Get("--send-dm") is { } peerId ? $"chat:Friend:TextMessages:{peerId}" : arguments.Get("--send-topic");
    if (sendTopic is not null && (!arguments.AllowWrite || arguments.Get("--text") is null))
    {
        Console.Error.WriteLine("Sending needs both --text and --allow-write.");
        return 2;
    }

    using var capture = new CaptureWriter("socket");
    Console.WriteLine($"capture: {capture.Path}");

    using var http = new HttpProbe(settings, false);
    var userId = await http.ReadOwnUserIdAsync(capture, cancellationToken);

    var keepAlive = arguments.Get("--keepalive-timeout") is { } seconds
        ? TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture))
        : (TimeSpan?)null;
    var options = new SocketProbeOptions(arguments.Get("--tab-id") ?? Guid.NewGuid().ToString(), !arguments.NoHeartbeat, keepAlive);

    using var socket = new SocketProbe(settings, capture, options);
    if (!await socket.ConnectAsync(cancellationToken))
    {
        return 1;
    }

    string[] topics = [$"self:{userId}", $"chat:Friend:TextMessages:{userId}", .. arguments.GetAll("--topic")];
    foreach (var topic in topics)
    {
        await socket.SendAsync("Subscribe", topic, cancellationToken: cancellationToken);
    }

    using var runtime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    if (arguments.GetInt("--seconds", 0) is > 0 and var duration)
    {
        runtime.CancelAfter(TimeSpan.FromSeconds(duration));
    }

    var run = socket.RunAsync(runtime.Token);

    if (arguments.GetInt("--resubscribe-after", 0) is > 0 and var delay)
    {
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(delay), runtime.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        if (!run.IsCompleted && !runtime.IsCancellationRequested)
        {
            await socket.SendAsync("Subscribe", topics[0], cancellationToken: runtime.Token);
        }
    }

    if (sendTopic is not null && !run.IsCompleted)
    {
        await socket.SendAsync("ChatMessage", sendTopic, arguments.Get("--text"), cancellationToken: runtime.Token);
    }

    await run;
    return 0;
}

static int Scrub(string capturePath)
{
    var outputDirectory = Path.Combine(CaptureWriter.CaptureDirectory, "scrubbed");
    Directory.CreateDirectory(outputDirectory);

    var scrubber = new FrameScrubber();
    var frameNumber = 0;
    foreach (var line in File.ReadLines(capturePath))
    {
        var entry = JsonSerializer.Deserialize<CaptureLine>(line, CaptureLine.SerializerOptions);
        if (entry is not { Direction: "in" })
        {
            continue;
        }

        var scrubbed = scrubber.Scrub(entry.Text);
        var code = scrubbed["code"]?.GetValue<string>() ?? "unknown";
        var fileName = $"{++frameNumber:D3}-{code}.json";
        File.WriteAllText(Path.Combine(outputDirectory, fileName), scrubbed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    Console.WriteLine($"{frameNumber} frames written to {outputDirectory}. Review them before committing.");
    return 0;
}
